using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #6: choose what an unknown (?) map node becomes — the room category AND its
/// content (which enemy, which event) — by player vote (see <see cref="UnknownPickSync"/>).
///
/// Vanilla rolls in two independent stages at travel, and the mod substitutes at both:
///   • category — UnknownMapPointOdds.Roll consumes one float from a dedicated RNG
///     stream over escalating odds (Monster/Elite/Treasure/Shop buckets, remainder =
///     Event). Pickable ⟺ final effective chance &gt; 0 through the exact vanilla inputs:
///     current escalated odds (negative = never), RunManager.BuildRoomTypeBlacklist,
///     and Hook.ModifyUnknownMapPointRoomTypes (Juzu Bracelet, Golden Compass, Deadly
///     Events, other mods) — no relic or modifier is ever named.
///   • content — the act pre-deals its event queue and encounter tables into RoomSet at
///     generation; the pull reads at a wrap-around cursor. Pickable ⟺ a member of that
///     exact dealt list (for events: additionally valid right now, mirroring
///     EnsureNextEventIsValid). The substitution is a list SWAP into the cursor slot —
///     the same operation vanilla's own RoomSet.SwapToOrCreateAtIndex performs for the
///     tutorial — so the pull, its hooks (ModifyNextEvent) and the save shape stay
///     pure vanilla. Never expands a pool by construction.
///
/// Save-safety: the forced category roll consumes one float and applies the exact
/// reset/escalation the real roll would for that outcome (all serialized vanilla state);
/// the swap permutes a vanilla-serialized list. Quitting before travel loses only the
/// in-memory votes: reload = untraveled node, pure vanilla.
///
/// Multiplayer: votes ride the networked-console lockstep channel BEFORE each sender's
/// map travel vote; travel needs every map vote, so all picks are tallied identically on
/// every client when RunManager.EnterMapCoord fires. Gated on ModWireCheck like every
/// sync feature. The very first run ever (NumberOfRuns == 0) keeps vanilla's forced
/// tutorial sequence — the picker stays closed.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
public static class UnknownPickerPatch
{
    private static bool _passThrough;
    private static bool _pickerOpen;

