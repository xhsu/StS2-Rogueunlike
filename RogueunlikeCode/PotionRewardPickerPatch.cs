using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

// Feature #2: a potion reward no longer grants its pre-rolled potion — the row reads
// "Select a Potion" and clicking it opens a Potion Lab-style picker (ModPotionPickerUi)
// offering every potion the reward could have rolled. The rolled potion is kept
// underneath, so cancelling, save/reload and every non-picker path stay pure vanilla.
// NRewardButton.GetReward is the only real-gameplay take-path (the other caller of
// SelectLocalReward is test-mode only), so these patches are the whole seam.
public static class PotionRewardPicker
{
    private static readonly object Marker = new();
    // Rewards whose potion we watched Populate() roll from the standard pool — exactly
    // the pool the picker offers. Predetermined rewards (new PotionReward(potion, player):
    // Potion Courier, tutorials, event-rolled potions) never enter — no roll, no choice,
    // and the standard pool would be the wrong roster — the row stays vanilla, like the
    // relic seam's _predeterminedRelic guard (a field PotionReward lacks, hence this
    // witness). In-memory only; MP-consistent (generation runs identically everywhere).
    private static readonly ConditionalWeakTable<PotionReward, object> Rolled = new();

    internal static void MarkRolled(PotionReward reward) => Rolled.TryAdd(reward, Marker);

    // ---- event-rolled predetermined rewards (feature #2's missing half, closed in v0.6.0) ----
    //
    // Five vanilla sources roll a potion with PlayerRng.Rewards.NextItem over the
    // character+shared unlocked set (Potion Courier's Ransack filters to Uncommon) and
    // wrap the result as a PREDETERMINED PotionReward — a roll the standard witness
    // above can't see. Their handlers are bracketed (RolledPotionScopePatches) and every
    // predetermined PotionReward constructed inside a bracket is tagged with the
    // bracket's pool filter; the picker then offers exactly that pool. Fixed-potion
    // rewards (Foul Potion, Glowwater) construct outside any bracket and stay vanilla.
    // In-memory only; tags exist identically on every client (event handlers run
    // everywhere in lockstep, like the reward replicas they build).
    internal sealed class EventRollScope
    {
        public PotionRarity? Rarity; // null = the full character+shared unlocked set
    }

    private static readonly ConditionalWeakTable<PotionReward, EventRollScope> ScopeTagged = new();

    internal static EventRollScope? CurrentScope;

    internal static void TagIfInScope(PotionReward reward)
    {
        if (CurrentScope is { } scope)
            ScopeTagged.AddOrUpdate(reward, scope);
    }

    internal static bool TryGetScope(PotionReward reward, out EventRollScope? scope) =>
        ScopeTagged.TryGetValue(reward, out scope);

    /// <summary>The exact pool those events roll from: character + shared unlocked, optionally rarity-filtered.</summary>
    internal static HashSet<PotionModel> EventPool(Player player, EventRollScope scope) =>
        player.Character.PotionPool.GetUnlockedPotions(player.UnlockState)
            .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(player.UnlockState))
            .Where(p => scope.Rarity == null || p.Rarity == scope.Rarity)
            .ToHashSet();

    // Multiplayer: remote peers re-run OnSelect against their replica of this reward, so
    // the pick is broadcast (RewardPickMessage, same FIFO channel as the vanilla claim)
    // right before the claim. Rewards a claim couldn't address on the wire (nested in a
    // linked set) stay vanilla in MP — the same rows vanilla itself syncs by index.
    public static bool IsActiveFor(Reward? reward) =>
        reward is PotionReward { IsPopulated: true } potionReward
        && (Rolled.TryGetValue(potionReward, out _) || ScopeTagged.TryGetValue(potionReward, out _))
        && ModWireCheck.SyncReady(potionReward.Player.RunState)
        && (potionReward.Player.RunState.Players.Count == 1
            || ModPickNet.TryResolveWireAddress(potionReward, out _, out _));
}

