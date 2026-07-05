using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

// Feature #3: a relic reward no longer grants its pre-rolled relic — the row reads
// "Select a Relic" and clicking it opens a Relic Collection-style picker
// (ModRelicPickerUi) offering every relic the reward could have rolled. The rolled relic
// is kept underneath, so cancelling, save/reload and every non-picker path stay pure
// vanilla. Mirrors the potion picker (feature #2) patch-for-patch.
public static class RelicRewardPicker
{
    // Multiplayer synced like the potion picker (RewardPickMessage before the claim).
    // Predetermined relics stay vanilla when the player already chose them (FakeMerchant
    // purchases, scripted tutorials) — but two vanilla sources build predetermined
    // rewards from genuine ROLLS and those get the picker: Toy Box (bag front-pulls,
    // marked wax) and Neow's Bones (a shuffle of Neow's allowed relic options). Their
    // AfterObtained bodies are bracketed below and every reward constructed inside the
    // bracket is tagged with its roll's pool. In-memory only; tags exist identically on
    // every client (relic effects run everywhere, like the reward replicas they build).
    private static readonly ConditionalWeakTable<RelicReward, RollScope> Rolled = new();

    /// <summary>The pool a bracketed source rolls from; null Pool = the standard bag pools.</summary>
    internal sealed class RollScope
    {
        public List<RelicModel>? Pool;
    }

    internal static RollScope? CurrentScope;

    internal static void TagIfInScope(RelicReward reward)
    {
        if (CurrentScope is { } scope)
            Rolled.AddOrUpdate(reward, scope);
    }

    internal static bool IsRolledPredetermined(RelicReward reward) => Rolled.TryGetValue(reward, out _);

    internal static bool TryGetRollPool(RelicReward reward, out List<RelicModel>? pool)
    {
        if (Rolled.TryGetValue(reward, out RollScope? scope))
        {
            pool = scope.Pool;
            return true;
        }
        pool = null;
        return false;
    }

    /// <summary>
    /// The substitute a pick installs: the choice, carrying over the roll's wax mark —
    /// Toy Box's pulls are wax and the melt cycle must find the pick just the same.
    /// </summary>
    internal static RelicModel Substitute(RelicReward reward, RelicModel choice)
    {
        RelicModel substitute = choice.ToMutable();
        if (reward._relic is { IsWax: true })
            substitute.IsWax = true;
        return substitute;
    }

    public static bool IsActiveFor(Reward? reward) =>
        reward is RelicReward { IsPopulated: true } relicReward
        && (relicReward._predeterminedRelic == null || IsRolledPredetermined(relicReward))
        && ModWireCheck.SyncReady(relicReward.Player.RunState)
        && (relicReward.Player.RunState.Players.Count == 1
            || ModPickNet.TryResolveWireAddress(relicReward, out _, out _));
}

// Bracket the two roll-built predetermined sources. Both construct their rewards in the
// async method's FIRST synchronous segment (before its first await), so a stub-level
// prefix/finalizer pair encloses exactly those ctor calls; the finalizer clears the
// scope even when the body throws.
[HarmonyPatch]
public static class RolledPredeterminedScopePatches
{
    [HarmonyPrefix, HarmonyPatch(typeof(ToyBox), nameof(ToyBox.AfterObtained))]
    static void ToyBoxBegin() =>
        RelicRewardPicker.CurrentScope = new RelicRewardPicker.RollScope(); // bag pools

    [HarmonyPrefix, HarmonyPatch(typeof(NeowsBones), nameof(NeowsBones.AfterObtained))]
    static void NeowsBonesBegin(NeowsBones __instance)
    {
        try
        {
            // The exact pool its shuffle deals from, CANONICALIZED — the Neow options
            // hold ToMutable clones, and the picker's pickable set compares canonical
            // references. A failed snapshot tags nothing — those rewards then stay pure
            // vanilla rather than getting a wrong pool.
            RelicRewardPicker.CurrentScope = __instance.Owner is Player owner
                ? new RelicRewardPicker.RollScope
                {
                    Pool = NeowsBones.GetValidRelics(owner).Select(r => r.CanonicalInstance).ToList()
                }
                : null;
        }
        catch (Exception e)
        {
            RelicRewardPicker.CurrentScope = null;
            MainFile.Logger.Error($"[relic picker] Neow's Bones pool snapshot failed (vanilla rewards): {e}");
        }
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(ToyBox), nameof(ToyBox.AfterObtained))]
    static void ToyBoxEnd() => RelicRewardPicker.CurrentScope = null;

    [HarmonyFinalizer, HarmonyPatch(typeof(NeowsBones), nameof(NeowsBones.AfterObtained))]
    static void NeowsBonesEnd() => RelicRewardPicker.CurrentScope = null;
}

