using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Rewards;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StS2Mod.StS2ModCode;

/// <summary>
/// "Select a relic" overlay: the compendium Relic Collection screen, repurposed as a
/// picker (the relic twin of <see cref="ModPotionPickerUi"/>). Borrows the game's own
/// relic_collection scene wholesale — rarity sections with localized headers, character
/// -pool outline colours, hover tips, back ribbon — and shows the full relic roster:
///   • pickable — a relic this reward could roll (remaining grab-bag relics of the
///     reward's eligible rarities, plus the already-rolled one); full colour, clickable;
///   • locked — progression unlock not reached; the collection's lock icon and "locked"
///     hover tip, not clickable (exactly as the vanilla collection renders it);
///   • not a valid loot — unlocked but outside this reward's pool (starter/shop/ancient/
///     event relics, other rarities, already-pulled ones); the collection's "not seen"
///     darkening repurposed as a "can't drop here" hint, real hover tips kept.
/// Click a pickable relic to highlight it, checkmark to take it, back ribbon to cancel.
/// </summary>
public partial class ModRelicPickerUi : Control
{
    private static readonly string SelectScene =
        SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen");

    // ponytail: gaps above/below the search bar within its list row; tune by eye.
    private const float SearchTopGap = 48f;
    private const float SearchBottomGap = 32f;

    private readonly TaskCompletionSource<RelicModel?> _tcs = new();
    private NConfirmButton _confirm = null!;
    private NRelicCollectionEntry? _selected;
    private RelicModel? _selectedModel;
    private Color _selectedOutlineOriginal;
    private readonly List<(NRelicCollectionEntry entry, NRelicCollectionCategory[] cats, string text)> _searchEntries = new();

    /// <summary>Shows the picker over the rewards screen. Null result = cancelled.</summary>
    public static Task<RelicModel?> Show(Node host, Player player, RelicReward reward)
    {
        // Attach to the rewards screen so the picker covers it and dies with it.
        Node? attach = host;
        while (attach != null && attach is not NRewardsScreen)
            attach = attach.GetParent();
        attach ??= host.GetTree().Root;

        var ui = new ModRelicPickerUi { Name = "ModRelicPickerUi" };
        attach.AddChildSafely(ui);
        try
        {
            ui.Build(player, reward);
        }
        catch
        {
            ui.QueueFreeSafely();
            throw;
        }
        return ui._tcs.Task;
    }

    // Also covers the rewards screen being torn down around us: resolve as "cancelled".
    public override void _ExitTree()
    {
        _tcs.TrySetResult(null);
    }

