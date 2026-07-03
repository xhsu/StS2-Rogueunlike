using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Run-start multiplayer handshake. Two hazards this closes:
///
/// 1. Mod INetMessage wire ids are POSITIONAL: MessageTypes.Initialize appends
///    ReflectionHelper.GetSubtypesInMods (= mod LOAD order) after the built-ins, and
///    load order is a PER-MACHINE setting (ModManager.SortModList breaks dependency
///    ties with the local settings order). Two clients with the same mod set can
///    disagree about RewardPickMessage's id as soon as any other INetMessage-registering
///    mod sits on a different side of this mod in their lists.
/// 2. Every sync feature assumes all clients run this mod (and the same version of it);
///    a bare client silently ignores our picks and desyncs later.
///
/// So each client announces (modVersion, pickMsgId, assignMsgId) over the networked
/// console-cmd channel — a BUILT-IN action type carrying a string, immune to ordering —
/// and every sync-dependent feature stays pure vanilla until every player in the run
/// has announced a matching tuple. Mismatch or absence degrades features, never state.
/// </summary>
internal static class ModWireCheck
{
    private static readonly HashSet<ulong> _matched = new();
    private static bool _broken;
    private static bool _announced;
    private static string? _version;

    /// <summary>Every player in the run announced a matching version + wire ids.</summary>
    internal static bool Verified { get; private set; }

    /// <summary>A mismatching announce arrived — incoming mod messages may be mis-decoded garbage.</summary>
    internal static bool Broken => _broken;

    internal static string ModVersion => _version ??=
        ModManager.GetLoadedMods().FirstOrDefault(m => m.assembly == typeof(ModWireCheck).Assembly)
            ?.manifest?.version ?? "unknown";

    /// <summary>
    /// May sync-dependent features act? Singleplayer and fake multiplayer always (one
    /// client, nothing to agree on); real multiplayer only once verified.
    /// </summary>
    internal static bool SyncReady(IRunState? runState) =>
        runState != null
        && (runState.Players.Count == 1
            || RunManager.Instance?.IsSingleplayerOrFakeMultiplayer == true
            || Verified);

    /// <summary>New RunManager (fresh run or load): all agreements must be re-earned.</summary>
    internal static void Reset()
    {
        _matched.Clear();
        _broken = false;
        _announced = false;
        Verified = false;
    }

    /// <summary>
    /// Announce once per run, from whichever trigger fires first (run generation, room
    /// entry, first rewards set). Failure leaves _announced false so a later trigger
    /// retries; until verification lands, features simply stay vanilla — safe default.
    /// </summary>
    internal static void TryAnnounce(IRunState? runState)
    {
        if (_announced || runState == null || runState.Players.Count <= 1
            || RunManager.Instance?.IsSingleplayerOrFakeMultiplayer != false)
            return;
        try
        {
            if (runState.Players.FirstOrDefault(p => LocalContext.IsMe(p)) is not Player me)
                return;
            int pickId = MessageTypes.TypeToId<RewardPickMessage>();
            int assignId = MessageTypes.TypeToId<ShopAssignMessage>();
            string cmd = $"{WireCheckConsoleCmd.Name} {ModVersion} {pickId} {assignId}";
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(
                me, cmd, CombatManager.Instance?.IsInProgress ?? false));
            _announced = true;
            MainFile.Logger.Info($"[wire check] announced {ModVersion} ids {pickId}/{assignId}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"[wire check] announce deferred ({e.Message}); will retry");
        }
    }

    internal static void RecordAnnounce(Player sender, string version, int pickId, int assignId, IRunState runState)
    {
        try
        {
            int myPick = MessageTypes.TypeToId<RewardPickMessage>();
            int myAssign = MessageTypes.TypeToId<ShopAssignMessage>();
            if (version != ModVersion || pickId != myPick || assignId != myAssign)
            {
                _broken = true;
                Verified = false;
                MainFile.Logger.Error(
                    $"[wire check] MISMATCH from player {sender.NetId}: theirs {version} ids {pickId}/{assignId}, "
                    + $"ours {ModVersion} ids {myPick}/{myAssign}. Pick-sync features stay VANILLA this run. "
                    + "Fix: every player must run the same Rogueunlike version and the same mod set; if versions "
                    + "already match, arrange the mods in the SAME ORDER in the Mods menu on every machine "
                    + "(mod message ids are load-order based).");
                return;
            }
            if (_broken)
                return;
            bool isNew = _matched.Add(sender.NetId);
            if (!Verified && _matched.Count >= runState.Players.Count)
            {
                Verified = true;
                MainFile.Logger.Info(
                    $"[wire check] verified: {_matched.Count} player(s) on {ModVersion}, ids {myPick}/{myAssign}");
            }
            // A newcomer (rejoin / late load) has an empty ledger and needs everyone's
            // announce to verify on THEIR side — re-announce so they can. Bounded: each
            // sender is "new" at most once per run per client.
            if (isNew && _announced && !LocalContext.IsMe(sender))
            {
                _announced = false;
                TryAnnounce(runState);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[wire check] record failed: {e}");
        }
    }
}

/// <summary>
/// The handshake wire format: `rl_wirecheck &lt;modVersion&gt; &lt;pickMsgId&gt; &lt;assignMsgId&gt;`,
/// executed lockstep on every client with the SENDER attributed (same official channel
/// as <see cref="AncientPickConsoleCmd"/>). A client without this mod ignores the
/// command — and equally never announces, which is exactly what keeps Verified false.
/// </summary>
public class WireCheckConsoleCmd : AbstractConsoleCmd
{
    public const string Name = "rl_wirecheck";

    public override string CmdName => Name;
    public override string Args => "<modVersion:string> <pickMsgId:int> <assignMsgId:int>";
    public override string Description => "[Rogueunlike] Internal multiplayer handshake: verifies mod version and message wire ids across all clients.";
    public override bool IsNetworked => true;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Never throw: NDevConsole rethrows command exceptions out of the action queue.
        try
        {
            if (issuingPlayer == null || args.Length < 3
                || !int.TryParse(args[1], out int pickId) || !int.TryParse(args[2], out int assignId))
                return new CmdResult(success: false, "malformed wire check");
            RunState? state = RunManager.Instance.State;
            if (state == null)
                return new CmdResult(success: false, "no run in progress");
            ModWireCheck.RecordAnnounce(issuingPlayer, args[0], pickId, assignId, state);
            return new CmdResult(success: true, $"[Rogueunlike] wire check from {issuingPlayer.NetId} recorded");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[wire check] command failed: {e}");
            return new CmdResult(success: false, "internal error (see log)");
        }
    }
}

// Announce triggers: run generation (fresh runs — before the first map click, so the
// act-start Ancient picker verifies in time) and room entry (loads/rejoins, retries).
// A third retry lives in ModPickNet.BeforeBeginRewardsSet for absolute coverage.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
internal static class WireCheckRunStartPatch
{
    static void Postfix() => ModWireCheck.TryAnnounce(RunManager.Instance?.State);
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
internal static class WireCheckRoomEnterPatch
{
    static void Postfix(IRunState runState) => ModWireCheck.TryAnnounce(runState);
}
