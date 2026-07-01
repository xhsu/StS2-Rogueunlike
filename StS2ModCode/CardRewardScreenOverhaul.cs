using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using System.Collections.Generic;
using Godot;

namespace StS2Mod.StS2ModCode;

// Thin Harmony seam that hands the reward screen's body over to ModRewardScreenUi. The game
// exposes no hook to replace the reward screen, so these two patches are the irreducible
// injection point; all the UI/logic lives in the standalone class. Together with
// ShowAllCardRewardsPatch (which fills the pool) this lets you pick from every valid card.
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))]
public static class CardRewardScreenOverhaul
{
    static bool Prefix(NCardRewardSelectionScreen __instance,
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        __instance._options = options;
        __instance._extraOptions = extraOptions;

        Control ui = __instance._ui;

        // Drop any previous view (first show is clean; reroll re-enters here).
        Node? old = ui.GetNodeOrNull(ModRewardScreenUi.ViewName);
        if (old != null) { old.Name = "ModRewardScreenUiDead"; old.QueueFreeSafely(); }

        var view = new ModRewardScreenUi();
        ui.AddChildSafely(view);
        try
        {
            if (view.Build(__instance, options, extraOptions))
                return false; // skip the vanilla 3-card layout
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"Card reward overhaul failed, using vanilla screen: {e}");
        }

        // Scene couldn't be borrowed (or threw) — fall back to vanilla.
        view.Name = "ModRewardScreenUiDead";
        view.QueueFreeSafely();
        return true;
    }
}

// GetCardHolder feeds the fly-to-deck VFX after a pick. Vanilla does _cardRow.First(...),
// which throws now that the row is empty. Return the grid's holder (null-safe) instead: a
// real holder => vanilla VFX + deck-count update run unchanged; null => the driver just
// skips the animation.
[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.GetCardHolder))]
public static class CardRewardGetHolderPatch
{
    static bool Prefix(NCardRewardSelectionScreen __instance, CardModel card, ref NCardHolder? __result)
    {
        NCardGrid? grid = __instance._ui?.FindChild(ModRewardScreenUi.GridName, recursive: true, owned: false) as NCardGrid;
        __result = grid?.GetCardHolder(card);
        return false;
    }
}
