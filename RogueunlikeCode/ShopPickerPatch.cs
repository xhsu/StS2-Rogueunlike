using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #4: the merchant's rolled stock (7 cards, 3 relics, 3 potions) renders as
/// shade-only buttons. Clicking one opens the matching picker over the slot's actual
/// loot pool — what THAT slot could have rolled at the current context: character card
/// slots any non-Basic rarity of their fixed type, colorless slots their fixed rarity,
/// relic slots the grab bag's deque of the slot's rolled rarity (shop-allowed only),
/// potion slots the whole out-of-combat potion pool — always minus what the other slots
/// already stock, plus the slot's own roll. Confirming assigns the slot's identity ONCE:
/// the slot then shows the real item at its recomputed vanilla price (sale halving
/// preserved) and buys/behaves purely vanilla. If an effect restocks the slot (The
/// Courier etc.), the new roll is shaded and assignable again.
///
/// Simulates the "best case" without save/load-scumming — the same philosophy as the
/// reward pickers, one room deeper.
///
/// Save-safety: every write lands in vanilla-reachable state (the entry's stocked
/// model, vanilla CalcCost RNG advancement, feature-#3-style grab-bag consumption of
/// the assigned relic). The assigned-once flags live in memory only — mid-shop
/// save/reload re-rolls the room vanilla-style, and install/uninstall mid-run is safe.
/// Singleplayer only (per-player shop state has no vanilla sync channel), and only the
/// real merchant room — event shops (FakeMerchant) stay vanilla.
/// </summary>
public static class ShopPicker
{
    private static readonly object Marker = new();
    // Entries stocked by the real merchant with the feature live (registration doubles
    // as the singleplayer + normal-merchant gate). Assigned = identity already chosen
    // for the CURRENT roll; restocking clears it. Both in-memory only, never saved.
    private static readonly ConditionalWeakTable<MerchantEntry, object> Eligible = new();
    private static readonly ConditionalWeakTable<MerchantEntry, object> Assigned = new();
    private static readonly ConditionalWeakTable<MerchantInventory, object> ActiveInventories = new();
    private static bool _pickerOpen;

    private static bool IsEligible(MerchantEntry? entry) =>
        entry != null && Eligible.TryGetValue(entry, out _);

    private static bool IsUnassigned(MerchantEntry? entry) =>
        entry != null && entry.IsStocked
        && Eligible.TryGetValue(entry, out _) && !Assigned.TryGetValue(entry, out _);

    private static string Loc(string key, string fallback) => PotionRewardPicker.Loc(key, fallback);

    // ---- stocking: gate registration + keep rolled identities undiscovered ----

