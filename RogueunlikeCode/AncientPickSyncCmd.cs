using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

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
    private static readonly Dictionary<ulong, (string AncientEntry, List<(int Slot, string Identity)> Options)> _optionPicks = new();

    public static void Record(ulong netId, AncientEventModel ancient) => _picks[netId] = ancient;

    /// <summary>Consumes (removes) the player's model pick; every player passes through
    /// the substitution exactly once per BeginEvent, so consume-on-read replaces the old
    /// clear-after-loop. Stale picks (departed players) die with GenerateRooms' Clear.</summary>
    public static bool TryTake(ulong netId, out AncientEventModel ancient) =>
        _picks.Remove(netId, out ancient!);

    public static void RecordOptions(ulong netId, string ancientEntry, List<(int Slot, string Identity)> options) =>
        _optionPicks[netId] = (ancientEntry, options);

    /// <summary>Consumes (removes) the player's option designations; false when none or stale.</summary>
    public static bool TryTakeOptions(ulong netId, string ancientEntry, out List<(int Slot, string Identity)> options)
    {
        options = null!;
        if (!_optionPicks.Remove(netId, out (string AncientEntry, List<(int Slot, string Identity)> Options) stored))
            return false;
        if (stored.AncientEntry != ancientEntry)
            return false; // stale designation for a different Ancient — dropped
        options = stored.Options;
        return true;
    }

    public static void Clear()
    {
        _picks.Clear();
        _optionPicks.Clear();
    }
}

/// <summary>
/// The synced pick message: `rl_ancient &lt;ANCIENT_ID&gt;`, attributed to the issuing player
/// on every client. Public with a parameterless ctor — DevConsole finds it by reflection.
/// </summary>
public class AncientPickConsoleCmd : AbstractConsoleCmd
{
    public const string Name = "rl_ancient";

    public override string CmdName => Name;
    public override string Args => "<ancientId:string> [slot:K|R:identity ...]";
    public override string Description => "[Rogueunlike] Sets the issuing player's Ancient (and optional per-slot option designations) for the upcoming act-start event.";
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
            AncientPickSync.RecordOptions(issuingPlayer.NetId, ancient.Id.Entry, ParseDesignations(issuingPlayer, ancient, args));
            // The room preloader only warms the canonical event's assets; warm the pick
            // now (map time) so the swap doesn't hitch on dialogue art/bgm at entry.
            TaskHelper.RunSafely(PreloadManager.LoadRoomEventAssets(ancient, state));
            MainFile.Logger.Info($"[ancient mp] player {issuingPlayer.NetId} picked {ancient.Id.Entry} ({args.Length - 1} designation(s))");
            return new CmdResult(success: true, $"[Rogueunlike] player {issuingPlayer.NetId} will meet {ancient.Id.Entry}");
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient mp] pick command failed: {e}");
            return new CmdResult(success: false, "internal error (see log)");
        }
    }

    // Extra args are per-slot option designations: "<slot>:<K|R>:<identity>". Each is
    // validated against the empirical slot pools — deterministic (fixed probe seeds +
    // lockstep-identical player state), so every client keeps or drops identically.
    private static List<(int Slot, string Identity)> ParseDesignations(
        Player player, AncientEventModel ancient, string[] args)
    {
        var designations = new List<(int Slot, string Identity)>();
        if (args.Length <= 1)
            return designations;
        List<List<AncientOptionInfo>> pools = AncientOptionProbe.SlotPools(player, ancient);
        for (int i = 1; i < args.Length; i++)
        {
            string[] parts = args[i].Split(':', 3);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int slot))
                continue;
            string identity = parts[1] + ":" + parts[2];
            if (slot < 0 || slot >= pools.Count || pools[slot].All(o => o.Identity != identity))
            {
                MainFile.Logger.Info($"[ancient mp] dropped invalid designation '{args[i]}' from player {player.NetId}");
                continue;
            }
            designations.Add((slot, identity));
        }
        return designations;
    }
}

