using Godot;
using MegaCrit.Sts2.Core.Assets;
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
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select an Ancient" modal over the map screen (feature #5). One row per Ancient in
/// the game — map-node icon, name, epithet, home act — in three states:
///   • pickable — this act's Ancient roll pool (unlocked natives + dealt shared subset);
///     full colour, clickable; the vanilla roll starts pre-selected so confirming
///     immediately keeps it;
///   • darkened — unlocked but not rollable at this act (another act's native, or a
///     shared Ancient dealt elsewhere this run); hover tips kept for reading;
///   • locked — progression (epoch) locked; blacked icon, hidden name.
/// Hovering a row shows the Ancient's epithet plus every option it can offer
/// (<see cref="AncientEventModel.AllPossibleOptions"/>), so "what does this one grant"
/// is readable before committing. No search bar: the roster is eight entries.
/// Checkmark confirms, back button cancels (stay on the map, node re-clickable).
/// </summary>
public partial class ModAncientPickerUi : Control
{
    // ponytail: fixed panel metrics, tuned by eye; revisit if MegaCrit adds many Ancients.
    private const float RowWidth = 900f;
    private const float RowHeight = 96f;
    private const float RowGap = 10f;
    private const float IconSize = 76f;
    private const int MaxOptionTips = 12; // hover-tip column sanity cap; drops are logged

    private static readonly Color RowBg = new(0f, 0f, 0f, 0.35f);      // AncientEventModel.ButtonColor
    private static readonly Color RowBgHover = new(1f, 1f, 1f, 0.12f);
    private static readonly Color RowBgSelected = new(0.94f, 0.78f, 0.32f, 0.25f); // gold tint

    private readonly TaskCompletionSource<AncientEventModel?> _tcs = new();
    private readonly Dictionary<AncientEventModel, (Control row, ColorRect bg, Label name)> _rows = new();
    private NConfirmButton _confirm = null!;
    private AncientEventModel? _pending;

    /// <summary>Resolves with the confirmed Ancient; null = cancelled (or torn down).</summary>
    public Task<AncientEventModel?> Result => _tcs.Task;

    /// <summary>
    /// Shows the picker as a child of the map screen (dies with it). <paramref name="valid"/>
    /// is this act's roll pool; <paramref name="current"/> the vanilla roll, pre-selected.
    /// </summary>
    public static ModAncientPickerUi Attach(Control host, UnlockState unlock,
        List<AncientEventModel> valid, AncientEventModel current)
    {
        var ui = new ModAncientPickerUi { Name = "ModAncientPickerUi" };
        host.AddChildSafely(ui); // NOT ModUi.Mount: the map screen itself is the host that must own us
        try
        {
            ui.Build(unlock, valid, current);
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

    private void Build(UnlockState unlock, List<AncientEventModel> valid, AncientEventModel current)
    {
        ModUi.SetupPickerRoot(this);
        ModUi.AddModalDim(this);

        // The pool and _rooms.Ancient are canonical ModelDb instances (GenerateRooms
        // rolls from GetUnlockedAncients; EventRoom asserts canonicality) — so the
        // roster, the valid set and the default pick all share one instance space.
        var validSet = valid.ToHashSet();
        // Unlocked anywhere = not progression-locked. Everything else renders locked.
        var unlockedSet = ModelDb.Acts.SelectMany(a => a.GetUnlockedAncients(unlock))
            .Concat(unlock.SharedAncients).ToHashSet();

        Label header = MakeLabel(ModUi.SelectAncientLabel, 44, StsColors.cream);
        this.AddChildSafely(header);
        header.SetAnchorsPreset(LayoutPreset.TopWide);
        header.HorizontalAlignment = HorizontalAlignment.Center;
        header.OffsetTop = 44f;
        header.OffsetBottom = 104f;

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

        // Centre the panel; cap its height so a grown roster scrolls instead of clipping.
        float panelH = Mathf.Min(roster.Count * (RowHeight + RowGap), 860f);
        scroll.CustomMinimumSize = new Vector2(RowWidth, panelH);
        scroll.SetAnchorsPreset(LayoutPreset.Center);
        scroll.OffsetLeft = -RowWidth / 2f;
        scroll.OffsetRight = RowWidth / 2f;
        scroll.OffsetTop = -panelH / 2f + 30f; // header clearance
        scroll.OffsetBottom = panelH / 2f + 30f;

        _confirm = ModUi.ExtractConfirmButton(this, () =>
        {
            if (_pending != null)
                Finish(_pending);
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

        string lockedTitle = new LocString("card_library", "LOCKED.title").GetFormattedText();
        Label name = MakeLabel(unlocked ? ancient.Title.GetFormattedText() : lockedTitle,
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

        if (!unlocked)
            icon.SelfModulate = StsColors.ninetyPercentBlack;
        else if (!pickable)
            icon.SelfModulate = StsColors.ninetyPercentBlack; // darkened: exists, can't spawn here

        List<IHoverTip> tips = unlocked ? BuildTips(ancient) : new List<IHoverTip>
        {
            new HoverTip(new LocString("card_library", "LOCKED.title"), new LocString("card_library", "LOCKED.description")),
        };
        row.MouseEntered += () =>
        {
            if (!pickable || _pending != ancient)
                bg.Color = RowBgHover;
            if (tips.Count > 0)
                NHoverTipSet.CreateAndShow(row, tips);
        };
        row.MouseExited += () =>
        {
            bg.Color = pickable && _pending == ancient ? RowBgSelected : RowBg;
            NHoverTipSet.Remove(row);
        };

        if (pickable)
            row.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    Select(ancient);
            };

        into.AddChildSafely(row);
        _rows[ancient] = (row, bg, name);
    }

    // The Ancient's epithet plus one tip per option it can possibly offer — the
    // "drastically different options" the pick is really about. Canonical models lack
    // run context, so every piece is harvested defensively and drops alone.
    private static List<IHoverTip> BuildTips(AncientEventModel ancient)
    {
        var tips = new List<IHoverTip>();
        try
        {
            tips.Add(new HoverTip(ancient.Title, ancient.Epithet));
        }
        catch (System.Exception) { }
        int dropped = 0;
        try
        {
            foreach (EventOption option in ancient.AllPossibleOptions)
            {
                if (option.IsProceed)
                    continue;
                if (tips.Count > MaxOptionTips)
                {
                    dropped++;
                    continue;
                }
                try
                {
                    tips.Add(new HoverTip(option.Title, option.Description));
                }
                catch (System.Exception) { }
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Info($"[ancient picker] options unreadable for {ancient.Id}: {e.Message}");
        }
        if (dropped > 0)
            MainFile.Logger.Info($"[ancient picker] {ancient.Id}: {dropped} option tips over the cap not shown");
        return tips;
    }

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

    private void Finish(AncientEventModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
