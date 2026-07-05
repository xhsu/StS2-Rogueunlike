using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Saves;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Shared machinery for the mod's card-grid pickers — the Grand Card Selection reward
/// screen (<see cref="ModRewardScreenUi"/>) and the merchant card picker
/// (<see cref="ModCardPickModal"/>, both mount modes). Owns everything they share:
///   • the scrollable <see cref="NCardGrid"/> + checkmark <see cref="NConfirmButton"/>
///     borrowed from the simple-card-select scene;
///   • the deck-view chrome: sort bar grafted into the grid's ScrollContainer (so it
///     scrolls away with the cards, as in NDeckViewScreen), "View Upgrades" tickbox,
///     bottom banner label, back button;
///   • the search bar under the sort strip, with vanilla's 250ms debounce, AND-token
///     matching and the long-term haystack (rendered face text + rarity + enchantment
///     name — the same calls NCard makes, so game updates flow into search);
///   • the three card states via <see cref="CardGridVisibilityPatch"/> /
///     <see cref="CardGridSparklePatch"/>: pickable full-colour (sparkling when
///     compendium-new), out-of-context pool cards darkened, locked cards locked;
///   • click-to-highlight selection with candidate-click discovery (ModSeenGate).
/// Subclasses fill <see cref="_pickable"/>/<see cref="_shown"/>, assemble via the
/// protected Build* steps, and define what confirm / back / alt-click mean.
/// </summary>
public abstract partial class ModCardGridPicker : Control
{
    private static readonly string SelectScene =
        SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen");
    private static readonly string DeckViewScene =
        SceneHelper.GetScenePath("screens/deck_view_screen");

    // ponytail: pushes the grid below the sort bar (matches NDeckViewScreen's YOffset=100).
    private const int GridTopOffset = 100;

    // ponytail: the relic HUD (a 68px HFlowContainer row, per NRelicInventory) sits top-left.
    // Deck view hides it behind an opaque capstone backstop; we have none, so drop the cards
    // below one relic row instead of hiding relics (you want to see them while picking).
    // The sort bar itself rests at y≈172 (grid inset 80 + authored 92), already clear of the
    // relic row, and scrolls up through it like the cards do. Bump if you carry 2+ relic rows.
    private const int SortBarDrop = 80;

    // ponytail: strip→bar gap and bar→first-card clearance; tune by eye if MegaCrit reskins.
    private const float SearchGap = 32f;
    private const float SearchClear = 48f;

    protected NCardGrid _grid = null!;
    protected NConfirmButton _confirm = null!;
    private Control? _scroller; // the grid's ScrollContainer, once the sort bar is grafted into it
    private float _sortStripBottom = 132f; // sort strip's bottom edge, scroller-local (authored 92 + strip height)

    protected readonly List<CardModel> _shown = new();     // pickable first, then display extras
    protected readonly HashSet<CardModel> _pickable = new();
    protected readonly HashSet<CardModel> _unseen = new(); // pickable & compendium-undiscovered: sparkle
    protected readonly Dictionary<CardModel, ModelVisibility> _visibility = new();
    private readonly Dictionary<CardModel, string> _searchText = new();
    private string _query = "";
    private Tween? _searchDebounce;
    protected CardModel? _pending;

    // Sort priority (index 0 = primary key); each sort button moves its key to the front.
    private readonly List<SortingOrders> _sort = new()
    {
        SortingOrders.RarityAscending, SortingOrders.CostAscending,
        SortingOrders.TypeAscending, SortingOrders.AlphabetAscending,
    };

    // ---- subclass hooks ----

    /// <summary>The checkmark was pressed with a pending pick.</summary>
    protected abstract void OnConfirmPressed(CardModel pending);

    /// <summary>The deck-view back button was pressed (only wired when requested).</summary>
    protected virtual void OnBackPressed() { }

    /// <summary>Alt-click on a pickable card. Default: the game's inspect screen.</summary>
    protected virtual void OnAltPressed(NCardHolder holder)
    {
        if (holder.CardModel != null && _pickable.Contains(holder.CardModel))
            NGame.Instance?.GetInspectCardScreen().Open(new List<CardModel> { holder.CardModel }, 0);
    }

    // ---- assembly steps (call from the subclass Build, in this order) ----

    /// <summary>Full-rect root, focus enabled, discovery suppressed while visible.</summary>
    protected void SetupRoot() => ModUi.SetupPickerRoot(this);

