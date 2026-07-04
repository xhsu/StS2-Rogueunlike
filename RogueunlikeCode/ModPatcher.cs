using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Patch bootstrap with blast-radius control. Patches install per FEATURE GROUP, each on
/// its own Harmony id; a group whose game-side target vanished (the classic game-update
/// break) rolls back completely and logs WHICH feature degrades to vanilla — the other
/// groups, and the whole mod, stay alive. A plain PatchAll would instead die at the
/// first bad target with everything before it applied and everything after it not.
///
/// Rules for future patches (enforced at startup, see VerifyAllPatchesGrouped):
///   • every [HarmonyPatch] class in this assembly must be listed in exactly one group;
///   • a group must stay consistent standing alone — bundle patches that share state;
///   • cross-group runtime dependencies are expressed as OnFailure poison flags
///     (e.g. reward-net failing forces the wire check broken so no MP substitution
///     ever runs against missing message handlers), never as load-order assumptions.
///
/// docs/GAME-API-SURFACE.md is the maintainer map of every game API this mod leans on.
/// </summary>
internal static class ModPatcher
{
    private sealed class Group
    {
        public string Name = "";
        public string Feature = "";
        public Type[] Patches = Array.Empty<Type>();
        public Action? OnFailure;
    }

    private static readonly List<string> _failed = new();

    /// <summary>Names of groups that failed to install this session (health summary).</summary>
    public static IReadOnlyList<string> FailedGroups => _failed;

    public static void ApplyAll()
    {
        Group[] groups =
        {
            new()
            {
                Name = "seen-gate",
                Feature = "candidate-click discovery rule (pickers would mark items seen on render, like vanilla)",
                Patches = new[] { typeof(SeenSuppressionPatch) },
            },
            new()
            {
                Name = "wire-check",
                Feature = "early multiplayer handshake triggers (the rewards-set backstop may still announce)",
                Patches = new[] { typeof(WireCheckRunStartPatch), typeof(WireCheckRoomEnterPatch) },
            },
            new()
            {
                Name = "reward-net",
                Feature = "multiplayer pick/assign sync",
                Patches = new[] { typeof(ModPickNet), typeof(MerchantRoomTrackPatch) },
                // Without handlers, a remote's substitution would never replay here (and
                // ours never there): no MP substitution may run at all this session.
                OnFailure = () => ModWireCheck.ForceBroken("reward message handlers failed to install"),
            },
            new()
            {
                Name = "card-rewards",
                Feature = "feature #1: full-pool card rewards",
                Patches = new[]
                {
                    typeof(ShowAllCardRewardsPatch), typeof(CardRewardScreenOverhaul),
                    typeof(CardGridVisibilityPatch), typeof(CardGridSparklePatch),
                    typeof(CardRewardGetHolderPatch),
                },
            },
            new()
            {
                Name = "reward-pickers",
                Feature = "features #2/#3: potion & relic reward pickers",
                Patches = new[]
                {
                    typeof(PotionRewardPickPatch), typeof(PotionRewardLabelPatch), typeof(PotionRewardHoverTipPatch),
                    typeof(RelicRewardPickPatch), typeof(RelicRewardLabelPatch), typeof(RelicRewardHoverTipPatch),
                },
            },
            new()
            {
                Name = "shop",
                Feature = "feature #4: merchant shade-and-assign",
                Patches = new[]
                {
                    typeof(ShopPicker.StockPatch), typeof(ShopPicker.ShopScreenAnchorPatch),
                    typeof(ShopPicker.RestockPatch), typeof(ShopPicker.SlotClickPatch),
                    typeof(ShopPicker.CardShadePatch), typeof(ShopPicker.RelicShadePatch),
                    typeof(ShopPicker.PotionShadePatch), typeof(ShopPicker.HoverTipPatch),
                    typeof(ShopPicker.CardPreviewPatch),
                },
            },
            new()
            {
                Name = "chest",
                Feature = "feature #3.1: treasure chest picker",
                Patches = new[]
                {
                    typeof(TreasureChestPicker.BeginRelicPickingPatch), typeof(TreasureChestPicker.OnPickedPatch),
                    typeof(TreasureChestPicker.InitializeRelicsPatch), typeof(TreasureChestPicker.ShadedHolderTipPatch),
                    typeof(TreasureChestPicker.CollectionTeardownPatch),
                },
            },
            new()
            {
                Name = "ancient",
                Feature = "features #5/#5.1: Ancient picker & option designation",
                Patches = new[]
                {
                    typeof(AncientPickerPatch), typeof(MapInputGatePatch),
                    typeof(AncientEventSubstitutionPatch), typeof(AncientPickResetPatch),
                    typeof(AncientOptionSubstitutionPatch),
                },
            },
            new()
            {
                Name = "unknown",
                Feature = "feature #6: unknown (?) node picker",
                Patches = new[]
                {
                    typeof(UnknownPickerPatch), typeof(UnknownTravelTallyPatch),
                    typeof(ForcedUnknownRollPatch), typeof(ForcedNextEventPatch),
                    typeof(ForcedNextEncounterPatch), typeof(UnknownPickResetPatch),
                },
            },
        };

        foreach (Group group in groups)
            Apply(group);
        VerifyAllPatchesGrouped(groups);
    }

