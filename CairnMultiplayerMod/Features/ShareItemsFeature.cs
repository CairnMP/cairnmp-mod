using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed class GroundItemsState : IPacket
{
    internal const int MaxItems = 32;
    public readonly List<GroundItemRecord> Items = new();

    public void Serialize(BinaryWriter writer)
    {
        if (Items.Count > MaxItems) throw new InvalidDataException("Too many ground items.");
        writer.Write((byte)Items.Count);
        foreach (var item in Items)
        {
            writer.Write(item.Id); writer.Write(item.DefinitionId);
            PacketCodec.WriteString(writer, item.Name);
            writer.Write(item.X); writer.Write(item.Y); writer.Write(item.Z);
            PacketCodec.WriteString(writer, item.Scene);
        }
    }

    public void Deserialize(BinaryReader reader)
    {
        Items.Clear();
        var count = reader.ReadByte();
        if (count > MaxItems) throw new InvalidDataException("Too many ground items.");
        var ids = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var item = new GroundItemRecord
            {
                Id = reader.ReadUInt32(),
                DefinitionId = reader.ReadInt32(),
                Name = PacketCodec.ReadString(reader),
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                Scene = PacketCodec.ReadString(reader),
            };
            if (item.Id == 0 || item.DefinitionId <= 0
                || ShareItemOffer.InvalidText(item.Name, 80)
                || ShareItemOffer.InvalidText(item.Scene, 160)
                || !float.IsFinite(item.X) || !float.IsFinite(item.Y) || !float.IsFinite(item.Z))
                throw new InvalidDataException("Invalid ground item.");
            if (!ids.Add(item.Id)) throw new InvalidDataException("Duplicate ground item.");
            Items.Add(item);
        }
    }
}

internal sealed class GroundItemRecord
{
    public uint Id;
    public int DefinitionId;
    public string Name = "";
    public float X, Y, Z;
    public string Scene = "";

    internal GroundItemRecord Copy() => new()
    { Id = Id, DefinitionId = DefinitionId, Name = Name, X = X, Y = Y, Z = Z, Scene = Scene };
}

internal sealed class DropItemRequest : IPacket
{
    public uint RequestId;
    public int DefinitionId;
    public string ItemName = "";
    public void Serialize(BinaryWriter writer)
    { writer.Write(RequestId); writer.Write(DefinitionId); PacketCodec.WriteString(writer, ItemName); }
    public void Deserialize(BinaryReader reader)
    {
        RequestId = reader.ReadUInt32(); DefinitionId = reader.ReadInt32();
        ItemName = PacketCodec.ReadString(reader);
        if (RequestId == 0 || DefinitionId <= 0 || ShareItemOffer.InvalidText(ItemName, 80))
            throw new InvalidDataException("Invalid drop request.");
    }
}

internal sealed class PickupGroundItemRequest : IPacket
{
    public uint GroundItemId;
    public void Serialize(BinaryWriter writer) => writer.Write(GroundItemId);
    public void Deserialize(BinaryReader reader)
    { GroundItemId = reader.ReadUInt32(); if (GroundItemId == 0) throw new InvalidDataException("Invalid pickup request."); }
}

internal sealed class GroundItemDelivery : IPacket
{
    public uint GroundItemId;
    public int TargetPlayerId;
    public int DefinitionId;
    public string ItemName = "";
    public void Serialize(BinaryWriter writer)
    { writer.Write(GroundItemId); writer.Write(TargetPlayerId); writer.Write(DefinitionId); PacketCodec.WriteString(writer, ItemName); }
    public void Deserialize(BinaryReader reader)
    {
        GroundItemId = reader.ReadUInt32(); TargetPlayerId = reader.ReadInt32();
        DefinitionId = reader.ReadInt32(); ItemName = PacketCodec.ReadString(reader);
        if (GroundItemId == 0 || TargetPlayerId <= 0 || DefinitionId <= 0
            || ShareItemOffer.InvalidText(ItemName, 80)) throw new InvalidDataException("Invalid pickup delivery.");
    }
}

internal sealed class GroundItemReceipt : IPacket
{
    public uint GroundItemId;
    public bool Accepted;
    public string Reason = "";
    public void Serialize(BinaryWriter writer)
    { writer.Write(GroundItemId); writer.Write(Accepted); PacketCodec.WriteString(writer, Reason); }
    public void Deserialize(BinaryReader reader)
    {
        GroundItemId = reader.ReadUInt32(); Accepted = reader.ReadBoolean(); Reason = PacketCodec.ReadString(reader);
        if (GroundItemId == 0 || ShareItemOffer.InvalidText(Reason, 160))
            throw new InvalidDataException("Invalid pickup receipt.");
    }
}

