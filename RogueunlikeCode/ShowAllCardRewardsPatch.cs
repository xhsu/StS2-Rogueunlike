using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Rogueunlike.RogueunlikeCode;

// Replaces the pool ROLL of the real card reward with the whole valid pool, while keeping
// every other stage of the vanilla per-card pipeline so reward-modifying effects extend
// to the full pool instead of a hidden 3:
//   • pool-injecting hooks (Prismatic Gem style) run first and define the pool,
//   • the vanilla upgrade roll runs per card, so upgrade-odds effects ("attacks in
//     rewards are upgraded") apply across the pool,
//   • TryModifyCardRewardOptions relics (Lasting Candy's bonus power, "extra trait on
//     your next card reward" effects) receive the full list and modify/extend it.
//
// Gated on IsCardReward: only CardReward.Populate sets it. Relic/event internal rolls
// (Lasting Candy's own 1-card pick, Sea Glass, Sealed Deck, Kaleidoscope...) stay vanilla.
//
// INVARIANT: __result must contain ONLY genuinely pickable loot — it becomes
// CardReward._cards, which drives selection indices, multiplayer sync and run history.
// Display-only entries (locked / out-of-context pool cards) are the UI's business:
// ModRewardScreenUi.BuildDisplayExtras adds them at render time, never here.
//
// Crash root-cause note: the hook view must contain ALL rarities of the pool, not just
// the displayable ones. Lasting Candy builds a custom pool of "Powers not yet offered"
// and inherits the reward's non-Uniform rarity odds; if the leftovers collapse to a
// single rarity the game's CardCreationOptions assert throws and the room-end flow dies
// (black screen on reload). Full coverage makes that query provably empty, so the relic
// takes its safe "any Power in the pool" fallback. Display then keeps C/U/R plus
// whatever hooks added.
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    typeof(Player), typeof(int), typeof(CardCreationOptions))]
public static class ShowAllCardRewardsPatch
{
    static bool Prefix(Player player, CardCreationOptions options,
        ref IEnumerable<CardCreationResult> __result)
    {
        if (!options.Flags.HasFlag(CardCreationFlags.IsCardReward))
            return true;

        try
        {
            // Let relics/modifiers inject their card pools (Prismatic Gem, CharacterCards, etc.)
            CardCreationOptions modified = Hook.ModifyCardRewardCreationOptions(player.RunState, player, options);
            List<CardCreationResult> results = CardFactory
                .FilterForPlayerCount(player.RunState, modified.GetPossibleCards(player))
                .Distinct()
                .Select(c => new CardCreationResult(player.RunState.CreateCard(c, player)))
                .ToList();

            // Vanilla rolls an upgrade per created reward card (act-scaled odds routed
            // through Hook.ModifyCardRewardUpgradeOdds); keep it per pool card.
            if (!options.Flags.HasFlag(CardCreationFlags.NoUpgradeRoll))
            {
                Rng rng = options.RngOverride ?? player.PlayerRng.Rewards;
                foreach (CardCreationResult r in results)
                    CardFactory.RollForUpgrade(player, r.Card, 0m, rng);
            }

            // Reward-list hooks see the full list, like vanilla's single call sees its 3.
            // We hand them the hook-modified options so their view of "the pool" matches
            // the list we built from it.
            var preHook = new HashSet<CardCreationResult>(results);
            try
            {
                if (!options.Flags.HasFlag(CardCreationFlags.NoModifyHooks)
                    && Hook.TryModifyCardRewardOptions(player.RunState, player, results, modified,
                        out List<AbstractModel> modifiers))
                {
                    TaskHelper.RunSafely(Hook.AfterModifyingCardRewardOptions(player.RunState, modifiers));
                }
            }
            catch (Exception e)
            {
                // A relic (or another mod) still choked on the full-pool list; lose that
                // bonus rather than the whole room-end flow (soft-lock / black screen).
                MainFile.Logger.Error($"[card rewards] reward-modifier hook failed, its bonus was skipped: {e}");
            }

            // Display: hook-added entries (Lasting Candy's bonus) always show and
            // supersede their plain pool copy; original pool entries show if C/U/R
            // (hidden other-rarity entries existed only to complete the hook view).
            var addedIds = results
                .Where(r => !preHook.Contains(r))
                .Select(r => r.Card.Id)
                .ToHashSet();
            __result = results
                .Where(r => !preHook.Contains(r)
                    || (!addedIds.Contains(r.Card.Id)
                        && r.Card.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare))
                .ToList();
            return false;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[card rewards] full-pool generation failed, falling back to vanilla reward: {e}");
            return true;
        }
    }
}

