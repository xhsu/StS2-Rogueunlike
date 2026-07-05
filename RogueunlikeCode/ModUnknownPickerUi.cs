using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Choose Your Fate" modal over the map screen (feature #6). Left: one row per ? room
/// category — Let Fate Decide (the vanilla roll, peeked and pre-selected), Enemy, Elite,
/// Treasure, Merchant, Event — pickable when its final effective chance &gt; 0 right now,
/// darkened with the reason otherwise. Right: the selected category's content —
///   • Enemy/Elite: the act's dealt encounter table (every member pickable; the act's
///     other encounters render darkened for context); unseen encounters carry the star;
///   • Event: the act's dealt event queue — valid-now entries pickable, visited/blocked
///     entries darkened with the reason, epoch-locked events locked; unseen events carry
///     the star (browsing, clicking and voting never mark seen — only walking in does,
///     via vanilla's own history-driven marking);
///   • Fate/Treasure/Merchant: a short explainer (chest and shop contents are already
///     full picks in-room via features #3.1/#4).
/// A search bar atop the content panel filters the list rows by title — results are
/// pickable-only (user rule, 2026-07-03); the query persists across category switches.
/// In multiplayer a live strip shows every player's current vote; plurality wins, ties
/// break to the earliest vote (see UnknownPickSync). Checkmark confirms, back cancels.
/// </summary>
public partial class ModUnknownPickerUi : Control
{
    // ponytail: fixed layout metrics, tuned by eye like the Ancient picker's.
    private const float CatWidth = 470f;
    private const float CatRowHeight = 76f;
    private const float RowGap = 10f;
    private const float PanelWidth = 880f;
    private const float PanelGap = 24f;
    private const float PanelPad = 20f;
    private const float GroupHeight = 720f;
    private const float ContentRowHeight = 56f;
    private const float SearchGap = 16f; // search bar bottom → content list top

    private static readonly Color RowBg = new(0f, 0f, 0f, 0.35f);
    private static readonly Color RowBgHover = new(1f, 1f, 1f, 0.12f);
    private static readonly Color RowBgSelected = new(0.94f, 0.78f, 0.32f, 0.25f);
    private static readonly Color PanelBg = new(0f, 0f, 0f, 0.55f);

    private readonly TaskCompletionSource<UnknownPickResult?> _tcs = new();
    private readonly Dictionary<RoomType, (Control row, ColorRect bg, Label name, bool pickable)> _catRows = new();
    private readonly Dictionary<RoomType, AbstractModel?> _contentChoice = new();
    private readonly List<(AbstractModel model, PanelContainer panel, Label title, bool pickable, string search)> _contentRows = new();

    private RunState _state = null!;
    private MapCoord _coord;
    private Dictionary<RoomType, float> _chances = null!;
    private (RoomType Category, AbstractModel? Content) _peek;
    private RoomType? _pendingCategory; // null = fate
    private bool _fateSelected = true;

    private NConfirmButton _confirm = null!;
    private ScrollContainer _contentScroll = null!;
    private VBoxContainer _contentList = null!;
    private MegaRichTextLabel _contentInfo = null!;
    private NSearchBar? _searchBar;
    private string _query = "";
    private Label _votesLabel = null!;

    /// <summary>Resolves with the confirmed vote; null = cancelled (or torn down).</summary>
    public Task<UnknownPickResult?> Result => _tcs.Task;