    private void Build(Player player, RelicReward reward)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // In-run there is no compendium backdrop; supply one. It also swallows every
        // click aimed at the rewards screen underneath, which makes the picker modal.
        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f) };
        this.AddChildSafely(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = MouseFilterEnum.Stop;

        NRelicCollection col = NRelicCollection.Create()
            ?? throw new InvalidOperationException("relic_collection scene unavailable");
        this.AddChildSafely(col); // entering the tree runs _Ready, binding its fields
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // The scene's back ribbon is wired to a submenu stack we don't have; rewire to cancel.
        NBackButton back = col._backButton;
        foreach (Godot.Collections.Dictionary conn in
                 back.GetSignalConnectionList(NClickableControl.SignalName.Released))
            back.Disconnect(NClickableControl.SignalName.Released, conn["callable"].AsCallable());
        back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Finish(null)));
        back.MoveToHidePosition();
        back.Enable();

        HashSet<RelicModel> valid = ValidDrops(player, reward);
        HashSet<RelicModel> unlocked = player.UnlockState.Relics.ToHashSet();
        // Real art for everything: the compendium's "???" mystery state would make the
        // picker unreadable. Locked stays locked (mirrors the potion picker).
        HashSet<RelicModel> seenAll = ModelDb.AllRelics.ToHashSet();

        NRelicCollectionCategory[] categories =
        {
            col._starter, col._common, col._uncommon, col._rare, col._shop, col._ancient, col._event,
        };
        // Vanilla loader does all the rendering (sections, subcategories, lock icons,
        // outline colours). Headers come from the same "relic_collection" loc table the
        // compendium uses. ponytail: the Starter section's Orobas-upgraded variants are
        // distinct models outside the unlock set, so they render as locked — they are
        // never valid drops anyway.
        col._starter.LoadRelics(RelicRarity.Starter, col, new LocString("relic_collection", "STARTER"), seenAll, player.UnlockState, unlocked);
        col._common.LoadRelics(RelicRarity.Common, col, new LocString("relic_collection", "COMMON"), seenAll, player.UnlockState, unlocked);
        col._uncommon.LoadRelics(RelicRarity.Uncommon, col, new LocString("relic_collection", "UNCOMMON"), seenAll, player.UnlockState, unlocked);
        col._rare.LoadRelics(RelicRarity.Rare, col, new LocString("relic_collection", "RARE"), seenAll, player.UnlockState, unlocked);
        col._shop.LoadRelics(RelicRarity.Shop, col, new LocString("relic_collection", "SHOP"), seenAll, player.UnlockState, unlocked);
        col._ancient.LoadRelics(RelicRarity.Ancient, col, new LocString("relic_collection", "ANCIENT"), seenAll, player.UnlockState, unlocked);
        col._event.LoadRelics(RelicRarity.Event, col, new LocString("relic_collection", "EVENT"), seenAll, player.UnlockState, unlocked);

        col._screenContents.InstantlyScrollToTop();

        // Checkmark confirm button, borrowed from the simple-card-select scene (as feature #1).
        Control select = PreloadManager.Cache.GetScene(SelectScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        NConfirmButton? confirm = FindDescendant<NConfirmButton>(select);
        if (confirm == null)
        {
            select.QueueFreeSafely();
            throw new InvalidOperationException("NConfirmButton not found in donor scene");
        }
        confirm.GetParent().RemoveChild(confirm);
        select.QueueFreeSafely();
        _confirm = confirm;
        this.AddChildSafely(_confirm); // self-anchors bottom-right, hidden until Enable()
        _confirm.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnConfirm));
        _confirm.Disable();

        foreach (NRelicCollectionCategory category in categories)
            WireCategory(category, valid);

        // Search bar in the scrolling flow above the sections (same as the potion picker).
        try
        {
            Control sections = (Control)col._starter.GetParent();
            int at = col._starter.GetIndex();
            var searchRow = new Control { Name = "ModSearchRow" };
            sections.AddChildSafely(searchRow);
            sections.MoveChild(searchRow, at);
            NSearchBar? bar = ModSearch.CreateBar(searchRow);
            if (bar == null)
            {
                searchRow.QueueFreeSafely();
            }
            else
            {
                searchRow.CustomMinimumSize = new Vector2(0,
                    SearchTopGap + bar.CustomMinimumSize.Y + SearchBottomGap);
                ModSearch.PlaceCentered(bar, SearchTopGap);
                bar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(OnQueryChanged));
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[search] relic bar failed (picker still works without it): {e}");
        }

        MainFile.Logger.Info($"[relic picker] built: {valid.Count} pickable of {ModelDb.AllRelics.Count()} total");
    }

    /// <summary>
    /// What <see cref="MegaCrit.Sts2.Core.Factories.RelicFactory"/> could return for this
    /// reward: the remaining grab-bag relics of the eligible rarities (still allowed this
    /// run), plus the already-pulled roll itself.
    /// ponytail: ignores the bag's empty-deque escalation (Shop→C→U→R) — when a deque runs
    /// dry vanilla borrows from the next rarity; here those stay darkened. Rare, cosmetic.
    /// </summary>
    private static HashSet<RelicModel> ValidDrops(Player player, RelicReward reward)
    {
        RelicRarity[] eligible = reward.Rarity == RelicRarity.None
            ? new[] { RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare } // RelicFactory.RollRarity outcomes
            : new[] { reward.Rarity };
        var set = new HashSet<RelicModel>();
        foreach (RelicRarity rarity in eligible)
            if (player.RelicGrabBag._deques.TryGetValue(rarity, out List<RelicModel>? deque))
                foreach (RelicModel relic in deque)
                    if (relic.IsAllowed(player.RunState))
                        set.Add(relic);
        if (reward.Relic != null)
            set.Add(reward.Relic.CanonicalInstance);
        return set;
    }

    private void WireCategory(NRelicCollectionCategory category, HashSet<RelicModel> valid)
    {
        var entries = new List<NRelicCollectionEntry>();
        Collect(category, entries);
        foreach (NRelicCollectionEntry entry in entries)
        {
            // The collection wires every entry's click to the compendium inspect screen; kill it.
            foreach (Godot.Collections.Dictionary conn in
                     entry.GetSignalConnectionList(NClickableControl.SignalName.Released))
                entry.Disconnect(NClickableControl.SignalName.Released, conn["callable"].AsCallable());

            RecordSearchText(entry);

            if (entry.ModelVisibility == ModelVisibility.Locked)
                continue; // vanilla lock icon + "locked" hover tip; not selectable

            if (valid.Contains(entry.relic.CanonicalInstance))
            {
                NRelicCollectionEntry captured = entry;
                entry.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => OnRelicClicked(captured)));
            }
            else if (entry._relicNode is NRelic relicNode)
            {
                // Unlocked but can't drop from this reward: repurpose the collection's
                // "not seen" darkening as the "not a valid loot" hint; hover tips kept.
                relicNode.Icon.SelfModulate = StsColors.ninetyPercentBlack;
                relicNode.Outline.SelfModulate = StsColors.halfTransparentWhite;
            }
        }
    }

    // Title + hover-tip text, canonicalised once (library-style matching), so queries
    // hit both names and effect text. Ancestor categories remembered so search can
    // collapse empty sections and subsections.
    private void RecordSearchText(NRelicCollectionEntry entry)
    {
        string text = entry.relic.Title.GetFormattedText();
        try
        {
            foreach (IHoverTip tip in entry.relic.HoverTips)
                if (tip is HoverTip hoverTip)
                    text += " " + hoverTip.Title + " " + hoverTip.Description;
        }
        catch (Exception)
        {
            // some tips need run context to format; title-only is fine then
        }
        var cats = new List<NRelicCollectionCategory>();
        for (Node? n = entry.GetParent(); n != null && n is not NRelicCollection; n = n.GetParent())
            if (n is NRelicCollectionCategory c)
                cats.Add(c);
        _searchEntries.Add((entry, cats.ToArray(), ModSearch.Canon(text)));
    }

    private void OnQueryChanged(string query)
    {
        string canon = ModSearch.Canon(query);
        foreach ((NRelicCollectionEntry entry, _, string text) in _searchEntries)
            entry.Visible = canon.Length == 0 || text.Contains(canon);
        foreach (NRelicCollectionCategory cat in _searchEntries.SelectMany(e => e.cats).Distinct())
            cat.Visible = _searchEntries.Any(e => e.entry.Visible && e.cats.Contains(cat));
        if (_selected != null && !_selected.IsVisibleInTree())
        {
            SetHighlight(_selected, false);
            _selected = null;
            _selectedModel = null;
            _confirm.Disable();
        }
    }

    private void OnRelicClicked(NRelicCollectionEntry entry)
    {
        if (_selected == entry) // click again to deselect
        {
            SetHighlight(entry, false);
            _selected = null;
            _selectedModel = null;
            _confirm.Disable();
            return;
        }
        if (_selected != null)
            SetHighlight(_selected, false);
        _selected = entry;
        _selectedModel = entry.relic.CanonicalInstance;
        SetHighlight(entry, true);
        _confirm.Enable();
    }

    // Gold outline marks the pending pick; restore the character-pool colour on deselect.
    private void SetHighlight(NRelicCollectionEntry entry, bool on)
    {
        if (entry._relicNode is not NRelic relicNode)
            return;
        if (on)
        {
            _selectedOutlineOriginal = relicNode.Outline.SelfModulate;
            relicNode.Outline.SelfModulate = StsColors.gold;
        }
        else
        {
            relicNode.Outline.SelfModulate = _selectedOutlineOriginal;
        }
    }

    private void OnConfirm(NButton _)
    {
        if (_selectedModel != null)
            Finish(_selectedModel);
    }

    private void Finish(RelicModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
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
