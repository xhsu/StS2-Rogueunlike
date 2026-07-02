using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Saves;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select a card" modal for the merchant (feature #4): a standalone scrollable card
/// grid, unlike <see cref="ModRewardScreenUi"/> which is welded into the reward screen.
/// Borrows the simple-card-select scene's <see cref="NCardGrid"/> + checkmark confirm
/// and the deck-view scene's back button (cancel). Card states mirror the other pickers
/// via <see cref="CardGridVisibilityPatch"/>/<see cref="CardGridSparklePatch"/>:
/// pickable full-colour (sparkling when compendium-new), out-of-context pool cards
/// darkened (NotSeen look + gray tint), locked cards locked. Selection pool = what this
/// shop slot could roll, never more. Click to highlight, checkmark to take, back or
/// right-click-elsewhere... back to cancel; alt-click inspects a pickable card.
/// </summary>
public partial class ModShopCardPickerUi : Control
{
    private static readonly string SelectScene =
        SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen");
    private static readonly string DeckViewScene =
        SceneHelper.GetScenePath("screens/deck_view_screen");

    // ponytail: search bar top-centre, first card row below it; tune by eye.
    private const float SearchTop = 96f;
    private const int GridTop = 210;

    private readonly TaskCompletionSource<CardModel?> _tcs = new();
    private NCardGrid _grid = null!;
    private NConfirmButton _confirm = null!;
    private readonly List<CardModel> _cards = new();   // pickable + display extras
    private readonly HashSet<CardModel> _pickable = new();
    private readonly HashSet<CardModel> _unseen = new();
    private readonly Dictionary<CardModel, ModelVisibility> _visibility = new();
    private readonly Dictionary<CardModel, string> _searchText = new();
    private string _query = "";
    private CardModel? _pending;

    private static readonly List<SortingOrders> Sort = new()
    {
        SortingOrders.RarityAscending, SortingOrders.CostAscending,
        SortingOrders.TypeAscending, SortingOrders.AlphabetAscending,
    };

    /// <summary>Resolves with the confirmed card; null = cancelled (or torn down).</summary>
    public Task<CardModel?> Result => _tcs.Task;

    /// <summary>
    /// Shows the picker over the merchant (or rewards) screen; dies with it.
    /// <paramref name="valid"/> is the pickable set; <paramref name="pool"/> the card
    /// pool whose remaining cast renders darkened/locked for context.
    /// </summary>
    public static ModShopCardPickerUi Attach(Node host, Player player,
        HashSet<CardModel> valid, CardPoolModel pool)
    {
        Node? attach = host;
        while (attach != null && attach is not NMerchantRoom && attach is not NRewardsScreen)
            attach = attach.GetParent();
        attach ??= host.GetTree().Root;

        var ui = new ModShopCardPickerUi { Name = "ModShopCardPickerUi" };
        attach.AddChildSafely(ui);
        try
        {
            ui.Build(player, valid, pool);
        }
        catch
        {
            ui.QueueFreeSafely();
            throw;
        }
        return ui;
    }

    public override void _ExitTree()
    {
        _tcs.TrySetResult(null);
    }