    private static void Apply(Group group)
    {
        var harmony = new Harmony($"{MainFile.ModId}.{group.Name}");
        try
        {
            foreach (Type type in group.Patches)
                harmony.CreateClassProcessor(type).Patch();
        }
        catch (Exception e)
        {
            _failed.Add(group.Name);
            try
            {
                harmony.UnpatchAll(harmony.Id); // no half-applied groups
            }
            catch (Exception rollback)
            {
                MainFile.Logger.Error($"[patcher] rollback of '{group.Name}' incomplete: {rollback}");
            }
            MainFile.Logger.Error(
                $"[patcher] group '{group.Name}' failed to install — {group.Feature} is DISABLED this session "
                + "(vanilla behavior); all other features are unaffected. Probable cause: a game update moved a "
                + $"method this group hooks — see docs/GAME-API-SURFACE.md for the quick-fix map. {e}");
            try
            {
                group.OnFailure?.Invoke();
            }
            catch (Exception poison)
            {
                MainFile.Logger.Error($"[patcher] '{group.Name}' failure handler threw: {poison}");
            }
        }
    }

    // Future-me guard: a new [HarmonyPatch] class that never got registered in a group
    // would silently never install (this replaced PatchAll). Loud, at startup, every run.
    private static void VerifyAllPatchesGrouped(Group[] groups)
    {
        try
        {
            var grouped = groups.SelectMany(g => g.Patches).ToHashSet();
            foreach (Type type in typeof(ModPatcher).Assembly.GetTypes())
                if (!grouped.Contains(type) && type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length > 0)
                    MainFile.Logger.Error($"[patcher] {type.FullName} carries [HarmonyPatch] but is in NO patch group — it is NOT installed. Register it in ModPatcher.ApplyAll.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[patcher] group-coverage verification failed: {e}");
        }
    }
}

/// <summary>
/// Startup health check: turns the silent-drift class of game-update breakage into loud,
/// actionable log lines. Three kinds of checks:
///   • game-version stamp — the mirrored vanilla bodies and semantic assumptions were
///     verified against a specific game version; a different one gets a single warning
///     listing exactly what to re-verify (see docs/GAME-API-SURFACE.md);
///   • live assumption probes — assumptions cheap enough to verify against the running
///     game (the ? odds bucket order, the chest VotesChanged event field);
///   • donor assets — the vanilla scenes/textures the pickers scavenge.
/// Warn-only by design: nothing here disables features (the patcher and each feature's
/// own fallbacks do the degrading); this is diagnosis, not enforcement.
/// </summary>
internal static class ModHealthCheck
{
    /// <summary>
    /// The game version the mirrored bodies / semantic assumptions were last verified
    /// against (see docs/GAME-API-SURFACE.md § mirrored bodies). Bump after re-verifying
    /// on a game update.
    /// </summary>
    public const string VerifiedGameVersion = "0.107.1";

