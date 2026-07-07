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
using System.Reflection;
using System.Reflection.Emit;
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
    // above can't see. Each handler's MoveNext gets a tag call INJECTED into its IL
    // right after the `newobj PotionReward` (RolledPotionTagTranspilers); the picker
    // then offers exactly that handler's pool. Fixed-potion rewards (Foul Potion,
    // Glowwater) construct in un-transpiled methods and stay vanilla. In-memory only;
    // tags exist identically on every client (event handlers run everywhere in
    // lockstep, like the reward replicas they build).
    internal sealed class EventRollScope
    {
        public PotionRarity? Rarity; // null = the full character+shared unlocked set
    }

    private static readonly ConditionalWeakTable<PotionReward, EventRollScope> ScopeTagged = new();

    internal static readonly EventRollScope FullPool = new();
    internal static readonly EventRollScope UncommonPool = new() { Rarity = PotionRarity.Uncommon };

    /// <summary>Target of the injected IL (see RolledPotionTagTranspilers). Public: called from patched game bodies.</summary>
    public static void TagRolledFromEvent(PotionReward reward, int rarityKind) =>
        ScopeTagged.AddOrUpdate(reward, rarityKind == 1 ? UncommonPool : FullPool);

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

// The tag: IL injected into the five handlers' MoveNext bodies (MethodType.Async), right
// after every `newobj PotionReward(PotionModel, Player)`: dup; ldc.i4 <kind>; call
// TagRolledFromEvent. The call sits in the CALLER's IL, so it cannot be lost to callee
// inlining — a ctor POSTFIX demonstrably was: Harmony compiles patched bodies as
// full-opt DynamicMethods, and the JIT inlined the three-statement PotionReward ctor
// straight past its detour, so the v0.6.0 bracket design (scope prefix/finalizer +
// ctor postfix) tagged nothing (field report 2026-07-06, The Legends Were True;
// reproduced in a standalone rig against the game's own 0Harmony/.NET 9 Release).
// Injection is position-independent, so rolls after an await (Legends, Dummy's
// post-combat Resume) are covered for free. A reshaped handler degrades LOUD: no
// matching newobj → patch-time throw → whole-group rollback → vanilla rows.
[HarmonyPatch]
public static class RolledPotionTagTranspilers
{
    private const int Full = 0, Uncommon = 1;

    [HarmonyTranspiler, HarmonyPatch(typeof(PotionCourier), "Ransack", MethodType.Async)]
    static IEnumerable<CodeInstruction> Ransack(IEnumerable<CodeInstruction> il) => Inject(il, Uncommon);

    [HarmonyTranspiler, HarmonyPatch(typeof(Wellspring), "Bottle", MethodType.Async)]
    static IEnumerable<CodeInstruction> Bottle(IEnumerable<CodeInstruction> il) => Inject(il, Full);

    [HarmonyTranspiler, HarmonyPatch(typeof(TheLegendsWereTrue), "SlowlyFindAnExit", MethodType.Async)]
    static IEnumerable<CodeInstruction> Legends(IEnumerable<CodeInstruction> il) => Inject(il, Full);

    [HarmonyTranspiler, HarmonyPatch(typeof(BattlewornDummy), "Resume", MethodType.Async)]
    static IEnumerable<CodeInstruction> Dummy(IEnumerable<CodeInstruction> il) => Inject(il, Full);

    [HarmonyTranspiler, HarmonyPatch(typeof(EndlessConveyor), "SuspiciousCondiment", MethodType.Async)]
    static IEnumerable<CodeInstruction> Condiment(IEnumerable<CodeInstruction> il) => Inject(il, Full);

    private static List<CodeInstruction> Inject(IEnumerable<CodeInstruction> il, int rarityKind)
    {
        ConstructorInfo ctor = AccessTools.DeclaredConstructor(
                typeof(PotionReward), new[] { typeof(PotionModel), typeof(Player) })
            ?? throw new InvalidOperationException("PotionReward(PotionModel, Player) ctor not found");
        var result = new List<CodeInstruction>();
        int injected = 0;
        foreach (CodeInstruction ins in il)
        {
            result.Add(ins);
            if (ins.opcode == OpCodes.Newobj && Equals(ins.operand, ctor))
            {
                result.Add(new CodeInstruction(OpCodes.Dup));
                result.Add(new CodeInstruction(OpCodes.Ldc_I4, rarityKind));
                result.Add(CodeInstruction.Call(typeof(PotionRewardPicker),
                    nameof(PotionRewardPicker.TagRolledFromEvent)));
                injected++;
            }
        }
        if (injected == 0)
            throw new InvalidOperationException(
                "no `newobj PotionReward(PotionModel, Player)` in the handler body — event reshaped by a game update");
        return result;
    }
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
