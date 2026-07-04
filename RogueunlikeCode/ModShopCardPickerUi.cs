using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// "Select a card" modal for the merchant (feature #4), presented with the same chrome
/// as the Grand Card Selection screen — all grid, sort, search and selection mechanics
/// live in <see cref="ModCardGridPicker"/>. This subclass is the modal host glue: a dim
/// backdrop over the shop, a task-based result (null = cancelled), the deck-view back
/// button as cancel. Pickable = what this shop slot could roll (passed in); the rest of
/// the slot's pool renders darkened/locked for context.
/// </summary>
public partial class ModShopCardPickerUi : ModCardGridPicker
{
    private readonly TaskCompletionSource<CardModel?> _tcs = new();

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
        var ui = new ModShopCardPickerUi { Name = "ModShopCardPickerUi" };
        ModUi.Mount(host, ui);
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
        SetupRoot();
        ModUi.AddModalDim(this); // modal: swallow clicks aimed at the shop

        _pickable.UnionWith(valid);
        _shown.AddRange(valid);
        AddDisplayExtras(player, new[] { pool });

        if (!ExtractGridAndConfirm())
            throw new System.InvalidOperationException("card grid donor scene unavailable");
        WireGrid("ModShopCardGrid");
        BuildDeckChrome(ModUi.SelectCardLabel, includeBackButton: true);
        BuildSearchBar();
    }

    protected override void OnConfirmPressed(CardModel pending) => Finish(pending);

    protected override void OnBackPressed() => Finish(null);

    private void Finish(CardModel? result)
    {
        _tcs.TrySetResult(result);
        this.QueueFreeSafely();
    }
}
