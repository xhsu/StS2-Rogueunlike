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
/// Feature #5: pick your Ancient. Every act starts on an Ancient node (the friendly
/// benefactor event — Neow, Darv, the Hive/Glory trios...). Vanilla rolls which Ancient
/// you meet with the run-seed RNG at run start; this seam intercepts the map-node click
/// and opens <see cref="ModAncientPickerUi"/> instead, then travels normally.
///
/// Selection pool = loot pool at current context (never expanded): exactly what
/// ActModel.GenerateRooms could have rolled for THIS act — the act's unlocked native
/// Ancients plus the shared-Ancient subset dealt to this act at run start. The picker
/// opens whenever there is any real choice: several candidates, or a single candidate
/// (act 1 is always {Neow}, pre-epoch acts) whose dialogue slots the probe measures as
/// having variation — the WHO may be forced while the option designations (feature
/// #5.1, "what I'd like to talk about") are still worth picking. No Ancient is ever
/// named: events whose options are modifier-built chains rather than slot rolls
/// (Sealed Deck's Neow) probe to no-variation — the probe stubs modifier option hooks
/// rather than execute them — and stay pure vanilla.
///
/// Save-safety, singleplayer: the only write is RoomSet.Ancient — the same field
/// GenerateRooms rolls, serialized as AncientId in the vanilla run save — done BEFORE
/// travel, so the room, run history, metrics and reload all read the choice through
/// pure vanilla paths.
///
/// Multiplayer (every client modded — see <see cref="AncientPickSyncCmd.cs"/>): each
/// player's confirm is broadcast as a networked console command through the vanilla
/// lockstep action queue, and EventSynchronizer.BeginEvent clones each player's event
/// from their pick. The saved state is untouched (the room keeps the vanilla-rolled
/// EventId): quitting after the event completes reloads to that Ancient's done page
/// with all gained benefits intact; quitting mid-event replays the room as the vanilla
/// roll from the entry save — picks live in memory only.
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
            if (LocalContext.GetMe(runState) is not Player me)
            {
                MainFile.Logger.Error("[ancient picker] local player not found; vanilla travel");
                return;
            }
            ModAncientPickerUi ui = ModAncientPickerUi.Attach(screen, runState.UnlockState, valid, act.Ancient, me);
            AncientPickResult? pick = await ui.Result;
            if (pick == null || !GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                return; // cancelled (or the map died under us): stay on the map, node re-clickable
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
    static bool Prefix() => !AncientPickerPatch.PickerOpen && !UnknownPickerPatch.PickerOpen;
}
