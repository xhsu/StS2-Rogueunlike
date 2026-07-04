using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #5.2, multiplayer half: "player S designated these option slots of their own
/// act-start Ancient event". Unlike the acts-2/3 flow (rl_ancient enqueued BEFORE the
/// travel vote, consumed at BeginEvent), the act-start event is AUTO-ENTERED at run start
/// (RunManager.EnterAct's StartedWithNeow branch) with no travel action to order against —
/// and event option choices are DIRECT messages (OptionIndexChosenMessage, resolved by
/// INDEX on receipt), so the designation must ride the SAME per-sender FIFO stream to land
/// on every client before the sender's first choice indexes into the swapped options.
/// Same channel rule as RewardPickMessage vs the reward claim (see ModNetMsg.cs).
/// </summary>
public class AncientDesignateMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public RunLocation location;
    public string ancientEntry = "";
    public string designations = ""; // space-joined "<slot>:<K|R>:<identity>" tokens (rl_ancient's format)

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => location;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(location);
        writer.WriteString(ancientEntry);
        writer.WriteString(designations);
    }

    public void Deserialize(PacketReader reader)
    {
        location = reader.Read<RunLocation>();
        ancientEntry = reader.ReadString();
        designations = reader.ReadString();
    }
}

/// <summary>
/// Feature #5.2: designate the act-START Ancient's dialogue options (the "never prompted
/// at act 1" gap). An act entered through RunManager.EnterAct's StartedWithNeow branch
/// auto-enters the starting Ancient node — no map click ever happens, so the feature-#5
/// map seam (NMapScreen.OnMapPointSelectedLocally) can never fire there. The designation
/// window therefore moves INSIDE the event: when the auto-entered Ancient event screen
/// opens for the local player and the probe measures designatable variation, the picker
/// modal (ModAncientPickerUi, pool-of-one — the WHO is already decided) opens over the
/// opening dialogue. Confirm swaps the designated slots of the LIVE event — in
/// _currentOptions (what the UI renders and net choices index into) and GeneratedOptions
/// (what run history records) — through the same Resolve/validation path as the pre-roll
/// substitution; cancel keeps the roll. The vanilla RNG already ran at BeginEvent and is
/// untouched either way, and nothing here is saved: a mid-event quit reloads the entry
/// save and replays the room as the vanilla roll (the modal simply offers again).
///
/// Multiplayer: confirm first broadcasts AncientDesignateMessage on the event message
/// stream (see the message's doc for why THAT channel), then applies locally in the same
/// frame — receivers apply the identical validated swap to their clone of the sender's
/// event, so a later "player S chose option i" resolves identically everywhere. A
/// designate that outruns a still-loading client buffers and drains when that event's
/// initial options are set (AncientEventModel.SetInitialEventState — event Begin is
/// async, so BeginEvent itself is too early). Gated on ModWireCheck.SyncReady: the modal
/// waits out the handshake under the opening dialogue, then gives up to pure vanilla.
/// </summary>
public static class AncientStartDesignate
{
    // ~15s at 4 polls/s: the handshake rides the run-start action queue and normally
    // lands within the first seconds of the room; SP/fake-MP pass on the first check.
    private const int WirecheckWaitTicks = 60;

    // Designates that arrived before this client began the sender's event (we were still
    // loading into the act). Consumed at that player's next SetInitialEventState — apply
    // if it names this event, drop otherwise (older designates can never apply later).
    private static readonly List<(ulong Sender, AncientDesignateMessage Msg)> _buffered = new();

    // ---- the in-event designation window (local player) ----

