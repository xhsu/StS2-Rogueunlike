using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using System.Collections.Generic;
using System.Linq;

namespace StS2Mod.StS2ModCode;

/// <summary>
/// The card-reward UI, rebuilt as a standalone class that supersedes the vanilla
/// NCardRewardSelectionScreen body so a reward can offer the whole valid pool.
///
/// It borrows the game's own laid-out widgets rather than restyling by hand:
///   • scrollable <see cref="NCardGrid"/> + checkmark <see cref="NConfirmButton"/>
///     (from the simple-card-select scene),
///   • the deck-view chrome (from the deck-view scene): the styled sort bar of
///     <see cref="NCardViewSortButton"/>s, the "View Upgrades" <see cref="NTickbox"/>,
///     the bottom banner label, and the back button (rewired as Skip).
///
/// Behaviour: click a card to HIGHLIGHT it (not take it); the checkmark commits it.
/// The vanilla screen is kept only as the overlay + completion shell.
/// </summary>
public partial class ModRewardScreenUi : Control
{
    public const string ViewName = "ModRewardScreenUi";
    public const string GridName = "ModCardGrid";

    private static readonly string SelectScene =
        SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen");
    private static readonly string DeckViewScene =
        SceneHelper.GetScenePath("screens/deck_view_screen");

    // ponytail: pushes the grid below the sort bar (matches NDeckViewScreen's YOffset=100).
    private const int GridTopOffset = 100;

    // ponytail: the relic HUD (a 68px HFlowContainer row, per NRelicInventory) sits top-left.
    // Deck view hides it behind an opaque capstone backstop; we have none, so the live relics
    // clip the leftmost sort button. Drop the sort bar + grid below one relic row instead of
    // hiding relics (you want to see them while picking). Bump if you carry 2+ relic rows.
    private const int SortBarDrop = 80;

    private NCardRewardSelectionScreen _screen = null!;
    private NCardGrid _grid = null!;
    private NConfirmButton _confirm = null!;
    private readonly List<CardModel> _cards = new();

    // Sort priority (index 0 = primary key); each sort button moves its key to the front.
    private readonly List<SortingOrders> _sort = new()
    {
        SortingOrders.RarityAscending, SortingOrders.CostAscending,
        SortingOrders.TypeAscending, SortingOrders.AlphabetAscending,
    };
    private CardModel? _pending;

    public bool Build(NCardRewardSelectionScreen screen,
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        _screen = screen;
        Name = ViewName;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _cards.Clear();
        _cards.AddRange(options.Select(o => o.Card));

        // --- Grid + checkmark confirm, from the simple-select scene ---
        Control select = PreloadManager.Cache.GetScene(SelectScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        _grid = Extract<NCardGrid>(select)!;
        _confirm = Extract<NConfirmButton>(select)!;
        select.QueueFreeSafely();
        if (_grid == null || _confirm == null)
            return false;

        _grid.Name = GridName;
        this.AddChildSafely(_grid);
        _grid.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _grid.InsetForTopBar();
        _grid.YOffset = GridTopOffset + SortBarDrop;
        _grid.Connect(NCardGrid.SignalName.HolderPressed, Callable.From<NCardHolder>(OnCardClicked));
        _grid.Connect(NCardGrid.SignalName.HolderAltPressed, Callable.From<NCardHolder>(_screen.InspectCard));
        PopulateDeferred();

        this.AddChildSafely(_confirm); // self-anchors bottom-right, hidden until Enable()
        _confirm.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnConfirm));
        _confirm.Disable();

        // --- Deck-view chrome ---
        ShaderMaterial? frame = _cards.Count > 0 ? _cards[0].FrameMaterial as ShaderMaterial : null;
        Control deck = PreloadManager.Cache.GetScene(DeckViewScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        BuildSortBar(deck, frame);
        BuildViewUpgrades(deck);
        BuildBottomLabel(deck);
        BuildBackButton(deck, extraOptions);
        deck.QueueFreeSafely();

        BuildOtherAlternatives(extraOptions); // Reroll / relic options (Skip is the back button)

        _screen._banner.Visible = false; // hide the big centre banner; the bottom label carries the title
        _screen._lastFocusedControl = _grid;
        MainFile.Logger.Info($"[reward] built cards={_cards.Count}");
        return true;
    }

    // ---- selection ----

    private void OnCardClicked(NCardHolder holder)
    {
        CardModel? card = holder.CardModel;
        if (card == null)
            return;
        if (_pending == card) // click again to deselect
        {
            _grid.UnhighlightCard(card);
            _pending = null;
            _confirm.Disable();
            return;
        }
        if (_pending != null)
            _grid.UnhighlightCard(_pending);
        _grid.HighlightCard(card);
        _pending = card;
        _confirm.Enable();
    }

    private void OnConfirm(NButton _)
    {
        if (_pending == null)
            return;
        NGridCardHolder? holder = _grid.GetCardHolder(_pending);
        if (holder != null)
            _screen.SelectCard(holder); // resolves the awaited OptionSelected() with this card's index
    }

    // NCardGrid derives its column count from its laid-out width; a freshly re-parented grid
    // is zero-width for a frame or two (=> zero columns => empty). Wait for layout, then fill.
    private async void PopulateDeferred()
    {
        try
        {
            SceneTree tree = GetTree();
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (IsInstanceValid(_grid))
                _grid.SetCards(_cards, PileType.None, new List<SortingOrders>(_sort));
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[reward] populate failed: {e}");
        }
    }

    // ---- deck-view chrome ----

