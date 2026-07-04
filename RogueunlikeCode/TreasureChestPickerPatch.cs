using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #3.1: the treasure chest as a "Grand Relic Selection" — every player picks
/// from the whole valid pool via the feature-#3 relic picker.
///
///   • The table shows the rolled relic(s) SHADED; any table click opens the picker
///     (no direct vote). A shaded relic reveals once somebody votes for it.
///   • Confirming casts your vote: a net-synced INDEX into the synchronizer's shared
///     _currentRelics list, deterministically expanded with the rest of the pool — so
///     vanilla actions, vote icons and hands all keep working.
///   • When every unresolved player has confirmed, the round resolves: sole voters take
///     their relic, contested relics run the vanilla RPS fight. Losers re-pick over the
///     shrunken pool; rounds repeat until everyone is resolved.
///   • Untaken rolled relics get vanilla's end-of-picking treatment (MoveToFallback);
///     untaken extras never left the bag.
///
/// Real MP requires every client modded (wire-gated): an unmodded client rejects votes
/// for indices beyond its unexpanded list.
/// </summary>
public static class TreasureChestPicker
{
    // RelicFactory.RollRarity outcomes — what a chest can pull from the shared bag.
    private static readonly RelicRarity[] ChestRarities =
        { RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare };

    // ponytail: dynamic holders go in rows under the authored four slots; tune by eye.
    private const float ExtraRowOffset = 280f;

