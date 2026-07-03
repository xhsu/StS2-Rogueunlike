using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using System;
using System.Collections.Generic;
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
    // Predetermined relics (e.g. event purchases like FakeMerchant) stay vanilla:
    // there the player already chose that specific relic.
    public static bool IsActiveFor(Reward? reward) =>
        reward is RelicReward { IsPopulated: true } relicReward
        && relicReward._predeterminedRelic == null
        && ModWireCheck.SyncReady(relicReward.Player.RunState)
        && (relicReward.Player.RunState.Players.Count == 1
            || ModPickNet.TryResolveWireAddress(relicReward, out _, out _));
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
                    reward._relic = choice.ToMutable();
                    ModPickNet.SendRewardPick(setId, rewardIndex, isRelic: true, choice.Id.Entry);
                }
                else
                    MainFile.Logger.Error("[relic picker] pick not syncable; vanilla roll kept");
            }
            else
                reward._relic = choice.ToMutable();
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
