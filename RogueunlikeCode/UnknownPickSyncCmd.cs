using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #6, vote half: every player casts a vote for what the upcoming unknown (?)
/// node should become; the plurality wins for EVERYONE (unlike the per-player Ancient
/// pick, a ? room is one shared canonical room).
///
/// Votes ride the same networked-console channel as the Ancient pick (vanilla
/// ConsoleCmdGameAction through the lockstep action queue): each player's vote is
/// enqueued BEFORE their map travel vote, and travel fires only after every player's
/// map vote — so when RunManager.EnterMapCoord runs, every cast vote has already been
/// recorded identically on every client. Tally is pure over that shared state.
///
/// Tie rule (deterministic on all clients): highest vote count wins; ties break to the
/// option whose earliest vote arrived first in the lockstep stream. Players who never
/// voted for the traveled coord count as "let fate decide" (arrival = after everyone).
/// Fate winning = pure vanilla roll.
/// </summary>
internal static class UnknownPickSync
{
    internal sealed class Vote
    {
        public ulong NetId;
        public bool IsFate;
        public RoomType Category;
        public ModelId? Model;
    }

    private static readonly Dictionary<(int col, int row), List<Vote>> _votes = new();

    /// <summary>Raised whenever a vote lands (live vote strip in the picker).</summary>
    public static event System.Action? Changed;

    public static void Record(MapCoord coord, ulong netId, bool isFate, RoomType category, ModelId? model)
    {
        (int col, int row) key = (coord.col, coord.row);
        if (!_votes.TryGetValue(key, out List<Vote>? list))
            _votes[key] = list = new List<Vote>();
        list.RemoveAll(v => v.NetId == netId); // re-vote replaces; arrival rank moves to the end
        list.Add(new Vote { NetId = netId, IsFate = isFate, Category = category, Model = model });
        Changed?.Invoke();
    }

    public static IReadOnlyList<Vote> VotesFor(MapCoord coord) =>
        _votes.TryGetValue((coord.col, coord.row), out List<Vote>? list)
            ? list
            : (IReadOnlyList<Vote>)System.Array.Empty<Vote>();

    /// <summary>Plurality winner for this coord; null = fate (vanilla roll).</summary>
    public static (RoomType Category, ModelId? Model)? Tally(MapCoord coord, int playerCount)
    {
        IReadOnlyList<Vote> votes = VotesFor(coord);
        // key -> (count, first arrival index); fate keyed separately.
        var buckets = new Dictionary<string, (int count, int first, Vote vote)>();
        for (int i = 0; i < votes.Count; i++)
        {
            Vote v = votes[i];
            string key = v.IsFate ? "fate" : $"{v.Category}|{v.Model?.Entry ?? ""}";
            buckets[key] = buckets.TryGetValue(key, out (int count, int first, Vote vote) b)
                ? (b.count + 1, b.first, b.vote)
                : (1, i, v);
        }
        int silent = playerCount - votes.Count; // never voted for this coord = fate
        if (silent > 0)
        {
            buckets["fate"] = buckets.TryGetValue("fate", out (int count, int first, Vote vote) f)
                ? (f.count + silent, f.first, f.vote)
                : (silent, int.MaxValue, new Vote { IsFate = true });
        }
        if (buckets.Count == 0)
            return null;
        (int count, int first, Vote vote) winner = buckets.Values
            .OrderByDescending(b => b.count).ThenBy(b => b.first).First();
        return winner.vote.IsFate ? null : (winner.vote.Category, winner.vote.Model);
    }

    public static void Clear()
    {
        _votes.Clear();
        Changed?.Invoke();
    }
}

/// <summary>
/// The synced vote message: `rl_unknown &lt;col&gt; &lt;row&gt; &lt;fate|monster|elite|treasure|shop|event&gt; [modelEntry]`,
/// attributed to the issuing player on every client. Public parameterless ctor —
/// DevConsole finds mod commands by reflection.
/// </summary>
public class UnknownPickConsoleCmd : AbstractConsoleCmd
{
    public const string Name = "rl_unknown";

