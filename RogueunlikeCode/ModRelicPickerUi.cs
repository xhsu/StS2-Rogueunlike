using Godot;
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
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select a relic" overlay: the compendium Relic Collection screen, repurposed as a
/// picker (the relic twin of <see cref="ModPotionPickerUi"/>). Serves both the relic
/// reward row (feature #3) and the treasure chest (feature #3.1). Borrows the game's own
/// relic_collection scene wholesale — rarity sections with localized headers, character
/// -pool outline colours, hover tips, back ribbon — and shows the relic roster:
///   • pickable — a relic this source could roll at the current context: the remaining
///     grab-bag relics of the eligible rarities, plus any already-rolled ones.
///     Undiscovered ones are included and render with full art (never the compendium's
///     "Unknown" silhouette); full colour, clickable;
///   • locked — progression unlock not reached; the collection's lock icon and "locked"
///     hover tip, not clickable (exactly as the vanilla collection renders it);
///   • not a valid loot — unlocked but not rollable at the current context (starter/
///     shop/ancient/event relics, other pools, relics already consumed this run); the
///     collection's "not seen" darkening repurposed as a "can't drop here" hint, real
///     hover tips kept.
/// The selection pool equals the loot pool, never more. Categories (and ancient
/// subcategories) with nothing pickable at all are hidden — locked relics don't keep
/// a category alive.
/// Click a pickable relic to highlight it, checkmark to take it, back ribbon to cancel.
/// </summary>
public partial class ModRelicPickerUi : Control
{
    // ponytail: gaps above/below the search bar within its list row; tune by eye.
    private const float SearchTopGap = 48f;
    private const float SearchBottomGap = 32f;

    private readonly TaskCompletionSource<RelicModel?> _tcs = new();
    private NConfirmButton _confirm = null!;
    private NRelicCollectionEntry? _selected;
    private RelicModel? _selectedModel;
    private Color _selectedOutlineOriginal;
    private readonly List<(NRelicCollectionEntry entry, NRelicCollectionCategory[] cats, string text, bool pickable)> _searchEntries = new();
    private readonly Dictionary<NRelicCollectionEntry, TextureRect> _unseenStars = new(); // pickable & compendium-undiscovered

    /// <summary>Resolves with the confirmed relic; null = cancelled (or torn down).</summary>
    public Task<RelicModel?> Result => _tcs.Task;

    /// <summary>Shows the picker over the rewards screen. Null result = cancelled.</summary>
    public static Task<RelicModel?> Show(Node host, Player player, RelicReward reward) =>
        Attach(host, player, ValidDrops(player, reward)).Result;

    /// <summary>
    /// Shows the picker with an explicit pickable set (treasure chest / merchant paths).
    /// Attaches to the enclosing rewards screen / treasure room / merchant room so it
    /// covers it and dies with it.
    /// </summary>
    public static ModRelicPickerUi Attach(Node host, Player player, HashSet<RelicModel> valid)
    {
        var ui = new ModRelicPickerUi { Name = "ModRelicPickerUi" };
        ModUi.Mount(host, ui);
        try
        {
            ui.Build(player, valid);
        }
        catch
        {
            ui.QueueFreeSafely();
            throw;
        }
        return ui;
    }

    // Also covers the rewards screen being torn down around us: resolve as "cancelled".
    public override void _ExitTree()
    {
        _tcs.TrySetResult(null);
    }