internal sealed class ShareItemOffer : IPacket
{
    public uint TransferId;
    public int TargetPlayerId;
    public int DefinitionId;
    public int Count;
    public string ItemName = "";

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(TransferId); writer.Write(TargetPlayerId); writer.Write(DefinitionId);
        writer.Write(Count); PacketCodec.WriteString(writer, ItemName);
    }

    public void Deserialize(BinaryReader reader)
    {
        TransferId = reader.ReadUInt32(); TargetPlayerId = reader.ReadInt32();
        DefinitionId = reader.ReadInt32(); Count = reader.ReadInt32();
        ItemName = PacketCodec.ReadString(reader);
        if (TransferId == 0 || DefinitionId <= 0 || Count is < 1 or > 10
            || InvalidText(ItemName, 80))
            throw new InvalidDataException("Invalid item transfer offer.");
    }

    internal static bool InvalidText(string value, int maxLength)
        => value == null || value.Length > maxLength || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;
}

internal sealed class ShareItemDelivery : IPacket
{
    public uint TransferId;
    public int SenderPlayerId;
    public int TargetPlayerId;
    public int DefinitionId;
    public int Count;
    public string ItemName = "";

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(TransferId); writer.Write(SenderPlayerId); writer.Write(TargetPlayerId);
        writer.Write(DefinitionId); writer.Write(Count); PacketCodec.WriteString(writer, ItemName);
    }

    public void Deserialize(BinaryReader reader)
    {
        TransferId = reader.ReadUInt32(); SenderPlayerId = reader.ReadInt32();
        TargetPlayerId = reader.ReadInt32(); DefinitionId = reader.ReadInt32();
        Count = reader.ReadInt32(); ItemName = PacketCodec.ReadString(reader);
        if (TransferId == 0 || SenderPlayerId <= 0 || TargetPlayerId <= 0
            || DefinitionId <= 0 || Count is < 1 or > 10 || ShareItemOffer.InvalidText(ItemName, 80))
            throw new InvalidDataException("Invalid item delivery.");
    }
}

internal sealed class ShareItemReceipt : IPacket
{
    public uint TransferId;
    public int SenderPlayerId;
    public bool Accepted;
    public string Reason = "";

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(TransferId); writer.Write(SenderPlayerId); writer.Write(Accepted);
        PacketCodec.WriteString(writer, Reason);
    }

    public void Deserialize(BinaryReader reader)
    {
        TransferId = reader.ReadUInt32(); SenderPlayerId = reader.ReadInt32();
        Accepted = reader.ReadBoolean(); Reason = PacketCodec.ReadString(reader);
        if (TransferId == 0 || SenderPlayerId <= 0 || ShareItemOffer.InvalidText(Reason, 160))
            throw new InvalidDataException("Invalid item transfer receipt.");
    }
}

internal sealed class ShareItemResult : IPacket
{
    public uint TransferId;
    public int SenderPlayerId;
    public int TargetPlayerId;
    public bool Accepted;
    public string Reason = "";

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(TransferId); writer.Write(SenderPlayerId); writer.Write(TargetPlayerId);
        writer.Write(Accepted); PacketCodec.WriteString(writer, Reason);
    }

    public void Deserialize(BinaryReader reader)
    {
        TransferId = reader.ReadUInt32(); SenderPlayerId = reader.ReadInt32();
        TargetPlayerId = reader.ReadInt32(); Accepted = reader.ReadBoolean();
        Reason = PacketCodec.ReadString(reader);
        if (TransferId == 0 || SenderPlayerId <= 0 || TargetPlayerId <= 0
            || ShareItemOffer.InvalidText(Reason, 160))
            throw new InvalidDataException("Invalid item transfer result.");
    }
}

/// <summary>
/// Proximity-limited transfer of ordinary consumables. The giver removes the item before
/// asking the host; a rejected or failed delivery restores it. The recipient acknowledges
/// successful storage, so a full backpack never silently consumes the giver's item.
///
/// Giving and dropping are only reachable from the native backpack actions: no chat
/// command sends an item, so a transfer always targets a stack the player has selected
/// in front of them rather than a name typed from memory.
/// </summary>
internal sealed class ShareItemsFeature : MultiplayerFeature
{
    private const float MaxDistance = 3.5f;
    private const float HostTimeoutSeconds = 12f;
    private const float SenderTimeoutSeconds = 20f;
    private const float OfferCooldownSeconds = 0.75f;
    private const int MaxHostPending = 32;
    private const int MaxTransfersPerSession = 4096;
    private const float PickupDistance = 1.6f;