    // Null when a game update renames the event — RaiseVotesChanged then no-ops (vote
    // icons stop refreshing on remote votes; resolution itself is unaffected). The
    // startup health check reports this case loudly.
    private static readonly FieldInfo? VotesChangedField = AccessTools.Field(
        typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.VotesChanged));

    // Per-room state, reset on every BeginRelicPicking.
    public static int RolledCount;
    public static List<RelicModel>? Pool;                  // the expanded _currentRelics (same object)
    public static readonly HashSet<RelicModel> Extras = new();
    public static NTreasureRoomRelicCollection? Collection;
    public static ModRelicPickerUi? OpenPicker;
    private static readonly HashSet<int> AwardedIndices = new();
    private static readonly HashSet<Player> Resolved = new();       // has a relic, or skipped
    private static readonly HashSet<NTreasureRoomRelicHolder> Shaded = new();
    private static readonly Dictionary<int, NTreasureRoomRelicHolder> DynamicHolders = new();
    private static Vector2[] _slotPositions = Array.Empty<Vector2>();
    private static int _cloneCount;
    private static bool _awardsBegan;

    private static void RaiseVotesChanged(TreasureRoomRelicSynchronizer sync) =>
        (VotesChangedField?.GetValue(sync) as Action)?.Invoke();

    // ---- pool expansion (state side; runs on every client identically) ----

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
    public static class BeginRelicPickingPatch
    {
        static void Postfix(TreasureRoomRelicSynchronizer __instance)
        {
            RolledCount = 0;
            Pool = null;
            Extras.Clear();
            AwardedIndices.Clear();
            Resolved.Clear();
            Shaded.Clear();
            DynamicHolders.Clear();
            _awardsBegan = false;
            RoundAnimator.Reset();
            ChestPhaseScreenTypePatch.Reset();
            List<RelicModel>? relics = __instance._currentRelics;
            if (relics == null || relics.Count == 0)
                return; // empty chest (SilverCrucible etc.) — leave it empty
            RolledCount = relics.Count;
            // No verified mod handshake in real MP -> an unverified client would not
            // expand the shared vote list identically; leave Pool null = vanilla chest
            // (the same degradation the expansion-failure path below uses).
            if (!ModWireCheck.SyncReady(__instance._playerCollection.Players.FirstOrDefault()?.RunState))
            {
                MainFile.Logger.Info("[chest picker] wire check not verified; vanilla chest");
                return;
            }
            try
            {
                // Everything PullFromFront could still return for a chest roll: the shared
                // bag's C/U/R deques, run-allowed only. The rolled relics were already
                // pulled out of the deques, so rolled ∩ extras = ∅ by construction.
                // ponytail: ignores the bag's dry-deque rarity escalation, same as feature #3.
                IRunState runState = __instance._playerCollection.Players[0].RunState;
                foreach (RelicRarity rarity in ChestRarities)
                    if (__instance._sharedGrabBag._deques.TryGetValue(rarity, out List<RelicModel>? deque))
                        foreach (RelicModel relic in deque)
                            if (relic.IsAllowed(runState) && !relics.Contains(relic) && Extras.Add(relic))
                                relics.Add(relic);
                Pool = relics;
                MainFile.Logger.Info($"[chest picker] pool: {RolledCount} rolled + {Extras.Count} extras");
            }
            catch (Exception e)
            {
                // Roll back so every client shows and votes on the same (vanilla) list.
                if (relics.Count > RolledCount)
                    relics.RemoveRange(RolledCount, relics.Count - RolledCount);
                Pool = null;
                Extras.Clear();
                MainFile.Logger.Error($"[chest picker] pool expansion failed, vanilla chest: {e}");
            }
        }
    }

    // ---- round-based vote resolution (replaces vanilla AwardRelics entirely) ----

    // Vanilla OnPicked resolves once, when every vote is in. Rounds instead: resolve the
    // unresolved players, award sole voters, fight over contested relics, reset losers'
    // votes, repeat. All of it runs inside the synced PickRelicAction on every client,
    // so ordering (and RNG use, sorted by pool index) is identical everywhere.
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
    public static class OnPickedPatch
    {
        static bool Prefix(TreasureRoomRelicSynchronizer __instance, Player player, int? index)
        {
            if (Pool == null)
                return true; // expansion inactive — pure vanilla
            OnPickedRounds(__instance, player, index);
            return false;
        }

        private static void OnPickedRounds(TreasureRoomRelicSynchronizer sync, Player player, int? index)
        {
            List<RelicModel>? relics = sync._currentRelics;
            if (relics == null)
            {
                MainFile.Logger.Warn("[chest picker] vote arrived while picking inactive");
                return;
            }
            if (index >= relics.Count)
                throw new IndexOutOfRangeException(
                    $"Attempted to pick relic at index {index}, but there are only {relics.Count} to choose from!");
            if (!index.HasValue && sync._playerCollection.Players.Count == 1)
            {
                sync._singleplayerSkipped = true; // vanilla singleplayer skip fast-path
                return;
            }
            if (Resolved.Contains(player))
            {
                MainFile.Logger.Warn($"[chest picker] ignoring vote from already-resolved player {player.NetId}");
                return;
            }
            TreasureRoomRelicSynchronizer.PlayerVote vote =
                sync._votes[sync._playerCollection.GetPlayerSlotIndex(player)];
            if (index is int picked && AwardedIndices.Contains(picked))
            {
                // Their pick got awarded while the vote was in flight: force a re-pick.
                vote.index = null;
                vote.voteReceived = false;
                if (player == sync.LocalPlayer)
                {
                    sync._predictedVote = null;
                    if (Collection != null && GodotObject.IsInstanceValid(Collection))
                        OpenMenu(Collection);
                }
                RaiseVotesChanged(sync);
                return;
            }
            vote.index = index;
            vote.voteReceived = true;
            if (player == sync.LocalPlayer)
                sync._predictedVote = null; // the real vote landed; prediction served its purpose
            RaiseVotesChanged(sync);
            if (AllUnresolvedVoted(sync))
                ResolveRound(sync);
        }
    }

    private static bool AllUnresolvedVoted(TreasureRoomRelicSynchronizer sync)
    {
        IReadOnlyList<Player> players = sync._playerCollection.Players;
        for (int i = 0; i < sync._votes.Count; i++)
            if (!Resolved.Contains(players[i]) && !sync._votes[i].voteReceived)
                return false;
        return true;
    }

    private static void ResolveRound(TreasureRoomRelicSynchronizer sync)
    {
        List<RelicModel> relics = sync._currentRelics!;
        IReadOnlyList<Player> players = sync._playerCollection.Players;

        var byIndex = new SortedDictionary<int, List<Player>>(); // ascending: deterministic RNG order
        var newlySkipped = new List<Player>();
        for (int i = 0; i < sync._votes.Count; i++)
        {
            Player p = players[i];
            if (Resolved.Contains(p))
                continue;
            TreasureRoomRelicSynchronizer.PlayerVote vote = sync._votes[i];
            if (vote.index is not int idx)
            {
                Resolved.Add(p); // skipped: out of the running, no relic (vanilla semantics)
                newlySkipped.Add(p);
                continue;
            }
            if (!byIndex.TryGetValue(idx, out List<Player>? voters))
                byIndex[idx] = voters = new List<Player>();
            voters.Add(p);
        }

        var results = new List<RelicPickingResult>();
        RelicPickingFightMove[] moves = Enum.GetValues<RelicPickingFightMove>();
        foreach ((int idx, List<Player> voters) in byIndex)
        {
            RelicModel relic = relics[idx];
            RelicPickingResult result = voters.Count == 1
                ? new RelicPickingResult
                {
                    type = RelicPickingResultType.OnlyOnePlayerVoted,
                    relic = relic,
                    player = voters[0],
                }
                : RelicPickingResult.GenerateRelicFight(voters, relic, () => sync._rng.NextItem(moves));
            Player winner = result.player!;
            AwardedIndices.Add(idx);
            Resolved.Add(winner);
            ModSeenGate.MarkPicked(relic); // definitive discovery even under table suppression
            // Grant in the state layer so bag mutations happen in the same order on every
            // client; fire-and-forget exactly like vanilla's award loop.
            TaskHelper.RunSafely(RelicCmd.Obtain(relic.ToMutable(), winner));
            foreach (Player other in players)
                if (other != winner)
                    other.RelicGrabBag.MoveToFallback(relic); // vanilla dedup across players
            foreach (Player loser in voters)
            {
                if (loser == winner)
                    continue;
                TreasureRoomRelicSynchronizer.PlayerVote lv =
                    sync._votes[sync._playerCollection.GetPlayerSlotIndex(loser)];
                lv.index = null;
                lv.voteReceived = false; // back to the menu
            }
            results.Add(result);
        }

        // Fake multiplayer: bot losers can't reopen a menu — re-vote them immediately,
        // uniformly over what's left (mirrors BeginRelicPicking's random bot votes).
        if (RunManager.Instance.IsSingleplayerOrFakeMultiplayer && players.Count > 1)
        {
            foreach (Player p in players)
            {
                if (Resolved.Contains(p) || p == sync.LocalPlayer)
                    continue;
                TreasureRoomRelicSynchronizer.PlayerVote vote =
                    sync._votes[sync._playerCollection.GetPlayerSlotIndex(p)];
                if (vote.voteReceived)
                    continue;
                var candidates = new List<int>();
                for (int i = 0; i < relics.Count; i++)
                    if (!AwardedIndices.Contains(i))
                        candidates.Add(i);
                vote.index = candidates.Count > 0 ? candidates[sync._rng.NextInt(candidates.Count)] : null;
                vote.voteReceived = true;
            }
        }

        bool done = players.All(p => Resolved.Contains(p));
        if (done)
        {
            // Table leftovers nobody took: vanilla end-of-picking treatment. (They were
            // pulled from the shared bag at Begin; untaken extras never left it.)
            for (int i = 0; i < RolledCount; i++)
                if (!AwardedIndices.Contains(i))
                    foreach (Player p in players)
                        p.RelicGrabBag.MoveToFallback(relics[i]);
            sync.EndRelicVoting();
        }
        RaiseVotesChanged(sync);
        RoundAnimator.Enqueue(results, newlySkipped, done);
        if (!done && AllUnresolvedVoted(sync))
            ResolveRound(sync); // fake-MP bots collided again — cascade until stable
    }

    // ---- per-round award ceremony (display only; the state is already settled) ----

    private static class RoundAnimator
    {
        private static Task _chain = Task.CompletedTask;

        public static void Reset() => _chain = Task.CompletedTask;

        public static void Enqueue(List<RelicPickingResult> results, List<Player> skipped, bool done)
        {
            NTreasureRoomRelicCollection? col = Collection;
            if (col == null || !GodotObject.IsInstanceValid(col))
                return; // no local chest UI — nothing awaits the ceremony
            Task prev = _chain;
            _chain = TaskHelper.RunSafely(Run());
            async Task Run()
            {
                await prev; // rounds animate strictly in order
                await AnimateRound(col, results, skipped, done);
            }
        }

        private static async Task AnimateRound(NTreasureRoomRelicCollection col,
            List<RelicPickingResult> results, List<Player> skipped, bool done)
        {
            try
            {
                if (!_awardsBegan)
                {
                    _awardsBegan = true;
                    col._relicPickingBeganTaskCompletionSource.TrySetResult();
                }
                foreach (Player p in skipped)
                    col._hands.GetHand(p.NetId)?.SetSkipped();
                col._hands.BeforeRelicsAwarded(); // freeze hands for the ceremony
                results.Sort((a, b) => a.type.CompareTo(b.type)); // vanilla presentation order
                var grabs = new List<Task>();
                foreach (RelicPickingResult result in results)
                {
                    NTreasureRoomRelicHolder? holder = HolderFor(col, result);
                    if (holder == null)
                        continue; // display only — the award itself already happened
                    Unshade(holder);
                    holder.AnimateAwayVotes();
                    if (result.type == RelicPickingResultType.FoughtOver && result.fight != null)
                    {
                        Vector2 home = holder.GlobalPosition;
                        holder.ZIndex = 1;
                        col._fightBackstop.Visible = true;
                        Tween tween = col.CreateTween();
                        tween.TweenProperty(holder, "global_position",
                                (col._fightBackstop.Size - holder.Size) * 0.5f, 0.25)
                            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
                        tween.TweenProperty(col._fightBackstop, "modulate:a", 1f, 0.25);
                        col._hands.BeforeFightStarted(result.fight.playersInvolved);
                        await tween.AwaitFinished(col._cts.Token);
                        await Cmd.Wait(1f, col._cts.Token);
                        await col._hands.DoFight(result, holder);
                        tween = col.CreateTween();
                        tween.TweenProperty(col._fightBackstop, "modulate:a", 0f, 0.25);
                        // Unlike vanilla's one-shot ceremony, later rounds reuse the table.
                        tween.TweenProperty(holder, "global_position", home, 0.25);
                        await tween.AwaitFinished(col._cts.Token);
                        col._fightBackstop.Visible = false;
                        holder.ZIndex = 0;
                    }
                    NHandImage? hand = col._hands.GetHand(result.player!.NetId);
                    if (hand != null)
                    {
                        grabs.Add(TaskHelper.RunSafely(hand.GrabRelic(holder)));
                        await Cmd.Wait(0.25f, col._cts.Token);
                    }
                }
                await Task.WhenAll(grabs);
                if (col._runState.Players.Count == 1)
                    foreach (RelicPickingResult result in results)
                        if (HolderFor(col, result) is { } taken)
                            taken.Visible = false; // vanilla hides the taken relic in singleplayer
                foreach (Player p in col._runState.Players)
                    if (!Resolved.Contains(p))
                        col._hands.GetHand(p.NetId)?.SetFrozenForRelicAwards(false); // losers wave on
                if (!done && Pool != null)
                {
                    Player? me = LocalContext.GetMe(col._runState);
                    if (me != null && !Resolved.Contains(me))
                        OpenMenu(col); // the loser reopens the updated menu
                }
            }
            finally
            {
                if (done)
                    col._relicPickingCompleteTaskCompletionSource.TrySetResult(); // NTreasureRoom proceeds
            }
        }

        private static NTreasureRoomRelicHolder? HolderFor(NTreasureRoomRelicCollection col, RelicPickingResult result)
        {
            int idx = Pool?.IndexOf(result.relic) ?? -1;
            if (idx >= 0)
                EnsureHolderFor(idx);
            return col._holdersInUse.FirstOrDefault(h => h.Relic?.Model == result.relic);
        }
    }

    // ---- table layout (display side) ----

    // Vanilla InitializeRelics branches on CurrentRelics.Count, which our expansion turned
    // into the whole pool — it would lay the first four pool relics on the table and lose
    // the singleplayer layout. Replacement: identical layout driven by RolledCount, with
    // the rolled relics shaded (feature-#3 reward-row look) and every click opening the
    // Grand menu instead of voting directly.
    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics))]
    public static class InitializeRelicsPatch
    {
        static bool Prefix(NTreasureRoomRelicCollection __instance)
        {
            if (Pool == null)
                return true; // empty chest or expansion rolled back — pure vanilla
            try
            {
                Init(__instance);
                return false;
            }
            catch (Exception e)
            {
                MainFile.Logger.Error($"[chest picker] table init failed, vanilla layout: {e}");
                return true;
            }
        }

        private static void Init(NTreasureRoomRelicCollection col)
        {
            List<RelicModel> pool = Pool!;
            IRunState runState = col._runState;
            Collection = col;
            DynamicHolders.Clear();
            Shaded.Clear();
            _cloneCount = 0;
            _slotPositions = col._multiplayerHolders.Select(h => h.Position).ToArray();
            col._holdersInUse.Clear();
            // Browsing/hovering the shaded table must not "discover" the rolled relics.
            ModSeenGate.SuppressWhile(col);

            if (RolledCount == 1)
            {
                NTreasureRoomRelicHolder sp = col.SingleplayerRelicHolder;
                sp.Initialize(pool[0], runState);
                sp.Visible = true;
                sp.Index = 0;
                Shade(sp);
                sp.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OpenMenu(col)));
                col._holdersInUse.Add(sp);
                foreach (NTreasureRoomRelicHolder holder in col._multiplayerHolders)
                    holder.Visible = false;
            }
            else
            {
                col.SingleplayerRelicHolder.Visible = false;
                for (int i = 0; i < col._multiplayerHolders.Count; i++)
                {
                    NTreasureRoomRelicHolder holder = col._multiplayerHolders[i];
                    if (i < RolledCount)
                    {
                        holder.Visible = true;
                        holder.Initialize(pool[i], runState);
                        Shade(holder);
                    }
                    else
                    {
                        holder.Visible = false; // spare — claimable by EnsureHolderFor
                    }
                    holder.Index = i;
                    holder.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OpenMenu(col)));
                    col._holdersInUse.Add(holder);
                    holder.VoteContainer.RefreshPlayerVotes();
                }
                List<NTreasureRoomRelicHolder> inUse = col._holdersInUse;
                for (int i = 0; i < inUse.Count; i++)
                {
                    inUse[i].SetFocusMode(Control.FocusModeEnum.All);
                    inUse[i].FocusNeighborTop = inUse[i].GetPath();
                    inUse[i].FocusNeighborBottom = inUse[i].GetPath();
                    inUse[i].FocusNeighborLeft = (i <= 0 ? inUse[^1] : inUse[i - 1]).GetPath();
                    inUse[i].FocusNeighborRight = (i < inUse.Count - 1 ? inUse[i + 1] : inUse[0]).GetPath();
                }
                if (RolledCount == 2)
                    col._multiplayerHolders[1].Position = col._multiplayerHolders[3].Position;
            }

            RunManager.Instance.TreasureRoomRelicSynchronizer.VotesChanged += OnVotesChanged;
            OnVotesChanged(); // holders/unshades for votes cast before this client opened its chest
        }
    }

    // The shaded holder's hover tip would reveal (and misdescribe) the rolled relic.
    // Vanilla OnFocus must still run (button hover state, scale tween), so swap the tip
    // afterwards — ModSeenGate suppression covers the vanilla tip's seen-marking.
    [HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnFocus")]
    public static class ShadedHolderTipPatch
    {
        static void Postfix(NTreasureRoomRelicHolder __instance)
        {
            if (!Shaded.Contains(__instance))
                return;
            NHoverTipSet.Remove(__instance);
            HoverTip tip = default;
            tip.Id = "Rogueunlike.SelectChestRelic";
            tip.Title = ModUi.SelectRelicLabel;
            tip.Description = ModUi.Loc("ROGUEUNLIKE.SELECT_RELIC.chest_tip",
                "Opens the Relic Collection so you can pick any relic this chest could have dropped. "
                + "The shaded relic is the chest's original roll — it is revealed once someone votes for it.");
            NHoverTipSet.CreateAndShow(__instance, new IHoverTip[] { tip })?.SetAlignmentForRelic(__instance.Relic);
        }
    }

    // ---- the Grand Relic Selection menu ----

    private static async void OpenMenu(NTreasureRoomRelicCollection col)
    {
        try
        {
            TreasureRoomRelicSynchronizer sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (Pool == null || sync._currentRelics == null || OpenPicker != null)
                return; // picking over, or a picker is already up
            Player? me = LocalContext.GetMe(col._runState);
            if (me == null || Resolved.Contains(me))
                return; // already have a relic (or skipped) — no vote to cast
            HashSet<RelicModel> valid = Pool.Where((relic, i) => !AwardedIndices.Contains(i)).ToHashSet();
            ModRelicPickerUi picker = ModRelicPickerUi.Attach(col, me, valid);
            OpenPicker = picker;
            RelicModel? choice = await picker.Result;
            OpenPicker = null;
            if (choice == null || sync._currentRelics == null)
                return; // cancelled, or picking completed while browsing
            int idx = Pool.IndexOf(choice);
            if (idx < 0 || AwardedIndices.Contains(idx))
                return; // stale by the time they confirmed; a holder click reopens
            // Holder first: in singleplayer the vote resolves immediately and the round
            // ceremony looks the relic's holder up by model.
            EnsureHolderFor(idx);
            sync.PickRelicLocally(idx);
        }
        catch (Exception e)
        {
            OpenPicker = null;
            MainFile.Logger.Error($"[chest picker] menu flow failed: {e}");
        }
    }

    // ---- holders for votes beyond the rolled table ----

    // Any received vote gets a table holder, so vote icons / fight staging / hand grabs
    // find their target; votes on shaded rolled relics reveal them (the vote itself is
    // public knowledge). Fires on every vote change — before rounds resolve, since
    // OnPicked raises VotesChanged ahead of ResolveRound.
    private static void OnVotesChanged()
    {
        NTreasureRoomRelicCollection? col = Collection;
        if (col == null || Pool == null || !GodotObject.IsInstanceValid(col))
            return;
        TreasureRoomRelicSynchronizer sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (sync._currentRelics == null)
            return; // picking already over
        foreach (Player player in col._runState.Players)
        {
            TreasureRoomRelicSynchronizer.PlayerVote vote = sync.GetPlayerVote(player);
            if (vote is not { voteReceived: true, index: int idx } || idx < 0 || idx >= Pool.Count)
                continue;
            EnsureHolderFor(idx);
            if (idx < RolledCount)
                UnshadeIndex(col, idx); // a candidate click revealed it — permanently
        }
    }

    private static void EnsureHolderFor(int index)
    {
        NTreasureRoomRelicCollection? col = Collection;
        if (col == null || Pool == null || !GodotObject.IsInstanceValid(col))
            return;
        if (index < RolledCount || DynamicHolders.ContainsKey(index))
            return; // rolled relics own the authored slots; dynamic ones are reused
        try
        {
            NTreasureRoomRelicHolder holder =
                col._multiplayerHolders.FirstOrDefault(h => !h.Visible) ?? CloneHolder(col);
            if (_slotPositions.Length > 0)
            {
                int dyn = DynamicHolders.Count;
                holder.Position = _slotPositions[dyn % _slotPositions.Length]
                    + new Vector2(0f, ExtraRowOffset * (1 + dyn / _slotPositions.Length));
            }
            holder.Visible = true;
            holder.Index = index;
            holder.VoteContainer._allPlayers.Clear(); // Initialize appends; avoid duplicates
            holder.Initialize(Pool[index], col._runState);
            if (!col._holdersInUse.Contains(holder))
                col._holdersInUse.Add(holder);
            holder.VoteContainer.RefreshPlayerVotes(); // catch the vote that spawned us
            DynamicHolders[index] = holder;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[chest picker] holder for pool #{index} failed: {e}");
        }
    }

    private static NTreasureRoomRelicHolder CloneHolder(NTreasureRoomRelicCollection col)
    {
        // All four authored slots taken (3-4 players all picking off-table). Clone one —
        // groups+scripts only: copied signal connections would fire the template's handler.
        NTreasureRoomRelicHolder template = col._multiplayerHolders[_cloneCount % col._multiplayerHolders.Count];
        _cloneCount++;
        var dup = (NTreasureRoomRelicHolder)template.Duplicate(
            (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts));
        template.GetParent().AddChildSafely(dup); // _Ready runs here, binding its fields
        foreach (Node child in dup.VoteContainer.GetChildren())
            child.QueueFreeSafely(); // duplicated vote icons are untracked ghosts
        dup._uncommonGlow.Visible = false;
        dup._rareGlow.Visible = false;
        dup.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            NTreasureRoomRelicCollection? c = Collection;
            if (c != null && GodotObject.IsInstanceValid(c))
                OpenMenu(c);
        }));
        return dup;
    }

    // ---- shading (feature-#3 reward-row look for the not-yet-claimed rolled relics) ----

    private static void Shade(NTreasureRoomRelicHolder holder)
    {
        NRelic? relicNode = holder.Relic;
        if (relicNode == null)
            return;
        relicNode.Icon.SelfModulate = StsColors.ninetyPercentBlack;
        relicNode.Outline.SelfModulate = StsColors.halfTransparentWhite;
        Shaded.Add(holder);
    }

    private static void Unshade(NTreasureRoomRelicHolder holder)
    {
        if (!Shaded.Remove(holder))
            return;
        NRelic? relicNode = holder.Relic;
        if (relicNode == null)
            return;
        relicNode.Icon.SelfModulate = Colors.White;
        relicNode.Outline.SelfModulate = Colors.White;
    }

    private static void UnshadeIndex(NTreasureRoomRelicCollection col, int index)
    {
        foreach (NTreasureRoomRelicHolder holder in col._holdersInUse)
            if (holder.Index == index)
            {
                Unshade(holder);
                return;
            }
    }

    // ---- teardown ----

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "_ExitTree")]
    public static class CollectionTeardownPatch
    {
        static void Postfix(NTreasureRoomRelicCollection __instance)
        {
            if (Collection != __instance)
                return;
            RunManager.Instance.TreasureRoomRelicSynchronizer.VotesChanged -= OnVotesChanged;
            Collection = null;
            OpenPicker = null; // the picker node dies with the room and self-resolves
            DynamicHolders.Clear();
            Shaded.Clear();
            ChestPhaseScreenTypePatch.Reset(); // hands died with the room; nothing to restore
        }
    }

    // ---- mid-round rewards overlap: the hand stays THE pointer ----

    // A relic won mid-rounds can pop a rewards overlay (Cauldron-style potions) while
    // shared picking is still live — an overlap vanilla's one-round chest never makes.
    // A Rewards report would let NHandImageCollection re-assert the OS cursor on every
    // peer tick (blinking cursor over the still-shown hands), so the chest phase keeps
    // reporting SharedRelicPicking: cursor hidden, remote arrows suppressed, hands in.
    // Pause/deck-view overlays are NOT rewritten — menus keep the OS cursor. While the
    // overlap is live the hand layer is z-lifted above the overlay stack (same canvas
    // layer; vanilla z-bumps hands for RPS fights) so hands render over the rewards
    // rows and the pickers on them; lift and restore ride the tracker's own re-syncs.
    [HarmonyPatch(typeof(ScreenStateTracker), "GetCurrentScreen")]
    public static class ChestPhaseScreenTypePatch
    {
        private const int HandsAboveOverlays = 10;
        private static int _priorZ;
        private static bool _lifted;

        internal static void Reset() => _lifted = false;

        static void Postfix(ScreenStateTracker __instance, ref NetScreenType __result)
        {
            bool overlap = __result == NetScreenType.Rewards
                && Pool != null
                && __instance._isInSharedRelicPicking;
            if (overlap)
                __result = NetScreenType.SharedRelicPicking;
            SetHandsLifted(overlap);
        }

        private static void SetHandsLifted(bool lift)
        {
            if (lift == _lifted)
                return;
            if (Collection is not { } col || !GodotObject.IsInstanceValid(col)
                || col._hands is not { } hands)
                return;
            if (lift)
            {
                _priorZ = hands.ZIndex;
                hands.ZIndex = HandsAboveOverlays;
            }
            else
            {
                hands.ZIndex = _priorZ;
            }
            _lifted = lift;
        }
    }
}