    private void Build(Player player, HashSet<RelicModel> valid)
    {
        ModUi.SetupPickerRoot(this);
        ModUi.AddModalDim(this);

        NRelicCollection col = NRelicCollection.Create()
            ?? throw new InvalidOperationException("relic_collection scene unavailable");
        this.AddChildSafely(col); // entering the tree runs _Ready, binding its fields
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ModUi.RewireBackRibbon(col._backButton, () => Finish(null));

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
        // distinct models outside the unlock set, so they render as locked — moot in
        // practice, since a section with no pickable entries is hidden entirely.
        col._starter.LoadRelics(RelicRarity.Starter, col, new LocString("relic_collection", "STARTER"), seenAll, player.UnlockState, unlocked);
        col._common.LoadRelics(RelicRarity.Common, col, new LocString("relic_collection", "COMMON"), seenAll, player.UnlockState, unlocked);
        col._uncommon.LoadRelics(RelicRarity.Uncommon, col, new LocString("relic_collection", "UNCOMMON"), seenAll, player.UnlockState, unlocked);
        col._rare.LoadRelics(RelicRarity.Rare, col, new LocString("relic_collection", "RARE"), seenAll, player.UnlockState, unlocked);
        col._shop.LoadRelics(RelicRarity.Shop, col, new LocString("relic_collection", "SHOP"), seenAll, player.UnlockState, unlocked);
        col._ancient.LoadRelics(RelicRarity.Ancient, col, new LocString("relic_collection", "ANCIENT"), seenAll, player.UnlockState, unlocked);
        col._event.LoadRelics(RelicRarity.Event, col, new LocString("relic_collection", "EVENT"), seenAll, player.UnlockState, unlocked);

        col._screenContents.InstantlyScrollToTop();

        _confirm = ModUi.ExtractConfirmButton(this, () =>
        {
            if (_selectedModel != null)
                Finish(_selectedModel);
        });

        foreach (NRelicCollectionCategory category in categories)
            WireCategory(category, valid);
        RefreshCategoryVisibility(); // hide sections with nothing pickable

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

    private static RelicRarity[] EligibleRarities(RelicReward reward) =>
        reward.Rarity == RelicRarity.None
            ? new[] { RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare } // RelicFactory.RollRarity outcomes
            : new[] { reward.Rarity };

    /// <summary>
    /// What <see cref="MegaCrit.Sts2.Core.Factories.RelicFactory"/> could return for this
    /// reward: the remaining grab-bag relics of the eligible rarities (still allowed this
    /// run), plus the already-pulled roll itself.
    /// ponytail: ignores the bag's empty-deque escalation (Shop→C→U→R) — when a deque runs
    /// dry vanilla borrows from the next rarity; here those stay darkened. Rare, cosmetic.
    /// </summary>
    private static HashSet<RelicModel> ValidDrops(Player player, RelicReward reward)
    {
        var set = new HashSet<RelicModel>();
        foreach (RelicRarity rarity in EligibleRarities(reward))
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
        ModUi.Collect(category, entries);
        foreach (NRelicCollectionEntry entry in entries)
        {
            // The collection wires every entry's click to the compendium inspect screen; kill it.
            foreach (Godot.Collections.Dictionary conn in
                     entry.GetSignalConnectionList(NClickableControl.SignalName.Released))
                entry.Disconnect(NClickableControl.SignalName.Released, conn["callable"].AsCallable());

            bool locked = entry.ModelVisibility == ModelVisibility.Locked;
            bool pickable = !locked && valid.Contains(entry.relic.CanonicalInstance);
            RecordSearchText(entry, pickable);

            // The entry scene's authored cursor is the compendium's inspect magnifier;
            // here a click selects, so keep the plain StS2 arrow (an OS pointing hand
            // would be alien — the card and potion pickers don't change the cursor either).
            entry.MouseDefaultCursorShape = CursorShape.Arrow;

            if (locked)
                continue; // vanilla lock icon + "locked" hover tip; not selectable

            if (pickable)
            {
                NRelicCollectionEntry captured = entry;
                entry.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => OnRelicClicked(captured)));
                if (entry._relicNode is NRelic relicNode
                    && !SaveManager.Instance.Progress.DiscoveredRelics.Contains(entry.relic.Id)
                    && ModUnseenFx.AddStar(relicNode) is TextureRect star)
                    _unseenStars[entry] = star;
            }
            else if (entry._relicNode is NRelic relicNode)
            {
                // Discovered but can't drop from this reward: repurpose the collection's
                // "not seen" darkening as the "not a valid loot" hint; hover tips kept.
                relicNode.Icon.SelfModulate = StsColors.ninetyPercentBlack;
                relicNode.Outline.SelfModulate = StsColors.halfTransparentWhite;
            }
        }
    }

    // Title + hover-tip text, canonicalised once (library-style matching), so queries
    // hit both names and effect text. Ancestor categories remembered so search can
    // collapse empty sections and subsections.
    private void RecordSearchText(NRelicCollectionEntry entry, bool pickable)
    {
        string text = entry.relic.Title.GetFormattedText() + " " + entry.relic.Rarity;
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
        _searchEntries.Add((entry, cats.ToArray(), ModSearch.Canon(text), pickable));
    }

    // A section (or ancient subsection) earns its place only through a visible pickable
    // entry — all-darkened and all-locked categories are pure noise in a picker.
    private void RefreshCategoryVisibility()
    {
        foreach (NRelicCollectionCategory cat in _searchEntries.SelectMany(e => e.cats).Distinct())
            cat.Visible = _searchEntries.Any(e => e.pickable && e.entry.Visible && e.cats.Contains(cat));
    }

    private void OnQueryChanged(string query)
    {
        string canon = ModSearch.Canon(query);
        foreach ((NRelicCollectionEntry entry, _, string text, _) in _searchEntries)
            entry.Visible = canon.Length == 0 || ModSearch.Matches(text, canon);
        RefreshCategoryVisibility();
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
        ModSeenGate.MarkPicked(entry.relic.CanonicalInstance); // candidacy is the reveal (see ModSeenGate)
        if (_unseenStars.Remove(entry, out TextureRect? star))
            star.QueueFreeSafely(); // discovered now — drop the "new" badge
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

    private void Finish(RelicModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