/// <summary>
/// The substitution seam. Vanilla BeginEvent clones each player's mutable event from the
/// shared canonical inside its per-player loop:
///     EventModel eventModel = canonicalEvent.ToMutable();
/// This transpiler redirects exactly that call to <see cref="PickFor"/>, which returns
/// the player's synced pick when one is pending (canonical otherwise) — so the WHOLE
/// vanilla body keeps running (vote resets, cleanup, logging, and anything MegaCrit adds
/// later flow through automatically), and other mods' patches on BeginEvent are never
/// bypassed (the old prefix skipped the original while substituting).
///
/// Deliberately assertion-loud instead of drift-silent: if the method ever stops having
/// exactly one ToMutable call site and exactly one Player local, the transpiler THROWS at
/// patch time — ModPatcher rolls back the whole ancient group and logs the feature as
/// disabled, rather than guessing at a reshaped body.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class AncientEventSubstitutionPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        List<CodeInstruction> code = instructions.ToList();
        List<int> sites = new();
        for (int i = 0; i < code.Count; i++)
            if ((code[i].opcode == OpCodes.Callvirt || code[i].opcode == OpCodes.Call)
                && code[i].operand is MethodInfo { Name: "ToMutable" })
                sites.Add(i);
        if (sites.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly 1 ToMutable call in EventSynchronizer.BeginEvent, found {sites.Count} — vanilla body changed, refusing to guess");

        List<LocalVariableInfo> playerLocals = (original.GetMethodBody()?.LocalVariables)
            ?.Where(l => l.LocalType == typeof(Player)).ToList()
            ?? throw new InvalidOperationException("EventSynchronizer.BeginEvent body unreadable");
        if (playerLocals.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly 1 Player local in EventSynchronizer.BeginEvent, found {playerLocals.Count} — vanilla body changed, refusing to guess");

        int site = sites[0];
        // Stack at the call site: [canonicalEvent]. Append the loop's player local and
        // the isPrefinished argument, then call PickFor(EventModel, Player, bool) in its
        // place — same net stack effect as the original callvirt.
        CodeInstruction loadPlayer = CodeInstruction.LoadLocal(playerLocals[0].LocalIndex);
        loadPlayer.labels.AddRange(code[site].labels); // keep any jumps-to-here landing before our loads
        code[site].labels.Clear();
        code[site] = new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(AncientEventSubstitutionPatch), nameof(PickFor)));
        code.InsertRange(site, new[] { loadPlayer, new CodeInstruction(OpCodes.Ldarg_2) });
        return code;
    }

    /// <summary>
    /// The per-player clone source. Never throws (it runs inside the vanilla body):
    /// any failure falls back to the canonical, exactly like a player without a pick.
    /// Consumption-time pool re-check keeps a raced/stale pick from leaking in —
    /// deterministic on every client.
    /// </summary>
    public static EventModel PickFor(EventModel canonical, Player player, bool isPrefinished)
    {
        try
        {
            if (canonical is AncientEventModel && !isPrefinished
                && AncientPickSync.TryTake(player.NetId, out AncientEventModel pick))
            {
                RunState? state = RunManager.Instance.State;
                if (state != null && AncientPickerPatch.ValidPool(state.Act, state).Contains(pick))
                {
                    MainFile.Logger.Info($"[ancient mp] player {player.NetId} meets {pick.Id.Entry}");
                    return pick.ToMutable();
                }
                MainFile.Logger.Error($"[ancient mp] pick {pick.Id.Entry} of player {player.NetId} no longer in the act pool; canonical kept");
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[ancient mp] substitution failed for player {player.NetId}, canonical kept: {e}");
        }
        return canonical.ToMutable();
    }
}

// Stale-pick backstop: a fresh run (or reload) starts with an empty pick map.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
public static class AncientPickResetPatch
{
    static void Postfix() => AncientPickSync.Clear();
}
