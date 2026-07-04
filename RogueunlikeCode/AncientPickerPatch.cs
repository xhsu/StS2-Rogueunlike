using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
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
/// Feature #5: pick your Ancient. Vanilla rolls the act-start Ancient at run start; this
/// seam intercepts the map-node click and opens <see cref="ModAncientPickerUi"/>, then
/// travels normally. (The RUN-START act auto-enters with no click — feature #5.2,
/// AncientStartDesignatePatch, covers it.)
///
/// Pool = exactly what ActModel.GenerateRooms could roll for THIS act (unlocked natives
/// + dealt shared subset; never expanded). Opens only on a real choice: several
/// candidates, or one whose dialogue slots the probe measures as designatable — no
/// Ancient names hardcoded; modifier-driven chains probe to no-variation, stay vanilla.
///
/// SP: the only write is RoomSet.Ancient — the field GenerateRooms rolls, saved as
/// AncientId — done before travel, so everything downstream reads the pick through
/// vanilla paths. MP: pick + designations broadcast via AncientPickConsoleCmd ahead of
/// the travel vote (AncientPickSyncCmd.cs); saved state untouched, so a mid-event
/// reload degrades to the vanilla roll.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
public static class AncientPickerPatch
{
    private static bool _passThrough;
    private static bool _pickerOpen;

    /// <summary>True while the Ancient picker modal is up (see MapInputGatePatch).</summary>
    internal static bool PickerOpen => _pickerOpen;

    static bool Prefix(NMapScreen __instance, NMapPoint point)
    {
        if (_passThrough)
            return true;
        try
        {
            RunState? runState = __instance._runState;
            if (runState == null)
                return true;
            if (point.Point.PointType != MapPointType.Ancient || point.State != MapPointState.Travelable)
                return true;
            ActModel act = runState.Act;
            if (!act._rooms.HasAncient)
                return true; // first-run tutorial layout has no Ancient rolled
            if (!ModWireCheck.SyncReady(runState))
                return true; // real MP without a verified mod handshake: vanilla travel
            List<AncientEventModel> valid = ValidPool(act, runState);
            if (valid.Count == 0)
                return true;
            // Open the picker whenever there is a real choice to make: several
            // candidates, OR one candidate whose dialogue slots have variation worth
            // designating (act 1 = {Neow}, pre-epoch acts). Variation is MEASURED by
            // the probe — no Ancient is named — and modifier-driven options (Sealed
            // Deck's Neow chain) are stubbed during probing, so they read as
            // no-variation and that scenario stays pure vanilla.
            if (valid.Count == 1
                && (LocalContext.GetMe(runState) is not Player probeMe
                    || !AncientOptionProbe.HasVariation(probeMe, valid[0])))
                return true;
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
            // The click was consumed to open the picker, so a picker failure MUST fall
            // through to vanilla travel — a persistently broken picker (game update
            // reshaping a scavenged scene) would otherwise make the node unclickable
            // and block the run. Only an explicit cancel stays on the map.
            try
            {
                if (LocalContext.GetMe(runState) is not Player me)
                    throw new System.InvalidOperationException("local player not found");
                ModAncientPickerUi ui = ModAncientPickerUi.Attach(screen, runState.UnlockState, valid, act.Ancient, me);
                AncientPickResult? pick = await ui.Result;
                if (pick == null)
                    return; // cancelled: stay on the map, node re-clickable
                if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                    return; // the map died under us; nothing to travel
                AncientEventModel choice = pick.Ancient;
                if (runState.Players.Count > 1)
                {
                    // MP (real or fake): broadcast pick + designations through the lockstep
                    // queue. Enqueued BEFORE the travel vote below, so every client records
                    // them before room creation. Shared/saved state stays untouched.
                    string cmd = $"{AncientPickConsoleCmd.Name} {choice.Id.Entry}"
                        + string.Concat(pick.Options.Select(o => $" {o.Slot}:{o.Option.Identity}"));
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new ConsoleCmdGameAction(me, cmd, inCombat: false));
                }
                else
                {
                    act._rooms.Ancient = choice; // vanilla-reachable: the field GenerateRooms rolls, saved as AncientId
                    AncientPickSync.RecordOptions(me.NetId, choice.Id.Entry,
                        pick.Options.Select(o => (o.Slot, o.Option.Identity)).ToList());
                }
                MainFile.Logger.Info($"[ancient picker] act {runState.CurrentActIndex + 1} ancient pick: "
                    + $"{choice.Id} ({pick.Options.Count} designation(s))");
                RefreshNodeIcon(point, choice);
            }
            catch (System.Exception e)
            {
                MainFile.Logger.Error($"[ancient picker] picker failed; falling back to vanilla travel: {e}");
            }
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                return;
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

    // The starting node (NAncientMapPoint) painted the vanilla roll's icon at map build;
    // repaint it with the pick so the travel animation (and, in MP, the wait-for-votes
    // map) shows who you will actually meet. Local cosmetics only — in MP each client
    // shows its own player's pick. The first-run GoldenPath map uses a plain node
    // instead; the cast just misses and nothing changes (the picker never opens there).
    private static void RefreshNodeIcon(NMapPoint point, AncientEventModel choice)
    {
        try
        {
            if (point is not NAncientMapPoint node)
                return;
            node._icon.Texture = choice.MapIcon;
            node._outline.Texture = choice.MapIconOutline;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient picker] node icon refresh failed (cosmetic): {e.Message}");
        }
    }
}

// The pickers are CHILDREN of NMapScreen, and gui events their controls don't consume
// bubble up the ancestor chain into NMapScreen._GuiInput — whose handlers scroll the map
// on wheel, pan it on left-drag and start map drawings on right/middle click, all under
// the modal. Gate the whole handler while either map picker (Ancient or ?) is up; the
// pickers' own scrolling and clicks are handled by their children before anything
// reaches the map screen.
[HarmonyPatch(typeof(NMapScreen), "_GuiInput")]
public static class MapInputGatePatch
{
    static bool Prefix(NMapScreen __instance)
    {
        if (!AncientPickerPatch.PickerOpen && !UnknownPickerPatch.PickerOpen)
            return true;
        // The node-click that OPENED the picker: its press armed the map's left-drag pan
        // before the picker existed, and its release bubbles here after _pickerOpen is
        // set — swallowed, leaving the map glued to the mouse once the picker closes
        // (visible in MP, where travel waits on the other players' votes). Swallowing
        // input while modal ⇒ also drop the drag arm.
        __instance._isDragging = false;
        return false;
    }
}