    private void Build(Player player, HashSet<CardModel> valid, CardPoolModel pool)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ModSeenGate.SuppressWhile(this); // rendering the pool must not "discover" it

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f) };
        this.AddChildSafely(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = MouseFilterEnum.Stop; // modal: swallow clicks aimed at the shop

        _pickable.UnionWith(valid);
        _cards.AddRange(valid);
        _unseen.UnionWith(valid.Where(c => !SaveManager.Instance.Progress.DiscoveredCards.Contains(c.Id)));
        BuildDisplayExtras(player, valid, pool);

        // --- grid + checkmark confirm, from the simple-select scene ---
        Control select = PreloadManager.Cache.GetScene(SelectScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        _grid = Extract<NCardGrid>(select) ?? throw new System.InvalidOperationException("no NCardGrid in donor scene");
        _confirm = Extract<NConfirmButton>(select) ?? throw new System.InvalidOperationException("no NConfirmButton in donor scene");
        select.QueueFreeSafely();

        _grid.Name = "ModShopCardGrid";
        this.AddChildSafely(_grid);
        _grid.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _grid.YOffset = GridTop;
        _grid.Connect(NCardGrid.SignalName.HolderPressed, Callable.From<NCardHolder>(OnCardClicked));
        _grid.Connect(NCardGrid.SignalName.HolderAltPressed, Callable.From<NCardHolder>(h =>
        {
            if (h.CardModel != null && _pickable.Contains(h.CardModel))
                NGame.Instance?.GetInspectCardScreen().Open(new List<CardModel> { h.CardModel }, 0);
        }));
        CardGridVisibilityPatch.Overrides.Add(_grid, _visibility);
        CardGridSparklePatch.Sets.Add(_grid, _unseen);
        PopulateDeferred();

        this.AddChildSafely(_confirm); // self-anchors bottom-right, hidden until Enable()
        _confirm.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnConfirm));
        _confirm.Disable();

        // --- back button (cancel), borrowed from the deck-view scene ---
        Control deck = PreloadManager.Cache.GetScene(DeckViewScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        NButton? back = deck.FindChild("BackButton", recursive: true, owned: false) as NButton;
        Node? widget = AncestorUnder(deck, back);
        if (back != null && widget != null)
        {
            widget.GetParent()?.RemoveChild(widget);
            this.AddChildSafely(widget);
            back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Finish(null)));
            back.Enable();
        }
        deck.QueueFreeSafely();

        // --- search bar, fixed top-centre ---
        try
        {
            NSearchBar? bar = ModSearch.CreateBar(this);
            if (bar != null)
            {
                ModSearch.PlaceCentered(bar, SearchTop);
                bar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(OnQueryChanged));
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[search] shop card bar failed (picker still works without it): {e}");
        }

        MainFile.Logger.Info($"[shop card picker] built: {_pickable.Count} pickable of {_cards.Count} shown");
    }

    // The slot's pool beyond its actual loot, compendium-style: locked pool cards with
    // the library's locked rendering, unlocked-but-excluded ones (wrong type/rarity for
    // this slot, or stocked elsewhere) darkened. Neither is selectable.
    private void BuildDisplayExtras(Player player, HashSet<CardModel> valid, CardPoolModel pool)
    {
        var unlocked = pool
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToHashSet();
        foreach (CardModel card in pool.AllCards)
        {
            if (valid.Contains(card) || !card.ShouldShowInCardLibrary)
                continue;
            if (card.Rarity is not (CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare))
                continue;
            if (card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                continue; // feature #4 is singleplayer-only
            _cards.Add(card);
            _visibility[card] = unlocked.Contains(card) ? ModelVisibility.NotSeen : ModelVisibility.Locked;
        }
    }

    private void OnCardClicked(NCardHolder holder)
    {
        CardModel? card = holder.CardModel;
        if (card == null || !_pickable.Contains(card))
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
        ModSeenGate.MarkPicked(card); // candidacy is the reveal (see ModSeenGate)
        if (_unseen.Remove(card))
        {
            NCard? node = _grid.GetCardNode(card);
            if (node != null)
                node._sparkles.Visible = false;
        }
    }

    private void OnConfirm(NButton _)
    {
        if (_pending != null)
            Finish(_pending);
    }

    private void Finish(CardModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }

    // NCardGrid derives its column count from its laid-out width; a freshly re-parented
    // grid is zero-width for a frame or two. Wait for layout, then fill.
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
            MainFile.Logger.Error($"[shop card picker] populate failed: {e}");
        }
    }

    private void OnQueryChanged(string query)
    {
        _query = ModSearch.Canon(query);
        RefreshCards();
    }

    private void RefreshCards()
    {
        List<CardModel> show = _query.Length == 0
            ? new List<CardModel>(_cards)
            : _cards.Where(c => SearchableOf(c).Contains(_query)).ToList();
        _grid.SetCards(show, PileType.None, new List<SortingOrders>(Sort));
        if (_pending == null)
            return;
        if (show.Contains(_pending))
            _grid.HighlightCard(_pending); // holders were rebuilt; re-apply
        else
        {
            _pending = null;
            _confirm.Disable();
        }
    }

    private string SearchableOf(CardModel card)
    {
        if (_searchText.TryGetValue(card, out string? text))
            return text;
        try
        {
            text = ModSearch.Canon(card.Title + " " + card.Description.GetFormattedText());
        }
        catch (System.Exception)
        {
            text = ModSearch.Canon(card.Title);
        }
        _searchText[card] = text;
        return text;
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

    // The ancestor of 'n' that is a direct child of 'root' (keeps its scene-authored
    // position when re-parented into our full-screen view). Null if not under 'root'.
    private static Node? AncestorUnder(Node root, Node? n)
    {
        if (n == null)
            return null;
        while (n.GetParent() != null && n.GetParent() != root)
            n = n.GetParent();
        return n.GetParent() == root ? n : null;
    }
}
