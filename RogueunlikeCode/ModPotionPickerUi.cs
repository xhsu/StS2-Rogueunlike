using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select one potion" overlay: the vanilla Potion Lab scene repurposed as a picker,
/// serving the reward row (feature #2: <see cref="PotionFactory.GetPotionOptions"/> for
/// standard rolls, the event's own pool for scope-tagged event rolls — see the
/// event-roll scopes in PotionRewardPickerPatch), and merchant slots (feature #4). Full roster
/// in the standard three states: pickable (this source could roll it — the caller-passed
/// set), locked (progression), darkened (unlocked but not a valid loot here — the lab's
/// "not seen" tint repurposed, hover tips kept). Sections with nothing pickable are
/// hidden. Click to select, checkmark to take, back ribbon to cancel.
/// </summary>
public partial class ModPotionPickerUi : Control
{
    // ponytail: gaps above/below the search bar within its list row; tune by eye.
    private const float SearchTopGap = 48f;
    private const float SearchBottomGap = 32f;

    private readonly TaskCompletionSource<PotionModel?> _tcs = new();
    private NConfirmButton _confirm = null!;
    private NLabPotionHolder? _selected;
    private PotionModel? _selectedModel;
    private Color _selectedOutlineOriginal;
    private readonly List<(NLabPotionHolder holder, NPotionLabCategory category, string text, bool pickable)> _searchEntries = new();
    private readonly Dictionary<NLabPotionHolder, TextureRect> _unseenStars = new(); // pickable & compendium-undiscovered

    /// <summary>Resolves with the confirmed potion; null = cancelled (or torn down).</summary>
    public Task<PotionModel?> Result => _tcs.Task;