// The event-handler brackets, MoveNext-level (MethodType.Async): the scope opens on
// every resumption slice of the handler and closes when the slice yields (finalizer, so
// throws close it too) — the reward ctor lands in one of those slices no matter how many
// awaits precede the roll (The Legends Were True rolls after a damage await; Battleworn
// Dummy rolls in its post-combat Resume). A stub-level pair would close at the first
// suspension and miss those. Anything constructed by code CALLED within a slice is
// event-owned by definition — vanilla builds these reward lists inline in the handler.
[HarmonyPatch]
public static class RolledPotionScopePatches
{
    private static readonly PotionRewardPicker.EventRollScope UncommonPool = new() { Rarity = PotionRarity.Uncommon };
    private static readonly PotionRewardPicker.EventRollScope FullPool = new();

    [HarmonyPrefix, HarmonyPatch(typeof(PotionCourier), "Ransack", MethodType.Async)]
    static void RansackBegin() => PotionRewardPicker.CurrentScope = UncommonPool;

    [HarmonyFinalizer, HarmonyPatch(typeof(PotionCourier), "Ransack", MethodType.Async)]
    static void RansackEnd() => PotionRewardPicker.CurrentScope = null;

    [HarmonyPrefix, HarmonyPatch(typeof(Wellspring), "Bottle", MethodType.Async)]
    static void BottleBegin() => PotionRewardPicker.CurrentScope = FullPool;

    [HarmonyFinalizer, HarmonyPatch(typeof(Wellspring), "Bottle", MethodType.Async)]
    static void BottleEnd() => PotionRewardPicker.CurrentScope = null;

    [HarmonyPrefix, HarmonyPatch(typeof(TheLegendsWereTrue), "SlowlyFindAnExit", MethodType.Async)]
    static void LegendsBegin() => PotionRewardPicker.CurrentScope = FullPool;

    [HarmonyFinalizer, HarmonyPatch(typeof(TheLegendsWereTrue), "SlowlyFindAnExit", MethodType.Async)]
    static void LegendsEnd() => PotionRewardPicker.CurrentScope = null;

    [HarmonyPrefix, HarmonyPatch(typeof(BattlewornDummy), "Resume", MethodType.Async)]
    static void DummyBegin() => PotionRewardPicker.CurrentScope = FullPool;

    [HarmonyFinalizer, HarmonyPatch(typeof(BattlewornDummy), "Resume", MethodType.Async)]
    static void DummyEnd() => PotionRewardPicker.CurrentScope = null;

    [HarmonyPrefix, HarmonyPatch(typeof(EndlessConveyor), "SuspiciousCondiment", MethodType.Async)]
    static void CondimentBegin() => PotionRewardPicker.CurrentScope = FullPool;

    [HarmonyFinalizer, HarmonyPatch(typeof(EndlessConveyor), "SuspiciousCondiment", MethodType.Async)]
    static void CondimentEnd() => PotionRewardPicker.CurrentScope = null;
}

// Tag every predetermined reward constructed inside a roll bracket.
[HarmonyPatch(typeof(PotionReward), MethodType.Constructor, typeof(PotionModel), typeof(Player))]
public static class PredeterminedPotionTagPatch
{
    static void Postfix(PotionReward __instance) => PotionRewardPicker.TagIfInScope(__instance);
}

// The roll witness: PotionReward.Populate rolls ⟺ Potion was null at entry
// (predetermined potions no-op through it). Mark only after the roll actually landed.
[HarmonyPatch(typeof(PotionReward), nameof(PotionReward.Populate))]
public static class PotionRewardRollWitnessPatch
{
    static void Prefix(PotionReward __instance, out bool __state) =>
        __state = __instance.Potion == null;

    static void Postfix(PotionReward __instance, bool __state)
    {
        if (__state && __instance.Potion != null)
            PotionRewardPicker.MarkRolled(__instance);
    }
}

// Replaces the take-flow: pick first, then run the vanilla claim with the picked potion.
[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.GetReward))]
public static class PotionRewardPickPatch
{
    // True while re-invoking the vanilla body after a pick (see below).
    private static bool _passThrough;

