using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace StS2Mod.StS2ModCode;

// ponytail: postfix appends the rest of the pool to the vanilla reward instead of
// replacing it. Vanilla's 3 rolls, upgrade rolls and relic hooks (Lasting Candy...)
// run untouched, so relics see the small card list they were written against.
//
// Gated on IsCardReward: only CardReward.Populate sets that flag. Relics, events
// and run modifiers (Lasting Candy, Sea Glass, Sealed Deck, Kaleidoscope...) also
// roll cards through CreateForReward and must get vanilla results — replacing
// their result with the whole pool made "pick 1 random card" deterministic and
// crashed Lasting Candy with a single-rarity custom pool.
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    typeof(Player), typeof(int), typeof(CardCreationOptions))]
public static class ShowAllCardRewardsPatch
{
    static void Postfix(Player player, CardCreationOptions options,
        ref IEnumerable<CardCreationResult> __result)
    {
        if (!options.Flags.HasFlag(CardCreationFlags.IsCardReward))
            return;

        try
        {
            var results = __result.ToList();
            var offered = results.Select(r => r.Card.Id).ToHashSet();

            // Let relics/modifiers inject their card pools (Prismatic Gem, CharacterCards, etc.)
            var extras = Hook.ModifyCardRewardCreationOptions(player.RunState, player, options)
                .GetPossibleCards(player)
                .Where(c => c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
                .Distinct()
                .Where(c => !offered.Contains(c.Id));

            extras = player.RunState.Players.Count > 1
                ? extras.Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly)
                : extras.Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);

            results.AddRange(extras.Select(c =>
                new CardCreationResult(player.RunState.CreateCard(c, player))));

            __result = results;
        }
        catch (Exception e)
        {
            // A relic or another mod we didn't anticipate — keep the vanilla reward
            // rather than killing the room-end flow (soft-lock / black screen on load).
            MainFile.Logger.Error($"ShowAllCardRewards failed, falling back to vanilla reward: {e}");
        }
    }
}