    public static ModUnknownPickerUi Attach(Control host, RunState state, Player me,
        MapCoord coord, Dictionary<RoomType, float> chances)
    {
        var ui = new ModUnknownPickerUi { Name = "ModUnknownPickerUi" };
        host.AddChildSafely(ui); // child of the map screen: dies with it
        try
        {
            ui.Build(state, coord, chances);
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
        UnknownPickSync.Changed -= RefreshVotes;
        _tcs.TrySetResult(null);
    }

    private void Build(RunState state, MapCoord coord, Dictionary<RoomType, float> chances)
    {
        _state = state;
        _coord = coord;
        _chances = chances;
        _peek = UnknownPools.Peek(state);
        ModUi.SetupPickerRoot(this);
        ModUi.AddModalDim(this);

        float groupHalf = (CatWidth + PanelGap + PanelWidth) / 2f;

        // Left: category rows.
        var catList = new VBoxContainer { Name = "UnknownCategories" };
        catList.AddThemeConstantOverride("separation", (int)RowGap);
        this.AddChildSafely(catList);
        catList.SetAnchorsPreset(LayoutPreset.Center);
        catList.OffsetLeft = -groupHalf;
        catList.OffsetRight = -groupHalf + CatWidth;
        catList.OffsetTop = -GroupHeight / 2f + 30f;
        catList.OffsetBottom = GroupHeight / 2f + 30f;

        BuildFateRow(catList);
        foreach (RoomType type in new[] { RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop, RoomType.Event })
            BuildCategoryRow(catList, type);

        // Right: content panel (list for enemy/elite/event, explainer otherwise).
        BuildContentPanel(groupHalf);

        // Bottom banner: title, then the live vote strip (MP only).
        Label title = MakeLabel(ModUi.SelectUnknownLabel, 40, StsColors.cream);
        this.AddChildSafely(title);
        title.SetAnchorsPreset(LayoutPreset.Center);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.OffsetLeft = -groupHalf;
        title.OffsetRight = groupHalf;
        title.OffsetTop = GroupHeight / 2f + 40f;
        title.OffsetBottom = GroupHeight / 2f + 96f;

        _votesLabel = MakeLabel("", 24, StsColors.gray);
        this.AddChildSafely(_votesLabel);
        _votesLabel.SetAnchorsPreset(LayoutPreset.Center);
        _votesLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _votesLabel.OffsetLeft = -groupHalf;
        _votesLabel.OffsetRight = groupHalf;
        _votesLabel.OffsetTop = GroupHeight / 2f + 100f;
        _votesLabel.OffsetBottom = GroupHeight / 2f + 136f;
        if (_state.Players.Count > 1)
        {
            UnknownPickSync.Changed += RefreshVotes;
            RefreshVotes();
        }

        _confirm = ModUi.ExtractConfirmButton(this, ConfirmPick);
        BuildBackButton();

        SelectFate(); // the vanilla roll is the default vote, matching the other pickers
    }

    private void ConfirmPick()
    {
        if (_fateSelected)
        {
            Finish(new UnknownPickResult { IsFate = true });
            return;
        }
        if (_pendingCategory is not RoomType category)
            return;
        _contentChoice.TryGetValue(category, out AbstractModel? content);
        if (NeedsContent(category) && content == null)
            return; // confirm stays disabled in this state anyway
        Finish(new UnknownPickResult
        {
            IsFate = false,
            Category = category,
            Model = content?.Id,
        });
    }

    private static bool NeedsContent(RoomType category) =>
        category is RoomType.Monster or RoomType.Elite or RoomType.Event;

    // ---- left column ----

    private void BuildFateRow(VBoxContainer into)
    {
        string peekText = DescribeOutcome(_peek.Category, _peek.Content);
        Control row = MakeCategoryRow(into,
            ModUi.Loc("ROGUEUNLIKE.UNKNOWN_FATE.label", "Let Fate Decide"),
            string.Format(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_PEEK.label", "Fate holds: {0}"), peekText),
            pickable: true,
            out ColorRect bg, out Label name);
        row.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                SelectFate();
        };
        _catRows[RoomType.Unassigned] = (row, bg, name, true); // Unassigned slot = the fate row
    }

    private void BuildCategoryRow(VBoxContainer into, RoomType type)
    {
        bool pickable = _chances.GetValueOrDefault(type) > 0f;
        string subtitle = pickable
            ? string.Format(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_CHANCE.label", "{0}% chance"),
                (_chances[type] * 100f).ToString("0.#"))
            : ModUi.Loc("ROGUEUNLIKE.UNKNOWN_NOT_HERE.label", "Cannot occur here");
        Control row = MakeCategoryRow(into, CategoryName(type), subtitle, pickable,
            out ColorRect bg, out Label name);
        if (pickable)
            row.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    SelectCategory(type);
            };
        _catRows[type] = (row, bg, name, pickable);
    }