    static bool Prefix(NRewardButton __instance, ref Task __result)
    {
        if (_passThrough || !PotionRewardPicker.IsActiveFor(__instance.Reward))
            return true;
        __result = PickThenClaim(__instance, (PotionReward)__instance.Reward!);
        return false;
    }

    private static async Task PickThenClaim(NRewardButton button, PotionReward reward)
    {
        button.Disable();
        PotionModel? choice;
        bool pickerFailed = false;
        try
        {
            // Standard-roll rewards offer the stateless loot pool; event-roll-tagged
            // rewards offer exactly the pool their event rolled from.
            HashSet<PotionModel> valid =
                PotionRewardPicker.TryGetScope(reward, out PotionRewardPicker.EventRollScope? scope)
                && scope != null
                    ? PotionRewardPicker.EventPool(reward.Player, scope)
                    : PotionFactory.GetPotionOptions(reward.Player, Array.Empty<PotionModel>()).ToHashSet();
            choice = await ModPotionPickerUi.Attach(button, reward.Player, valid).Result;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[potion picker] failed, falling back to vanilla reward: {e}");
            choice = null;
            pickerFailed = true; // grant the rolled potion instead of soft-locking the row
        }
        if (!GodotObject.IsInstanceValid(button))
            return; // rewards screen was torn down while picking
        if (choice == null && !pickerFailed)
        {
            button.Enable(); // cancelled -> the reward stays claimable
            return;
        }
        if (choice != null)
        {
            // MP: substitute ONLY when peers will too — they apply the pick to their
            // replica before the vanilla claim (sent next, same channel, FIFO) grants it.
            // A local-only substitution would desync; the roll is the safe fallback.
            if (reward.Player.RunState.Players.Count > 1)
            {
                if (ModWireCheck.SyncReady(reward.Player.RunState)
                    && ModPickNet.TryResolveWireAddress(reward, out int setId, out int rewardIndex))
                {
                    reward.Potion = choice.ToMutable();
                    ModPickNet.SendRewardPick(setId, rewardIndex, isRelic: false, choice.Id.Entry);
                }
                else
                    MainFile.Logger.Error("[potion picker] pick not syncable; vanilla roll kept");
            }
            else
                reward.Potion = choice.ToMutable();
        }
        // Re-enter the vanilla GetReward (procure + belt animation + claim/skip signals).
        // The flag makes the prefix wave the recursive call through; it is reset right
        // after the synchronous kick-off, before any other click can be processed.
        _passThrough = true;
        Task vanilla;
        try { vanilla = button.GetReward(); }
        finally { _passThrough = false; }
        await vanilla;
    }
}

// Reward-row cosmetics: generic label + mystery-silhouette icon instead of the rolled potion.
[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.Reload))]
public static class PotionRewardLabelPatch
{
    static void Postfix(NRewardButton __instance)
    {
        if (!__instance.IsNodeReady() || !PotionRewardPicker.IsActiveFor(__instance.Reward))
            return;
        __instance._label.Text = ModUi.SelectPotionLabel;
        // Same styling the Potion Lab uses for not-yet-seen potions.
        if (((PotionReward)__instance.Reward!)._icon is NPotion icon && icon.IsNodeReady())
        {
            icon.Image.SelfModulate = StsColors.ninetyPercentBlack;
            icon.Outline.Modulate = StsColors.halfTransparentWhite;
        }
    }
}

// The row's hover tip would reveal (and misdescribe) the rolled potion; replace it.
[HarmonyPatch(typeof(PotionReward), "ExtraHoverTips", MethodType.Getter)]
public static class PotionRewardHoverTipPatch
{
    static bool Prefix(PotionReward __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!PotionRewardPicker.IsActiveFor(__instance))
            return true;
        HoverTip tip = default;
        tip.Id = "Rogueunlike.SelectPotion";
        tip.Title = ModUi.SelectPotionLabel;
        tip.Description = ModUi.Loc("ROGUEUNLIKE.SELECT_POTION.tip",
            "Opens the Potion Lab so you can take any potion this reward could have rolled. "
            + "Darkened potions cannot drop from this reward. Locked potions have not been unlocked yet.");
        __result = new IHoverTip[] { tip };
        return false;
    }
}
