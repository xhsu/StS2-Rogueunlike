using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

// Feature #2: a potion reward no longer grants its pre-rolled potion — the row reads
// "Select a Potion" and clicking it opens a Potion Lab-style picker (ModPotionPickerUi)
// offering every potion the reward could have rolled. The rolled potion is kept
// underneath, so cancelling, save/reload and every non-picker path stay pure vanilla.
// NRewardButton.GetReward is the only real-gameplay take-path (the other caller of
// SelectLocalReward is test-mode only), so these three patches are the whole seam.
public static class PotionRewardPicker
{
    // Multiplayer: remote peers re-run OnSelect against their replica of this reward, so
    // the pick is broadcast (RewardPickMessage, same FIFO channel as the vanilla claim)
    // right before the claim. Rewards a claim couldn't address on the wire (nested in a
    // linked set) stay vanilla in MP — the same rows vanilla itself syncs by index.
    public static bool IsActiveFor(Reward? reward) =>
        reward is PotionReward { IsPopulated: true } potionReward
        && ModWireCheck.SyncReady(potionReward.Player.RunState)
        && (potionReward.Player.RunState.Players.Count == 1
            || ModPickNet.TryResolveWireAddress(potionReward, out _, out _));
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
            choice = await ModPotionPickerUi.Show(button, reward.Player);
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
