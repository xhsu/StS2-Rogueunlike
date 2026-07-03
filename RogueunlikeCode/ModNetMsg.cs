using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rogueunlike.RogueunlikeCode;

// ---------------------------------------------------------------------------------------
// The mod's multiplayer messages. INetMessage is officially mod-extensible: the game's
// MessageTypes.Initialize splices ReflectionHelper.GetSubtypesInMods<INetMessage>() into
// the wire-type registry (same official pattern DevConsole uses for mod console commands).
// Wire ids are index-based over built-ins + mod types, so every client must run the same
// mod set — already this mod's standing multiplayer requirement.
//
// Why messages and not console commands here: reward claims are NOT lockstep actions.
// Vanilla sends RewardSelectedMessage {setId, rewardIndex} on the direct message channel
// and each client re-derives the item from its own replica of that player's reward. Our
// pick must land on every client BEFORE that claim — which is only guaranteed if it rides
// the SAME channel (per-sender FIFO; the game's own ActionQueueSet doc relies on
// "all action messages are received in the same order that they are sent"). A console
// command would ride the host-ordered ACTION queue and could lose the cross-channel race.
// ---------------------------------------------------------------------------------------

/// <summary>
/// Features #2/#3, multiplayer half: "player S substituted reward[rewardIndex] of their
/// set[setId] with this potion/relic". Sent by the picker right before the vanilla claim
/// (RewardSelectedMessage), mirroring its traits and its set-buffering semantics, so every
/// client substitutes first and then grants through the untouched vanilla claim path.
/// </summary>
public class RewardPickMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public RunLocation location;
    public int setId;
    public int rewardIndex;
    public bool isRelic;      // false = potion
    public string itemEntry = "";

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => location;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(location);
        writer.WriteInt(setId);
        writer.WriteInt(rewardIndex, 8);
        writer.WriteBool(isRelic);
        writer.WriteString(itemEntry);
    }

    public void Deserialize(PacketReader reader)
    {
        location = reader.Read<RunLocation>();
        setId = reader.ReadInt();
        rewardIndex = reader.ReadInt(8);
        isRelic = reader.ReadBool();
        itemEntry = reader.ReadString();
    }
}

/// <summary>
/// Feature #4, multiplayer half: "player S assigned this item to slot[entryIndex] of
/// their own shop". Each client replays the exact assignment on its replica of the
/// sender's inventory. The shop itself is vanilla-local (purchases replicate as
/// RewardObtainedMessage with the full model, remote inventories are never displayed),
/// but the assignment consumes from the sender's relic grab bag / Shops rng / card
/// creation — state that FEEDS DETERMINISTIC rolls later — so it must stay identical
/// on every client.
/// </summary>
public class ShopAssignMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public RunLocation location;
    public int entryIndex;    // index into MerchantInventory.AllEntries (deterministic order)
    public string itemEntry = "";

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;
    public RunLocation Location => location;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(location);
        writer.WriteInt(entryIndex, 8);
        writer.WriteString(itemEntry);
    }

    public void Deserialize(PacketReader reader)
    {
        location = reader.Read<RunLocation>();
        entryIndex = reader.ReadInt(8);
        itemEntry = reader.ReadString();
    }
}

/// <summary>
/// Registration, sending and handling of the mod's messages. Handlers live on the same
/// RunLocationTargetedMessageBuffer the vanilla reward synchronizer uses (location
/// gating + received-order replay), registered/unregistered alongside it.
/// </summary>
[HarmonyPatch]
internal static class ModPickNet
{
    // Picks that arrived before their reward set was begun on this client (the sender
    // reached their rewards faster) — exactly RewardsSetSynchronizer's own buffering
    // rule for early RewardSelectedMessages. Drained in the BeginRewardsSet prefix.
    private static readonly List<(ulong Sender, RewardPickMessage Msg)> _bufferedPicks = new();

    // The one live merchant room, for late shop-assign resolution (registered at entry;
    // weak so a torn-down room can't be resurrected by a stale message).
    private static WeakReference<MerchantRoom>? _merchantRoom;

    internal static void RememberMerchantRoom(MerchantRoom room) =>
        _merchantRoom = new WeakReference<MerchantRoom>(room);