// Tag every predetermined reward constructed inside a roll bracket.
[HarmonyPatch(typeof(RelicReward), MethodType.Constructor, typeof(RelicModel), typeof(Player))]
public static class PredeterminedRelicTagPatch
{
    static void Postfix(RelicReward __instance) => RelicRewardPicker.TagIfInScope(__instance);
}

// Replaces the take-flow: pick first, then run the vanilla claim with the picked relic.
[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.GetReward))]
public static class RelicRewardPickPatch
{
    // True while re-invoking the vanilla body after a pick (see below).
    private static bool _passThrough;

    static bool Prefix(NRewardButton __instance, ref Task __result)
    {
        if (_passThrough || !RelicRewardPicker.IsActiveFor(__instance.Reward))
            return true;
        __result = PickThenClaim(__instance, (RelicReward)__instance.Reward!);
        return false;
    }

    private static async Task PickThenClaim(NRewardButton button, RelicReward reward)
    {
        button.Disable();
        RelicModel? choice;
        bool pickerFailed = false;
        try
        {
            choice = await ModRelicPickerUi.Show(button, reward.Player, reward);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[relic picker] failed, falling back to vanilla reward: {e}");
            choice = null;
            pickerFailed = true; // grant the rolled relic instead of soft-locking the row
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
            // No bag consumption here: the claim's RelicCmd.Obtain removes the granted
            // relic from both grab bags (by ModelId, stackable-aware) on every client.
            // MP: substitute ONLY when peers will too (see the potion picker's note).
            if (reward.Player.RunState.Players.Count > 1)
            {
                if (ModWireCheck.SyncReady(reward.Player.RunState)
                    && ModPickNet.TryResolveWireAddress(reward, out int setId, out int rewardIndex))
                {
                    reward._relic = RelicRewardPicker.Substitute(reward, choice);
                    ModPickNet.SendRewardPick(setId, rewardIndex, isRelic: true, choice.Id.Entry);
                }
                else
                    MainFile.Logger.Error("[relic picker] pick not syncable; vanilla roll kept");
            }
            else
                reward._relic = RelicRewardPicker.Substitute(reward, choice);
        }
        // Re-enter the vanilla GetReward (claim + relic fly-to-inventory animation).
        // The flag makes the prefix wave the recursive call through; it is reset right
        // after the synchronous kick-off, before any other click can be processed.
        _passThrough = true;
        Task vanilla;
        try { vanilla = button.GetReward(); }
        finally { _passThrough = false; }
        await vanilla;
    }
}

// Reward-row cosmetics: generic label + darkened icon instead of the rolled relic.
[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.Reload))]
public static class RelicRewardLabelPatch
{
    static void Postfix(NRewardButton __instance)
    {
        if (!__instance.IsNodeReady() || !RelicRewardPicker.IsActiveFor(__instance.Reward))
            return;
        __instance._label.Text = ModUi.SelectRelicLabel;
        // Same darkening the collection uses for not-yet-seen relics.
        foreach (Node child in __instance._iconContainer.GetChildren())
            if (child is TextureRect icon)
                icon.SelfModulate = StsColors.ninetyPercentBlack;
    }
}

// The row's hover tip would reveal (and misdescribe) the rolled relic; replace it.
[HarmonyPatch(typeof(RelicReward), "ExtraHoverTips", MethodType.Getter)]
public static class RelicRewardHoverTipPatch
{
    static bool Prefix(RelicReward __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!RelicRewardPicker.IsActiveFor(__instance))
            return true;
        HoverTip tip = default;
        tip.Id = "Rogueunlike.SelectRelic";
        tip.Title = ModUi.SelectRelicLabel;
        tip.Description = ModUi.Loc("ROGUEUNLIKE.SELECT_RELIC.tip",
            "Opens the Relic Collection so you can take any relic this reward could have rolled. "
            + "Darkened relics cannot drop from this reward. Locked relics have not been unlocked yet.");
        __result = new IHoverTip[] { tip };
        return false;
    }
}
