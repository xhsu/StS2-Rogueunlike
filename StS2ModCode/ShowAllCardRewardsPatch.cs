using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace StS2Mod.StS2ModCode;

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
                MainFile.Logger.Error($"ShowAllCardRewards: reward-modifier hook failed, its bonus was skipped: {e}");
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
            MainFile.Logger.Error($"ShowAllCardRewards failed, falling back to vanilla reward: {e}");
            return true;
        }
    }
}
