using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

// Feature #1's missing half (gap closed in v0.6.0): full-pool picks for the choose-a-card
// flows that bypass the rewards screen — always inside #1's "pick any card the roll could
// produce" promise, just never delivered until the Lead Paperweight report exposed it.
// Two vanilla seams take a CreateForReward roll and put it straight in front of the
// player; both expand through the witnessed-roll gate (see WitnessedRolls): expansion
// happens ⟺ EVERY offered card was witnessed rolling out of CreateForReward, so the
// roll's exact pool is known. That one rule sorts the whole game:
//   • deck-building pickups expand (Lead Paperweight, Hefty Tablet, Massive Scroll via
//     FromChooseACardScreen; Brain Leech and Room Full of Cheese via
//     FromSimpleGridForRewards);
//   • in-combat generators never do (Discovery, the Attack/Skill/Power/Colorless
//     potions, Toolbox roll through GetDistinctForCombat — never witnessed);
//   • hand-built lists never do (not rolls); Sealed Deck self-expands through feature #1
//     (it sets IsCardReward on its own roll), and its cards are feature #1 creations,
//     not witnessed — so these seams leave it alone (no double expansion).
//
// Both prefixes run at Priority.Last and honor __runOriginal, so wrappers other mods put
// on these methods (SteamRandomMatchMod re-invokes them reflectively for its choice
// dedup) compose: their outer call skips us, their re-entrant inner call is the one we
// take over — and we always hand back a real Task.
//
// MP: every client runs these methods in lockstep with identically-rolled lists (that is
// what vanilla's own index sync relies on), and the expansion is deterministic, so the
// vanilla choice protocol keeps working over the expanded index space unchanged. Gated
// on ModWireCheck.SyncReady — unverified real MP stays pure vanilla.
//
// Save-safety: expanded entries are CreateForReward-shaped run cards (CreateCard + the
// origin's upgrade roll); a pick lands exactly where the vanilla roll's pick would.

// Seam A: CardSelectCmd.FromChooseACardScreen — the bespoke ≤3-card fan (it throws above
// 3, so a bigger list needs a different screen). ≤3 after expansion: swap the argument
// and stay fully vanilla (fan UI, protocol, seen-marking). >3: replace the call with our
// own task speaking the exact vanilla choice protocol around ModCardPickModal, pushed
// on the game's NOverlayStack — the same layering the vanilla screen would have used.
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
[HarmonyPriority(Priority.Last)]
public static class ChooseACardExpandPatch
{
    private static readonly HashSet<ulong> _inFlight = new(); // per-player reentrancy guard

    static bool Prefix(PlayerChoiceContext context, ref IReadOnlyList<CardModel> cards,
        Player player, bool canSkip, ref Task<CardModel?> __result, bool __runOriginal)
    {
        if (!__runOriginal)
            return true; // another prefix already skipped the original: not ours to touch
        try
        {
            if (CardSelectCmd.Selector != null // tests/AutoSlay pick blind — keep their list vanilla
                || cards == null || cards.Count == 0
                || player == null
                || _inFlight.Contains(player.NetId)
                || !ModWireCheck.SyncReady(player.RunState))
                return true;
            List<CardCreationOptions>? origins = WitnessedRolls.OriginsOf(cards);
            if (origins == null)
                return true; // hand-built (or already-expanded) list: vanilla
            List<CardModel> extras = WitnessedRolls.ExtraModels(player, cards, origins);
            if (extras.Count == 0)
                return true;
            var expanded = new List<CardModel>(cards.Count + extras.Count);
            expanded.AddRange(cards);
            expanded.AddRange(extras);
            if (expanded.Count <= 3)
            {
                cards = expanded; // tiny pool: the vanilla fan can host it — stay vanilla
                return true;
            }
            __result = PickFlow(context, cards, expanded, WitnessedRolls.PoolsOf(origins), player, canSkip);
            return false;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[choose-a-card] expansion setup failed, vanilla screen: {e}");
            return true;
        }
    }