    /// <summary>
    /// The pool's whole cast beyond the pickable loot, shown compendium-style: locked
    /// pool cards with the library's locked rendering, unlocked-but-excluded ones (a
    /// pool/type/rarity constraint applies at this context) with its darkened NotSeen
    /// look. Neither is selectable — the selection pool equals the loot pool.
    /// </summary>
    protected void AddDisplayExtras(Player player, IEnumerable<CardPoolModel> poolSource)
    {
        List<CardPoolModel> pools = poolSource.Distinct().ToList();
        var offeredIds = _pickable.Select(c => c.Id).ToHashSet();
        var unlocked = pools
            .SelectMany(p => p.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint))
            .ToHashSet();
        foreach (CardModel card in pools.SelectMany(p => p.AllCards).Distinct())
        {
            if (offeredIds.Contains(card.Id) || !card.ShouldShowInCardLibrary)
                continue;
            if (card.Rarity is not (CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare))
                continue;
            if (player.RunState.Players.Count > 1
                    ? card.MultiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly
                    : card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                continue;
            _shown.Add(card);
            _visibility[card] = unlocked.Contains(card) ? ModelVisibility.NotSeen : ModelVisibility.Locked;
        }
    }

    /// <summary>Borrow grid + checkmark from the simple-select scene. False if either is missing.</summary>
    protected bool ExtractGridAndConfirm()
    {
        Control select = PreloadManager.Cache.GetScene(SelectScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        _grid = ModUi.Extract<NCardGrid>(select)!;
        _confirm = ModUi.Extract<NConfirmButton>(select)!;
        select.QueueFreeSafely();
        return _grid != null && _confirm != null;
    }

    /// <summary>Mount + wire the grid and confirm button; register the per-grid state tables.</summary>
    protected void WireGrid(string gridName)
    {
        _grid.Name = gridName;
        this.AddChildSafely(_grid);
        _grid.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _grid.InsetForTopBar();
        _grid.YOffset = GridTopOffset + SortBarDrop; // pre-search default; BuildSearchBar raises it
        _grid.Connect(NCardGrid.SignalName.HolderPressed, Callable.From<NCardHolder>(OnCardClicked));
        _grid.Connect(NCardGrid.SignalName.HolderAltPressed, Callable.From<NCardHolder>(OnAltPressed));
        _unseen.UnionWith(_pickable.Where(c => !SaveManager.Instance.Progress.DiscoveredCards.Contains(c.Id)));
        CardGridVisibilityPatch.Overrides.Add(_grid, _visibility);
        CardGridSparklePatch.Sets.Add(_grid, _unseen);
        PopulateDeferred();

        this.AddChildSafely(_confirm); // self-anchors bottom-right, hidden until Enable()
        _confirm.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            if (_pending != null)
                OnConfirmPressed(_pending);
        }));
        _confirm.Disable();
    }

    /// <summary>
    /// Graft the deck-view chrome: sort bar (into the grid's scroller), "View Upgrades"
    /// tickbox, bottom banner label, and — when requested — the back button, wired to
    /// <see cref="OnBackPressed"/>.
    /// </summary>
    protected void BuildDeckChrome(string bottomLabelText, bool includeBackButton)
    {
        ShaderMaterial? frame = _shown.Count > 0 ? _shown[0].FrameMaterial as ShaderMaterial : null;
        Control deck = PreloadManager.Cache.GetScene(DeckViewScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        BuildSortBar(deck, frame);
        BuildViewUpgrades(deck);
        BuildBottomLabel(deck, bottomLabelText);
        if (includeBackButton)
            BuildBackButton(deck);
        deck.QueueFreeSafely();
    }

    // ---- selection ----

    // Selection visuals run on the clicked holder's own card node, not only through
    // Highlight/UnhighlightCard: the grid calls stay for their bookkeeping (scroll-recycle
    // re-apply), but the visual is asserted directly via ForceHighlight, which survives
    // the pooled-NCard corruption that intermittently eats the vanilla tween (see below).
    private void OnCardClicked(NCardHolder holder)
    {
        CardModel? card = holder.CardModel;
        if (card == null || !_pickable.Contains(card))
            return;
        NCard? clicked = holder.CardNode;
        if (_pending == card) // click again to deselect
        {
            _grid.UnhighlightCard(card);
            if (clicked != null)
                ForceHighlight(clicked, false);
            _pending = null;
            _confirm.Disable();
            return;
        }
        if (_pending != null)
        {
            _grid.UnhighlightCard(_pending);
            if (_grid.GetCardNode(_pending) is NCard oldNode)
                ForceHighlight(oldNode, false);
        }
        _grid.HighlightCard(card);
        if (clicked != null)
            ForceHighlight(clicked, true);
        _pending = card;
        _confirm.Enable();
        ModSeenGate.MarkPicked(card); // candidacy is the reveal (see ModSeenGate)
        if (_unseen.Remove(card) && clicked != null) // discovered now — stop advertising it as new
            clicked._sparkles.Visible = false;
    }

    // The selection outline is a shader "width" param tweened by NCardHighlight through a
    // material reference cached at its first _Ready. NCards are pooled for the whole game
    // process, and field reports show instances coming back with that path dead — random
    // cards ignore AnimShow all session until a game restart rebuilds the pool. So drive
    // the param directly on the material the node is CURRENTLY rendering with (no tween),
    // and when the cached reference has diverged, repair it so vanilla AnimShow/AnimHide
    // (combat hand glow included) work again for that instance. Logs only when it finds
    // something wrong — a "healed" line in godot.log confirms the diagnosis.
    private static void ForceHighlight(NCard node, bool on)
    {
        NCardHighlight? highlight = node.CardHighlight;
        if (highlight == null)
            return;
        if (!highlight.Visible)
        {
            MainFile.Logger.Info($"[card picker] {node.Model?.Id}: highlight node was hidden; re-shown");
            highlight.Visible = true;
        }
        if (highlight.Material is not ShaderMaterial live)
        {
            MainFile.Logger.Info($"[card picker] {node.Model?.Id}: highlight material is "
                + $"{highlight.Material?.GetType().Name ?? "null"} — nothing to drive");
            return;
        }
        if (!ReferenceEquals(live, highlight._shaderMaterial))
        {
            MainFile.Logger.Info($"[card picker] {node.Model?.Id}: highlight material cache diverged; healed");
            highlight._shaderMaterial = live;
        }
        live.SetShaderParameter(NCardHighlight._shaderParameterWidth, on ? 0.075f : 0f); // AnimShow's target width
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
                RefreshCards();
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[card picker] populate failed: {e}");
        }
    }

    // ---- search ----

    protected void BuildSearchBar()
    {
        try
        {
            NSearchBar? bar;
            if (_scroller != null)
            {
                // Inside the grid's scroller, centred under the sort strip: scrolls away
                // with the cards (same mechanism as the sort bar) and stays out of the
                // relic row, which a fixed top-centre spot would collide with.
                bar = ModSearch.CreateBar(_scroller);
                if (bar != null)
                {
                    float top = _sortStripBottom + SearchGap;
                    ModSearch.PlaceCentered(bar, top);
                    _grid.YOffset = (int)(top + bar.CustomMinimumSize.Y + SearchClear);
                }
            }
            else
            {
                bar = ModSearch.CreateBar(this); // no sort strip to sit under; fixed top-centre fallback
                if (bar != null)
                    ModSearch.PlaceCentered(bar, 96f);
            }
            bar?.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(OnQueryChanged));
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[search] card bar failed (picker still works without it): {e}");
        }
    }

    private void OnQueryChanged(string query)
    {
        _query = ModSearch.Canon(query);
        // Rebuilding a full card grid per keystroke is what made typing stutter; vanilla
        // debounces text input by 250ms (DisplayCardsAfterShortDelay) — mirror it. The
        // tween dies with this node, so a picker closed mid-delay never refreshes.
        _searchDebounce?.Kill();
        _searchDebounce = CreateTween();
        _searchDebounce.TweenInterval(0.25);
        _searchDebounce.TweenCallback(Callable.From(RefreshCards));
    }

    // Single funnel for (re)filling the grid: applies the search filter and the current
    // sort, and keeps the pending pick consistent when the filter hides it. Search
    // results are pickable-only — searching means looking for something to PICK; the
    // darkened/locked context cast only clutters results (user rule, 2026-07-03).
    protected void RefreshCards()
    {
        List<CardModel> show = _query.Length == 0
            ? new List<CardModel>(_shown)
            : _shown.Where(c => _pickable.Contains(c) && ModSearch.Matches(SearchableOf(c), _query)).ToList();
        _grid.SetCards(show, PileType.None, new List<SortingOrders>(_sort));
        if (_pending == null)
            return;
        if (show.Contains(_pending))
        {
            _grid.HighlightCard(_pending); // holders were rebuilt; re-apply
            if (_grid.GetCardNode(_pending) is NCard node)
                ForceHighlight(node, true);
        }
        else
        {
            _pending = null;
            _confirm.Disable();
        }
    }

    // The long-term haystack: the RENDERED text via the game's own contracts — Title and
    // GetDescriptionForPile (body + keyword/trait lines + enchantment card text), the
    // same calls NCard makes to fill its labels — plus the rarity token (the library's
    // special search keywords are exactly the rarity names) and the enchantment's NAME
    // (rendered only as an icon tab, but worth finding by name). Tooltip text is
    // deliberately excluded: keyword-explanation tips made every card carrying a
    // "Block" tip match "block" — pure noise. Pieces fail independently.
    private string SearchableOf(CardModel card)
    {
        if (_searchText.TryGetValue(card, out string? text))
            return text;
        text = card.Title + " " + card.Rarity;
        try
        {
            text += " " + card.GetDescriptionForPile(PileType.None);
        }
        catch (System.Exception) { }
        try
        {
            if (card.Enchantment is { } enchantment)
                text += " " + enchantment.Title.GetFormattedText();
        }
        catch (System.Exception) { }
        text = ModSearch.Canon(text);
        _searchText[card] = text;
        return text;
    }

    // ---- deck-view chrome ----

    // In the deck-view scene the bar ("SortingOptions": SortingBg + four sort buttons) is a
    // child of the grid's ScrollContainer — the node NCardGrid slides to scroll. That is the
    // whole vanilla mechanism for the bar scrolling away with the cards. Recreate it: graft
    // SortingOptions into OUR grid's ScrollContainer (InitGrid only frees its own card
    // holders, so the graft survives re-sorts). The donor grid — including its BorderGradient
    // screen-edge fade, which our grid already duplicates — is discarded with the donor scene.
    private void BuildSortBar(Node deck, ShaderMaterial? frame)
    {
        Control? bg = deck.FindChild("SortingBg", recursive: true, owned: false) as Control;
        Node? options = bg?.GetParent(); // "SortingOptions"
        if (bg == null || options == null
            || _grid.FindChild("ScrollContainer", recursive: true, owned: false) is not Control scroller)
            return;
        var sorters = new List<NCardViewSortButton>();
        ModUi.Collect(options, sorters);
        if (sorters.Count < 4)
            return;

        options.GetParent().RemoveChild(options);
        scroller.AddChildSafely(options); // keeps its authored top-wide anchors within the container
        _scroller = scroller;
        if (options is Control oc)
            _sortStripBottom = oc.Position.Y + bg.Position.Y + bg.Size.Y;
        if (frame != null)
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
        RefreshCards();
    }

    private void BuildViewUpgrades(Node deck)
    {
        NTickbox? tick = ModUi.FindDescendant<NTickbox>(deck);
        Node? widget = ModUi.AncestorUnder(deck, tick);
        if (tick == null || widget == null)
            return;
        (deck.FindChild("ViewUpgradesLabel", recursive: true, owned: false) as MegaLabel)?.SetTextAutoSize(Loc("VIEW_UPGRADES"));
        Adopt(widget);
        tick.IsTicked = false;
        tick.Connect(NTickbox.SignalName.Toggled, Callable.From<NTickbox>(t => _grid.IsShowingUpgrades = t.IsTicked));
    }

    private void BuildBottomLabel(Node deck, string text)
    {
        RichTextLabel? label = deck.FindChild("BottomLabel", recursive: true, owned: false) as RichTextLabel;
        Node? widget = ModUi.AncestorUnder(deck, label);
        if (label == null || widget == null)
            return;
        Adopt(widget);
        MakeClickThrough(widget); // a full-width bottom strip would otherwise block the grid
        label.Text = text;
    }

    private void BuildBackButton(Node deck)
    {
        NButton? back = deck.FindChild("BackButton", recursive: true, owned: false) as NButton;
        Node? widget = ModUi.AncestorUnder(deck, back);
        if (back == null || widget == null)
            return;
        Adopt(widget);
        back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnBackPressed()));
        back.Enable();
    }

    // ---- helpers ----

    protected void Adopt(Node widget)
    {
        widget.GetParent()?.RemoveChild(widget);
        this.AddChildSafely(widget);
    }

    // Make a harvested wrapper mouse-transparent so it can't cover the grid; buttons (NButton
    // and its subclasses — sorters, tickbox, back) keep their own input and are left untouched.
    protected static void MakeClickThrough(Node node)
    {
        if (node is NButton)
            return;
        if (node is Control c)
            c.MouseFilter = MouseFilterEnum.Ignore;
        foreach (Node child in node.GetChildren())
            MakeClickThrough(child);
    }

    // Vanilla localized string from the game's "gameplay_ui" table (SORT_*, VIEW_UPGRADES, CHOOSE_CARD_HEADER).
    protected static string Loc(string key) => new LocString("gameplay_ui", key).GetRawText();
}