    /// <summary>True while the ? picker modal is up (see MapInputGatePatch).</summary>
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
            if (point.Point.PointType != MapPointType.Unknown || point.State != MapPointState.Travelable)
                return true;
            if (!ModWireCheck.SyncReady(runState))
                return true; // real MP without a verified mod handshake: vanilla travel
            if (runState.UnlockState.NumberOfRuns == 0)
                return true; // vanilla's forced first-run ? sequence owns these rolls
            Dictionary<RoomType, float> chances = UnknownPools.CategoryChances(runState);
            if (!chances.Values.Any(c => c > 0f))
                return true;
            if (_pickerOpen)
                return false;
            TaskHelper.RunSafely(PickFlow(__instance, point, runState, chances));
            return false;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unknown picker] intercept failed, vanilla travel: {e}");
            return true;
        }
    }

    private static async Task PickFlow(NMapScreen screen, NMapPoint point,
        RunState runState, Dictionary<RoomType, float> chances)
    {
        _pickerOpen = true;
        try
        {
            // The click was consumed to open the picker, so a picker failure MUST fall
            // through to vanilla travel (no vote = a fate vote) — a persistently broken
            // picker would otherwise make every ? node unclickable and block the run.
            // Only an explicit cancel stays on the map.
            try
            {
                if (LocalContext.GetMe(runState) is not Player me)
                    throw new System.InvalidOperationException("local player not found");
                MapCoord coord = point.Point.coord;
                ModUnknownPickerUi ui = ModUnknownPickerUi.Attach(screen, runState, me, coord, chances);
                UnknownPickResult? pick = await ui.Result;
                if (pick == null)
                    return; // cancelled: stay on the map, node re-clickable
                if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                    return; // the map died under us; nothing to travel
                string content = pick.Model != null ? $" {pick.Model.Entry}" : "";
                if (runState.Players.Count > 1)
                {
                    // MP (real or fake): broadcast the vote through the lockstep queue —
                    // enqueued BEFORE the travel vote below, so every client records it
                    // before any client can begin the travel that consumes it.
                    string cmd = pick.IsFate
                        ? $"{UnknownPickConsoleCmd.Name} {coord.col} {coord.row} fate"
                        : $"{UnknownPickConsoleCmd.Name} {coord.col} {coord.row} {pick.Category}{content}";
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new ConsoleCmdGameAction(me, cmd, inCombat: false));
                }
                else
                {
                    UnknownPickSync.Record(coord, me.NetId, pick.IsFate, pick.Category, pick.Model);
                }
                MainFile.Logger.Info($"[unknown picker] local vote at {coord}: "
                    + (pick.IsFate ? "fate" : $"{pick.Category}{content}"));
            }
            catch (System.Exception e)
            {
                MainFile.Logger.Error($"[unknown picker] picker failed; falling back to vanilla travel: {e}");
            }
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(point))
                return;
            _passThrough = true;
            try
            {
                screen.OnMapPointSelectedLocally(point); // vanilla vote/travel; tally runs at EnterMapCoord
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

/// <summary>The picker's confirmed choice. Fate = keep the vanilla roll.</summary>
public sealed class UnknownPickResult
{
    public bool IsFate;
    public RoomType Category;
    public ModelId? Model;
}

/// <summary>
/// One-shot substitution order for the travel currently executing: set when the vote
/// tally resolves at EnterMapCoord, consumed by the forced roll / content-pull patches
/// within that same travel. In-memory only.
/// </summary>
internal static class UnknownPickArm
{
    internal sealed class ArmedPick
    {
        public MapCoord Coord;
        public RoomType Category;
        public ModelId? Model;
    }

    public static ArmedPick? Armed { get; private set; }

    public static void Arm(MapCoord coord, RoomType category, ModelId? model) =>
        Armed = new ArmedPick { Coord = coord, Category = category, Model = model };

    public static void Disarm() => Armed = null;
}

/// <summary>
/// The selection pools, mirroring vanilla's two roll stages over live state. Everything
/// here is a pure read — no RNG consumed, no lists mutated.
/// </summary>
internal static class UnknownPools
{
    // Vanilla walks _nonEventOdds in insertion order (see UnknownMapPointOdds ctor);
    // SetBaseOdds / the odds setters only rewrite values, so the order is stable.
    private static readonly RoomType[] BucketOrder =
        { RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop };

    /// <summary>The candidate set exactly as Roll builds it (blacklist + hook filter).</summary>
    public static IReadOnlySet<RoomType> AllowedTypes(RunState state) =>
        AllowedTypes(state, RunManager.BuildRoomTypeBlacklist(
            state.CurrentMapPointHistoryEntry,
            state.CurrentMapPoint?.Children ?? new HashSet<MapPoint>()));

    public static IReadOnlySet<RoomType> AllowedTypes(RunState state, IEnumerable<RoomType> blacklist)
    {
        IReadOnlySet<RoomType> set = BucketOrder.Append(RoomType.Event).Except(blacklist).ToHashSet();
        return Hook.ModifyUnknownMapPointRoomTypes(state, set);
    }

    /// <summary>
    /// Final effective chance per category at the current context. Pickable ⟺ &gt; 0.
    /// Mirrors Roll's bucket walk: negative odds never roll, buckets clip against the
    /// [0,1) roll range, the leftover goes to Event (or, if a hook removed Event, to the
    /// lowest-valued allowed type — vanilla's default fallback).
    /// </summary>
    public static Dictionary<RoomType, float> CategoryChances(RunState state) =>
        ChancesCore(state.Odds.UnknownMapPoint, AllowedTypes(state));

    public static Dictionary<RoomType, float> ChancesCore(UnknownMapPointOdds odds, IReadOnlySet<RoomType> allowed)
    {
        var chances = new Dictionary<RoomType, float>
        {
            [RoomType.Monster] = 0f,
            [RoomType.Elite] = 0f,
            [RoomType.Treasure] = 0f,
            [RoomType.Shop] = 0f,
            [RoomType.Event] = 0f,
        };
        float mass = 0f;
        foreach (RoomType type in BucketOrder)
        {
            float o = odds._nonEventOdds[type];
            if (!allowed.Contains(type) || o < 0f)
                continue;
            chances[type] = Mathf.Min(o, Mathf.Max(0f, 1f - mass));
            mass += o;
        }
        float leftover = Mathf.Max(0f, 1f - mass);
        if (allowed.Contains(RoomType.Event))
            chances[RoomType.Event] = leftover;
        else if (leftover > 0f && allowed.Count > 0)
            chances[allowed.OrderBy(t => t).First()] += leftover;
        return chances;
    }

    /// <summary>
    /// What the vanilla roll WOULD produce right now (the picker's pre-selected "fate"
    /// preview): simulate the category roll on a cloned RNG (seed + counter are public,
    /// the clone fast-forwards), then read the content the pull would return — the
    /// event-validity walk and the encounter cursor are pure reads.
    /// </summary>
    public static (RoomType Category, AbstractModel? Content) Peek(RunState state)
    {
        UnknownMapPointOdds odds = state.Odds.UnknownMapPoint;
        IReadOnlySet<RoomType> allowed = AllowedTypes(state);
        float roll = new Rng(odds._rng.Seed, odds._rng.Counter).NextFloat();
        RoomType category = SimulateRoll(odds, allowed, roll);
        AbstractModel? content = category switch
        {
            RoomType.Event => PeekNextEvent(state),
            RoomType.Monster or RoomType.Elite => PeekNextEncounter(state, category),
            _ => null,
        };
        return (category, content);
    }

    /// <summary>Roll's bucket walk for a given roll value (pure; mirrors only the walk order + sign rule).</summary>
    public static RoomType SimulateRoll(UnknownMapPointOdds odds, IReadOnlySet<RoomType> allowed, float roll)
    {
        float mass = 0f;
        foreach (RoomType type in BucketOrder)
        {
            float o = odds._nonEventOdds[type];
            if (!allowed.Contains(type) || o < 0f)
                continue;
            mass += o;
            if (roll <= mass)
                return type;
        }
        return allowed.Contains(RoomType.Event)
            ? RoomType.Event
            : allowed.OrderBy(t => t).FirstOrDefault(RoomType.Event); // vanilla default fallback
    }

    /// <summary>
    /// A roll value that lands the vanilla bucket walk on <paramref name="category"/>
    /// given the current odds — the midpoint of its effective range. Null when the
    /// category cannot win right now. Callers must confirm with <see cref="SimulateRoll"/>
    /// before use (guards float-precision edges and walk-order drift alike).
    /// </summary>
    public static float? ForcingFloat(UnknownMapPointOdds odds, IReadOnlySet<RoomType> allowed, RoomType category)
    {
        float mass = 0f;
        foreach (RoomType type in BucketOrder)
        {
            float o = odds._nonEventOdds[type];
            if (!allowed.Contains(type) || o < 0f)
                continue;
            if (type == category)
            {
                float effective = Mathf.Min(o, Mathf.Max(0f, 1f - mass));
                return effective > 0f ? mass + effective / 2f : null;
            }
            mass += o;
        }
        float leftover = Mathf.Max(0f, 1f - mass);
        if (leftover <= 0f)
            return null;
        RoomType fallback = allowed.Contains(RoomType.Event)
            ? RoomType.Event
            : allowed.OrderBy(t => t).FirstOrDefault(RoomType.Event);
        return category == fallback ? mass + leftover / 2f : null;
    }

    // EnsureNextEventIsValid's walk, without mutating the cursor.
    private static EventModel? PeekNextEvent(RunState state)
    {
        RoomSet rooms = state.Act._rooms;
        if (rooms.events.Count == 0)
            return null;
        for (int i = 0; i < rooms.events.Count; i++)
        {
            EventModel candidate = rooms.events[(rooms.eventsVisited + i) % rooms.events.Count];
            if (candidate.IsAllowed(state) && !state.VisitedEventIds.Contains(candidate.Id))
                return candidate;
        }
        return rooms.events[rooms.eventsVisited % rooms.events.Count]; // exhausted: vanilla repeats
    }

    private static EncounterModel? PeekNextEncounter(RunState state, RoomType category)
    {
        RoomSet rooms = state.Act._rooms;
        List<EncounterModel> list = category == RoomType.Elite ? rooms.eliteEncounters : rooms.normalEncounters;
        if (list.Count == 0)
            return null;
        int visited = category == RoomType.Elite ? rooms.eliteEncountersVisited : rooms.normalEncountersVisited;
        return list[visited % list.Count];
    }

    /// <summary>Events the pull could return right now: dealt-list members, valid, unvisited.</summary>
    public static List<EventModel> EventPool(RunState state) =>
        state.Act._rooms.events
            .Where(e => e.IsAllowed(state) && !state.VisitedEventIds.Contains(e.Id))
            .Distinct()
            .ToList();

    /// <summary>Encounters the pull could return: the act's dealt table (wrap-around cursor ⇒ every member reachable).</summary>
    public static List<EncounterModel> EncounterPool(RunState state, RoomType category)
    {
        RoomSet rooms = state.Act._rooms;
        List<EncounterModel> list = category == RoomType.Elite ? rooms.eliteEncounters : rooms.normalEncounters;
        return list.Distinct().ToList();
    }
}

/// <summary>
/// Tally seam: every client executes EnterMapCoord for the winning coord after all map
/// votes are in — by then every rl_unknown vote has been recorded (per-sender FIFO).
/// Resolve the plurality here and arm the substitution for THIS travel.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
public static class UnknownTravelTallyPatch
{
    static void Prefix(RunManager __instance, MapCoord coord)
    {
        UnknownPickArm.Disarm(); // stale arms never survive into a new travel
        try
        {
            RunState? state = __instance.State;
            if (state == null || state.VisitedMapCoords.Contains(coord))
                return; // mirrors vanilla's already-visited early-out: no travel happens
            if (state.Map.GetPoint(coord) is not MapPoint point || point.PointType != MapPointType.Unknown)
                return;
            if (!ModWireCheck.SyncReady(state) || state.UnlockState.NumberOfRuns == 0)
                return;
            (RoomType Category, ModelId? Model)? winner = UnknownPickSync.Tally(coord, state.Players.Count);
            if (winner == null)
            {
                MainFile.Logger.Info($"[unknown picker] fate wins at {coord}; vanilla roll");
                return;
            }
            UnknownPickArm.Arm(coord, winner.Value.Category, winner.Value.Model);
            MainFile.Logger.Info($"[unknown picker] vote result at {coord}: {winner.Value.Category}"
                + (winner.Value.Model != null ? $" {winner.Value.Model.Entry}" : ""));
        }
        catch (System.Exception e)
        {
            UnknownPickArm.Disarm();
            MainFile.Logger.Error($"[unknown picker] travel tally failed, vanilla roll: {e}");
        }
    }

    // Any travel stales every node's votes (the map row advanced). The armed pick for
    // THIS travel was already captured in the prefix.
    static void Postfix() => UnknownPickSync.Clear();
}

/// <summary>
/// Category substitution, vanilla-executed: when a vote is armed for this travel, the
/// prefix computes a roll VALUE that lands the bucket walk on the voted type (verified
/// by simulating the walk first) and the transpiler feeds it in place of Roll's single
/// NextFloat draw — a real draw still happens underneath, so the dedicated RNG stream
/// advances exactly as vanilla. Everything else (the walk, the winner's base reset, the
/// escalation hooks, the first-run forcing) is the ORIGINAL Roll body executing: any
/// float in [0,1) is a roll vanilla could have made, so the resulting odds state is
/// vanilla-reachable by construction, and future changes to Roll flow through untouched.
///
/// Assertion-loud instead of drift-silent (same policy as the Ancient transpiler): if
/// Roll ever stops having exactly one single-argument NextFloat call site, the
/// transpiler THROWS at patch time — ModPatcher rolls back the whole unknown group.
/// </summary>
[HarmonyPatch(typeof(UnknownMapPointOdds), nameof(UnknownMapPointOdds.Roll))]
public static class ForcedUnknownRollPatch
{
    private static float? _forcedRoll;

    static void Prefix(UnknownMapPointOdds __instance, IEnumerable<RoomType> blacklist, IRunState runState)
    {
        _forcedRoll = null;
        UnknownPickArm.ArmedPick? armed = UnknownPickArm.Armed;
        if (armed == null)
            return;
        try
        {
            if (runState is not RunState state
                || state.VisitedMapCoords.Count == 0
                || !state.VisitedMapCoords[state.VisitedMapCoords.Count - 1].Equals(armed.Coord))
            {
                UnknownPickArm.Disarm();
                MainFile.Logger.Error("[unknown picker] armed pick does not match this travel; vanilla roll");
                return;
            }
            if (state.UnlockState.NumberOfRuns == 0)
            {
                // Vanilla's first-run forcing returns before drawing — no float to feed.
                UnknownPickArm.Disarm();
                return;
            }
            // Re-derive the candidate set from the CALLER's blacklist and re-test the
            // pick — votes were validated at cast time, but state may have moved; every
            // client re-tests identically. The simulation doubles as the safety net for
            // float-precision edges and bucket-order drift: no confirmed landing, no force.
            IReadOnlySet<RoomType> allowed = UnknownPools.AllowedTypes(state, blacklist);
            float? forced = UnknownPools.ForcingFloat(__instance, allowed, armed.Category);
            if (forced is not float roll
                || UnknownPools.SimulateRoll(__instance, allowed, roll) != armed.Category)
            {
                UnknownPickArm.Disarm();
                MainFile.Logger.Error($"[unknown picker] voted {armed.Category} is not rollable here; vanilla roll");
                return;
            }
            _forcedRoll = roll;
            MainFile.Logger.Info($"[unknown picker] forcing ? roll: {armed.Category} (value {roll:0.###})");
        }
        catch (System.Exception e)
        {
            _forcedRoll = null;
            UnknownPickArm.Disarm();
            MainFile.Logger.Error($"[unknown picker] forced roll setup failed, vanilla: {e}");
        }
    }

    /// <summary>Stands in for Roll's `_rng.NextFloat()`: the real draw still advances the stream.</summary>
    public static float ForcedFloat(Rng rng, float max)
    {
        float natural = rng.NextFloat(max);
        if (_forcedRoll is not float forced)
            return natural;
        _forcedRoll = null;
        return forced;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        List<int> sites = new();
        for (int i = 0; i < code.Count; i++)
            if ((code[i].opcode == OpCodes.Callvirt || code[i].opcode == OpCodes.Call)
                && code[i].operand is MethodInfo { Name: "NextFloat" } target
                && target.GetParameters().Length == 1)
                sites.Add(i);
        if (sites.Count != 1)
            throw new System.InvalidOperationException(
                $"expected exactly 1 NextFloat call in UnknownMapPointOdds.Roll, found {sites.Count} — vanilla body changed, refusing to guess");
        int site = sites[0];
        CodeInstruction call = new(OpCodes.Call,
            AccessTools.Method(typeof(ForcedUnknownRollPatch), nameof(ForcedFloat)));
        call.labels.AddRange(code[site].labels);
        code[site] = call; // stack [rng, max] -> float, same as the original callvirt
        return code;
    }

    // The roll is fully vanilla now, so re-check its outcome: a landing that somehow
    // differs from the vote must not leave the content arm alive for the wrong pull.
    static void Postfix(RoomType __result)
    {
        UnknownPickArm.ArmedPick? armed = UnknownPickArm.Armed;
        if (armed == null)
            return;
        if (__result != armed.Category)
        {
            UnknownPickArm.Disarm();
            MainFile.Logger.Error($"[unknown picker] roll produced {__result} instead of voted {armed.Category}; content pick dropped");
        }
        else if (armed.Category is not (RoomType.Event or RoomType.Monster or RoomType.Elite))
        {
            UnknownPickArm.Disarm(); // shop/treasure carry no content pull
        }
    }
}

/// <summary>
/// Content substitution, event half: swap the voted event into the cursor slot before
/// the vanilla pull reads it. The pull itself (validity walk, ModifyNextEvent hook —
/// which may still override the pick, matching vanilla precedence — visited-marking)
/// runs untouched. PullNextEvent only ever serves ?-rolled events (Ancients use
/// PullAncient), so an armed Event vote is always for this pull.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEvent))]
public static class ForcedNextEventPatch
{
    static void Prefix(ActModel __instance, RunState runState)
    {
        UnknownPickArm.ArmedPick? armed = UnknownPickArm.Armed;
        if (armed == null || armed.Category != RoomType.Event)
            return;
        UnknownPickArm.Disarm(); // one-shot: this is the pull the vote was for
        if (armed.Model == null)
            return; // category-only vote: vanilla event within the forced category
        try
        {
            RoomSet rooms = __instance._rooms;
            if (rooms.events.Count == 0)
                return;
            int idx = rooms.events.FindIndex(e => e.Id == armed.Model);
            if (idx < 0
                || !rooms.events[idx].IsAllowed(runState)
                || runState.VisitedEventIds.Contains(armed.Model))
            {
                MainFile.Logger.Error($"[unknown picker] voted event {armed.Model.Entry} not pullable now; vanilla event");
                return;
            }
            int cursor = rooms.eventsVisited % rooms.events.Count;
            (rooms.events[cursor], rooms.events[idx]) = (rooms.events[idx], rooms.events[cursor]);
            MainFile.Logger.Info($"[unknown picker] event swap: {armed.Model.Entry} moved to the pull slot");
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unknown picker] event swap failed, vanilla event: {e}");
        }
    }
}