    // The vanilla method body's protocol, verbatim (ReserveChoiceId → SignalBegun →
    // local pick / remote wait → SyncLocalChoice(FromIndex) → SignalEnded), with the mod
    // grid picker as the local UI. The index rides the same PlayerChoiceSynchronizer
    // stream vanilla uses, into the same deterministic expanded list on every client.
    // Failure policy: once ReserveChoiceId ran, the protocol is COMPLETED no matter what
    // (a -1 skip is always legal) — restarting it (or re-calling the vanilla method)
    // would double-reserve ids and strand remote waiters. Only the local UI has
    // fallbacks: mod picker → vanilla fan with the roll → skip. Protocol calls
    // themselves are unguarded like vanilla's — they fault the caller task exactly where
    // vanilla's would (teardown etc.).
    // Deliberately NOT vanilla here: no bulk MarkCardAsSeen over the offered cards —
    // discovery is candidate-click only (ModSeenGate) while the mod picker is up.
    private static async Task<CardModel?> PickFlow(PlayerChoiceContext context,
        IReadOnlyList<CardModel> original, List<CardModel> expanded, List<CardPoolModel> pools,
        Player player, bool canSkip)
    {
        _inFlight.Add(player.NetId);
        try
        {
            PlayerChoiceSynchronizer sync = RunManager.Instance.PlayerChoiceSynchronizer;
            uint choiceId = sync.ReserveChoiceId(player);
            await context.SignalPlayerChoiceBegun(PlayerChoiceOptions.None);
            CardModel? chosen = null;
            if (LocalContext.IsMe(player) && RunManager.Instance.NetService.Type != NetGameType.Replay)
            {
                NPlayerHand.Instance?.CancelAllCardPlay();
                try
                {
                    chosen = await ModCardPickModal.AttachAsOverlay(player, expanded, pools, canSkip).Result;
                }
                catch (Exception e)
                {
                    // UI failure inside the protocol: degrade to the vanilla fan with the
                    // rolled options. Their indexes are valid in `expanded` (it starts
                    // with them), so the sync below stays correct either way.
                    MainFile.Logger.Error($"[choose-a-card] mod picker failed, vanilla fan with the roll: {e}");
                    try
                    {
                        NChooseACardSelectionScreen? screen = NChooseACardSelectionScreen.ShowScreen(original, canSkip);
                        if (screen != null)
                            chosen = (await screen.CardsSelected()).FirstOrDefault();
                    }
                    catch (Exception e2)
                    {
                        MainFile.Logger.Error($"[choose-a-card] vanilla fan failed too; skipping: {e2}");
                    }
                }
                int index = chosen != null ? expanded.IndexOf(chosen) : -1;
                sync.SyncLocalChoice(player, choiceId, PlayerChoiceResult.FromIndex(index));
            }
            else
            {
                int index = (await sync.WaitForRemoteChoice(player, choiceId)).AsIndex();
                chosen = index >= 0 && index < expanded.Count ? expanded[index] : null;
            }
            await context.SignalPlayerChoiceEnded();
            MainFile.Logger.Info($"[choose-a-card] player {player.NetId} chose "
                + $"{chosen?.Id.Entry ?? "nothing"} of {expanded.Count} options");
            return chosen;
        }
        finally
        {
            _inFlight.Remove(player.NetId);
        }
    }
}

// Seam B: CardSelectCmd.FromSimpleGridForRewards — the grid select over a rolled list
// (Brain Leech's pick-1-of-5, Room Full of Cheese's pick-2-of-8). The vanilla screen
// hosts arbitrary list sizes and the whole choice protocol lives inside the method, so
// expansion is just an argument swap — every client swaps identically and the
// FromIndexes sync maps into the same list. Constraints that keep every outcome
// vanilla-reachable:
//   • single roll origin only — with several origins the roll caps picks per origin
//     (Sea Glass's 5C/5U/5R) and the screen's one global max cannot express that;
//   • prefs.MaxSelect ≤ rolled count — any MaxSelect-sized subset of one origin's pool
//     can then co-occur in one roll of that size (rolls are distinct-draw).
// Re-entrant calls (wrapper mods re-invoking) are naturally idempotent: the swapped
// list's extras were not created by CreateForReward, so the witness gate fails.
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards))]
[HarmonyPriority(Priority.Last)]
public static class SimpleGridRewardsExpandPatch
{
    static void Prefix(ref List<CardCreationResult> cards, Player player, CardSelectorPrefs prefs,
        bool __runOriginal)
    {
        if (!__runOriginal)
            return;
        try
        {
            if (CardSelectCmd.Selector != null
                || cards == null || cards.Count == 0
                || player == null
                || prefs.MaxSelect > cards.Count
                || !ModWireCheck.SyncReady(player.RunState))
                return;
            List<CardCreationOptions>? origins = WitnessedRolls.OriginsOf(cards.Select(r => r?.Card));
            if (origins == null || origins.Count != 1)
                return; // hand-built, already expanded, or multi-origin (quota-bound)
            List<CardModel> extras = WitnessedRolls.ExtraModels(player, cards.Select(r => r.Card), origins);
            if (extras.Count == 0)
                return;
            var expanded = new List<CardCreationResult>(cards);
            expanded.AddRange(extras.Select(c => new CardCreationResult(c)));
            cards = expanded;
            MainFile.Logger.Info($"[card grid select] rolled grid expanded to {expanded.Count} options");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[card grid select] expansion failed, rolled grid kept: {e}");
        }
    }
}
