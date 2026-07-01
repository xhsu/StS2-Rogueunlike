using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace StS2Mod.StS2ModCode;

// ponytail: prefix patch replaces the 3-card random draw with the full pool.
// If the reward screen can't scroll for 30+ cards, we'll need UI work.
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    typeof(Player), typeof(int), typeof(CardCreationOptions))]
public static class ShowAllCardRewardsPatch
{
    static bool Prefix(Player player, CardCreationOptions options,
        ref IEnumerable<CardCreationResult> __result)
    {
        // Let relics/modifiers inject their card pools (Prismatic Gem, CharacterCards, etc.)
        options = Hook.ModifyCardRewardCreationOptions(player.RunState, player, options);

        var allCards = options.GetPossibleCards(player)
            .Where(c => c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
            .Distinct()
            .ToList();

        if (player.RunState.Players.Count > 1)
            allCards.RemoveAll(c => c.MultiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly);
        else
            allCards.RemoveAll(c => c.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly);

        var results = allCards
            .Select(c => new CardCreationResult(player.RunState.CreateCard(c, player)))
            .ToList();

        if (!options.Flags.HasFlag(CardCreationFlags.NoModifyHooks)
            && Hook.TryModifyCardRewardOptions(player.RunState, player, results, options,
                out List<AbstractModel> modifiers))
        {
            TaskHelper.RunSafely(Hook.AfterModifyingCardRewardOptions(player.RunState, modifiers));
        }

        __result = results;
        return false;
    }
}
