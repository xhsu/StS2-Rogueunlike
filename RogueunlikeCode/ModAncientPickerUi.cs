using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Unlocks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select an Ancient" modal over the map screen (feature #5). Left: one row per Ancient
/// in the game — map-node icon, name, epithet, home act — in three states:
///   • pickable — this act's Ancient roll pool (unlocked natives + dealt shared subset);
///     full colour; clicking selects it (the vanilla roll starts pre-selected);
///   • darkened — unlocked but not rollable at this act (another act's native, or a
///     shared Ancient dealt elsewhere this run);
///   • locked — progression (epoch) locked; blacked icon, hidden name.
/// Right: an info panel showing the clicked row's full offering list — every option the
/// Ancient can present (<see cref="AncientEventModel.AllPossibleOptions"/>), scrollable,
/// so "what does this one grant" is readable before committing. Clicking ANY unlocked
/// row shows its info; only pickable rows also become the pending pick. (Hover tooltips
/// were tried first and clipped off-screen: the tip system's column caps at viewport
/// height and silently wraps overflow into off-screen flow columns.)
/// Checkmark confirms, back button cancels (stay on the map, node re-clickable).
/// </summary>
public partial class ModAncientPickerUi : Control
{
    // ponytail: fixed layout metrics, tuned by eye; revisit if MegaCrit adds many Ancients.
    private const float RowWidth = 900f;
    private const float RowHeight = 96f;
    private const float RowGap = 10f;
    private const float IconSize = 76f;
    private const float PanelWidth = 460f;
    private const float PanelGap = 24f;
    private const float PanelPad = 20f;

    private static readonly Color RowBg = new(0f, 0f, 0f, 0.35f);      // AncientEventModel.ButtonColor
    private static readonly Color RowBgHover = new(1f, 1f, 1f, 0.12f);
    private static readonly Color RowBgSelected = new(0.94f, 0.78f, 0.32f, 0.25f); // gold tint
    private static readonly Color PanelBg = new(0f, 0f, 0f, 0.55f);

    private readonly TaskCompletionSource<AncientPickResult?> _tcs = new();
    private readonly Dictionary<AncientEventModel, (Control row, ColorRect bg, Label name)> _rows = new();
    private readonly Dictionary<AncientEventModel, string> _optionsTextCache = new();
    private readonly Dictionary<AncientEventModel, List<List<AncientOptionInfo>>> _poolsCache = new();
    private readonly Dictionary<AncientEventModel, Dictionary<int, AncientOptionInfo>> _designations = new();
    private readonly List<(int Slot, AncientOptionInfo Option, PanelContainer Panel, Label Title)> _entryNodes = new();
    private NConfirmButton _confirm = null!;
    private AncientEventModel? _pending;
    private Player _player = null!;

    private TextureRect _infoIcon = null!;
    private Label _infoName = null!;
    private Label _infoEpithet = null!;
    private Label _infoStatus = null!;
    private MegaRichTextLabel _infoOptions = null!;
    private VBoxContainer _infoSlots = null!;

    /// <summary>Resolves with the confirmed pick (+ designations); null = cancelled (or torn down).</summary>
    public Task<AncientPickResult?> Result => _tcs.Task;