    // ---- lifecycle: ride the vanilla synchronizer's own buffer ----

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardsSetSynchronizer), MethodType.Constructor,
        typeof(RunLocationTargetedMessageBuffer), typeof(INetGameService), typeof(IPlayerCollection), typeof(ulong))]
    static void AfterCtor(RewardsSetSynchronizer __instance)
    {
        _bufferedPicks.Clear(); // fresh run/reload: no stale picks may leak into reused set ids
        ModWireCheck.Reset();   // new run/load: mod-set + wire-id agreement must be re-earned
        __instance._messageBuffer.RegisterMessageHandler<RewardPickMessage>(HandleRewardPick);
        __instance._messageBuffer.RegisterMessageHandler<ShopAssignMessage>(HandleShopAssign);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.Dispose))]
    static void AfterDispose(RewardsSetSynchronizer __instance)
    {
        __instance._messageBuffer.UnregisterMessageHandler<RewardPickMessage>(HandleRewardPick);
        __instance._messageBuffer.UnregisterMessageHandler<ShopAssignMessage>(HandleShopAssign);
        _bufferedPicks.Clear();
    }

    // ---- reward picks (features #2/#3) ----

    /// <summary>
    /// Where SelectLocalReward would address this reward on the wire: the top of its
    /// player's set stack + index within it. False = not wire-addressable (e.g. a reward
    /// nested inside a linked set) — multiplayer then keeps that row vanilla.
    /// </summary>
    internal static bool TryResolveWireAddress(Reward reward, out int setId, out int rewardIndex)
    {
        setId = -1;
        rewardIndex = -1;
        try
        {
            RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
            var state = sync.GetRewardStateForPlayer(reward.Player);
            if (state.rewardsStack.Count == 0)
                return false;
            RewardsSet set = state.rewardsStack[state.rewardsStack.Count - 1].set;
            setId = set.Id;
            rewardIndex = set.Rewards.IndexOf(reward);
            return rewardIndex >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Broadcast a local pick. Call right before the vanilla claim re-entry.</summary>
    internal static void SendRewardPick(int setId, int rewardIndex, bool isRelic, string itemEntry)
    {
        try
        {
            RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
            sync._netService.SendMessage(new RewardPickMessage
            {
                location = sync._messageBuffer.CurrentLocation,
                setId = setId,
                rewardIndex = rewardIndex,
                isRelic = isRelic,
                itemEntry = itemEntry
            });
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[pick sync] reward pick broadcast failed: {e}");
        }
    }

    private static void HandleRewardPick(RewardPickMessage msg, ulong senderId)
    {
        if (ModWireCheck.Broken)
            return; // wire ids disagree across clients: this "message" may be another mod's bytes
        try
        {
            RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
            if (sync._playerCollection.GetPlayer(senderId) is not Player player)
                return;
            var state = sync.GetRewardStateForPlayer(player);
            if (state.nextId <= msg.setId)
            {
                // Set not begun here yet (vanilla buffers its claim the same way); the
                // claim message is behind us in FIFO, so it buffers too — order kept.
                _bufferedPicks.Add((senderId, msg));
                return;
            }
            var setState = state.rewardsStack.FirstOrDefault(s => s.set.Id == msg.setId);
            if (setState == null)
            {
                MainFile.Logger.Error($"[pick sync] pick for unknown set {msg.setId} of player {senderId}; roll kept");
                return;
            }
            ApplyRewardPick(setState.set, msg, senderId);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[pick sync] reward pick handling failed (roll kept): {e}");
        }
    }

    // Substitute before the (FIFO-later) vanilla claim selects. Mirrors vanilla's own
    // buffered-message drain in BeginRewardsSet — but as a PREFIX, because the vanilla
    // body drains its buffered RewardSelectedMessages (which grant) inside the call.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
    static void BeforeBeginRewardsSet(RewardsSetSynchronizer __instance, RewardsSet set)
    {
        ModWireCheck.TryAnnounce(RunManager.Instance?.State); // backstop announce trigger
        if (_bufferedPicks.Count == 0)
            return;
        try
        {
            int upcomingId = __instance.GetRewardStateForPlayer(set.Player).nextId;
            for (int i = 0; i < _bufferedPicks.Count; i++)
            {
                (ulong sender, RewardPickMessage msg) = _bufferedPicks[i];
                if (msg.setId != upcomingId || __instance._playerCollection.GetPlayer(sender) != set.Player)
                    continue;
                ApplyRewardPick(set, msg, sender);
                _bufferedPicks.RemoveAt(i);
                i--;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[pick sync] buffered pick drain failed (rolls kept): {e}");
        }
    }

    // The same substitution the picking client did locally: write the reward's item.
    // No pool re-validation — the sender's picker only offers pool-legal items, and what
    // matters for sync is that every client applies the SAME thing. Bag consumption is
    // NOT mirrored here: RelicCmd.Obtain removes the granted relic from both grab bags
    // (by ModelId, stackable-aware) on every client when the claim lands.
    private static void ApplyRewardPick(RewardsSet set, RewardPickMessage msg, ulong senderId)
    {
        if (msg.rewardIndex < 0 || msg.rewardIndex >= set.Rewards.Count)
        {
            MainFile.Logger.Error($"[pick sync] pick index {msg.rewardIndex} out of range for set {msg.setId}; roll kept");
            return;
        }
        Reward reward = set.Rewards[msg.rewardIndex];
        if (!msg.isRelic && reward is PotionReward potionReward)
        {
            ModelId id = new(ModelDb.GetCategory(typeof(PotionModel)), msg.itemEntry);
            if (ModelDb.GetByIdOrNull<PotionModel>(id) is { } potion)
            {
                potionReward.Potion = potion.ToMutable();
                MainFile.Logger.Info($"[pick sync] player {senderId} reward potion -> {msg.itemEntry}");
                return;
            }
        }
        else if (msg.isRelic && reward is RelicReward { _predeterminedRelic: null } relicReward)
        {
            ModelId id = new(ModelDb.GetCategory(typeof(RelicModel)), msg.itemEntry);
            if (ModelDb.GetByIdOrNull<RelicModel>(id) is { } relic)
            {
                relicReward._relic = relic.ToMutable();
                MainFile.Logger.Info($"[pick sync] player {senderId} reward relic -> {msg.itemEntry}");
                return;
            }
        }
        MainFile.Logger.Error($"[pick sync] unresolvable pick '{msg.itemEntry}' for {reward.GetType().Name}; roll kept");
    }

    // ---- shop assignments (feature #4) ----

    /// <summary>
    /// Broadcast a local slot assignment; true when remotes will replay it (or no
    /// remotes exist). False = the slot can't be addressed on the wire — the caller
    /// must then abort the assignment to protect grab-bag/rng convergence.
    /// </summary>
    internal static bool TryBroadcastShopAssign(MerchantEntry entry, string itemEntry)
    {
        try
        {
            Player player = entry._player;
            if (player.RunState.Players.Count == 1)
                return true; // singleplayer: nothing to sync
            if (!ModWireCheck.SyncReady(player.RunState))
                return false; // handshake not verified: remotes may not replay — abort
            if (ResolveInventory(player) is not MerchantInventory inventory)
                return false;
            int index = IndexOfEntry(inventory, entry);
            if (index < 0)
                return false;
            RewardsSetSynchronizer sync = RunManager.Instance.RewardsSetSynchronizer;
            sync._netService.SendMessage(new ShopAssignMessage
            {
                location = sync._messageBuffer.CurrentLocation,
                entryIndex = index,
                itemEntry = itemEntry
            });
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[pick sync] shop assign broadcast failed: {e}");
            return false;
        }
    }

    private static void HandleShopAssign(ShopAssignMessage msg, ulong senderId)
    {
        if (ModWireCheck.Broken)
            return; // wire ids disagree across clients: this "message" may be another mod's bytes
        // Marking-seen guard: the vanilla stock/assign path marks models seen
        // unconditionally — a teammate's pick must not enter THIS client's compendium.
        ModSeenGate.PushSuppression();
        try
        {
            RunState? runState = RunManager.Instance.State;
            if (runState == null)
                return;
            Player player = runState.Players.FirstOrDefault(p => p.NetId == senderId)
                ?? throw new InvalidOperationException($"unknown sender {senderId}");
            if (ResolveInventory(player) is not MerchantInventory inventory)
            {
                MainFile.Logger.Error($"[pick sync] shop assign from {senderId} but no merchant room here; their bag/rng may go stale");
                return;
            }
            MerchantEntry? entry = inventory.AllEntries.ElementAtOrDefault(msg.entryIndex);
            switch (entry)
            {
                case MerchantCardEntry card when ResolveModel<CardModel>(msg.itemEntry) is { } model:
                    ShopPicker.ApplyCardAssignment(card, model);
                    break;
                case MerchantRelicEntry relic when ResolveModel<RelicModel>(msg.itemEntry) is { } model:
                    ShopPicker.ApplyRelicAssignment(relic, model);
                    break;
                case MerchantPotionEntry potion when ResolveModel<PotionModel>(msg.itemEntry) is { } model:
                    ShopPicker.ApplyPotionAssignment(potion, model);
                    break;
                default:
                    MainFile.Logger.Error($"[pick sync] shop assign slot {msg.entryIndex} / '{msg.itemEntry}' unresolvable");
                    return;
            }
            MainFile.Logger.Info($"[pick sync] player {senderId} shop slot {msg.entryIndex} -> {msg.itemEntry}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"[pick sync] shop assign handling failed: {e}");
        }
        finally
        {
            ModSeenGate.PopSuppression();
        }
    }

    private static MerchantInventory? ResolveInventory(Player player)
    {
        MerchantRoom? room = player.RunState.CurrentRoom as MerchantRoom;
        if (room == null && _merchantRoom != null && _merchantRoom.TryGetTarget(out MerchantRoom? remembered))
            room = remembered; // travel action raced ahead of the message: the room object still exists
        if (room == null)
            return null;
        int slot = player.RunState.GetPlayerSlotIndex(player);
        return slot >= 0 && slot < room.Inventories.Count ? room.Inventories[slot] : null;
    }

    private static int IndexOfEntry(MerchantInventory inventory, MerchantEntry entry)
    {
        int i = 0;
        foreach (MerchantEntry candidate in inventory.AllEntries)
        {
            if (ReferenceEquals(candidate, entry))
                return i;
            i++;
        }
        return -1;
    }

    private static T? ResolveModel<T>(string entry) where T : AbstractModel =>
        ModelDb.GetByIdOrNull<T>(new ModelId(ModelDb.GetCategory(typeof(T)), entry));
}

// The merchant room handle for late-message resolution (see ResolveInventory).
[HarmonyPatch(typeof(MerchantRoom), "EnterInternal")]
internal static class MerchantRoomTrackPatch
{
    static void Postfix(MerchantRoom __instance) => ModPickNet.RememberMerchantRoom(__instance);
}
