using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// THE modal card picker — the single host for every modal card-pick flow (the reward
/// screen keeps its own host, <see cref="ModRewardScreenUi"/>, because it replaces an
/// existing screen's body instead of opening a modal). All grid/sort/search/selection
/// mechanics live in <see cref="ModCardGridPicker"/>; this class is mount + result glue
/// with two mount modes:
///   • <see cref="AttachToScreen"/> (feature #4, merchant slots): child of the merchant/
///     rewards screen via <see cref="ModUi.Mount"/> + the mod's modal dim — covers that
///     screen and dies with it.
///   • <see cref="AttachAsOverlay"/> (feature #1's off-screen choose-a-card seams):
///     pushed on the game's own <see cref="NOverlayStack"/> as an IOverlayScreen with
///     the shared backstop — the EXACT layering of the vanilla
///     NChooseACardSelectionScreen it stands in for: correct dim, input isolation,
///     stacking, and the same NetScreenType.CardSelection reported to MP peers. Never
///     mount a picker at the raw tree root: that skips vanilla's screen layering
///     entirely and renders with no backstop (the "black seam" report, 2026-07-06).
/// Back button = cancel (shop: the slot stays assignable) / skip (choose flows: shown
/// only when the vanilla call site passes canSkip — skipping there is a VANILLA-legal
/// outcome, `if (chosenCard != null)` guards every add; user-confirmed 2026-07-06).
/// Null result = cancelled/skipped (or torn down).
/// </summary>
public partial class ModCardPickModal : ModCardGridPicker, IOverlayScreen
{
    private readonly TaskCompletionSource<CardModel?> _tcs = new();
    private bool _overlay;

    /// <summary>Resolves with the confirmed card; null = cancelled/skipped (or torn down).</summary>
    public Task<CardModel?> Result => _tcs.Task;

    // ---- IOverlayScreen: minimal vanilla-shaped lifecycle (only used in overlay mode) ----

    public NetScreenType ScreenType => NetScreenType.CardSelection; // what vanilla's choose screen reports
    public bool UseSharedBackstop => true;
    public Control? DefaultFocusedControl => _confirm; // null before Build; the stack tolerates it

    public void AfterOverlayOpened() { }

    public void AfterOverlayClosed() => this.QueueFreeSafely();

    public void AfterOverlayShown() => Visible = true;

    public void AfterOverlayHidden() => Visible = false;

    /// <summary>Merchant-slot picker: modal over the shop; cancel = the slot keeps its roll.</summary>
    public static ModCardPickModal AttachToScreen(Node host, Player player,
        HashSet<CardModel> valid, CardPoolModel pool)
    {
        var ui = new ModCardPickModal { Name = "ModCardPickModal" };
        ModUi.Mount(host, ui);
        try
        {
            ui.Build(player, valid, new[] { pool }, dim: true, backButton: true);
        }
        catch
        {
            ui.QueueFreeSafely();
            throw;
        }
        return ui;
    }

    /// <summary>Choose-a-card picker with vanilla overlay-stack layering. Null result = skip.</summary>
    public static ModCardPickModal AttachAsOverlay(Player player, List<CardModel> pickable,
        List<CardPoolModel> pools, bool canSkip)
    {
        NOverlayStack stack = NOverlayStack.Instance
            ?? throw new System.InvalidOperationException("NOverlayStack unavailable");
        var ui = new ModCardPickModal { Name = "ModCardPickModal", _overlay = true };
        stack.Push(ui); // enters the tree here (the search bar needs _Ready wiring) + backstop up
        try
        {
            ui.Build(player, pickable, pools, dim: false, backButton: canSkip);
        }
        catch
        {
            ui.CloseModal(); // through the stack, so the backstop and screen below are restored
            throw;
        }
        return ui;
    }

    public override void _ExitTree()
    {
        _tcs.TrySetResult(null);
    }

    private void Build(Player player, IEnumerable<CardModel> pickable,
        IEnumerable<CardPoolModel> pools, bool dim, bool backButton)
    {
        SetupRoot();
        if (dim)
            ModUi.AddModalDim(this); // in-screen mode only; overlay mode has the vanilla backstop
        _pickable.UnionWith(pickable);
        _shown.AddRange(pickable);
        AddDisplayExtras(player, pools); // no named pools (custom-pool origins) = no extras
        if (!ExtractGridAndConfirm())
            throw new System.InvalidOperationException("card grid donor scene unavailable");
        WireGrid("ModCardPickGrid");
        BuildDeckChrome(ModUi.SelectCardLabel, includeBackButton: backButton);
        BuildSearchBar();
    }

    protected override void OnConfirmPressed(CardModel pending) => Finish(pending);

    protected override void OnBackPressed() => Finish(null);

    private void Finish(CardModel? result)
    {
        _tcs.TrySetResult(result);
        CloseModal();
    }

    // Overlay screens must leave through the stack (Remove → AfterOverlayClosed → free)
    // so the backstop fades and the screen underneath is re-shown; in-screen mode frees
    // directly, exactly like before.
    private void CloseModal()
    {
        if (_overlay && NOverlayStack.Instance is { } stack)
            stack.Remove(this);
        else
            this.QueueFreeSafely();
    }
}