// Origin witness for the INTERNAL (non-IsCardReward) reward rolls: Kaleidoscope-style
// effects roll cards through the same CreateForReward and hand them to CardReward's
// fixed-list ctor. Remembering each created card's options lets that ctor recover what
// its rolls could have produced. Vanilla returns a materialized List, so iterating the
// result cannot re-roll anything. In-memory weak table; card instances are unique.
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    typeof(Player), typeof(int), typeof(CardCreationOptions))]
public static class CardRollOriginWitnessPatch
{
    internal static readonly ConditionalWeakTable<CardModel, CardCreationOptions> Origins = new();

    static void Postfix(CardCreationOptions options, IEnumerable<CardCreationResult> __result)
    {
        if (options.Flags.HasFlag(CardCreationFlags.IsCardReward) // feature #1's own calls
            || __result is not List<CardCreationResult> results)
            return;
        foreach (CardCreationResult r in results)
            if (r?.Card is CardModel card)
                Origins.AddOrUpdate(card, options);
    }
}

// The shared witnessed-roll expansion recipe: given cards that all rolled out of
// CreateForReward, produce every OTHER card those rolls could have produced, through the
// same per-card pipeline the rolls used (fresh run card + the origin's upgrade roll).
// Consumers: the fixed-list reward ctor below (Kaleidoscope) and the off-screen
// choose-a-card seams in ChooseFromRollPatches. Origin lookup is all-or-nothing — one
// unwitnessed card means a hand-built list, and hand-built lists never expand.
// Deterministic on every client (same witness, same pools, same creation order), so
// index-addressed MP picks stay aligned.
internal static class WitnessedRolls
{
    /// <summary>The distinct roll origins of <paramref name="cards"/>; null unless every card was witnessed.</summary>
    internal static List<CardCreationOptions>? OriginsOf(IEnumerable<CardModel?> cards)
    {
        var origins = new List<CardCreationOptions>();
        foreach (CardModel? card in cards)
        {
            if (card == null
                || !CardRollOriginWitnessPatch.Origins.TryGetValue(card, out CardCreationOptions? origin))
                return null;
            if (!origins.Contains(origin))
                origins.Add(origin);
        }
        return origins.Count > 0 ? origins : null;
    }

    /// <summary>Every other C/U/R card the origins could have rolled, excluding <paramref name="offered"/>.</summary>
    internal static List<CardModel> ExtraModels(Player player, IEnumerable<CardModel> offered,
        List<CardCreationOptions> origins)
    {
        HashSet<CardModel> present = offered.Select(c => c.CanonicalInstance).ToHashSet();
        var extras = new List<CardModel>();
        foreach (CardCreationOptions origin in origins)
        {
            Rng rng = origin.RngOverride ?? player.PlayerRng.Rewards;
            foreach (CardModel canonical in CardFactory
                .FilterForPlayerCount(player.RunState, origin.GetPossibleCards(player))
                .Distinct())
            {
                // The vanilla roll can only produce C/U/R (rarity switch), same
                // reachability rule as feature #1's display filter.
                if (canonical.Rarity is not (CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
                    || !present.Add(canonical))
                    continue;
                CardModel card = player.RunState.CreateCard(canonical, player);
                if (!origin.Flags.HasFlag(CardCreationFlags.NoUpgradeRoll))
                    CardFactory.RollForUpgrade(player, card, 0m, rng);
                extras.Add(card);
            }
        }
        return extras;
    }

    /// <summary>The origins' named pools, for compendium-context display (custom pools contribute none).</summary>
    internal static List<CardPoolModel> PoolsOf(List<CardCreationOptions> origins) =>
        origins.SelectMany(o => o.CardPools).Distinct().ToList();
}

// Feature #1 for FIXED-LIST card rewards (Kaleidoscope's foreign-pool picks and any kin):
// when every offered card was witnessed rolling out of CreateForReward, the reward could
// equally have offered any C/U/R card of those rolls' pools — so offer them all, through
// the same per-card pipeline feature #1 uses. Hand-built lists (tutorial, scripted
// events) are never witnessed and stay vanilla. Runs in the ctor, before Populate's
// reward-list hooks, so hooks see the expanded list exactly like feature #1's.
[HarmonyPatch(typeof(CardReward), MethodType.Constructor,
    typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player),
    typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer))]
public static class FixedCardRewardExpandPatch
{
    static void Postfix(CardReward __instance, Player player)
    {
        try
        {
            List<CardCreationResult> cards = __instance._cards;
            if (cards.Count == 0)
                return;
            List<CardCreationOptions>? origins = WitnessedRolls.OriginsOf(cards.Select(r => r?.Card));
            if (origins == null)
                return; // a hand-built card: not a roll, no pool to expand to
            List<CardModel> extras = WitnessedRolls.ExtraModels(player, cards.Select(r => r.Card), origins);
            if (extras.Count == 0)
                return;
            cards.AddRange(extras.Select(c => new CardCreationResult(c)));
            MainFile.Logger.Info($"[card rewards] fixed-list reward expanded to {cards.Count} options ({origins.Count} roll origin(s))");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[card rewards] fixed-list expansion failed, original list kept: {e}");
        }
    }
}