/// <summary>
/// Content substitution, encounter half: same swap on the act's dealt encounter table.
/// Armed only between the forced category roll and CreateRoom's pull (one synchronous
/// stretch), so a Monster/Elite arm can only meet its own pull; boss pulls and regular
/// monster nodes never see an armed pick.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
public static class ForcedNextEncounterPatch
{
    static void Prefix(ActModel __instance, RoomType roomType)
    {
        UnknownPickArm.ArmedPick? armed = UnknownPickArm.Armed;
        if (armed == null || armed.Category is not (RoomType.Monster or RoomType.Elite))
            return;
        UnknownPickArm.Disarm(); // one-shot
        if (armed.Model == null)
            return;
        try
        {
            if (roomType != armed.Category)
            {
                MainFile.Logger.Error($"[unknown picker] armed {armed.Category} met a {roomType} pull; vanilla encounter");
                return;
            }
            RoomSet rooms = __instance._rooms;
            List<EncounterModel> list = roomType == RoomType.Elite ? rooms.eliteEncounters : rooms.normalEncounters;
            if (list.Count == 0)
                return;
            int idx = list.FindIndex(e => e.Id == armed.Model);
            if (idx < 0)
            {
                MainFile.Logger.Error($"[unknown picker] voted encounter {armed.Model.Entry} not in the dealt table; vanilla encounter");
                return;
            }
            int visited = roomType == RoomType.Elite ? rooms.eliteEncountersVisited : rooms.normalEncountersVisited;
            int cursor = visited % list.Count;
            (list[cursor], list[idx]) = (list[idx], list[cursor]);
            MainFile.Logger.Info($"[unknown picker] encounter swap: {armed.Model.Entry} moved to the pull slot");
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unknown picker] encounter swap failed, vanilla encounter: {e}");
        }
    }
}