    /// <summary>
    /// Shows the picker as a child of the map screen (dies with it). <paramref name="valid"/>
    /// is this act's roll pool; <paramref name="current"/> the vanilla roll, pre-selected.
    /// <paramref name="player"/> is the local player — slot pools probe against their state.
    /// </summary>
    public static ModAncientPickerUi Attach(Control host, UnlockState unlock,
        List<AncientEventModel> valid, AncientEventModel current, Player player)
    {
        var ui = new ModAncientPickerUi { Name = "ModAncientPickerUi" };
        host.AddChildSafely(ui); // NOT ModUi.Mount: the map screen itself is the host that must own us
        try
        {
            ui.Build(unlock, valid, current, player);
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

    private void Build(UnlockState unlock, List<AncientEventModel> valid, AncientEventModel current, Player player)
    {
        _player = player;
        ModUi.SetupPickerRoot(this);
        ModUi.AddModalDim(this);

        // The pool and _rooms.Ancient are canonical ModelDb instances (GenerateRooms
        // rolls from GetUnlockedAncients; EventRoom asserts canonicality) — so the
        // roster, the valid set and the default pick all share one instance space.
        var validSet = valid.ToHashSet();
        // Unlocked anywhere = not progression-locked. Everything else renders locked.
        var unlockedSet = ModelDb.Acts.SelectMany(a => a.GetUnlockedAncients(unlock))
            .Concat(unlock.SharedAncients).ToHashSet();

        var list = new VBoxContainer { Name = "AncientRows" };
        list.AddThemeConstantOverride("separation", (int)RowGap);
        var scroll = new ScrollContainer
        {
            Name = "AncientScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        this.AddChildSafely(scroll);
        scroll.AddChildSafely(list);

        List<AncientEventModel> roster = ModelDb.AllAncients.ToList();
        foreach (AncientEventModel ancient in roster)
            BuildRow(list, ancient, validSet.Contains(ancient), unlockedSet.Contains(ancient));

        // List + info panel as one centred group; the list caps its height and scrolls.
        float groupHalf = (RowWidth + PanelGap + PanelWidth) / 2f; // 692
        float panelH = Mathf.Min(roster.Count * (RowHeight + RowGap), 860f);
        scroll.CustomMinimumSize = new Vector2(RowWidth, panelH);
        scroll.SetAnchorsPreset(LayoutPreset.Center);
        scroll.OffsetLeft = -groupHalf;
        scroll.OffsetRight = -groupHalf + RowWidth;
        scroll.OffsetTop = -panelH / 2f + 30f; // header clearance
        scroll.OffsetBottom = panelH / 2f + 30f;

        BuildInfoPanel(groupHalf, panelH);

        // Title as a bottom banner (like the card pickers' bottom label): the top strip
        // belongs to the HUD — top bar + relic rows draw ABOVE this modal and clip
        // anything placed there. Anchors AND offsets set explicitly; SetAnchorsPreset
        // alone leaves a fresh label as a text-width rect at the origin.
        Label title = MakeLabel(ModUi.SelectAncientLabel, 40, StsColors.cream);
        this.AddChildSafely(title);
        title.SetAnchorsPreset(LayoutPreset.Center);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.OffsetLeft = -groupHalf;
        title.OffsetRight = groupHalf;
        title.OffsetTop = panelH / 2f + 40f;
        title.OffsetBottom = panelH / 2f + 96f;

        _confirm = ModUi.ExtractConfirmButton(this, () =>
        {
            if (_pending == null)
                return;
            var result = new AncientPickResult { Ancient = _pending };
            if (_designations.TryGetValue(_pending, out Dictionary<int, AncientOptionInfo>? chosen))
                foreach (KeyValuePair<int, AncientOptionInfo> kv in chosen)
                    result.Options.Add((kv.Key, kv.Value));
            Finish(result);
        });
        BuildBackButton();

        if (_rows.ContainsKey(current))
            Select(current); // vanilla roll = the default pick; confirming keeps it

        MainFile.Logger.Info($"[ancient picker] built: {validSet.Count} pickable of {roster.Count} total");
    }

    private void BuildRow(VBoxContainer into, AncientEventModel ancient, bool pickable, bool unlocked)
    {
        var row = new Control { CustomMinimumSize = new Vector2(RowWidth, RowHeight), MouseFilter = MouseFilterEnum.Stop };
        var bg = new ColorRect { Color = RowBg, MouseFilter = MouseFilterEnum.Ignore };
        row.AddChildSafely(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            Size = new Vector2(IconSize, IconSize),
            Position = new Vector2(14f, (RowHeight - IconSize) / 2f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        try
        {
            icon.Texture = ancient.MapIcon;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient picker] no map icon for {ancient.Id}: {e.Message}");
        }
        row.AddChildSafely(icon);

        Label name = MakeLabel(unlocked ? ancient.Title.GetFormattedText() : LockedTitle(),
            34, pickable ? StsColors.cream : StsColors.gray);
        name.Position = new Vector2(110f, 14f);
        row.AddChildSafely(name);

        if (unlocked)
        {
            Label epithet = MakeLabel(SafeEpithet(ancient), 24, StsColors.gray);
            epithet.Position = new Vector2(110f, 54f);
            row.AddChildSafely(epithet);
        }

        // Home act, right-aligned — explains WHY a row is darkened (it lives elsewhere).
        ActModel? home = ModelDb.Acts.FirstOrDefault(a => a.AllAncients.Contains(ancient));
        if (home != null)
        {
            Label act = MakeLabel(home.Title.GetFormattedText(), 24, StsColors.gray);
            act.HorizontalAlignment = HorizontalAlignment.Right;
            act.CustomMinimumSize = new Vector2(240f, 0f);
            act.Position = new Vector2(RowWidth - 254f, (RowHeight - 30f) / 2f);
            row.AddChildSafely(act);
        }

        if (!unlocked || !pickable)
            icon.SelfModulate = StsColors.ninetyPercentBlack; // locked, or darkened: can't spawn here

        row.MouseEntered += () =>
        {
            if (!pickable || _pending != ancient)
                bg.Color = RowBgHover;
        };
        row.MouseExited += () =>
        {
            bg.Color = pickable && _pending == ancient ? RowBgSelected : RowBg;
        };
        row.GuiInput += ev =>
        {
            if (ev is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                return;
            if (pickable)
                Select(ancient); // selection also shows its info
            else
                ShowInfo(ancient, unlocked, pickable: false); // read-only inspect
        };

        into.AddChildSafely(row);
        _rows[ancient] = (row, bg, name);
    }

    // ---- the info panel: full offering list of the clicked row ----

    private void BuildInfoPanel(float groupHalf, float panelH)
    {
        var panel = new Control { Name = "AncientInfoPanel" };
        this.AddChildSafely(panel);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = groupHalf - PanelWidth;
        panel.OffsetRight = groupHalf;
        panel.OffsetTop = -panelH / 2f + 30f;
        panel.OffsetBottom = panelH / 2f + 30f;

        var bg = new ColorRect { Color = PanelBg, MouseFilter = MouseFilterEnum.Ignore };
        panel.AddChildSafely(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _infoIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            Size = new Vector2(IconSize, IconSize),
            Position = new Vector2(PanelPad, PanelPad),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        panel.AddChildSafely(_infoIcon);

        _infoName = MakeLabel("", 32, StsColors.cream);
        _infoName.Position = new Vector2(PanelPad + IconSize + 16f, PanelPad + 6f);
        panel.AddChildSafely(_infoName);

        _infoEpithet = MakeLabel("", 22, StsColors.gray);
        _infoEpithet.Position = new Vector2(PanelPad + IconSize + 16f, PanelPad + 44f);
        panel.AddChildSafely(_infoEpithet);

        _infoStatus = MakeLabel("", 22, StsColors.gray);
        _infoStatus.Position = new Vector2(PanelPad, PanelPad + IconSize + 12f);
        panel.AddChildSafely(_infoStatus);

        float textTop = PanelPad + IconSize + 48f;
        var textScroll = new ScrollContainer
        {
            Name = "AncientInfoScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        panel.AddChildSafely(textScroll);
        textScroll.SetAnchorsPreset(LayoutPreset.FullRect);
        textScroll.OffsetLeft = PanelPad;
        textScroll.OffsetRight = -PanelPad;
        textScroll.OffsetTop = textTop;
        textScroll.OffsetBottom = -PanelPad;

        // MegaRichTextLabel = the game's rich label (same class the hover tips use), so
        // option descriptions render their colour bbcode and inline glyphs correctly.
        // Its AutoSizeEnabled default (true, up to 100px) inflates the font to FILL the
        // rect — disable it and pin the size through its own SetFontSize, which keeps
        // the normal/bbcode theme sizes consistent.
        var content = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(PanelWidth - 2f * PanelPad - 12f, 0f), // -12: scrollbar clearance
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        content.AddThemeConstantOverride("separation", 10);
        textScroll.AddChildSafely(content);

        _infoOptions = MakeRichText(24);
        _infoOptions.MouseFilter = MouseFilterEnum.Ignore;
        content.AddChildSafely(_infoOptions);

        _infoSlots = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _infoSlots.AddThemeConstantOverride("separation", 8);
        content.AddChildSafely(_infoSlots);
    }

    // MegaRichTextLabel = the game's rich label (same class the hover tips use), so
    // option descriptions render their colour bbcode and inline glyphs correctly. Its
    // AutoSizeEnabled default (true, up to 100px) inflates the font to FILL the rect —
    // disable it and pin the size through its own SetFontSize, which keeps the
    // normal/bbcode theme sizes consistent.
    private static MegaRichTextLabel MakeRichText(int fontSize)
    {
        var label = new MegaRichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        // MegaRichTextLabel._Ready ASSERTS a normal_font override (engine-bug workaround
        // in MegaLabelHelper) — without one it throws on AddChild and skips its own
        // font/effect setup. Mirror the theme font back as the override; RefreshFont
        // still swaps in the CJK substitute after this for locales that need it.
        label.AddThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont,
            label.GetThemeFont(ThemeConstants.RichTextLabel.NormalFont, "RichTextLabel"));
        label.AutoSizeEnabled = false;
        label.SetFontSize(fontSize);
        return label;
    }

    private void ShowInfo(AncientEventModel ancient, bool unlocked, bool pickable)
    {
        try
        {
            _infoIcon.Texture = ancient.MapIcon;
        }
        catch (System.Exception) { }
        _infoIcon.SelfModulate = unlocked ? Colors.White : StsColors.ninetyPercentBlack;
        _infoName.Text = unlocked ? ancient.Title.GetFormattedText() : LockedTitle();
        _infoEpithet.Text = unlocked ? SafeEpithet(ancient) : "";

        List<List<AncientOptionInfo>> pools = pickable ? PoolsOf(ancient) : new List<List<AncientOptionInfo>>();
        bool interactive = pickable && pools.Count > 0;

        _infoStatus.Text = interactive
            ? string.Format(ModUi.Loc("ROGUEUNLIKE.ANCIENT_ROLLS.label", "Will roll {0} — click to substitute:"), pools.Count)
            : pickable
                ? ""
                : unlocked
                    ? ModUi.Loc("ROGUEUNLIKE.ANCIENT_NOT_HERE.label", "Cannot appear at this act")
                    : LockedTitle(); // vanilla card_library "Locked"

        _infoOptions.Visible = !interactive;
        _infoSlots.Visible = interactive;
        if (interactive)
            BuildSlotSections(ancient, pools);
        else
            _infoOptions.Text = unlocked
                ? OptionsTextOf(ancient)
                : new LocString("card_library", "LOCKED.description").GetFormattedText();
    }

    // ---- interactive slot sections (pickable Ancients): designate ≤1 option per slot ----

    private List<List<AncientOptionInfo>> PoolsOf(AncientEventModel ancient)
    {
        if (_poolsCache.TryGetValue(ancient, out List<List<AncientOptionInfo>>? pools))
            return pools;
        pools = AncientOptionProbe.SlotPools(_player, ancient);
        _poolsCache[ancient] = pools;
        return pools;
    }

    private void BuildSlotSections(AncientEventModel ancient, List<List<AncientOptionInfo>> pools)
    {
        foreach (Node child in _infoSlots.GetChildren())
            child.QueueFreeSafely();
        _entryNodes.Clear();

        string slotLabel = ModUi.Loc("ROGUEUNLIKE.ANCIENT_SLOT.label", "Slot {0}");
        for (int slot = 0; slot < pools.Count; slot++)
        {
            Label header = MakeLabel(string.Format(slotLabel, slot + 1), 22, StsColors.gray);
            _infoSlots.AddChildSafely(header);
            foreach (AncientOptionInfo option in pools[slot])
                BuildOptionEntry(ancient, slot, option);
        }
        RefreshEntryStyles(ancient);
    }

    private void BuildOptionEntry(AncientEventModel ancient, int slot, AncientOptionInfo option)
    {
        var entry = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        entry.AddChildSafely(box);

        var titleRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        titleRow.AddThemeConstantOverride("separation", 8);
        box.AddChildSafely(titleRow);
        if (option.Relic != null)
        {
            var icon = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(36f, 36f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            try
            {
                icon.Texture = option.Relic.Icon;
            }
            catch (System.Exception) { }
            titleRow.AddChildSafely(icon);
        }
        Label title = MakeLabel(option.Title, 24, StsColors.cream);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChildSafely(title);

        if (option.Description.Length > 0)
        {
            MegaRichTextLabel desc = MakeRichText(20);
            desc.MouseFilter = MouseFilterEnum.Ignore;
            desc.Text = option.Description;
            box.AddChildSafely(desc);
        }

        entry.MouseEntered += () =>
        {
            if (option.Tips.Length > 0)
                NHoverTipSet.CreateAndShow(entry, option.Tips, HoverTip.GetHoverTipAlignment(entry))?.SetFollowOwner();
        };
        entry.MouseExited += () => NHoverTipSet.Remove(entry);
        entry.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                ToggleDesignation(ancient, slot, option);
        };

        _infoSlots.AddChildSafely(entry);
        _entryNodes.Add((slot, option, entry, title));
    }

    // Click to designate; click the same designation again to remove it; an option may be
    // designated in only ONE slot (vanilla never offers duplicates), so designating it
    // elsewhere moves it. Undesignated slots keep the vanilla roll at the event.
    private void ToggleDesignation(AncientEventModel ancient, int slot, AncientOptionInfo option)
    {
        if (!_designations.TryGetValue(ancient, out Dictionary<int, AncientOptionInfo>? chosen))
            _designations[ancient] = chosen = new Dictionary<int, AncientOptionInfo>();
        if (chosen.TryGetValue(slot, out AncientOptionInfo? existing) && existing.Identity == option.Identity)
        {
            chosen.Remove(slot);
        }
        else
        {
            foreach (int other in chosen.Where(kv => kv.Value.Identity == option.Identity)
                         .Select(kv => kv.Key).ToList())
                chosen.Remove(other);
            chosen[slot] = option;
        }
        RefreshEntryStyles(ancient);
    }

    private void RefreshEntryStyles(AncientEventModel ancient)
    {
        _designations.TryGetValue(ancient, out Dictionary<int, AncientOptionInfo>? chosen);
        foreach ((int slot, AncientOptionInfo option, PanelContainer panel, Label title) in _entryNodes)
        {
            bool designated = chosen != null
                && chosen.TryGetValue(slot, out AncientOptionInfo? sel)
                && sel.Identity == option.Identity;
            var style = new StyleBoxFlat
            {
                BgColor = designated ? RowBgSelected : RowBg,
                ContentMarginLeft = 10f,
                ContentMarginRight = 10f,
                ContentMarginTop = 8f,
                ContentMarginBottom = 8f,
            };
            panel.AddThemeStyleboxOverride("panel", style);
            title.AddThemeColorOverride("font_color", designated ? StsColors.gold : StsColors.cream);
        }
    }

    // Everything the Ancient can offer, one bbcode block: gold option titles above their
    // full descriptions. Canonical models lack run context, so every piece is harvested
    // defensively and drops alone. Cached — the roster is static for the picker's life.
    private string OptionsTextOf(AncientEventModel ancient)
    {
        if (_optionsTextCache.TryGetValue(ancient, out string? cached))
            return cached;
        var sb = new StringBuilder();
        sb.Append("[color=#efc851]").Append(ModUi.Loc("ROGUEUNLIKE.ANCIENT_OFFERS.label", "Can offer"))
          .Append(":[/color]\n\n");
        int shown = 0;
        try
        {
            foreach (EventOption option in ancient.AllPossibleOptions)
            {
                if (option.IsProceed)
                    continue;
                try
                {
                    string title = option.Title.GetFormattedText();
                    string description = option.Description.GetFormattedText();
                    sb.Append("[color=#efc851]").Append(title).Append("[/color]\n")
                      .Append(description).Append("\n\n");
                    shown++;
                }
                catch (System.Exception) { }
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Info($"[ancient picker] options unreadable for {ancient.Id}: {e.Message}");
        }
        string text = shown > 0 ? sb.ToString().TrimEnd() : SafeEpithet(ancient);
        _optionsTextCache[ancient] = text;
        return text;
    }

    private static string LockedTitle() => new LocString("card_library", "LOCKED.title").GetFormattedText();

    private static string SafeEpithet(AncientEventModel ancient)
    {
        try
        {
            return ancient.Epithet.GetFormattedText();
        }
        catch (System.Exception)
        {
            return "";
        }
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private void Select(AncientEventModel ancient)
    {
        if (_pending != null && _rows.TryGetValue(_pending, out var old))
        {
            old.bg.Color = RowBg;
            old.name.AddThemeColorOverride("font_color", StsColors.cream);
        }
        _pending = ancient;
        if (_rows.TryGetValue(ancient, out var picked))
        {
            picked.bg.Color = RowBgSelected;
            picked.name.AddThemeColorOverride("font_color", StsColors.gold);
        }
        _confirm.Enable();
        ShowInfo(ancient, unlocked: true, pickable: true);
    }

    // The deck-view back button, scavenged like the card pickers do — cancel = stay on map.
    private void BuildBackButton()
    {
        Control deck = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("screens/deck_view_screen"))
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        NButton? back = deck.FindChild("BackButton", recursive: true, owned: false) as NButton;
        Node? widget = ModUi.AncestorUnder(deck, back);
        if (back != null && widget != null)
        {
            widget.GetParent()?.RemoveChild(widget);
            this.AddChildSafely(widget);
            back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Finish(null)));
            back.Enable();
        }
        deck.QueueFreeSafely();
    }

    private void Finish(AncientPickResult? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