    private HostCommand<ShareItemOffer> _offers;
    private HostCommand<ShareItemReceipt> _receipts;
    private HostEvent<ShareItemDelivery> _deliveries;
    private HostEvent<ShareItemResult> _results;
    private HostState<GroundItemsState> _groundState;
    private HostCommand<DropItemRequest> _dropRequests;
    private HostCommand<PickupGroundItemRequest> _pickupRequests;
    private HostEvent<GroundItemDelivery> _pickupDeliveries;
    private HostCommand<GroundItemReceipt> _pickupReceipts;
    private uint _nextTransferId;
    private uint _nextGroundItemId;

    private readonly Dictionary<TransferKey, PendingTransfer> _hostPending = new();
    private readonly HashSet<TransferKey> _seenTransfers = new();
    private readonly Dictionary<uint, PendingTransfer> _outgoing = new();
    private readonly Dictionary<int, float> _lastOfferAt = new();
    private readonly Dictionary<uint, GroundItemRecord> _groundItems = new();
    private readonly Dictionary<uint, PendingDrop> _outgoingDrops = new();
    private readonly Dictionary<uint, PendingPickup> _pendingPickups = new();
    private readonly HashSet<TransferKey> _seenDropRequests = new();
    private readonly HashSet<uint> _pickupRequested = new();
    private readonly List<GroundItem> _nearbyGroundItems = new();

    public override string Id => "share-items";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _deliveries = feature.HostEvent<ShareItemDelivery>("delivery", OnDelivery);
        _results = feature.HostEvent<ShareItemResult>("result", OnResult);
        _offers = feature.HostCommand<ShareItemOffer>("offer", HandleOfferOnHost);
        _receipts = feature.HostCommand<ShareItemReceipt>("receipt", HandleReceiptOnHost);
        _groundState = feature.HostState<GroundItemsState>("ground", OnGroundItemsChanged);
        _dropRequests = feature.HostCommand<DropItemRequest>("drop", HandleDropOnHost);
        _pickupRequests = feature.HostCommand<PickupGroundItemRequest>("pickup", HandlePickupOnHost);
        _pickupDeliveries = feature.HostEvent<GroundItemDelivery>("pickup-delivery", OnPickupDelivery);
        _pickupReceipts = feature.HostCommand<GroundItemReceipt>("pickup-receipt", HandlePickupReceiptOnHost);