    /// <summary>
    /// Shows the picker with the caller's pickable set. Attaches to the enclosing
    /// rewards screen / merchant room so it covers it and dies with it.
    /// </summary>
    public static ModPotionPickerUi Attach(Node host, Player player, HashSet<PotionModel> valid)
    {
        var ui = new ModPotionPickerUi { Name = "ModPotionPickerUi" };
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

    private void Build(Player player, HashSet<PotionModel> valid)
    {
        ModUi.SetupPickerRoot(this);
        ModUi.AddModalDim(this);

        NPotionLab lab = NPotionLab.Create()
            ?? throw new InvalidOperationException("potion_lab scene unavailable");
        this.AddChildSafely(lab); // entering the tree runs _Ready, binding its fields
        lab.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ModUi.RewireBackRibbon(lab._backButton, () => Finish(null));

        // What this source could actually roll (passed in), and what's unlocked at all.
        HashSet<PotionModel> unlocked = player.UnlockState.Potions.ToHashSet();

        Fill(lab._common, "COMMON",
            ModelDb.AllPotions.Where(p => p.Rarity == PotionRarity.Common), valid, unlocked);
        Fill(lab._uncommon, "UNCOMMON",
            ModelDb.AllPotions.Where(p => p.Rarity == PotionRarity.Uncommon), valid, unlocked);
        Fill(lab._rare, "RARE",
            ModelDb.AllPotions.Where(p => p.Rarity == PotionRarity.Rare), valid, unlocked);
        // Same allowlist as the vanilla lab: Special is exactly Event+Token. Anything
        // else (debug/deprecated models are PotionRarity.None) never renders, so new
        // junk entries in game updates stay excluded with zero maintenance.
        Fill(lab._special, "SPECIAL",
            ModelDb.AllPotions.Where(p => p.Rarity is PotionRarity.Event or PotionRarity.Token),
            valid, unlocked);

        RefreshCategoryVisibility(); // hide sections with nothing pickable
        lab._screenContents.InstantlyScrollToTop();

        _confirm = ModUi.ExtractConfirmButton(this, () =>
        {
            if (_selectedModel != null)
                Finish(_selectedModel);
        });

        // Search bar (the Card Library's widget) in the scrolling flow above the rarity
        // sections — a stretched row hosting the centred bar — so it scrolls away with
        // the potions instead of sitting fixed over the screen top. The row is the
        // spacer: the list only sees its height, so the gaps are row height minus bar.
        try
        {
            Control sections = (Control)lab._common.GetParent();
            int at = lab._common.GetIndex();
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
            MainFile.Logger.Error($"[search] potion bar failed (picker still works without it): {e}");
        }
    }

    private void Fill(NPotionLabCategory category, string headerKey,
        IEnumerable<PotionModel> rarityPotions,
        HashSet<PotionModel> valid, HashSet<PotionModel> unlocked)
    {
        List<PotionModel> all = rarityPotions.ToList();
        category.Visible = all.Count > 0;
        if (all.Count == 0)
            return;
        // Vanilla header text ("Common: The most frequent potions found in the Spire." etc).
        category._headerLabel.Text = new LocString("potion_lab", headerKey).GetFormattedText();

        // Vanilla lab ordering: shared potions alphabetical, then character-pool potions
        // grouped in character order (NPotionLabCategory.LoadPotions).
        List<PotionModel> charGrouped = new();
        foreach (PotionPoolModel pool in ModelDb.AllCharacterPotionPools)
            foreach (PotionModel p in all)
                if (pool.AllPotionIds.Contains(p.Id))
                    charGrouped.Add(p);
        List<PotionModel> shared = all.Where(p => !charGrouped.Contains(p)).ToList();
        shared.Sort((a, b) => LocManager.Instance.StringComparer
            .Compare(a.Title.GetFormattedText(), b.Title.GetFormattedText()));

        foreach (PotionModel potion in shared.Concat(charGrouped))
        {
            bool isLocked = !unlocked.Contains(potion);
            bool pickable = !isLocked && valid.Contains(potion);
            PotionModel mutable = potion.ToMutable();
            NLabPotionHolder holder = NLabPotionHolder.Create(mutable,
                isLocked ? ModelVisibility.Locked : ModelVisibility.Visible);
            category._potionContainer.AddChildSafely(holder);
            RecordSearchText(holder, category, mutable, pickable);
            if (isLocked)
                continue; // vanilla lock icon + "locked" hover tip; not selectable
            if (pickable)
            {
                holder.GuiInput += ev =>
                {
                    if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                        OnPotionClicked(holder, potion);
                };
                if (!SaveManager.Instance.Progress.DiscoveredPotions.Contains(potion.Id)
                    && ModUnseenFx.AddStar(holder._potionNode) is TextureRect star)
                    _unseenStars[holder] = star;
            }
            else
            {
                // Unlocked but can't drop from this reward (other characters' pools,
                // event-only potions): repurpose the lab's "not seen" darkening as the
                // "not a valid loot" hint; real hover tips are kept so it stays browsable.
                NPotion node = holder._potionNode;
                node.Image.SelfModulate = StsColors.ninetyPercentBlack;
                node.Outline.Modulate = StsColors.halfTransparentWhite;
            }
        }
    }

    // Title + hover-tip text, canonicalised once (library-style matching), so queries
    // hit both names ("fire") and effect text ("draw", "strength").
    private void RecordSearchText(NLabPotionHolder holder, NPotionLabCategory category, PotionModel potion, bool pickable)
    {
        string text = potion.Title.GetFormattedText() + " " + potion.Rarity;
        try
        {
            foreach (IHoverTip tip in potion.HoverTips)
                if (tip is HoverTip hoverTip)
                    text += " " + hoverTip.Title + " " + hoverTip.Description;
        }
        catch (Exception)
        {
            // some tips need combat context to format; title-only is fine then
        }
        _searchEntries.Add((holder, category, ModSearch.Canon(text), pickable));
    }

    // A section earns its place only through a visible pickable potion — all-darkened
    // and all-locked sections are pure noise in a picker.
    private void RefreshCategoryVisibility()
    {
        foreach (NPotionLabCategory category in _searchEntries.Select(e => e.category).Distinct())
            category.Visible = _searchEntries.Any(e => e.category == category && e.pickable && e.holder.Visible);
    }

    // Search results are pickable-only — searching means looking for something to PICK;
    // the darkened/locked context cast only clutters results (user rule, 2026-07-03).
    private void OnQueryChanged(string query)
    {
        string canon = ModSearch.Canon(query);
        foreach ((NLabPotionHolder holder, _, string text, bool pickable) in _searchEntries)
            holder.Visible = canon.Length == 0 || (pickable && ModSearch.Matches(text, canon));
        RefreshCategoryVisibility();
        if (_selected != null && !_selected.IsVisibleInTree())
        {
            SetHighlight(_selected, false);
            _selected = null;
            _selectedModel = null;
            _confirm.Disable();
        }
    }

    private void OnPotionClicked(NLabPotionHolder holder, PotionModel potion)
    {
        if (_selected == holder) // click again to deselect
        {
            SetHighlight(holder, false);
            _selected = null;
            _selectedModel = null;
            _confirm.Disable();
            return;
        }
        if (_selected != null)
            SetHighlight(_selected, false);
        _selected = holder;
        _selectedModel = potion;
        SetHighlight(holder, true);
        _confirm.Enable();
        ModSeenGate.MarkPicked(potion); // candidacy is the reveal (see ModSeenGate)
        if (_unseenStars.Remove(holder, out TextureRect? star)) // discovered now — drop the "new" badge
            star.QueueFreeSafely();
    }

    // Gold outline marks the pending pick; restore the character-pool colour on deselect.
    private void SetHighlight(NLabPotionHolder holder, bool on)
    {
        TextureRect outline = holder._potionNode.Outline;
        if (on)
        {
            _selectedOutlineOriginal = outline.Modulate;
            outline.Modulate = StsColors.gold;
        }
        else
        {
            outline.Modulate = _selectedOutlineOriginal;
        }
    }

    private void Finish(PotionModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