    private void BuildSortBar(Node deck, ShaderMaterial? frame)
    {
        Control? bg = deck.FindChild("SortingBg", recursive: true, owned: false) as Control;
        Node? bar = AncestorUnder(deck, bg);
        if (bar == null)
            return;
        var sorters = new List<NCardViewSortButton>();
        Collect(bar, sorters);
        if (sorters.Count < 4)
            return;

        Adopt(bar);
        if (bar is Control barCtrl) // shift down so the relic HUD no longer clips the left button
            barCtrl.Position += new Vector2(0, SortBarDrop);
        MakeClickThrough(bar); // don't let the bar's backdrop/containers eat grid clicks + scroll
        if (frame != null && bg != null)
            bg.Material = frame;

        WireSort(sorters[0], Loc("SORT_RARITY"), SortingOrders.RarityAscending, SortingOrders.RarityDescending, frame);
        WireSort(sorters[1], Loc("SORT_TYPE"), SortingOrders.TypeAscending, SortingOrders.TypeDescending, frame);
        WireSort(sorters[2], Loc("SORT_COST"), SortingOrders.CostAscending, SortingOrders.CostDescending, frame);
        WireSort(sorters[3], Loc("SORT_ALPHABET"), SortingOrders.AlphabetAscending, SortingOrders.AlphabetDescending, frame);
    }

    private void WireSort(NCardViewSortButton s, string label, SortingOrders asc, SortingOrders desc, ShaderMaterial? frame)
    {
        s.SetLabel(label);
        if (frame != null)
            s.SetHue(frame);
        s.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Sort(s, asc, desc)));
    }

    // The button flips its own IsDescending before emitting Released (as in NDeckViewScreen);
    // we just move its key to the front of the priority list and re-display.
    private void Sort(NCardViewSortButton s, SortingOrders asc, SortingOrders desc)
    {
        _sort.Remove(asc);
        _sort.Remove(desc);
        _sort.Insert(0, s.IsDescending ? desc : asc);
        _grid.SetCards(_cards, PileType.None, new List<SortingOrders>(_sort));
    }

    private void BuildViewUpgrades(Node deck)
    {
        NTickbox? tick = FindDescendant<NTickbox>(deck);
        Node? widget = AncestorUnder(deck, tick);
        if (tick == null || widget == null)
            return;
        (deck.FindChild("ViewUpgradesLabel", recursive: true, owned: false) as MegaLabel)?.SetTextAutoSize(Loc("VIEW_UPGRADES"));
        Adopt(widget);
        tick.IsTicked = false;
        tick.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(t => _grid.IsShowingUpgrades = t.IsTicked));
    }

    private void BuildBottomLabel(Node deck)
    {
        RichTextLabel? label = deck.FindChild("BottomLabel", recursive: true, owned: false) as RichTextLabel;
        Node? widget = AncestorUnder(deck, label);
        if (label == null || widget == null)
            return;
        Adopt(widget);
        MakeClickThrough(widget); // a full-width bottom strip would otherwise block the grid
        label.Text = Loc("CHOOSE_CARD_HEADER"); // vanilla "Choose a Card" (the hidden banner's text)
    }

    private void BuildBackButton(Node deck, IReadOnlyList<CardRewardAlternative> extraOptions)
    {
        int skip = -1;
        for (int i = 0; i < extraOptions.Count; i++)
            if (extraOptions[i].OptionId == "Skip") { skip = i; break; }
        if (skip < 0)
            return; // reward can't be skipped -> no back button

        NButton? back = deck.FindChild("BackButton", recursive: true, owned: false) as NButton;
        Node? widget = AncestorUnder(deck, back);
        if (back == null || widget == null)
            return;
        Adopt(widget);
        back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => _screen.OnAlternateRewardSelected(skip)));
        back.Enable();
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

    // ---- helpers ----

    private void Adopt(Node widget)
    {
        widget.GetParent()?.RemoveChild(widget);
        this.AddChildSafely(widget);
    }

    // Make a harvested wrapper mouse-transparent so it can't cover the grid; buttons (NButton
    // and its subclasses — sorters, tickbox, back) keep their own input and are left untouched.
    private static void MakeClickThrough(Node node)
    {
        if (node is NButton)
            return;
        if (node is Control c)
            c.MouseFilter = MouseFilterEnum.Ignore;
        foreach (Node child in node.GetChildren())
            MakeClickThrough(child);
    }

    // Vanilla localized string from the game's "gameplay_ui" table (SORT_*, VIEW_UPGRADES, CHOOSE_CARD_HEADER).
    private static string Loc(string key) => new LocString("gameplay_ui", key).GetRawText();

    // The ancestor of 'n' that is a direct child of 'root' (so its scene-authored position,
    // which was relative to the full-screen root, is preserved when re-parented into our
    // full-screen view). Null if 'n' isn't under 'root'.
    private static Node? AncestorUnder(Node root, Node? n)
    {
        if (n == null)
            return null;
        while (n.GetParent() != null && n.GetParent() != root)
            n = n.GetParent();
        return n.GetParent() == root ? n : null;
    }

    private static T? Extract<T>(Node donor) where T : Node
    {
        T? node = FindDescendant<T>(donor);
        node?.GetParent().RemoveChild(node);
        return node;
    }

    private static T? FindDescendant<T>(Node node) where T : class
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is T deep)
                return deep;
        }
        return null;
    }

    private static void Collect<T>(Node node, List<T> into) where T : class
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                into.Add(match);
            Collect(child, into);
        }
    }
}