    public override string CmdName => Name;
    public override string Args => "<col:int> <row:int> <fate|monster|elite|treasure|shop|event> [modelEntry:string]";
    public override string Description => "[Rogueunlike] Casts the issuing player's vote for what the unknown (?) node at the given map coord becomes.";
    public override bool IsNetworked => true;
    public override bool DebugOnly => false; // must register in release builds

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        // Never throw: NDevConsole rethrows command exceptions out of the action queue.
        try
        {
            if (issuingPlayer == null || args.Length < 3)
                return new CmdResult(success: false, "issuing player or args missing");
            RunState? state = RunManager.Instance.State;
            if (state == null)
                return new CmdResult(success: false, "no run in progress");
            if (!int.TryParse(args[0], out int col) || !int.TryParse(args[1], out int row))
                return new CmdResult(success: false, "bad coord");
            var coord = new MapCoord(col, row);
            if (state.Map?.GetPoint(coord) is not MapPoint point || point.PointType != MapPointType.Unknown)
                return new CmdResult(success: false, "coord is not an unknown map point");

            if (args[2].Equals("fate", System.StringComparison.OrdinalIgnoreCase))
            {
                UnknownPickSync.Record(coord, issuingPlayer.NetId, isFate: true, default, null);
                MainFile.Logger.Info($"[unknown mp] player {issuingPlayer.NetId} voted fate at {coord}");
                return new CmdResult(success: true, $"[Rogueunlike] player {issuingPlayer.NetId} lets fate decide");
            }

            if (!System.Enum.TryParse(args[2], ignoreCase: true, out RoomType category))
                return new CmdResult(success: false, "bad category");
            // Same law as everywhere in the mod: pickable ⟺ the roll could produce it
            // right now. Derived from shared deterministic state, so every client
            // accepts or rejects identically.
            if (UnknownPools.CategoryChances(state).GetValueOrDefault(category) <= 0f)
                return new CmdResult(success: false, $"{category} cannot occur at this node");

            ModelId? model = null;
            if (args.Length > 3)
            {
                model = ValidateModel(state, category, args[3]);
                if (model == null)
                    MainFile.Logger.Info($"[unknown mp] dropped invalid content '{args[3]}' from player {issuingPlayer.NetId}; category vote kept");
            }
            UnknownPickSync.Record(coord, issuingPlayer.NetId, isFate: false, category, model);
            MainFile.Logger.Info($"[unknown mp] player {issuingPlayer.NetId} voted {category}{(model != null ? $" {model.Entry}" : "")} at {coord}");
            return new CmdResult(success: true, $"[Rogueunlike] player {issuingPlayer.NetId} voted {category}");
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unknown mp] vote command failed: {e}");
            return new CmdResult(success: false, "internal error (see log)");
        }
    }

    // Content must be a member of the exact pool the pull could return (the act's dealt
    // lists) — never anything else. Deterministic across clients.
    private static ModelId? ValidateModel(RunState state, RoomType category, string entry)
    {
        switch (category)
        {
        case RoomType.Event:
        {
            var id = new ModelId(ModelDb.GetCategory(typeof(EventModel)), entry.ToUpperInvariant());
            return UnknownPools.EventPool(state).Any(e => e.Id == id) ? id : null;
        }
        case RoomType.Monster:
        case RoomType.Elite:
        {
            var id = new ModelId(ModelDb.GetCategory(typeof(EncounterModel)), entry.ToUpperInvariant());
            return UnknownPools.EncounterPool(state, category).Any(e => e.Id == id) ? id : null;
        }
        default:
            return null; // shop/treasure carry no content pick
        }
    }
}

// Stale-vote backstop: a fresh run (or reload) starts with an empty vote map.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
public static class UnknownPickResetPatch
{
    static void Postfix()
    {
        UnknownPickSync.Clear();
        UnknownPickArm.Disarm();
    }
}
