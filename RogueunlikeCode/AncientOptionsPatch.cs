using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>One designatable option of an Ancient slot, as discovered by the probe.</summary>
public sealed class AncientOptionInfo
{
    public string Identity = "";  // "K:<textKey>" for authored options, "R:<relicEntry>" for relic options
    public string Title = "";
    public string Description = "";
    public RelicModel? Relic;
    public IHoverTip[] Tips = Array.Empty<IHoverTip>();
}

/// <summary>The Ancient picker's confirmed result: the Ancient plus per-slot designations.</summary>
public sealed class AncientPickResult
{
    public AncientEventModel Ancient = null!;
    public readonly List<(int Slot, AncientOptionInfo Option)> Options = new();
}

/// <summary>
/// Feature #5.1, discovery half: the slot pools of an Ancient AT CURRENT CONTEXT,
/// discovered empirically. EventModel.Rng is a per-event RNG documented as independent
/// of the run's centralized streams (its state is disposable), so we can run the
/// Ancient's own option generation on throwaway clones with reseeded RNG and union what
/// each slot index produced. Anything observed is by definition vanilla-rollable for
/// this player's deck and state — conditional options (Tanx's Tri-Boomerang, Pael's
/// Claw...) appear exactly when their conditions hold, structure (Vakuu's one-per-pool
/// slots) is captured per index, and no per-Ancient knowledge is hardcoded, so future
/// game updates flow in. Fixed seeds + lockstep-identical player state make the result
/// deterministic across multiplayer clients, which is what lets it validate picks.
/// </summary>
public static class AncientOptionProbe
{
    // ponytail: sample count for pool convergence. Branches in generators are coin
    // flips and small shuffles; 128 draws bounds a miss to (1/2)^128-ish per branch.
    private const int Samples = 128;

    public static List<List<AncientOptionInfo>> SlotPools(Player player, AncientEventModel canonicalAncient)
    {
        var pools = new List<List<AncientOptionInfo>>();
        var seen = new List<HashSet<string>>();
        for (int i = 0; i < Samples; i++)
        {
            try
            {
                if (canonicalAncient.ToMutable() is not AncientEventModel probe)
                    return pools;
                probe.Owner = player;
                probe.Rng = new Rng((uint)(0x524F4755 + i)); // "ROGU" + index: fixed → identical pools on every client
                if (AccessTools.Method(probe.GetType(), "GenerateInitialOptions")
                        ?.Invoke(probe, null) is not IReadOnlyList<EventOption> options)
                    return pools;
                for (int slot = 0; slot < options.Count; slot++)
                {
                    EventOption option = options[slot];
                    if (option.IsProceed)
                        continue;
                    while (pools.Count <= slot)
                    {
                        pools.Add(new List<AncientOptionInfo>());
                        seen.Add(new HashSet<string>());
                    }
                    string identity = IdentityOf(option);
                    if (seen[slot].Add(identity))
                        pools[slot].Add(Describe(option, identity));
                }
            }
            catch (Exception e)
            {
                // A generator that throws once throws every sample — bail, don't spam.
                MainFile.Logger.Info($"[ancient options] probe failed for {canonicalAncient.Id}: {e.Message}");
                return pools;
            }
        }
        return pools;
    }

    // Authored options carry unique loc TextKeys (the same identity the game's own
    // debug option-forcing matches on); relic options all share a page key, so theirs
    // is the relic id.
    public static string IdentityOf(EventOption option) =>
        option.Relic != null ? "R:" + option.Relic.Id.Entry : "K:" + option.TextKey;

    private static AncientOptionInfo Describe(EventOption option, string identity)
    {
        var info = new AncientOptionInfo { Identity = identity, Relic = option.Relic };
        try
        {
            info.Title = option.Title.GetFormattedText();
        }
        catch (Exception)
        {
            info.Title = identity;
        }
        try
        {
            info.Description = option.Description.GetFormattedText();
        }
        catch (Exception) { }
        try
        {
            info.Tips = option.HoverTips.ToArray();
        }
        catch (Exception) { }
        return info;
    }
}

/// <summary>
/// Feature #5.1, substitution half. Runs AFTER the vanilla roll (whose RNG was consumed
/// exactly as vanilla — save-safe) and swaps each designated slot for the designated
/// option, resolved against the LIVE event instance so effects bind to the real run:
/// authored options by TextKey from AllPossibleOptions (mirroring the game's own
/// DebugOption forcing), relic options manufactured through the event's vanilla
/// RelicOption helper. Slot count and structure therefore stay exactly vanilla;
/// designations only ever put an option where the probe saw vanilla put it.
/// Designations are consumed per player here; a Wax-Choker-blocked event (proceed-only)
/// keeps its block, and a mid-event reload has no designations left — vanilla roll.
/// </summary>
[HarmonyPatch(typeof(AncientEventModel), "GenerateInitialOptionsWrapper")]
public static class AncientOptionSubstitutionPatch
{
    static void Postfix(AncientEventModel __instance, ref IReadOnlyList<EventOption> __result)
    {
        try
        {
            Player? owner = __instance.Owner;
            if (owner == null || __result == null || __result.Count == 0)
                return;
            if (!AncientPickSync.TryTakeOptions(owner.NetId, __instance.Id.Entry,
                    out List<(int Slot, string Identity)> picks) || picks.Count == 0)
                return;
            if (__result.Count == 1 && __result[0].IsProceed)
                return; // event blocked (Wax Choker): nothing to substitute into

            List<EventOption> options = __result.ToList();
            var used = options.Select(SafeIdentity).ToHashSet(); // distinctness across kept rolls too
            foreach ((int slot, string identity) in picks)
            {
                if (slot < 0 || slot >= options.Count || used.Contains(identity))
                    continue;
                EventOption? replacement = Resolve(__instance, identity);
                if (replacement == null)
                {
                    MainFile.Logger.Info($"[ancient options] {identity} not resolvable on {__instance.Id.Entry}; slot {slot} keeps the roll");
                    continue;
                }
                used.Remove(SafeIdentity(options[slot]));
                options[slot] = replacement;
                used.Add(identity);
                MainFile.Logger.Info($"[ancient options] player {owner.NetId}: slot {slot} of {__instance.Id.Entry} -> {identity}");
            }
            __result = options;
            __instance.GeneratedOptions = options; // run history records what was actually offered
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[ancient options] substitution failed, vanilla roll kept: {e}");
        }
    }

    private static string SafeIdentity(EventOption option)
    {
        try
        {
            return AncientOptionProbe.IdentityOf(option);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static EventOption? Resolve(AncientEventModel ev, string identity)
    {
        try
        {
            if (identity.StartsWith("R:"))
            {
                ModelId id = new(ModelDb.GetCategory(typeof(RelicModel)), identity.Substring(2));
                if (ModelDb.GetByIdOrNull<RelicModel>(id) is not RelicModel relic
                    || relic.ToMutable() is not RelicModel mutable)
                    return null;
                return ev.RelicOption(mutable);
            }
            string key = identity.StartsWith("K:") ? identity.Substring(2) : identity;
            return ev.AllPossibleOptions.FirstOrDefault(o => o.Relic == null && o.TextKey == key);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"[ancient options] resolve failed for {identity}: {e.Message}");
            return null;
        }
    }
}
