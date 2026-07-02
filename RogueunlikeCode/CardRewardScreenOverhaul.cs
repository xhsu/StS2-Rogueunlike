using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Rogueunlike.RogueunlikeCode;

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

// The reward grid borrows the card library's per-card visibility states — Locked for
// progression-locked pool cards, NotSeen (the library's darkened look) for unlocked
// cards outside the current loot context. Plain NCardGrid always answers Visible, and
// the grid re-asks on every holder (re)assignment, so an override table per grid is the
// whole mechanism. Entries die with the grid (weak table).
[HarmonyPatch(typeof(NCardGrid), "GetCardVisibility")]
public static class CardGridVisibilityPatch
{
    public static readonly ConditionalWeakTable<NCardGrid, Dictionary<CardModel, ModelVisibility>> Overrides = new();

    static void Postfix(NCardGrid __instance, CardModel card, ref ModelVisibility __result)
    {
        if (Overrides.TryGetValue(__instance, out Dictionary<CardModel, ModelVisibility>? map)
            && map.TryGetValue(card, out ModelVisibility visibility))
            __result = visibility;
    }
}

// Unseen-pickable cards sparkle: NCard's own reward sparkle particles (_sparkles, the
// bits vanilla shows over rare reward cards) flag compendium-new cards at a glance.
// NCard resets the node whenever a holder is (re)assigned and the grid recycles holders
// while scrolling, so — exactly like NCardLibraryGrid re-applies its per-holder state —
// re-apply after InitGrid (creation) and after every AssignCardsToRow (scroll recycle).
// Registered per grid, same lifetime story as CardGridVisibilityPatch above.
[HarmonyPatch(typeof(NCardGrid))]
public static class CardGridSparklePatch
{
    public static readonly ConditionalWeakTable<NCardGrid, HashSet<CardModel>> Sets = new();

    // NB: explicit empty args — NCardGrid also has "private async Task InitGrid(Task?)",
    // and a name-only patch dies with AmbiguousMatchException at mod init.
    [HarmonyPostfix, HarmonyPatch("InitGrid", new System.Type[0])]
    static void AfterInit(NCardGrid __instance)
    {
        if (!Sets.TryGetValue(__instance, out HashSet<CardModel>? sparkling))
            return;
        foreach (List<NGridCardHolder> row in __instance._cardRows)
            Apply(row, sparkling);
    }

    [HarmonyPostfix, HarmonyPatch("AssignCardsToRow")]
    static void AfterAssign(NCardGrid __instance, List<NGridCardHolder> row)
    {
        if (Sets.TryGetValue(__instance, out HashSet<CardModel>? sparkling))
            Apply(row, sparkling);
    }

    private static void Apply(List<NGridCardHolder> row, HashSet<CardModel> sparkling)
    {
        foreach (NGridCardHolder holder in row)
            if (holder.CardNode is NCard node && node.Model is CardModel card)
                node._sparkles.Visible = sparkling.Contains(card);
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