    [HarmonyPatch(typeof(NEventRoom), "_Ready")]
    public static class EventRoomReadyPatch
    {
        static void Postfix(NEventRoom __instance)
        {
            try
            {
                if (__instance._event is not AncientEventModel live)
                    return;
                RunState? runState = RunManager.Instance?.State;
                // Ancient event rooms are rare; one breadcrumb with every gate value so a
                // "nothing prompted" report is diagnosable from the log alone.
                MainFile.Logger.Info("[ancient start] event room ready: "
                    + $"{live.Id.Entry}, actIndex={(runState == null ? "no-state" : runState.CurrentActIndex.ToString())}, "
                    + $"startedWithNeow={runState?.ExtraFields.StartedWithNeow.ToString() ?? "?"}, "
                    + $"preFinished={__instance._isPreFinished}, "
                    + $"owner={(live.Owner == null ? "null" : live.Owner.NetId.ToString())}, "
                    + $"isMe={live.Owner != null && LocalContext.IsMe(live.Owner)}");
                if (runState == null
                    || runState.CurrentActIndex != 0
                    || !runState.ExtraFields.StartedWithNeow
                    || __instance._isPreFinished
                    || live.Owner is not Player owner
                    || !LocalContext.IsMe(owner))
                    return;
                MainFile.Logger.Info("[ancient start] act-start seam armed; starting designation flow");
                TaskHelper.RunSafely(Flow(__instance, live, owner, runState));
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"[ancient start] seam failed (vanilla event): {e}");
            }
        }
    }

    private static async Task Flow(NEventRoom room, AncientEventModel live, Player owner, RunState runState)
    {
        try
        {
            // Wait under the opening dialogue for (a) the event's initial options — Begin
            // is async (heal animation first) — and (b) the wirecheck, announced at run
            // start on the action queue and possibly still in flight this early.
            int waited = 0;
            for (; waited < WirecheckWaitTicks; waited++)
            {
                if (!GodotObject.IsInstanceValid(room) || live.IsFinished)
                {
                    MainFile.Logger.Info("[ancient start] room gone/event finished while waiting; vanilla");
                    return;
                }
                if (live.CurrentOptions.Count > 0 && ModWireCheck.SyncReady(runState))
                    break;
                await WaitTick(room);
            }
            MainFile.Logger.Info($"[ancient start] settled after {waited} tick(s): "
                + $"options={live.CurrentOptions.Count}, syncReady={ModWireCheck.SyncReady(runState)}, finished={live.IsFinished}");
            if (!GodotObject.IsInstanceValid(room) || live.IsFinished || live.CurrentOptions.Count == 0)
                return;
            if (!ModWireCheck.SyncReady(runState))
            {
                MainFile.Logger.Info("[ancient start] no verified handshake; act-start options stay vanilla");
                return;
            }
            if (!IsDesignatable(live))
            {
                MainFile.Logger.Info("[ancient start] event not designatable (blocked or already advanced); vanilla");
                return;
            }
            if (ModelDb.GetByIdOrNull<EventModel>(live.Id) is not AncientEventModel canonical)
            {
                MainFile.Logger.Info($"[ancient start] no canonical model for {live.Id.Entry}; vanilla");
                return;
            }
            // Measured, never hardcoded — modifier-driven chains (Sealed Deck's Neow)
            // probe to no-variation and stay pure vanilla, same rule as the map picker.
            if (!AncientOptionProbe.HasVariation(owner, canonical))
            {
                MainFile.Logger.Info($"[ancient start] {live.Id.Entry} has no designatable variation (fixed or modifier-driven); vanilla");
                return;
            }

            MainFile.Logger.Info($"[ancient start] opening designation modal for {live.Id.Entry}");
            ModAncientPickerUi ui = ModAncientPickerUi.Attach(room, runState.UnlockState,
                new List<AncientEventModel> { canonical }, canonical, owner);
            AncientPickResult? pick = await ui.Result;
            if (pick == null || pick.Options.Count == 0)
            {
                MainFile.Logger.Info("[ancient start] cancelled / nothing designated; the roll stands");
                return;
            }
            if (!GodotObject.IsInstanceValid(room) || !IsDesignatable(live))
            {
                MainFile.Logger.Info("[ancient start] room/event moved on while designating; vanilla");
                return;
            }

            string tokens = string.Join(" ",
                pick.Options.Select(o => $"{o.Slot}:{o.Option.Identity}"));
            // MP: peers must swap too, or the sender's choice-by-index desyncs — so no
            // broadcast means no local swap either. Broadcast, then apply in the SAME
            // frame (no await in between): the applicability we just checked cannot
            // regress, and validation/resolution is deterministic on every client.
            if (runState.Players.Count > 1 && RunManager.Instance?.IsSingleplayerOrFakeMultiplayer == false
                && !TryBroadcast(live, tokens))
            {
                MainFile.Logger.Error("[ancient start] designation not broadcast; vanilla roll kept");
                return;
            }
            int applied = ValidateAndApply(live, owner, tokens);
            if (applied > 0)
                RefreshRoomOptions(room, live);
            MainFile.Logger.Info($"[ancient start] player {owner.NetId} designated {applied} slot(s) of {live.Id.Entry}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[ancient start] designation flow failed (vanilla roll kept): {e}");
        }
    }

    // NOT Cmd.Wait: that helper deliberately no-ops under FastMode=Instant (and skips
    // when combat is ending), which collapses the poll loop into a same-frame spin that
    // gives the async event Begin no time to set the options. A raw scene-tree timer
    // ticks regardless of the player's speed settings.
    private static async Task WaitTick(NEventRoom room)
    {
        if (room.GetTree() is not SceneTree tree)
            return;
        SceneTreeTimer timer = tree.CreateTimer(0.25f);
        await timer.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    private static bool TryBroadcast(AncientEventModel ev, string tokens)
    {
        try
        {
            EventSynchronizer sync = RunManager.Instance.EventSynchronizer;
            sync._netService.SendMessage(new AncientDesignateMessage
            {
                location = sync._messageBuffer.CurrentLocation,
                ancientEntry = ev.Id.Entry,
                designations = tokens,
            });
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[ancient start] designation broadcast failed: {e}");
            return false;
        }
    }

    // The local rebuild of the option rows (vanilla builder: clears + re-adds from
    // CurrentOptions; the playing dialogue is untouched — only RefreshEventState clears
    // that, and we deliberately do not go through SetEventState).
    private static void RefreshRoomOptions(NEventRoom room, AncientEventModel ev)
    {
        try
        {
            if (GodotObject.IsInstanceValid(room) && room._event == ev)
                room.SetOptions(ev);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[ancient start] option row refresh failed (model already swapped): {e}");
        }
    }

    // ---- the live swap (both sides run exactly this) ----

    /// <summary>
    /// The wrapper's roll (GeneratedOptions) is designatable while its options still sit
    /// on the current page. The page may hold MORE than the roll — other mods append
    /// their own options (BaseLib's Neow "additional interactions" put 4 on a 3-roll
    /// page) — so alignment is per-option by reference, never whole-list. Blocked events
    /// (Wax Choker's proceed-only wrapper) and advanced/finished events read as false,
    /// identically on every client.
    /// </summary>
    private static bool IsDesignatable(AncientEventModel ev)
    {
        if (ev.IsFinished
            || ev.GeneratedOptions is not { Count: > 0 } generated
            || ev.CurrentOptions is not List<EventOption> current || current.Count == 0)
            return false;
        if (generated.Count == 1 && generated[0].IsProceed)
            return false;
        return generated.Any(current.Contains); // some rolled slot is still live
    }

    /// <summary>
    /// Validate tokens against the empirical slot pools (deterministic across clients:
    /// fixed probe seeds + lockstep-identical player state — the same validation
    /// rl_ancient's designations get), then swap each surviving designation in: the
    /// wrapper-slot's rolled option is located on the live page BY REFERENCE (foreign
    /// insertions shift indices; reference lookup doesn't care) and replaced in place —
    /// in _currentOptions (what the UI renders and by-index net choices resolve against)
    /// and GeneratedOptions (what run history records). A slot whose roll is no longer
    /// on the page (another mod replaced it, page advanced) is skipped, on every client
    /// alike. Returns how many slots changed.
    /// </summary>
    private static int ValidateAndApply(AncientEventModel live, Player owner, string designations)
    {
        if (!IsDesignatable(live)
            || ModelDb.GetByIdOrNull<EventModel>(live.Id) is not AncientEventModel canonical)
            return 0;
        List<List<AncientOptionInfo>> pools = AncientOptionProbe.SlotPools(owner, canonical);
        var picks = new List<(int Slot, string Identity)>();
        foreach (string token in designations.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':', 3);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int slot))
                continue;
            string identity = parts[1] + ":" + parts[2];
            if (slot < 0 || slot >= pools.Count || pools[slot].All(o => o.Identity != identity))
            {
                MainFile.Logger.Info($"[ancient start] dropped invalid designation '{token}' for player {owner.NetId}");
                continue;
            }
            picks.Add((slot, identity));
        }
        if (picks.Count == 0)
            return 0;

        List<EventOption> generated = live.GeneratedOptions!;
        var current = (List<EventOption>)live.CurrentOptions;
        var used = current.Select(AncientOptionSubstitutionPatch.SafeIdentity).ToHashSet();
        int applied = 0;
        foreach ((int slot, string identity) in picks)
        {
            if (slot < 0 || slot >= generated.Count || used.Contains(identity))
                continue;
            int liveIdx = current.IndexOf(generated[slot]);
            if (liveIdx < 0)
            {
                MainFile.Logger.Info($"[ancient start] slot {slot} roll not on the live page; kept");
                continue;
            }
            EventOption? replacement = AncientOptionSubstitutionPatch.Resolve(live, identity);
            if (replacement == null)
            {
                MainFile.Logger.Info($"[ancient start] {identity} not resolvable on {live.Id.Entry}; slot {slot} keeps the roll");
                continue;
            }
            used.Remove(AncientOptionSubstitutionPatch.SafeIdentity(current[liveIdx]));
            current[liveIdx] = replacement;
            generated[slot] = replacement;
            used.Add(identity);
            applied++;
            MainFile.Logger.Info($"[ancient start] player {owner.NetId}: slot {slot} (live row {liveIdx}) of {live.Id.Entry} -> {identity}");
        }
        return applied;
    }

    // ---- receiving a remote player's designation ----

    private static void HandleDesignate(AncientDesignateMessage msg, ulong senderId)
    {
        if (ModWireCheck.Broken)
            return; // wire ids disagree across clients: these bytes may be another mod's message
        try
        {
            if (!TryApplyRemote(msg, senderId))
                _buffered.Add((senderId, msg)); // sender's event not begun here yet (still loading)
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[ancient start] designate handling failed (roll kept): {e}");
        }
    }

    private static bool TryApplyRemote(AncientDesignateMessage msg, ulong senderId)
    {
        EventSynchronizer sync = RunManager.Instance.EventSynchronizer;
        AncientEventModel? live = sync._events.OfType<AncientEventModel>()
            .FirstOrDefault(e => e.Owner?.NetId == senderId && e.Id.Entry == msg.ancientEntry);
        if (live == null || live.CurrentOptions.Count == 0)
            return false; // not begun (or options not set yet — Begin is async): buffer
        if (sync._playerCollection.GetPlayer(senderId) is not Player owner)
            return true; // sender gone: consume, nothing to apply to
        int applied = ValidateAndApply(live, owner, msg.designations);
        MainFile.Logger.Info($"[ancient start] player {senderId} designated {applied} slot(s) of {msg.ancientEntry} (remote)");
        return true;
    }

    // The buffered-designate drain point: the event's options exist exactly from here
    // (event Begin is async — the BeginEvent loop itself is too early). Any buffered
    // entry for this player is consumed NOW: applied if it names this event, dropped
    // otherwise — an act-start designate can never legally apply to a later event, and
    // leaving it around would apply on receivers only (the sender never buffers).
    [HarmonyPatch(typeof(AncientEventModel), "SetInitialEventState")]
    public static class InitialStateDrainPatch
    {
        static void Postfix(AncientEventModel __instance)
        {
            if (_buffered.Count == 0 || __instance.Owner is not Player owner)
                return;
            try
            {
                for (int i = 0; i < _buffered.Count; i++)
                {
                    (ulong sender, AncientDesignateMessage msg) = _buffered[i];
                    if (sender != owner.NetId)
                        continue;
                    if (msg.ancientEntry == __instance.Id.Entry)
                    {
                        int applied = ValidateAndApply(__instance, owner, msg.designations);
                        MainFile.Logger.Info($"[ancient start] player {sender} designated {applied} slot(s) of {msg.ancientEntry} (drained)");
                    }
                    _buffered.RemoveAt(i);
                    i--;
                }
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"[ancient start] designate drain failed (roll kept): {e}");
            }
        }
    }

    // ---- lifecycle: ride the vanilla event synchronizer ----

    [HarmonyPatch(typeof(EventSynchronizer), MethodType.Constructor,
        typeof(RunLocationTargetedMessageBuffer), typeof(INetGameService), typeof(IPlayerCollection), typeof(ulong), typeof(uint))]
    public static class SynchronizerCtorPatch
    {
        static void Postfix(EventSynchronizer __instance)
        {
            _buffered.Clear(); // fresh run/reload: no stale designates may leak in
            __instance._messageBuffer.RegisterMessageHandler<AncientDesignateMessage>(HandleDesignate);
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.Dispose))]
    public static class SynchronizerDisposePatch
    {
        static void Postfix(EventSynchronizer __instance)
        {
            __instance._messageBuffer.UnregisterMessageHandler<AncientDesignateMessage>(HandleDesignate);
            _buffered.Clear();
        }
    }
}