        Game.Inventory.AddShareActions(CanGiveSelected, GiveSelectedToNearest,
            CanDropSelected, DropSelected);

        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnDrawHud(Game.Inventory.DrawGroundItems);
        feature.OnSessionStarted(() => { if (IsHost) _groundState.Set(new GroundItemsState()); });
        feature.OnPlayerLeft(OnPlayerLeft);
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        TickTimeouts();
        TickGroundPickup();
    }

    private bool CanGiveSelected()
        => IsConnected && Game.State.IsLocalPlayerInGame && TryFindNearestPlayer(out _, out _);

    private bool CanDropSelected()
        => IsConnected && Game.State.IsLocalPlayerInGame
           && _groundItems.Count < GroundItemsState.MaxItems;

    private void GiveSelectedToNearest(ShareableItem item)
    {
        if (!TryFindNearestPlayer(out var targetId, out var targetName))
        { Game.Chat.AddSystemLine("No player is close enough to receive this item."); return; }
        StartGive(item, 1, targetId, targetName);
    }

    /// <summary>
    /// Sends the stack selected in the backpack. Giving always goes through the native
    /// inventory action, so the item to remove is always a precise stack (unique id) and
    /// never a quantity picked by name.
    /// </summary>
    private void StartGive(ShareableItem item, int count, int targetId, string targetName)
    {
        if (!AreCloseEnough(LocalPlayerId, targetId, out var proximityReason))
        { Game.Chat.AddSystemLine(proximityReason); return; }

        var transferId = NextTransferId();
        if (!Game.Inventory.TryRemove(item.UniqueId, item.DefinitionId, count, out var reason))
        { Game.Chat.AddSystemLine(reason); return; }

        _outgoing.Add(transferId, new PendingTransfer
        {
            TransferId = transferId,
            SenderId = LocalPlayerId,
            TargetId = targetId,
            DefinitionId = item.DefinitionId,
            Count = count,
            ItemName = item.Name,
            ExpiresAt = Game.Time.UnscaledTime + SenderTimeoutSeconds,
        });

        _offers.Send(new ShareItemOffer
        {
            TransferId = transferId,
            TargetPlayerId = targetId,
            DefinitionId = item.DefinitionId,
            Count = count,
            ItemName = item.Name,
        }, (accepted, answer) =>
        {
            if (accepted)
            {
                if (_outgoing.ContainsKey(transferId))
                    Game.Chat.AddSystemLine($"Sending {item.Name} x{count} to {targetName}...");
                return;
            }
            RestoreOutgoing(transferId, string.IsNullOrWhiteSpace(answer)
                ? "Transfer rejected by host." : answer);
        });
    }

    private bool TryFindNearestPlayer(out int playerId, out string playerName)
    {
        playerId = 0; playerName = "";
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var local)
            || local.State != PlayerState.InGame) return false;
        var bestDistance = MaxDistance * MaxDistance;
        foreach (var player in Players)
        {
            if (player.IsLocal || !Game.Players.TryGetLocation(player.Id, out var other)
                || other.State != PlayerState.InGame
                || !string.Equals(local.Scene, other.Scene, StringComparison.Ordinal)) continue;
            var dx = local.X - other.X; var dy = local.Y - other.Y; var dz = local.Z - other.Z;
            var distance = dx * dx + dy * dy + dz * dz;
            if (distance > bestDistance) continue;
            bestDistance = distance; playerId = player.Id; playerName = player.Name;
        }
        return playerId != 0;
    }

    private void HandleOfferOnHost(HostRequest<ShareItemOffer> request)
    {
        var offer = request.Message;
        var key = new TransferKey(request.FromPlayerId, offer.TransferId);
        if (offer.TargetPlayerId == request.FromPlayerId)
        { request.Reject("You cannot give an item to yourself."); return; }
        if (!Game.Inventory.IsShareableDefinition(offer.DefinitionId))
        { request.Reject("That item cannot be shared."); return; }
        if (!HasPlayer(offer.TargetPlayerId))
        { request.Reject("Target player is no longer connected."); return; }
        if (_hostPending.Count >= MaxHostPending)
        { request.Reject("Too many item transfers are pending. Try again shortly."); return; }
        if (_seenTransfers.Contains(key))
        { request.Reject("Duplicate item transfer."); return; }
        if (_seenTransfers.Count >= MaxTransfersPerSession)
        { request.Reject("This session has reached its item-transfer limit."); return; }
        if (_lastOfferAt.TryGetValue(request.FromPlayerId, out var last)
            && Game.Time.UnscaledTime - last < OfferCooldownSeconds)
        { request.Reject("Please wait before sharing another item."); return; }
        if (!AreCloseEnough(request.FromPlayerId, offer.TargetPlayerId, out var reason))
        { request.Reject(reason); return; }

        var pending = new PendingTransfer
        {
            TransferId = offer.TransferId,
            SenderId = request.FromPlayerId,
            TargetId = offer.TargetPlayerId,
            DefinitionId = offer.DefinitionId,
            Count = offer.Count,
            ItemName = offer.ItemName,
            ExpiresAt = Game.Time.UnscaledTime + HostTimeoutSeconds,
        };
        // Register before emitting: host events are also applied locally during commit. If
        // the host is the recipient, its receipt can therefore arrive immediately.
        _hostPending[key] = pending;
        _seenTransfers.Add(key);
        _lastOfferAt[request.FromPlayerId] = Game.Time.UnscaledTime;
        request.Emit(_deliveries, new ShareItemDelivery
        {
            TransferId = pending.TransferId,
            SenderPlayerId = pending.SenderId,
            TargetPlayerId = pending.TargetId,
            DefinitionId = pending.DefinitionId,
            Count = pending.Count,
            ItemName = pending.ItemName,
        });
    }

    private void OnDelivery(int sourcePlayerId, ShareItemDelivery delivery)
    {
        if (sourcePlayerId != delivery.SenderPlayerId || delivery.TargetPlayerId != LocalPlayerId) return;
        var accepted = Game.Inventory.TryAdd(delivery.DefinitionId, delivery.Count, out var reason);
        if (accepted)
        {
            reason = "";
            Game.Chat.AddSystemLine($"Received {delivery.ItemName} x{delivery.Count} from "
                                    + $"{GetPlayerName(delivery.SenderPlayerId)}.");
        }

        _receipts.Send(new ShareItemReceipt
        {
            TransferId = delivery.TransferId,
            SenderPlayerId = delivery.SenderPlayerId,
            Accepted = accepted,
            Reason = reason ?? "Recipient could not store the item.",
        }, (committed, answer) =>
        {
            if (committed || !accepted) return;
            // The host did not accept our receipt: undo the local delivery. The giver will
            // either receive a failure result or restore on timeout.
            Game.Inventory.TryRemoveAny(delivery.DefinitionId, delivery.Count, out _);
            Game.Chat.AddSystemLine("Item transfer was cancelled by the host.");
        });
    }

    private void HandleReceiptOnHost(HostRequest<ShareItemReceipt> request)
    {
        var receipt = request.Message;
        var key = new TransferKey(receipt.SenderPlayerId, receipt.TransferId);
        if (!_hostPending.TryGetValue(key, out var pending))
        { request.Reject("Item transfer is no longer pending."); return; }
        if (pending.TargetId != request.FromPlayerId)
        { request.Reject("Only the intended recipient may acknowledge this transfer."); return; }

        request.Emit(_results, new ShareItemResult
        {
            TransferId = pending.TransferId,
            SenderPlayerId = pending.SenderId,
            TargetPlayerId = pending.TargetId,
            Accepted = receipt.Accepted,
            Reason = receipt.Accepted ? "" : receipt.Reason,
        });
        request.AfterCommit(() => _hostPending.Remove(key));
    }

    private void DropSelected(ShareableItem item)
    {
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var location)
            || location.State != PlayerState.InGame)
        { Game.Chat.AddSystemLine("You must be active in the game to drop an item."); return; }
        if (_groundItems.Count >= GroundItemsState.MaxItems)
        { Game.Chat.AddSystemLine("Too many shared items are already on the ground."); return; }
        if (!Game.Inventory.TryRemove(item.UniqueId, item.DefinitionId, 1, out var reason))
        { Game.Chat.AddSystemLine(reason); return; }

        var requestId = NextTransferId();
        _outgoingDrops[requestId] = new PendingDrop(item,
            Game.Time.UnscaledTime + SenderTimeoutSeconds);
        _dropRequests.Send(new DropItemRequest
        { RequestId = requestId, DefinitionId = item.DefinitionId, ItemName = item.Name },
            (accepted, answer) =>
            {
                if (accepted)
                {
                    _outgoingDrops.Remove(requestId);
                    Game.Chat.AddSystemLine($"Dropped {item.Name}.");
                    return;
                }
                RestoreDrop(requestId, string.IsNullOrWhiteSpace(answer)
                    ? "Drop rejected by host." : answer);
            });
    }

    private void HandleDropOnHost(HostRequest<DropItemRequest> request)
    {
        var message = request.Message;
        var key = new TransferKey(request.FromPlayerId, message.RequestId);
        if (_seenDropRequests.Contains(key)) { request.Reject("Duplicate drop request."); return; }
        if (_seenDropRequests.Count >= MaxTransfersPerSession)
        { request.Reject("This session has reached its item-drop limit."); return; }
        if (!Game.Inventory.IsShareableDefinition(message.DefinitionId))
        { request.Reject("That item cannot be shared."); return; }
        if (_groundItems.Count >= GroundItemsState.MaxItems)
        { request.Reject("Too many shared items are already on the ground."); return; }
        if (!Game.Players.TryGetLocation(request.FromPlayerId, out var location)
            || location.State != PlayerState.InGame || string.IsNullOrWhiteSpace(location.Scene))
        { request.Reject("Player location is unavailable."); return; }

        var record = new GroundItemRecord
        {
            Id = NextGroundItemId(),
            DefinitionId = message.DefinitionId,
            Name = message.ItemName,
            X = location.X,
            Y = location.Y,
            Z = location.Z,
            Scene = location.Scene,
        };
        var state = CaptureGroundState(record);
        request.Publish(_groundState, state);
        request.AfterCommit(() => _seenDropRequests.Add(key));
    }

    private void TickGroundPickup()
    {
        if (!IsConnected || !Game.State.IsLocalPlayerInGame || KeyboardCaptured
            || Game.Inventory.TryGetSelectedShareableItem(out _))
        { Game.Inventory.SelectGroundItem(Array.Empty<GroundItem>()); return; }
        if (!TryFindNearestGroundItem(out var item))
        { Game.Inventory.SelectGroundItem(Array.Empty<GroundItem>()); return; }

        _nearbyGroundItems.Clear();
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var local)) return;
        foreach (var candidate in _groundItems.Values)
        {
            var dx = candidate.X - item.X;
            var dy = candidate.Y - item.Y;
            var dz = candidate.Z - item.Z;
            if (candidate.Scene != item.Scene || dx * dx + dy * dy + dz * dz > 1.5f * 1.5f
                || SquaredDistance(local, candidate) > PickupDistance * PickupDistance) continue;
            _nearbyGroundItems.Add(new GroundItem(candidate.Id, candidate.DefinitionId,
                candidate.Name, candidate.X, candidate.Y, candidate.Z, candidate.Scene));
        }
        _nearbyGroundItems.Sort((left, right) => left.Id.CompareTo(right.Id));
        var selectedId = Game.Inventory.SelectGroundItem(_nearbyGroundItems);
        if (!_groundItems.TryGetValue(selectedId, out item)) return;
        if (!Game.Input.WasKeyPressed(GameKey.E) || !_pickupRequested.Add(item.Id)) return;
        _pickupRequests.Send(new PickupGroundItemRequest { GroundItemId = item.Id },
            (accepted, answer) =>
            {
                if (accepted) return;
                _pickupRequested.Remove(item.Id);
                if (!string.IsNullOrWhiteSpace(answer)) Game.Chat.AddSystemLine(answer);
            });
    }

    private bool TryFindNearestGroundItem(out GroundItemRecord nearest)
    {
        nearest = null;
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var local)) return false;
        var best = PickupDistance * PickupDistance;
        foreach (var item in _groundItems.Values)
        {
            if (!string.Equals(local.Scene, item.Scene, StringComparison.Ordinal)) continue;
            var dx = local.X - item.X; var dy = local.Y - item.Y; var dz = local.Z - item.Z;
            var distance = dx * dx + dy * dy + dz * dz;
            if (distance > best) continue;
            best = distance; nearest = item;
        }
        return nearest != null;
    }

    private void HandlePickupOnHost(HostRequest<PickupGroundItemRequest> request)
    {
        var id = request.Message.GroundItemId;
        if (!_groundItems.TryGetValue(id, out var item))
        { request.Reject("That item is no longer on the ground."); return; }
        if (_pendingPickups.ContainsKey(id))
        { request.Reject("That item is already being picked up."); return; }
        if (!Game.Players.TryGetLocation(request.FromPlayerId, out var player)
            || player.State != PlayerState.InGame
            || !string.Equals(player.Scene, item.Scene, StringComparison.Ordinal)
            || SquaredDistance(player, item) > PickupDistance * PickupDistance)
        { request.Reject("Move closer to the item to pick it up."); return; }

        var pending = new PendingPickup(item.Copy(), request.FromPlayerId,
            Game.Time.UnscaledTime + HostTimeoutSeconds);
        _pendingPickups[id] = pending;
        request.Publish(_groundState, CaptureGroundState(excludeId: id));
        request.Emit(_pickupDeliveries, new GroundItemDelivery
        {
            GroundItemId = id,
            TargetPlayerId = request.FromPlayerId,
            DefinitionId = item.DefinitionId,
            ItemName = item.Name,
        });
    }

    private void OnPickupDelivery(int _, GroundItemDelivery delivery)
    {
        if (delivery.TargetPlayerId != LocalPlayerId) return;
        _pickupRequested.Remove(delivery.GroundItemId);
        var accepted = Game.Inventory.TryAdd(delivery.DefinitionId, 1, out var reason);
        if (accepted)
        {
            reason = "";
            Game.Chat.AddSystemLine($"Picked up {delivery.ItemName}.");
        }
        _pickupReceipts.Send(new GroundItemReceipt
        { GroundItemId = delivery.GroundItemId, Accepted = accepted, Reason = reason ?? "Backpack is full." },
            (committed, _) =>
            {
                if (committed || !accepted) return;
                Game.Inventory.TryRemoveAny(delivery.DefinitionId, 1, out _);
            });
    }

    private void HandlePickupReceiptOnHost(HostRequest<GroundItemReceipt> request)
    {
        var receipt = request.Message;
        if (!_pendingPickups.TryGetValue(receipt.GroundItemId, out var pending))
        { request.Reject("Pickup is no longer pending."); return; }
        if (pending.TargetPlayerId != request.FromPlayerId)
        { request.Reject("Only the intended player may confirm this pickup."); return; }
        if (!receipt.Accepted)
            request.Publish(_groundState, CaptureGroundState(pending.Item));
        request.AfterCommit(() => _pendingPickups.Remove(receipt.GroundItemId));
    }

    private void OnGroundItemsChanged(GroundItemsState state)
    {
        _groundItems.Clear();
        var views = new List<GroundItem>();
        foreach (var item in state.Items)
        {
            _groundItems[item.Id] = item.Copy();
            views.Add(new GroundItem(item.Id, item.DefinitionId, item.Name,
                item.X, item.Y, item.Z, item.Scene));
        }
        _pickupRequested.RemoveWhere(id => !_groundItems.ContainsKey(id));
        Game.Inventory.SetGroundItems(views);
    }

    private GroundItemsState CaptureGroundState(GroundItemRecord include = null, uint excludeId = 0)
    {
        var state = new GroundItemsState();
        foreach (var item in _groundItems.Values)
            if (item.Id != excludeId) state.Items.Add(item.Copy());
        if (include != null) state.Items.Add(include.Copy());
        return state;
    }

    private static float SquaredDistance(PlayerLocation player, GroundItemRecord item)
    {
        var dx = player.X - item.X; var dy = player.Y - item.Y; var dz = player.Z - item.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private void OnResult(int _, ShareItemResult result)
    {
        if (result.SenderPlayerId != LocalPlayerId) return;
        if (!_outgoing.TryGetValue(result.TransferId, out var pending)) return;
        if (result.Accepted)
        {
            _outgoing.Remove(result.TransferId);
            Game.Chat.AddSystemLine($"Gave {pending.ItemName} x{pending.Count} to "
                                    + $"{GetPlayerName(pending.TargetId)}.");
        }
        else
        {
            RestoreOutgoing(result.TransferId, string.IsNullOrWhiteSpace(result.Reason)
                ? "Recipient could not accept the item." : result.Reason);
        }
    }

    private void TickTimeouts()
    {
        var now = Game.Time.UnscaledTime;
        if (IsHost && _hostPending.Count > 0)
        {
            List<TransferKey> expired = null;
            foreach (var pair in _hostPending)
                if (now >= pair.Value.ExpiresAt) (expired ??= new()).Add(pair.Key);
            if (expired != null)
                foreach (var key in expired)
                {
                    var pending = _hostPending[key];
                    _hostPending.Remove(key);
                    SendFailure(pending, "Recipient did not answer in time.");
                }
        }

        if (_outgoing.Count > 0)
        {
            List<uint> senderExpired = null;
            foreach (var pair in _outgoing)
                if (now >= pair.Value.ExpiresAt) (senderExpired ??= new()).Add(pair.Key);
            if (senderExpired != null)
                foreach (var transferId in senderExpired)
                    RestoreOutgoing(transferId, "Transfer timed out.");
        }

        if (IsHost && _pendingPickups.Count > 0)
        {
            List<uint> pickupExpired = null;
            foreach (var pair in _pendingPickups)
                if (now >= pair.Value.ExpiresAt) (pickupExpired ??= new()).Add(pair.Key);
            if (pickupExpired != null)
                foreach (var id in pickupExpired)
                {
                    var pending = _pendingPickups[id];
                    _pendingPickups.Remove(id);
                    _groundState.Set(CaptureGroundState(pending.Item));
                }
        }

        if (_outgoingDrops.Count > 0)
        {
            List<uint> dropExpired = null;
            foreach (var pair in _outgoingDrops)
                if (now >= pair.Value.ExpiresAt) (dropExpired ??= new()).Add(pair.Key);
            if (dropExpired != null)
                foreach (var id in dropExpired) RestoreDrop(id, "Drop timed out.");
        }
    }

    private void OnPlayerLeft(int playerId, string _)
    {
        if (!IsHost) return;
        List<TransferKey> cancelled = null;
        foreach (var pair in _hostPending)
            if (pair.Value.TargetId == playerId || pair.Value.SenderId == playerId)
                (cancelled ??= new()).Add(pair.Key);
        if (cancelled != null)
            foreach (var key in cancelled)
            {
                var pending = _hostPending[key];
                _hostPending.Remove(key);
                if (pending.TargetId == playerId)
                    SendFailure(pending, "Recipient left the session.");
            }

        List<uint> abandonedPickups = null;
        foreach (var pair in _pendingPickups)
            if (pair.Value.TargetPlayerId == playerId) (abandonedPickups ??= new()).Add(pair.Key);
        if (abandonedPickups != null)
            foreach (var id in abandonedPickups)
            {
                var pickup = _pendingPickups[id];
                _pendingPickups.Remove(id);
                _groundState.Set(CaptureGroundState(pickup.Item));
            }
    }

    private void SendFailure(PendingTransfer pending, string reason)
        => _results.Send(new ShareItemResult
        {
            TransferId = pending.TransferId,
            SenderPlayerId = pending.SenderId,
            TargetPlayerId = pending.TargetId,
            Accepted = false,
            Reason = reason,
        });

    private void RestoreOutgoing(uint transferId, string reason)
    {
        if (!_outgoing.Remove(transferId, out var pending)) return;
        if (Game.Inventory.TryAdd(pending.DefinitionId, pending.Count, out var restoreReason))
            Game.Chat.AddSystemLine($"{reason} {pending.ItemName} x{pending.Count} was returned.");
        else
        {
            LogWarning($"Could not restore transfer {pending.SenderId}:{pending.TransferId}: {restoreReason}");
            Game.Chat.AddSystemLine($"WARNING: {reason} Could not return {pending.ItemName} x{pending.Count}: "
                                    + restoreReason);
        }
    }

    private void RestoreDrop(uint requestId, string reason)
    {
        if (!_outgoingDrops.Remove(requestId, out var pending)) return;
        var item = pending.Item;
        if (Game.Inventory.TryAdd(item.DefinitionId, 1, out var restoreReason))
            Game.Chat.AddSystemLine($"{reason} {item.Name} was returned.");
        else
            Game.Chat.AddSystemLine($"WARNING: {reason} Could not return {item.Name}: {restoreReason}");
    }

    private void Reset()
    {
        var ids = new List<uint>(_outgoing.Keys);
        foreach (var id in ids) RestoreOutgoing(id, "Session ended.");
        var dropIds = new List<uint>(_outgoingDrops.Keys);
        foreach (var id in dropIds) RestoreDrop(id, "Session ended.");
        _hostPending.Clear();
        _pendingPickups.Clear();
        _groundItems.Clear();
        _pickupRequested.Clear();
        _seenDropRequests.Clear();
        Game.Inventory.SetGroundItems(Array.Empty<GroundItem>());
        Game.Inventory.SelectGroundItem(Array.Empty<GroundItem>());
        Game.Hud.HideMessage("pickup");
        _seenTransfers.Clear();
        _lastOfferAt.Clear();
    }

    private bool AreCloseEnough(int senderId, int targetId, out string reason)
    {
        reason = "";
        if (!Game.Players.TryGetLocation(senderId, out var sender)
            || !Game.Players.TryGetLocation(targetId, out var target)
            || sender.State != PlayerState.InGame || target.State != PlayerState.InGame)
        { reason = "Both players must be active in the game."; return false; }
        if (!string.Equals(sender.Scene, target.Scene, StringComparison.Ordinal))
        { reason = "Both players must be in the same area."; return false; }
        var dx = sender.X - target.X; var dy = sender.Y - target.Y; var dz = sender.Z - target.Z;
        if (dx * dx + dy * dy + dz * dz <= MaxDistance * MaxDistance) return true;
        reason = $"Move within {MaxDistance.ToString("0.0", CultureInfo.InvariantCulture)} m "
                 + "of the other player to share items.";
        return false;
    }

    private bool HasPlayer(int playerId)
    {
        foreach (var player in Players) if (player.Id == playerId) return true;
        return false;
    }

    private uint NextTransferId()
    {
        do { _nextTransferId++; } while (_nextTransferId == 0 || _outgoing.ContainsKey(_nextTransferId));
        return _nextTransferId;
    }

    private uint NextGroundItemId()
    {
        do { _nextGroundItemId++; }
        while (_nextGroundItemId == 0 || _groundItems.ContainsKey(_nextGroundItemId)
               || _pendingPickups.ContainsKey(_nextGroundItemId));
        return _nextGroundItemId;
    }

    private readonly struct TransferKey : IEquatable<TransferKey>
    {
        internal TransferKey(int senderId, uint transferId)
        { SenderId = senderId; TransferId = transferId; }
        internal int SenderId { get; }
        internal uint TransferId { get; }
        public bool Equals(TransferKey other) => SenderId == other.SenderId && TransferId == other.TransferId;
        public override bool Equals(object obj) => obj is TransferKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SenderId, TransferId);
    }

    private sealed class PendingTransfer
    {
        internal uint TransferId;
        internal int SenderId;
        internal int TargetId;
        internal int DefinitionId;
        internal int Count;
        internal string ItemName;
        internal float ExpiresAt;
    }

    private sealed class PendingDrop
    {
        internal PendingDrop(ShareableItem item, float expiresAt)
        { Item = item; ExpiresAt = expiresAt; }
        internal ShareableItem Item { get; }
        internal float ExpiresAt { get; }
    }

    private sealed class PendingPickup
    {
        internal PendingPickup(GroundItemRecord item, int targetPlayerId, float expiresAt)
        { Item = item; TargetPlayerId = targetPlayerId; ExpiresAt = expiresAt; }
        internal GroundItemRecord Item { get; }
        internal int TargetPlayerId { get; }
        internal float ExpiresAt { get; }
    }
}
