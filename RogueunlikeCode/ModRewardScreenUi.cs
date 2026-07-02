using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// The card-reward UI ("Grand Card Selection"), superseding the vanilla
/// NCardRewardSelectionScreen body so a reward can offer the whole valid pool. All grid,
/// chrome, search and selection mechanics live in <see cref="ModCardGridPicker"/>; this
/// subclass is the reward-screen host glue:
///   • pickable cards = the reward's actual loot (the results the patched CreateForReward
///     produced); confirming resolves the vanilla screen's awaited selection;
///   • the deck-view back button doubles as Skip (only when the reward is skippable);
///   • non-skip alternatives (Reroll, relic options) stay as native buttons;
///   • the vanilla screen is kept only as the overlay + completion shell.
/// The selection pool equals the loot pool, never more.
/// </summary>
public partial class ModRewardScreenUi : ModCardGridPicker
{
    public const string ViewName = "ModRewardScreenUi";
    public const string GridName = "ModCardGrid";

    private NCardRewardSelectionScreen _screen = null!;
    private int _skipIndex = -1;

    public bool Build(NCardRewardSelectionScreen screen,
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        _screen = screen;
        Name = ViewName;
        SetupRoot();

        List<CardModel> loot = options.Select(o => o.Card).ToList();
        _pickable.UnionWith(loot);
        _shown.AddRange(loot);
        if (loot.Count > 0)
            AddDisplayExtras(loot[0].Owner, loot.Select(c => c.Pool).OfType<CardPoolModel>());

        if (!ExtractGridAndConfirm())
            return false;
        WireGrid(GridName);

        for (int i = 0; i < extraOptions.Count; i++)
            if (extraOptions[i].OptionId == "Skip") { _skipIndex = i; break; }
        BuildDeckChrome(Loc("CHOOSE_CARD_HEADER"), includeBackButton: _skipIndex >= 0);
        BuildOtherAlternatives(extraOptions); // Reroll / relic options (Skip is the back button)
        BuildSearchBar();

        _screen._banner.Visible = false; // hide the big centre banner; the bottom label carries the title
        _screen._lastFocusedControl = _grid;
        MainFile.Logger.Info($"[reward] built cards={loot.Count}");
        return true;
    }

    protected override void OnConfirmPressed(CardModel pending)
    {
        NGridCardHolder? holder = _grid.GetCardHolder(pending);
        if (holder != null)
            _screen.SelectCard(holder); // resolves the awaited OptionSelected() with this card's index
    }

    protected override void OnBackPressed()
    {
        _screen.OnAlternateRewardSelected(_skipIndex);
    }

    // The reward screen has its own inspect flow (adds upgrade-preview handling).
    protected override void OnAltPressed(NCardHolder holder)
    {
        if (holder.CardModel != null && _pickable.Contains(holder.CardModel))
            _screen.InspectCard(holder);
    }

    // Non-skip alternatives (Reroll, relic options) kept as native buttons in the vanilla
    // container so nothing is lost; Skip itself is handled by the back button.
    private void BuildOtherAlternatives(IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        Control container = _screen._rewardAlternativesContainer;
        foreach (NCardRewardAlternativeButton oldBtn in container.GetChildren().OfType<NCardRewardAlternativeButton>())
            oldBtn.QueueFreeSafely();
        container.MouseFilter = MouseFilterEnum.Ignore;

        for (int i = 0; i < extraOptions.Count; i++)
        {
            if (extraOptions[i].OptionId == "Skip")
                continue;
            int idx = i;
            NCardRewardAlternativeButton btn =
                NCardRewardAlternativeButton.Create(extraOptions[i].Title.GetFormattedText(), extraOptions[i].Hotkey);
            container.AddChildSafely(btn);
            btn.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => _screen.OnAlternateRewardSelected(idx)));
        }
        _screen._ui.MoveChild(container, -1);
    }
}
