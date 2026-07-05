using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
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
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Feature #4: the merchant's rolled stock renders as shade-only buttons; clicking one
/// opens the matching picker over THAT slot's loot pool at current context (character
/// card slots: any non-Basic rarity of their fixed type; colorless slots: their fixed
/// rarity; relic slots: the grab bag's deque of the rolled rarity, shop-allowed only;
/// potion slots: the out-of-combat pool — always minus what other slots stock, plus the
/// slot's own roll). Confirming assigns the identity ONCE: the slot shows the real item
/// at its recomputed vanilla price and buys purely vanilla; restocks re-shade.
///
/// Save-safety: every write is vanilla-reachable (stocked model, CalcCost RNG advance,
/// stock-pull-style bag consumption); assigned-once flags are in-memory, so a mid-shop
/// reload re-rolls vanilla. Real merchant only — event shops (FakeMerchant) stay vanilla.
///
/// MP: each player assigns their OWN shop; the assignment broadcasts (ShopAssignMessage)
/// and replays on every replica because it consumes grab-bag / Shops-rng / card-creation
/// state that feeds later deterministic rolls. Purchases replicate by model, as vanilla.
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

    // ---- stocking: gate registration + keep rolled identities undiscovered ----

    [HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.CreateForNormalMerchant))]
    public static class StockPatch
    {
        // The entries mark their rolls seen while stocking (before any shop UI exists to
        // anchor suppression on) — mute discovery for the whole roll. Unconditional: in
        // multiplayer EVERY player's inventory stocks on EVERY client, and the vanilla
        // SetModel marking is not IsMe-gated — without this, teammates' shop rolls would
        // leak into this client's compendium save (a vanilla leak this closes).
        static void Prefix() => ModSeenGate.PushSuppression();

        static void Finalizer() => ModSeenGate.PopSuppression(); // finalizer: an exception must not mute discovery forever

        static void Postfix(MerchantInventory __result)
        {
            // No verified mod handshake in real MP -> assignments couldn't replay on
            // every client, so don't shade at all: the shop stays pure vanilla. (The
            // seen-suppression prefix above stays unconditional — that closes a vanilla
            // leak and is per-client only.)
            if (!ModWireCheck.SyncReady(RunManager.Instance?.State))
            {
                MainFile.Logger.Info("[shop picker] wire check not verified; shop stays vanilla");
                return;
            }
            ActiveInventories.TryAdd(__result, Marker);
            foreach (MerchantEntry entry in __result.AllEntries)
                if (entry is MerchantCardEntry or MerchantRelicEntry or MerchantPotionEntry)
                    Eligible.TryAdd(entry, Marker);
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
            EndCostPreview();
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

        BeginCostPreview(entry, valid);
        CardModel? choice = await ModCardPickModal.AttachToScreen(slot, player, valid, pool).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        if (!ModPickNet.TryBroadcastShopAssign(entry, choice.Id.Entry))
        {
            MainFile.Logger.Error("[shop picker] card assignment not wire-addressable; slot stays shaded");
            return; // MP without a synced replay would diverge card-creation state
        }
        ModSeenGate.MarkPicked(choice);
        ApplyCardAssignment(entry, choice);
    }

    // The assignment mutation, replayed verbatim on remote clients (ModNetMsg).
    internal static void ApplyCardAssignment(MerchantCardEntry entry, CardModel choice)
    {
        Player player = entry._player;
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

        BeginCostPreview(entry, valid);
        RelicModel? choice = await ModRelicPickerUi.Attach(slot, player, valid).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        if (!ModPickNet.TryBroadcastShopAssign(entry, choice.Id.Entry))
        {
            MainFile.Logger.Error("[shop picker] relic assignment not wire-addressable; slot stays shaded");
            return; // MP without a synced replay would diverge the grab bags
        }
        ModSeenGate.MarkPicked(choice);
        ApplyRelicAssignment(entry, choice);
    }

    // The assignment mutation, replayed verbatim on remote clients (ModNetMsg).
    internal static void ApplyRelicAssignment(MerchantRelicEntry entry, RelicModel choice)
    {
        Player player = entry._player;
        entry.SetModel(choice.ToMutable()); // vanilla stock path: CalcCost + seen-mark inside
        // The roll already left the bags at stock time; consume the pick too so it can't
        // drop again this run (mirrors PullNextRelicFromBack, the vanilla stock pull).
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

        BeginCostPreview(entry, valid);
        PotionModel? choice = await ModPotionPickerUi.Attach(slot, player, valid).Result;
        if (choice == null || !GodotObject.IsInstanceValid(slot) || !entry.IsStocked)
            return;
        if (!ModPickNet.TryBroadcastShopAssign(entry, choice.Id.Entry))
        {
            MainFile.Logger.Error("[shop picker] potion assignment not wire-addressable; slot stays shaded");
            return; // MP without a synced replay would diverge the Shops rng
        }
        ModSeenGate.MarkPicked(choice);
        ApplyPotionAssignment(entry, choice);
    }

    // The assignment mutation, replayed verbatim on remote clients (ModNetMsg).
    internal static void ApplyPotionAssignment(MerchantPotionEntry entry, PotionModel choice)
    {
        entry.Model = choice.ToMutable(); // mirror the stock ctor...
        entry.CalcCost();                 // ...vanilla price for the new rarity
        Assigned.TryAdd(entry, Marker);
        entry.OnMerchantInventoryUpdated();
    }

    // ---- cost preview: hovering a pickable item in an assign picker shows its price ----

    // Active only between BeginCostPreview (right before a picker opens for a slot) and
    // the AssignFlow finally — reward/chest/compendium uses of the same picker scenes
    // never see a session and stay untouched.
    private static MerchantEntry? _previewEntry;
    private static HashSet<AbstractModel>? _previewValid;

    private static void BeginCostPreview(MerchantEntry entry, IEnumerable<AbstractModel> valid)
    {
        _previewEntry = entry;
        _previewValid = valid.ToHashSet();
    }

    private static void EndCostPreview()
    {
        _previewEntry = null;
        _previewValid = null;
    }

    /// <summary>
    /// The exact price the slot would charge if <paramref name="canonical"/> were assigned
    /// right now, computed PURELY: base cost via the entry's own (publicized) tables, the
    /// jitter drawn from a CLONE of the Shops rng — the very next draw the assignment's
    /// CalcCost will consume — and the display transform via the same Hook the Cost getter
    /// applies. Mirrors each CalcCost body (jitter ranges, sale halving, rounding).
    /// </summary>
    private static bool TryPreviewCost(AbstractModel? canonical, out int cost)
    {
        cost = 0;
        try
        {
            if (canonical == null || _previewEntry is not MerchantEntry entry
                || _previewValid is not { } valid || !valid.Contains(canonical))
                return false;
            Player player = entry._player;
            Rng shops = player.PlayerRng.Shops;
            Rng peek = new(shops.Seed, shops.Counter);
            int raw;
            switch (entry)
            {
                case MerchantCardEntry cardEntry when canonical is CardModel card:
                    raw = Mathf.RoundToInt((float)MerchantCardEntry.GetCost(card) * peek.NextFloat(0.95f, 1.05f));
                    if (cardEntry.IsOnSale)
                        raw /= 2;
                    break;
                case MerchantRelicEntry when canonical is RelicModel relic:
                    raw = (int)System.Math.Round((float)relic.MerchantCost * peek.NextFloat(0.85f, 1.15f));
                    break;
                case MerchantPotionEntry when canonical is PotionModel potion:
                    raw = MerchantPotionEntry.GetCost(potion.Rarity);
                    if (TestMode.IsOff)
                        raw = (int)Mathf.Round((float)raw * peek.NextFloat(0.95f, 1.05f));
                    break;
                default:
                    return false;
            }
            cost = (int)Hook.ModifyMerchantPrice(player.RunState, player, entry, raw);
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[shop picker] cost preview failed (tip skipped): {e}");
            return false;
        }
    }

    private static IHoverTip[] WithCostTip(int cost, IEnumerable<IHoverTip> tips)
    {
        HoverTip costTip = default;
        costTip.Id = "Rogueunlike.ShopCost";
        costTip.Title = string.Format(ModUi.Loc("ROGUEUNLIKE.SHOP_COST.title", "{0} Gold"), cost);
        return new IHoverTip[] { costTip }.Concat(tips).ToArray();
    }

    // Vanilla price labels turn StsColors.red when unaffordable (NMerchantCard.UpdateVisual,
    // same Cost-vs-Gold comparison as EnoughGold). Tip titles are plain MegaLabels — no
    // markup — so tint the cost tip's title node (the FIRST tip's) directly. Cosmetic:
    // a reshaped tip scene simply keeps the default color.
    private static void TintIfUnaffordable(NHoverTipSet? set, int cost)
    {
        try
        {
            if (set == null || _previewEntry is not MerchantEntry entry || cost <= entry._player.Gold)
                return;
            if (set.FindChild("Title", recursive: true, owned: false) is MegaLabel title)
                title.Modulate = StsColors.red;
        }
        catch (Exception)
        {
            // cosmetic only
        }
    }

    // Each patch rides the holder's own tip creation: compute the price first, and only
    // then rebuild the freshly-shown set with the cost tip prepended (mirroring the
    // vanilla alignment/follow calls). Any failure leaves the vanilla tips untouched.

    [HarmonyPatch(typeof(NLabPotionHolder), "OnFocus")]
    public static class PotionCostTipPatch
    {
        static void Postfix(NLabPotionHolder __instance)
        {
            if (__instance._potionNode?.Model is not PotionModel model
                || !TryPreviewCost(model.CanonicalInstance, out int cost))
                return;
            NHoverTipSet.Remove(__instance);
            NHoverTipSet? set = NHoverTipSet.CreateAndShow(__instance,
                WithCostTip(cost, model.HoverTips), HoverTip.GetHoverTipAlignment(__instance));
            set?.SetFollowOwner();
            set?.SetExtraFollowOffset(new Vector2(32f, 0f));
            TintIfUnaffordable(set, cost);
        }
    }

    [HarmonyPatch(typeof(NRelicCollectionEntry), "OnFocus")]
    public static class RelicCostTipPatch
    {
        static void Postfix(NRelicCollectionEntry __instance)
        {
            if (__instance.relic is not RelicModel model
                || !TryPreviewCost(model.CanonicalInstance, out int cost))
                return;
            NHoverTipSet.Remove(__instance);
            NHoverTipSet? set = NHoverTipSet.CreateAndShow(__instance,
                WithCostTip(cost, model.HoverTips), HoverTip.GetHoverTipAlignment(__instance));
            set?.SetFollowOwner();
            TintIfUnaffordable(set, cost);
        }
    }

    [HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
    public static class CardCostTipPatch
    {
        static void Postfix(NCardHolder __instance)
        {
            if (__instance.CardModel is not CardModel model
                || !TryPreviewCost(model.CanonicalInstance, out int cost))
                return;
            NHoverTipSet.Remove(__instance);
            NHoverTipSet? set = NHoverTipSet.CreateAndShow(__instance,
                WithCostTip(cost, model.HoverTips));
            set?.SetAlignmentForCardHolder(__instance);
            TintIfUnaffordable(set, cost);
        }
    }

    // ---- shade rendering (postfixes normalize both ways: a re-pick of the very item
    // that was rolled keeps the same node, so assignment must explicitly un-shade) ----

    // Two application points: UpdateVisual (stocking, gold changes, assignment) and
    // OnInventoryOpened (the save-reload path builds the slots before the room is in
    // the tree, and a later re-render restored the card face — real art in gray, an
    // identity leak — so re-assert when the rug actually opens).
    [HarmonyPatch]
    public static class CardShadePatch
    {
        [HarmonyPostfix, HarmonyPatch(typeof(NMerchantCard), "UpdateVisual")]
        static void AfterUpdateVisual(NMerchantCard __instance) => ApplyCardShade(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(NMerchantCard), nameof(NMerchantCard.OnInventoryOpened))]
        static void AfterInventoryOpened(NMerchantCard __instance) => ApplyCardShade(__instance);

        private static void ApplyCardShade(NMerchantCard slot)
        {
            MerchantCardEntry entry = slot._cardEntry;
            if (!IsEligible(entry) || !entry.IsStocked || slot._cardNode is not NCard node)
                return;
            bool shaded = IsUnassigned(entry);
            ModelVisibility want = shaded ? ModelVisibility.NotSeen : ModelVisibility.Visible;
            if (node.Visibility != want)
            {
                node.Visibility = want; // Reload(): blurred art / restored art
                if (node.IsNodeReady())
                    node.UpdateVisuals(PileType.None, CardPreviewMode.Normal); // labels follow visibility
            }
            node.Modulate = shaded ? StsColors.gray : Colors.White;
            if (shaded)
                MaskCost(slot); // the sale banner stays visible — picking WHERE to spend
                                // the assignment is exactly the strategy the discount feeds
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
                ModUi.SelectCardLabel,
                ModUi.Loc("ROGUEUNLIKE.SELECT_CARD.shop_tip",
                    "This slot's identity is yours to choose, once: opens the card grid with everything "
                    + "this slot could have stocked. The price follows your pick."));

        [HarmonyPrefix, HarmonyPatch(typeof(NMerchantRelic), "CreateHoverTip")]
        static bool Relic(NMerchantRelic __instance) =>
            ShowSelectTip(__instance, __instance._relicEntry,
                ModUi.SelectRelicLabel,
                ModUi.Loc("ROGUEUNLIKE.SELECT_RELIC.shop_tip",
                    "This slot's identity is yours to choose, once: opens the Relic Collection with everything "
                    + "this slot could have stocked. The price follows your pick."));

        [HarmonyPrefix, HarmonyPatch(typeof(NMerchantPotion), "CreateHoverTip")]
        static bool Potion(NMerchantPotion __instance) =>
            ShowSelectTip(__instance, __instance._potionEntry,
                ModUi.SelectPotionLabel,
                ModUi.Loc("ROGUEUNLIKE.SELECT_POTION.shop_tip",
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