    private Control MakeCategoryRow(VBoxContainer into, string title, string subtitle,
        bool pickable, out ColorRect bg, out Label name)
    {
        var row = new Control
        {
            CustomMinimumSize = new Vector2(CatWidth, CatRowHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        ColorRect rowBg = new() { Color = RowBg, MouseFilter = MouseFilterEnum.Ignore };
        row.AddChildSafely(rowBg);
        rowBg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        Label rowName = MakeLabel(title, 30, pickable ? StsColors.cream : StsColors.gray);
        rowName.Position = new Vector2(18f, 8f);
        row.AddChildSafely(rowName);

        Label sub = MakeLabel(subtitle, 20, StsColors.gray);
        sub.Position = new Vector2(18f, 44f);
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        sub.CustomMinimumSize = new Vector2(CatWidth - 36f, 0f);
        row.AddChildSafely(sub);

        ColorRect capturedBg = rowBg;
        row.MouseEntered += () => { if (capturedBg.Color == RowBg) capturedBg.Color = RowBgHover; };
        row.MouseExited += () => { if (capturedBg.Color == RowBgHover) capturedBg.Color = RowBg; };

        into.AddChildSafely(row);
        bg = rowBg;
        name = rowName;
        return row;
    }

    private void SelectFate()
    {
        _fateSelected = true;
        _pendingCategory = null;
        RefreshCategoryStyles();
        ShowExplainer(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_FATE.info",
            "Keep the vanilla roll: the room type and its contents are decided by the game's own dice.")
            + "\n\n" + string.Format(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_PEEK.label", "Fate holds: {0}"),
                DescribeOutcome(_peek.Category, _peek.Content)));
        _confirm.Enable();
    }

    private void SelectCategory(RoomType type)
    {
        _fateSelected = false;
        _pendingCategory = type;
        RefreshCategoryStyles();
        switch (type)
        {
        case RoomType.Monster:
        case RoomType.Elite:
            ShowEncounterList(type);
            break;
        case RoomType.Event:
            ShowEventList();
            break;
        case RoomType.Treasure:
            ShowExplainer(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_TREASURE.info",
                "A treasure chest. Its contents open as a full pick when you arrive."));
            break;
        case RoomType.Shop:
            ShowExplainer(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_SHOP.info",
                "A merchant sets up shop. Each slot's stock is yours to assign when you arrive."));
            break;
        }
        RefreshConfirm();
    }

    private void RefreshCategoryStyles()
    {
        foreach (KeyValuePair<RoomType, (Control row, ColorRect bg, Label name, bool pickable)> kv in _catRows)
        {
            bool selected = kv.Key == RoomType.Unassigned
                ? _fateSelected
                : !_fateSelected && _pendingCategory == kv.Key;
            kv.Value.bg.Color = selected ? RowBgSelected : RowBg;
            kv.Value.name.AddThemeColorOverride("font_color",
                selected ? StsColors.gold : kv.Value.pickable ? StsColors.cream : StsColors.gray);
        }
    }

    private void RefreshConfirm()
    {
        bool ready = _fateSelected
            || (_pendingCategory is RoomType cat
                && (!NeedsContent(cat)
                    || (_contentChoice.TryGetValue(cat, out AbstractModel? chosen) && chosen != null)));
        if (ready)
            _confirm.Enable();
        else
            _confirm.Disable();
    }

    // ---- right panel ----

    private void BuildContentPanel(float groupHalf)
    {
        var panel = new Control { Name = "UnknownContentPanel" };
        this.AddChildSafely(panel);
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = groupHalf - PanelWidth;
        panel.OffsetRight = groupHalf;
        panel.OffsetTop = -GroupHeight / 2f + 30f;
        panel.OffsetBottom = GroupHeight / 2f + 30f;

        var bg = new ColorRect { Color = PanelBg, MouseFilter = MouseFilterEnum.Ignore };
        panel.AddChildSafely(bg);
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _contentScroll = new ScrollContainer
        {
            Name = "UnknownContentScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        panel.AddChildSafely(_contentScroll);
        _contentScroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _contentScroll.OffsetLeft = PanelPad;
        _contentScroll.OffsetRight = -PanelPad;
        _contentScroll.OffsetTop = PanelPad;
        _contentScroll.OffsetBottom = -PanelPad;

        _contentList = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(PanelWidth - 2f * PanelPad - 12f, 0f), // -12: scrollbar clearance
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _contentList.AddThemeConstantOverride("separation", 8);
        _contentScroll.AddChildSafely(_contentList);

        _contentInfo = MakeRichText(26);
        _contentInfo.MouseFilter = MouseFilterEnum.Ignore;
        panel.AddChildSafely(_contentInfo);
        _contentInfo.SetAnchorsPreset(LayoutPreset.FullRect);
        _contentInfo.OffsetLeft = PanelPad;
        _contentInfo.OffsetRight = -PanelPad;
        _contentInfo.OffsetTop = PanelPad;
        _contentInfo.OffsetBottom = -PanelPad;

        // Search bar pinned atop the panel (the Card Library widget, as everywhere else);
        // the scrolling list starts below it. Shown only with a content list — explainer
        // categories have nothing to filter.
        try
        {
            _searchBar = ModSearch.CreateBar(panel);
            if (_searchBar != null)
            {
                ModSearch.PlaceCentered(_searchBar, PanelPad);
                _contentScroll.OffsetTop = PanelPad + _searchBar.CustomMinimumSize.Y + SearchGap;
                _searchBar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(OnQueryChanged));
                _searchBar.Visible = false;
            }
        }
        catch (System.Exception e)
        {
            _searchBar = null;
            MainFile.Logger.Error($"[search] unknown-picker bar failed (picker still works without it): {e}");
        }
    }

    private void ShowExplainer(string text)
    {
        _contentScroll.Visible = false;
        _contentInfo.Visible = true;
        _contentInfo.Text = text;
        if (_searchBar != null)
            _searchBar.Visible = false;
    }

    private void ClearContentList()
    {
        foreach (Node child in _contentList.GetChildren())
            child.QueueFreeSafely();
        _contentRows.Clear();
        _contentScroll.Visible = true;
        _contentInfo.Visible = false;
        if (_searchBar != null)
            _searchBar.Visible = true;
    }

    private void ShowEncounterList(RoomType type)
    {
        ClearContentList();
        List<EncounterModel> pool = UnknownPools.EncounterPool(_state, type);
        // Compendium context: the act's other same-tier encounters, not dealt this run.
        // Diff by ID: after a reload the dealt lists hold ModelDb canonicals while the
        // act's encounter cache may hold different instances.
        var poolIds = pool.Select(e => e.Id).ToHashSet();
        IEnumerable<EncounterModel> tier = type == RoomType.Elite
            ? _state.Act.AllEliteEncounters
            : _state.Act.AllWeakEncounters.Concat(_state.Act.AllRegularEncounters);
        List<EncounterModel> extras = tier.Where(e => !poolIds.Contains(e.Id)).Distinct().ToList();

        foreach (EncounterModel encounter in pool.OrderBy(TitleOf))
            AddContentRow(encounter, TitleOf(encounter), pickable: true, reason: "",
                star: !_state.UnlockState.HasSeenEncounter(encounter));
        foreach (EncounterModel encounter in extras.OrderBy(TitleOf))
            AddContentRow(encounter, TitleOf(encounter), pickable: false,
                reason: ModUi.Loc("ROGUEUNLIKE.UNKNOWN_NOT_HERE.label", "Cannot occur here"),
                star: !_state.UnlockState.HasSeenEncounter(encounter));
        ApplyFilter();
        RefreshContentStyles();
    }

    private void ShowEventList()
    {
        ClearContentList();
        List<EventModel> dealt = _state.Act._rooms.events.Distinct().ToList();
        var pool = UnknownPools.EventPool(_state).ToHashSet();
        // Epoch-hidden events were filtered out of the dealt queue at act generation.
        // Diff by ID (see the encounter note above).
        var dealtIds = dealt.Select(e => e.Id).ToHashSet();
        List<EventModel> locked = _state.Act.AllEvents.Concat(ModelDb.AllSharedEvents)
            .Distinct().Where(e => !dealtIds.Contains(e.Id)).ToList();
        IReadOnlySet<ModelId> discovered = SaveManager.Instance.Progress.DiscoveredEvents;

        foreach (EventModel ev in dealt.Where(pool.Contains).OrderBy(TitleOf))
            AddContentRow(ev, TitleOf(ev), pickable: true, reason: "",
                star: !discovered.Contains(ev.Id));
        foreach (EventModel ev in dealt.Where(e => !pool.Contains(e)).OrderBy(TitleOf))
        {
            string reason = _state.VisitedEventIds.Contains(ev.Id)
                ? ModUi.Loc("ROGUEUNLIKE.UNKNOWN_VISITED.label", "Already encountered this run")
                : ModUi.Loc("ROGUEUNLIKE.UNKNOWN_CONDITION.label", "Requirements not met");
            AddContentRow(ev, TitleOf(ev), pickable: false, reason: reason,
                star: !discovered.Contains(ev.Id));
        }
        foreach (EventModel ev in locked)
            AddContentRow(ev, LockedTitle(), pickable: false, reason: "", star: false);
        ApplyFilter();
        RefreshContentStyles();
    }

    // Search results are pickable-only — searching means looking for something to PICK;
    // the darkened/locked context rows only clutter results (user rule, 2026-07-03).
    private void OnQueryChanged(string query)
    {
        _query = ModSearch.Canon(query);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach ((_, PanelContainer panel, _, bool pickable, string search) in _contentRows)
            panel.Visible = _query.Length == 0 || (pickable && ModSearch.Matches(search, _query));
        // The filter hid the pending pick: unpick it — never confirm something invisible.
        if (_pendingCategory is RoomType cat
            && _contentChoice.GetValueOrDefault(cat) is AbstractModel chosen
            && _contentRows.Any(r => r.model == chosen && !r.panel.Visible))
        {
            _contentChoice[cat] = null;
            RefreshContentStyles();
            RefreshConfirm();
        }
    }

    private void AddContentRow(AbstractModel model, string title, bool pickable, string reason, bool star)
    {
        var entry = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(0f, ContentRowHeight),
        };
        var inner = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        inner.AddThemeConstantOverride("separation", 12);
        entry.AddChildSafely(inner);

        Label titleLabel = MakeLabel(title, 26, pickable ? StsColors.cream : StsColors.gray);
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.VerticalAlignment = VerticalAlignment.Center;
        inner.AddChildSafely(titleLabel);

        if (reason.Length > 0)
        {
            Label reasonLabel = MakeLabel(reason, 20, StsColors.gray);
            reasonLabel.VerticalAlignment = VerticalAlignment.Center;
            inner.AddChildSafely(reasonLabel);
        }

        if (star)
            ModUnseenFx.AddStar(entry);

        if (pickable)
        {
            entry.MouseEntered += () => entry.SelfModulate = new Color(1.15f, 1.15f, 1.15f);
            entry.MouseExited += () => entry.SelfModulate = Colors.White;
            entry.GuiInput += ev =>
            {
                if (ev is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    return;
                if (_pendingCategory is not RoomType cat)
                    return;
                _contentChoice[cat] = _contentChoice.TryGetValue(cat, out AbstractModel? old) && old == model
                    ? null   // click again to unpick
                    : model;
                RefreshContentStyles();
                RefreshConfirm();
            };
        }

        _contentList.AddChildSafely(entry);
        _contentRows.Add((model, entry, titleLabel, pickable, ModSearch.Canon(title)));
    }

    private void RefreshContentStyles()
    {
        AbstractModel? chosen = _pendingCategory is RoomType cat
            ? _contentChoice.GetValueOrDefault(cat)
            : null;
        foreach ((AbstractModel model, PanelContainer panel, Label title, bool pickable, _) in _contentRows)
        {
            bool selected = chosen != null && chosen == model;
            var style = new StyleBoxFlat
            {
                BgColor = selected ? RowBgSelected : RowBg,
                ContentMarginLeft = 12f,
                ContentMarginRight = 12f,
                ContentMarginTop = 6f,
                ContentMarginBottom = 6f,
            };
            panel.AddThemeStyleboxOverride("panel", style);
            title.AddThemeColorOverride("font_color",
                selected ? StsColors.gold : pickable ? StsColors.cream : StsColors.gray);
        }
    }

    // ---- live vote strip ----

    private void RefreshVotes()
    {
        if (!GodotObject.IsInstanceValid(this) || _votesLabel == null)
            return;
        try
        {
            IReadOnlyList<UnknownPickSync.Vote> votes = UnknownPickSync.VotesFor(_coord);
            var sb = new StringBuilder();
            sb.Append(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_VOTES.label", "Votes"));
            sb.Append(":  ");
            bool first = true;
            foreach (Player player in _state.Players)
            {
                if (!first)
                    sb.Append("   ·   ");
                first = false;
                sb.Append(PlayerName(player.NetId)).Append(": ");
                UnknownPickSync.Vote? vote = votes.FirstOrDefault(v => v.NetId == player.NetId);
                if (vote == null)
                    sb.Append(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_WAITING.label", "deciding…"));
                else if (vote.IsFate)
                    sb.Append(ModUi.Loc("ROGUEUNLIKE.UNKNOWN_FATE.label", "Let Fate Decide"));
                else
                    sb.Append(DescribeOutcome(vote.Category, vote.Model != null
                        ? ModelDb.GetByIdOrNull<AbstractModel>(vote.Model)
                        : null));
            }
            _votesLabel.Text = sb.ToString();
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unknown picker] vote strip refresh failed (cosmetic): {e.Message}");
        }
    }

    private static string PlayerName(ulong netId)
    {
        try
        {
            return PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, netId);
        }
        catch (System.Exception)
        {
            return $"P{netId}";
        }
    }

    // ---- helpers ----

    private string DescribeOutcome(RoomType category, AbstractModel? content)
    {
        string name = CategoryName(category);
        string? contentTitle = content != null ? TitleOf(content) : null;
        return contentTitle != null ? $"{name} — {contentTitle}" : name;
    }

    private static string CategoryName(RoomType type) => type switch
    {
        RoomType.Monster => ModUi.Loc("ROGUEUNLIKE.ROOMTYPE_MONSTER.label", "Enemy"),
        RoomType.Elite => ModUi.Loc("ROGUEUNLIKE.ROOMTYPE_ELITE.label", "Elite"),
        RoomType.Treasure => ModUi.Loc("ROGUEUNLIKE.ROOMTYPE_TREASURE.label", "Treasure"),
        RoomType.Shop => ModUi.Loc("ROGUEUNLIKE.ROOMTYPE_SHOP.label", "Merchant"),
        RoomType.Event => ModUi.Loc("ROGUEUNLIKE.ROOMTYPE_EVENT.label", "Event"),
        _ => type.ToString(),
    };

    private static string TitleOf(AbstractModel model)
    {
        try
        {
            return model switch
            {
                EventModel ev => ev.Title.GetFormattedText(),
                EncounterModel enc => enc.Title.GetFormattedText(),
                _ => model.Id.Entry,
            };
        }
        catch (System.Exception)
        {
            return model.Id.Entry;
        }
    }

    private static string LockedTitle() => new LocString("card_library", "LOCKED.title").GetFormattedText();

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    // Same MegaRichTextLabel construction as the Ancient picker (see the notes there:
    // the _Ready normal_font assert and the AutoSize inflation).
    private static MegaRichTextLabel MakeRichText(int fontSize)
    {
        var label = new MegaRichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont,
            label.GetThemeFont(ThemeConstants.RichTextLabel.NormalFont, "RichTextLabel"));
        label.AutoSizeEnabled = false;
        label.SetFontSize(fontSize);
        return label;
    }

    // The deck-view back button, scavenged like every other picker — cancel = stay on map.
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

    private void Finish(UnknownPickResult? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