    public static void Run()
    {
        int warnings = 0;
        warnings += Check(CheckGameVersion);
        warnings += Check(CheckUnknownOddsBucketOrder);
        warnings += Check(CheckChestVotesChangedEvent);
        warnings += Check(CheckDonorAssets);
        warnings += ModPatcher.FailedGroups.Count;
        MainFile.Logger.Info(warnings == 0
            ? $"[health] startup checks green (game matches verified {VerifiedGameVersion}; all patch groups installed)"
            : $"[health] startup checks: {warnings} warning(s) — see lines above");
    }

    private static int Check(Func<int> check)
    {
        try
        {
            return check();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[health] a startup check itself failed (mod continues): {e}");
            return 1;
        }
    }

    private static int CheckGameVersion()
    {
        SemanticVersion? running = ModManager._gameVersion;
        if (running == null)
        {
            MainFile.Logger.Warn("[health] game reports no version (no ReleaseInfo); cannot compare against the verified one");
            return 1;
        }
        if (SemanticVersion.TryFromString(VerifiedGameVersion, out SemanticVersion? verified)
            && running.CompareTo(verified) == 0)
            return 0;
        MainFile.Logger.Warn(
            $"[health] game version {running} differs from {VerifiedGameVersion}, which this mod build was verified against. "
            + "Features keep running, but re-verify the mirrored vanilla logic against the new decompile "
            + "(docs/GAME-API-SURFACE.md § mirrored logic): the ? odds bucket walk (peek/chances), RoomSet pull "
            + "semantics, reward/shop/chest pool mirrors. The BeginEvent/Roll substitutions self-assert at patch time.");
        return 1;
    }

    // Feature #6's chance display, peek and forced-roll escalation mirror Roll's bucket
    // walk, which iterates the odds dictionaries in insertion order (Monster, Elite,
    // Treasure, Shop). Verify against a live instance — no baseline files, no IL games.
    private static int CheckUnknownOddsBucketOrder()
    {
        RoomType[] expected = { RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop };
        var probe = new UnknownMapPointOdds(new Rng(0u));
        if (probe._nonEventOdds.Keys.SequenceEqual(expected) && probe._baseOdds.Keys.SequenceEqual(expected))
            return 0;
        MainFile.Logger.Warn(
            "[health] UnknownMapPointOdds bucket order changed (expected Monster, Elite, Treasure, Shop). "
            + "Feature #6's chance percentages and fate peek may be wrong until the mirror in UnknownPools is updated; "
            + "picks themselves stay pool-validated.");
        return 1;
    }

    // The chest picker raises the synchronizer's VotesChanged through its backing field
    // (no public raise API). If the event is renamed the picker degrades (guarded null),
    // but say so up front instead of one quiet log line mid-chest.
    private static int CheckChestVotesChangedEvent()
    {
        if (AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "VotesChanged") != null)
            return 0;
        MainFile.Logger.Warn(
            "[health] TreasureRoomRelicSynchronizer.VotesChanged backing field not found — chest picker vote "
            + "icons/holders will not refresh on remote votes (picking still resolves).");
        return 1;
    }

    // The vanilla scenes/textures the pickers scavenge widgets from. Each scavenger
    // already degrades gracefully; this makes a reshaped donor visible at startup
    // instead of as a missing search bar mid-run.
    private static int CheckDonorAssets()
    {
        (string path, string usedFor)[] donors =
        {
            (SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen"), "card grid + confirm checkmark"),
            (SceneHelper.GetScenePath("screens/deck_view_screen"), "sort bar, upgrades tickbox, back button"),
            (SceneHelper.GetScenePath("screens/card_library/card_library"), "search bar"),
            ("res://images/packed/sprite_fonts/star_icon.png", "unseen-star badge"),
        };
        int warnings = 0;
        foreach ((string path, string usedFor) in donors)
        {
            if (ResourceLoader.Exists(path))
                continue;
            MainFile.Logger.Warn($"[health] donor asset missing: {path} ({usedFor}) — the dependent widgets degrade gracefully");
            warnings++;
        }
        return warnings;
    }
}
