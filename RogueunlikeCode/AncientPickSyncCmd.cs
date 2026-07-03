using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Generic;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #5, multiplayer half: each player meets THEIR OWN picked Ancient.
///
/// The net channel is the game's own networked-console pipeline: DevConsole registers
/// mod-defined <see cref="AbstractConsoleCmd"/> subclasses (ReflectionHelper.GetSubtypesInMods),
/// and an IsNetworked command rides the vanilla ConsoleCmdGameAction/NetConsoleCmdGameAction
/// wire type through the lockstep action queue — every client executes it, in the same
/// order, with the SENDER as issuingPlayer. No serializer patching, no custom packets.
///
/// Determinism: picks land in <see cref="AncientPickSync"/> identically on every client
/// (validated against the same act pool), and the queue guarantees each player's pick
/// executes before their travel vote — so before room creation. EventSynchronizer.BeginEvent
/// then clones each player's event from their pick instead of the room's canonical; option
/// choices already sync per player against their own event, so play proceeds vanilla.
///
/// Save-safety: nothing here writes saved state. The room still saves the vanilla-rolled
/// EventId; the run saves at room entry and again (benefits + pre-finished flag, in one
/// write) only when every player's event has finished. Reload after completion shows the
/// saved Ancient's done page with all benefits intact; a mid-event quit reloads the
/// entry save and replays the room as the vanilla roll, nothing gained twice.
///
/// Requires every client to run the mod (same rule as the treasure feature): an unmodded
/// client would ignore the pick command and desync when a picked event's choice arrives.
/// </summary>
internal static class AncientPickSync
{
    private static readonly Dictionary<ulong, AncientEventModel> _picks = new();

    public static bool Any => _picks.Count > 0;

    public static void Record(ulong netId, AncientEventModel ancient) => _picks[netId] = ancient;

    public static bool TryGet(ulong netId, out AncientEventModel ancient) =>
        _picks.TryGetValue(netId, out ancient!);

    public static void Clear() => _picks.Clear();
}

/// <summary>
/// The synced pick message: `rl_ancient &lt;ANCIENT_ID&gt;`, attributed to the issuing player
/// on every client. Public with a parameterless ctor — DevConsole finds it by reflection.
/// </summary>
public class AncientPickConsoleCmd : AbstractConsoleCmd
{
    public const string Name = "rl_ancient";

    public override string CmdName => Name;
    public override string Args => "<ancientId:string>";
    public override string Description => "[Rogueunlike] Sets the issuing player's Ancient for the upcoming act-start event.";
    public override bool IsNetworked => true;
    public override bool DebugOnly => false; // must register in release builds (no dev flag)

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Never throw: NDevConsole rethrows command exceptions out of the action queue.
        try
        {
            if (issuingPlayer == null || args.Length == 0)
                return new CmdResult(success: false, "issuing player or ancient id missing");
            RunState? state = RunManager.Instance.State;
            if (state == null)
                return new CmdResult(success: false, "no run in progress");
            ModelId id = new(ModelDb.GetCategory(typeof(EventModel)), args[0].ToUpperInvariant());
            if (ModelDb.GetByIdOrNull<EventModel>(id) is not AncientEventModel ancient)
                return new CmdResult(success: false, "invalid ancient id");
            // Same law as everywhere in the mod: pickable ⟺ this act could roll it.
            // The pool derives from shared deterministic state, so every client accepts
            // or rejects identically.
            if (!AncientPickerPatch.ValidPool(state.Act, state).Contains(ancient))
                return new CmdResult(success: false, $"{ancient.Id.Entry} is not rollable for this act");
            AncientPickSync.Record(issuingPlayer.NetId, ancient);
            // The room preloader only warms the canonical event's assets; warm the pick
            // now (map time) so the swap doesn't hitch on dialogue art/bgm at entry.
            TaskHelper.RunSafely(PreloadManager.LoadRoomEventAssets(ancient, state));
            MainFile.Logger.Info($"[ancient mp] player {issuingPlayer.NetId} picked {ancient.Id.Entry}");
            return new CmdResult(success: true, $"[Rogueunlike] player {issuingPlayer.NetId} will meet {ancient.Id.Entry}");
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient mp] pick command failed: {e}");
            return new CmdResult(success: false, "internal error (see log)");
        }
    }
}

/// <summary>
/// The substitution seam: mirrors vanilla <see cref="EventSynchronizer.BeginEvent"/> but
/// clones each player's mutable event from their synced pick (canonical for players who
/// didn't pick). Runs only for a live (non-prefinished) Ancient event with picks pending —
/// reloads and every other event in the game take the vanilla path untouched.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class AncientEventSubstitutionPatch
{
    static bool Prefix(EventSynchronizer __instance, EventModel canonicalEvent,
        bool isPrefinished, System.Action<EventModel>? debugOnStart)
    {
        if (canonicalEvent is not AncientEventModel || isPrefinished || !AncientPickSync.Any)
            return true;
        try
        {
            RunState? state = RunManager.Instance.State;
            List<AncientEventModel>? pool = state != null
                ? AncientPickerPatch.ValidPool(state.Act, state)
                : null;

            // -- vanilla BeginEvent, per-player model swapped in ------------------
            for (int i = __instance._playerVotes.Count; i < __instance._playerCollection.Players.Count; i++)
                __instance._playerVotes.Add(null);
            foreach (EventModel stale in __instance._events)
                if (!stale.IsFinished)
                    stale.EnsureCleanup();
            __instance._events.Clear();
            __instance._pendingOptionTasks.Clear();
            for (int i = 0; i < __instance._playerVotes.Count; i++)
                __instance._playerVotes[i] = null;
            __instance._pageIndex = 0u;
            __instance._canonicalEvent = canonicalEvent;
            foreach (Player player in __instance._playerCollection.Players)
            {
                // Consumption-time pool re-check: a pick raced past room creation (or
                // outlived its act) must not leak in — deterministic on all clients.
                EventModel source =
                    AncientPickSync.TryGet(player.NetId, out AncientEventModel pick)
                        && pool != null && pool.Contains(pick)
                    ? pick
                    : canonicalEvent;
                EventModel model = source.ToMutable();
                debugOnStart?.Invoke(model);
                __instance._events.Add(model);
                TaskHelper.RunSafely(model.BeginEvent(player, isPrefinished));
                MainFile.Logger.Info($"[ancient mp] player {player.NetId} meets {source.Id.Entry}");
            }
            AncientPickSync.Clear(); // consumed
            return false;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient mp] substitution failed, vanilla event for everyone: {e}");
            AncientPickSync.Clear();
            return true; // vanilla BeginEvent re-runs from a clean slate (it clears state first)
        }
    }
}

// Stale-pick backstop: a fresh run (or reload) starts with an empty pick map.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
public static class AncientPickResetPatch
{
    static void Postfix() => AncientPickSync.Clear();
}