    [HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.CreateForNormalMerchant))]
    public static class StockPatch
    {
        static bool IsActiveFor(Player player) => player.RunState.Players.Count == 1;

        // The entries mark their rolls seen while stocking (before any shop UI exists to
        // anchor suppression on) — mute discovery for the whole roll.
        static void Prefix(Player player)
        {
            if (IsActiveFor(player))
                ModSeenGate.PushSuppression();
        }

        static void Finalizer(Player player)
        {
            if (IsActiveFor(player))
                ModSeenGate.PopSuppression(); // finalizer: an exception must not mute discovery forever
        }

        static void Postfix(Player player, MerchantInventory __result)
        {
            if (!IsActiveFor(player))
                return;
            ActiveInventories.TryAdd(__result, Marker);
            foreach (MerchantEntry entry in __result.AllEntries)
                if (entry is MerchantCardEntry or MerchantRelicEntry or MerchantPotionEntry)
                    Eligible.TryAdd(entry, Marker);
            MainFile.Logger.Info("[shop picker] merchant stocked; slots shaded");
        }
    }

    // Restocks (and later renders/hover tips) happen while the shop screen is up; make
    // it a discovery-suppression anchor so shaded rolls stay unknown until assigned.
    [HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Initialize))]
    public static class ShopScreenAnchorPatch
    {
        static void Postfix(NMerchantInventory __instance, MerchantInventory inventory)
        {
            if (ActiveInventories.TryGetValue(inventory, out _))
                ModSeenGate.SuppressWhile(__instance);
        }
    }

    // A restocked slot rolls a fresh identity: shaded and assignable again.
    [HarmonyPatch]
    public static class RestockPatch
    {
        [HarmonyPostfix, HarmonyPatch(typeof(MerchantCardEntry), "RestockAfterPurchase")]
        static void Card(MerchantCardEntry __instance) => Assigned.Remove(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(MerchantRelicEntry), "RestockAfterPurchase")]
        static void Relic(MerchantRelicEntry __instance) => Assigned.Remove(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(MerchantPotionEntry), "RestockAfterPurchase")]
        static void Potion(MerchantPotionEntry __instance) => Assigned.Remove(__instance);
    }

    // ---- clicking an unassigned slot opens the picker instead of buying ----

    [HarmonyPatch(typeof(NMerchantSlot), "OnSelected")]
    public static class SlotClickPatch
    {
        static bool Prefix(NMerchantSlot __instance, ref Task __result)
        {
            if (!IsUnassigned(__instance.Entry))
                return true; // assigned (or vanilla) slots buy as usual
            __result = AssignFlow(__instance);
            return false;
        }
    }

    private static async Task AssignFlow(NMerchantSlot slot)
    {
        if (_pickerOpen)
            return;
        _pickerOpen = true;
        NHoverTipSet.Remove(slot);
        try
        {
            switch (slot.Entry)
            {
                case MerchantCardEntry card: await AssignCard(slot, card); break;
                case MerchantRelicEntry relic: await AssignRelic(slot, relic); break;
                case MerchantPotionEntry potion: await AssignPotion(slot, potion); break;
            }
            if (GodotObject.IsInstanceValid(slot) && slot._isHovered && slot.Entry is { IsStocked: true } e
                && !IsUnassigned(e))
            {
                // Assigned while hovered: show the real tip now. Protected virtual, so
                // the publicizer skips it (IncludeVirtualMembers=false) — reflect once.
                AccessTools.Method(slot.GetType(), "CreateHoverTip")?.Invoke(slot, null);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[shop picker] assign failed, slot stays as rolled: {e}");
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private static async Task AssignCard(NMerchantSlot slot, MerchantCardEntry entry)
    {
        Player player = entry._player;
        MerchantInventory? inventory = slot._merchantRug?.Inventory;
        CardPoolModel pool = entry._cardType.HasValue
            ? player.Character.CardPool
            : ModelDb.CardPool<ColorlessCardPool>();

        // Mirror CardFactory.CreateForMerchant's filtering over the entry's own pool
        // snapshot: minus other stocked cards, hook-modified, non-Basic, player-count.
        HashSet<CardModel> stockedElsewhere = inventory?.CardEntries
            .Where(e => e != entry)
            .Select(e => e.CreationResult?.Card.CanonicalInstance)
            .OfType<CardModel>().ToHashSet() ?? new HashSet<CardModel>();
        IEnumerable<CardModel> options = entry._cardPool.Except(stockedElsewhere);
        options = Hook.ModifyMerchantCardPool(player.RunState, player, options);
        options = options.Where(c => c.Rarity != CardRarity.Basic);
        options = CardFactory.FilterForPlayerCount(player.RunState, options);
        HashSet<CardModel> valid;
        if (entry._cardType is CardType type)
        {
            valid = options.Where(c => c.Type == type).ToHashSet(); // rarity is rolled → all reachable
        }
        else
        {
            CardRarity rarity = Hook.ModifyMerchantCardRarity(player.RunState, player, entry._cardRarity!.Value);
            valid = options.Where(c => c.Rarity == rarity).ToHashSet();
        }
        valid.Add(entry.CreationResult!.Card.CanonicalInstance);

        CardModel? choice = await ModShopCardPickerUi.Attach(slot, player, valid, pool).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        ModSeenGate.MarkPicked(choice);
        // Mirror Populate: fresh run card, the (never-succeeding) shop upgrade roll,
        // the merchant reward-modifier hook, then the vanilla price for the new card.
        CardModel card = player.RunState.CreateCard(choice, player);
        CardFactory.RollForUpgrade(player, card, -999999999m);
        entry.CreationResult = new CardCreationResult(card);
        Hook.ModifyMerchantCardCreationResults(player.RunState, player,
            new List<CardCreationResult> { entry.CreationResult });
        entry.CalcCost(); // rarity-based price, on-sale halving preserved
        Assigned.TryAdd(entry, Marker);
        entry.OnMerchantInventoryUpdated();
    }

    private static async Task AssignRelic(NMerchantSlot slot, MerchantRelicEntry entry)
    {
        Player player = entry._player;
        MerchantInventory? inventory = slot._merchantRug?.Inventory;
        HashSet<RelicModel> stockedElsewhere = inventory?.RelicEntries
            .Where(e => e != entry)
            .Select(e => e.Model?.CanonicalInstance)
            .OfType<RelicModel>().ToHashSet() ?? new HashSet<RelicModel>();

        // What PullNextRelicFromBack could return for this slot: the bag's deque of the
        // slot's (possibly escalated) rolled rarity, shop-allowed, minus other stock.
        var valid = new HashSet<RelicModel>();
        if (player.RelicGrabBag._deques.TryGetValue(entry.Model!.Rarity, out List<RelicModel>? deque))
            foreach (RelicModel relic in deque)
                if (relic.IsAllowedInShops && relic.IsAllowed(player.RunState) && !stockedElsewhere.Contains(relic))
                    valid.Add(relic);
        valid.Add(entry.Model.CanonicalInstance);

        RelicModel? choice = await ModRelicPickerUi.Attach(slot, player, valid).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        ModSeenGate.MarkPicked(choice);
        entry.SetModel(choice.ToMutable()); // vanilla stock path: CalcCost + seen-mark inside
        // The roll already left the bags at stock time; consume the pick too so it can't
        // drop again this run (mirrors PullNextRelicFromBack, same as feature #3).
        player.RelicGrabBag.Remove(choice);
        player.RunState.SharedRelicGrabBag.Remove(choice);
        Assigned.TryAdd(entry, Marker);
        entry.OnMerchantInventoryUpdated();
    }

    private static async Task AssignPotion(NMerchantSlot slot, MerchantPotionEntry entry)
    {
        Player player = entry._player;
        MerchantInventory? inventory = slot._merchantRug?.Inventory;
        HashSet<PotionModel> stockedElsewhere = inventory?.PotionEntries
            .Where(e => e != entry)
            .Select(e => e.Model?.CanonicalInstance)
            .OfType<PotionModel>().ToHashSet() ?? new HashSet<PotionModel>();

        HashSet<PotionModel> valid = PotionFactory
            .GetPotionOptions(player, stockedElsewhere)
            .ToHashSet();
        valid.Add(entry.Model!.CanonicalInstance);

        PotionModel? choice = await ModPotionPickerUi.Attach(slot, player, valid).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        ModSeenGate.MarkPicked(choice);
        entry.Model = choice.ToMutable(); // mirror the stock ctor...
        entry.CalcCost();                 // ...vanilla price for the new rarity
        Assigned.TryAdd(entry, Marker);
        entry.OnMerchantInventoryUpdated();
    }

    // ---- shade rendering (postfixes normalize both ways: a re-pick of the very item
    // that was rolled keeps the same node, so assignment must explicitly un-shade) ----

    [HarmonyPatch(typeof(NMerchantCard), "UpdateVisual")]
    public static class CardShadePatch
    {
        static void Postfix(NMerchantCard __instance)
        {
            MerchantCardEntry entry = __instance._cardEntry;
            if (!IsEligible(entry) || !entry.IsStocked || __instance._cardNode is not NCard node)
                return;
            bool shaded = IsUnassigned(entry);
            node.Visibility = shaded ? ModelVisibility.NotSeen : ModelVisibility.Visible;
            node.Modulate = shaded ? StsColors.gray : Colors.White;
            if (shaded)
            {
                __instance._saleVisual.Visible = false; // the sale banner is price info
                MaskCost(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantRelic), "UpdateVisual")]
    public static class RelicShadePatch
    {
        static void Postfix(NMerchantRelic __instance)
        {
            MerchantRelicEntry entry = __instance._relicEntry;
            if (!IsEligible(entry) || !entry.IsStocked || __instance._relicNode is not NRelic node)
                return;
            bool shaded = IsUnassigned(entry);
            node.Icon.SelfModulate = shaded ? StsColors.ninetyPercentBlack : Colors.White;
            node.Outline.SelfModulate = shaded ? StsColors.halfTransparentWhite : Colors.White;
            if (shaded)
                MaskCost(__instance);
        }
    }

    [HarmonyPatch(typeof(NMerchantPotion), "UpdateVisual")]
    public static class PotionShadePatch
    {
        static void Postfix(NMerchantPotion __instance)
        {
            MerchantPotionEntry entry = __instance._potionEntry;
            if (!IsEligible(entry) || !entry.IsStocked || __instance._potionNode is not { } node)
                return;
            bool shaded = IsUnassigned(entry);
            node.Image.SelfModulate = shaded ? StsColors.ninetyPercentBlack : Colors.White;
            node.Outline.Modulate = shaded ? StsColors.halfTransparentWhite : Colors.White;
            if (shaded)
                MaskCost(__instance);
        }
    }

    // The price is rolled-item info and changes with assignment — mask it until then.
    private static void MaskCost(NMerchantSlot slot)
    {
        slot._costLabel.SetTextAutoSize("?");
        slot._costLabel.Modulate = StsColors.cream;
    }

    // ---- unassigned slots must not leak the rolled identity ----

    [HarmonyPatch]
    public static class HoverTipPatch
    {
        [HarmonyPrefix, HarmonyPatch(typeof(NMerchantCard), "CreateHoverTip")]
        static bool Card(NMerchantCard __instance) =>
            ShowSelectTip(__instance, __instance._cardEntry,
                Loc("ROGUEUNLIKE.SELECT_CARD.label", "Select a Card"),
                Loc("ROGUEUNLIKE.SELECT_CARD.shop_tip",
                    "This slot's identity is yours to choose, once: opens the card grid with everything "
                    + "this slot could have stocked. The price follows your pick."));

        [HarmonyPrefix, HarmonyPatch(typeof(NMerchantRelic), "CreateHoverTip")]
        static bool Relic(NMerchantRelic __instance) =>
            ShowSelectTip(__instance, __instance._relicEntry,
                RelicRewardPicker.SelectRelicLabel,
                Loc("ROGUEUNLIKE.SELECT_RELIC.shop_tip",
                    "This slot's identity is yours to choose, once: opens the Relic Collection with everything "
                    + "this slot could have stocked. The price follows your pick."));

        [HarmonyPrefix, HarmonyPatch(typeof(NMerchantPotion), "CreateHoverTip")]
        static bool Potion(NMerchantPotion __instance) =>
            ShowSelectTip(__instance, __instance._potionEntry,
                PotionRewardPicker.SelectPotionLabel,
                Loc("ROGUEUNLIKE.SELECT_POTION.shop_tip",
                    "This slot's identity is yours to choose, once: opens the Potion Lab with everything "
                    + "this slot could have stocked. The price follows your pick."));

        // True = run vanilla (assigned/vanilla slots); false = we showed the select-tip.
        private static bool ShowSelectTip(NMerchantSlot slot, MerchantEntry entry, string title, string description)
        {
            if (!IsUnassigned(entry))
                return true;
            HoverTip tip = default;
            tip.Id = "Rogueunlike.SelectShopItem";
            tip.Title = title;
            tip.Description = description;
            NHoverTipSet.CreateAndShow(slot, new IHoverTip[] { tip })?.SetGlobalPosition(slot.GlobalPosition);
            return false;
        }
    }

    // Right-click inspect would reveal the rolled card.
    [HarmonyPatch(typeof(NMerchantCard), "OnPreview")]
    public static class CardPreviewPatch
    {
        static bool Prefix(NMerchantCard __instance) => !IsUnassigned(__instance._cardEntry);
    }
}
