using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #5: pick your Ancient. Every act starts on an Ancient node (the friendly
/// benefactor event — Neow, Darv, the Hive/Glory trios...). Vanilla rolls which Ancient
/// you meet with the run-seed RNG at run start; this seam intercepts the map-node click
/// and opens <see cref="ModAncientPickerUi"/> instead, then travels normally.
///
/// Selection pool = loot pool at current context (never expanded): exactly what
/// ActModel.GenerateRooms could have rolled for THIS act — the act's unlocked native
/// Ancients plus the shared-Ancient subset dealt to this act at run start. Act 1's pool
/// is always {Neow} in vanilla, so the picker never opens there — which also leaves
/// custom-modifier scenarios (Sealed Deck's pick-10-of-30 rides Neow's option
/// generation) untouched by construction.
///
/// Save-safety: the only write is RoomSet.Ancient — the same field GenerateRooms rolls,
/// serialized as AncientId in the vanilla run save — done BEFORE travel, so the room,
/// run history, metrics and reload all read the choice through pure vanilla paths.
///
/// Multiplayer: vanilla keeps ONE canonical event per room (EventRoom asserts it and
/// the save stores a single EventId; EventSynchronizer clones per-player copies of that
/// same model and only syncs option CHOICES). Per-player different Ancients is not
/// vanilla-reachable state and no sync channel exists for the identity — so this is
/// singleplayer-only; in multiplayer the node behaves vanilla.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
public static class AncientPickerPatch
{
    private static bool _passThrough;
    private static bool _pickerOpen;

    static bool Prefix(NMapScreen __instance, NMapPoint point)
    {
        if (_passThrough)
            return true;
        try
        {
            RunState? runState = __instance._runState;
            if (runState == null || runState.Players.Count > 1)
                return true; // MP: no vanilla channel for the ancient's identity — stay vanilla
            if (point.Point.PointType != MapPointType.Ancient || point.State != MapPointState.Travelable)
                return true;
            ActModel act = runState.Act;
            if (!act._rooms.HasAncient)
                return true; // first-run tutorial layout has no Ancient rolled
            List<AncientEventModel> valid = ValidPool(act, runState);
            if (valid.Count <= 1)
                return true; // nothing to pick: act 1 (Neow-only), pre-epoch, modifier scenarios
            if (_pickerOpen)
                return false;
            TaskHelper.RunSafely(PickFlow(__instance, point, act, runState, valid));
            return false;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient picker] intercept failed, vanilla travel: {e}");
            return true;
        }
    }

    // What GenerateRooms rolls _rooms.Ancient from (its exact pool expression): the
    // act's unlocked native Ancients plus this act's dealt shared subset.
    internal static List<AncientEventModel> ValidPool(ActModel act, RunState runState) =>
        act.GetUnlockedAncients(runState.UnlockState)
            .Concat(act._sharedAncientSubset ?? new List<AncientEventModel>())
            .Distinct()
            .ToList();

    private static async Task PickFlow(NMapScreen screen, NMapPoint point,
        ActModel act, RunState runState, List<AncientEventModel> valid)
    {
        _pickerOpen = true;
        try
        {
            ModAncientPickerUi ui = ModAncientPickerUi.Attach(screen, runState.UnlockState, valid, act.Ancient);
            AncientEventModel? choice = await ui.Result;
            if (choice == null || !GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                return; // cancelled (or the map died under us): stay on the map, node re-clickable
            act._rooms.Ancient = choice; // vanilla-reachable: the field GenerateRooms rolls, saved as AncientId
            MainFile.Logger.Info($"[ancient picker] act {runState.CurrentActIndex + 1} ancient set to {choice.Id}");
            _passThrough = true;
            try
            {
                screen.OnMapPointSelectedLocally(point); // vanilla vote/travel; room creation reads our pick
            }
            finally
            {
                _passThrough = false;
            }
        }
        finally
        {
            _pickerOpen = false;
        }
    }
}
