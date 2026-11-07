using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Rendezvous;

public sealed class PairingBroker(TimeProvider clock)
{
    private sealed record Device(Identity Identity, SignalConnection Connection, bool SignalStatus)
    {
        public PresenceUpdate? Presence { get; set; }
        public long RenewedAt { get; set; }
    }
    private sealed class Slot(string owner, Invitation invitation)
    {
        public string Owner { get; } = owner;
        public Invitation Invitation { get; } = invitation;
        public Pairing? Pairing { get; set; }
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Device> devices = [];
    private readonly Dictionary<string, Slot> invitations = [];

    public void PruneExpired()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var key in invitations.Where(x => x.Value.Invitation.ExpiresAt <= now).Select(x => x.Key).ToArray())
                invitations.Remove(key);
            foreach (var device in devices.Values)
                if (!Fresh(device)) device.Presence = null;
        }
    }

    public void Register(string id, Identity identity, SignalConnection connection, bool signalStatus = false)
    {
        lock (gate)
        {
            if (devices.Count >= 64 || devices.ContainsKey(id)) throw new InvalidDataException("Device unavailable.");
            devices.Add(id, new(identity.Clone(), connection, signalStatus));
        }
    }

    public void Unregister(string id, SignalConnection connection)
    {
        lock (gate)
        {
            if (devices.TryGetValue(id, out var device) && ReferenceEquals(device.Connection, connection))
            {
                devices.Remove(id);
                foreach (var key in invitations.Where(x => x.Value.Owner == id ||
                    (x.Value.Pairing is { } p && ProtocolRules.DeviceId(p.Guest.PublicKey.ToByteArray()) == id))
                    .Select(x => x.Key).ToArray()) invitations.Remove(key);
            }
        }
    }

    private bool Fresh(Device device) => device.Presence?.Enabled == true && clock.GetElapsedTime(device.RenewedAt) < PresenceTracker.Lease;

    public async Task HandleAsync(string id, SignalConnection connection, Frame frame, CancellationToken ct)
    {
        List<(SignalConnection Connection, string Kind, IMessage Payload)> deliveries = [];
        lock (gate)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var key in invitations.Where(x => x.Value.Invitation.ExpiresAt <= now).Select(x => x.Key).ToArray())
                invitations.Remove(key);
            if (!devices.TryGetValue(id, out var device) || !ReferenceEquals(device.Connection, connection))
                throw new InvalidDataException("Registration required.");
            switch (frame.Kind)
            {
                case "presence":
                {
                    var update = PresenceUpdate.Parser.ParseFrom(frame.Payload);
                    PresenceTracker.Validate(update, id);
                    device.Presence = update.Enabled ? update.Clone() : null;
                    device.RenewedAt = clock.GetTimestamp();
                    var snapshot = new PresenceSnapshot { RequestId = update.RequestId };
                    foreach (var peerId in update.PeerIds)
                        snapshot.Peers.Add(new PeerAvailability { PeerId = peerId, Available =
                            devices.TryGetValue(peerId, out var peer) && Fresh(peer) && peer.Presence!.PeerIds.Contains(id) });
                    deliveries.Add((device.Connection, "presence", snapshot));
                    break;
                }
                case "invite":
                {
                    foreach (var key in invitations.Where(x => x.Value.Owner == id).Select(x => x.Key).ToArray()) invitations.Remove(key);
                    if (invitations.Count >= 64) throw new InvalidDataException("Invitation capacity exceeded.");
                    var invitation = new Invitation { Token = ProtocolRules.NewToken(), ExpiresAt = now + 120_000 };
                    invitations.Add(invitation.Token, new(id, invitation));
                    deliveries.Add((device.Connection, "invitation", invitation));
                    break;
                }
                case "claim":
                {
                    var claim = Claim.Parser.ParseFrom(frame.Payload);
                    if (!invitations.TryGetValue(claim.Token, out var slot) || slot.Pairing is not null || slot.Owner == id ||
                        !devices.TryGetValue(slot.Owner, out var owner) || !owner.Identity.PublicKey.Equals(claim.HostPublicKey))
                        throw new InvalidDataException("Invitation is unavailable.");
                    var pairing = new Pairing
                    {
                        TransactionId = ProtocolRules.NewToken(), Token = claim.Token, ExpiresAt = slot.Invitation.ExpiresAt,
                        Host = owner.Identity.Clone(), Guest = device.Identity.Clone()
                    };
                    slot.Pairing = pairing;
                    deliveries.Add((owner.Connection, "pairing", pairing.Clone()));
                    deliveries.Add((device.Connection, "pairing", pairing.Clone()));
                    break;
                }
                case "approve":
                {
                    var approval = Approval.Parser.ParseFrom(frame.Payload);
                    var entry = invitations.SingleOrDefault(x => x.Value.Pairing?.TransactionId == approval.TransactionId);
                    var slot = entry.Value ?? throw new InvalidDataException("Pairing expired.");
                    var pairing = slot.Pairing!;
                    var guestId = ProtocolRules.DeviceId(pairing.Guest.PublicKey.ToByteArray());
                    if ((id != slot.Owner && id != guestId) || !devices.TryGetValue(slot.Owner, out var owner) ||
                        !devices.TryGetValue(guestId, out var guest)) throw new InvalidDataException("Pairing unavailable.");
                    if (!approval.Approved)
                    {
                        invitations.Remove(entry.Key);
                        deliveries.Add((owner.Connection, "rejected", new Notice { Message = "Pairing rejected." }));
                        deliveries.Add((guest.Connection, "rejected", new Notice { Message = "Pairing rejected." }));
                        break;
                    }
                    if (!ProtocolRules.Verify(device.Identity.PublicKey.ToByteArray(), ProtocolRules.PairingTranscript(pairing),
                        approval.Signature.ToByteArray())) throw new InvalidDataException("Invalid pairing approval.");
                    if (id == slot.Owner) pairing.HostApproval = approval.Signature;
                    else pairing.GuestApproval = approval.Signature;
                    if (pairing.HostApproval.Length > 0 && pairing.GuestApproval.Length > 0)
                    {
                        invitations.Remove(entry.Key);
                        deliveries.Add((owner.Connection, "paired", pairing.Clone()));
                        deliveries.Add((guest.Connection, "paired", pairing.Clone()));
                    }
                    break;
                }
                case "signal":
                {
                    var signal = Signal.Parser.ParseFrom(frame.Payload);
                    if (signal.FromDevice != id || signal.ToDevice.Length != 64 || signal.ToDevice.Any(c => !char.IsAsciiHexDigitLower(c)))
                        throw new InvalidDataException("Invalid signal recipient.");
                    ProtocolRules.ValidateSignal(signal, id, signal.ToDevice, device.Identity.PublicKey.ToByteArray(), clock.GetUtcNow());
                    if (!devices.TryGetValue(signal.ToDevice, out var target))
                    {
                        if (!device.SignalStatus) throw new InvalidDataException("Peer is unavailable.");
                        deliveries.Add((device.Connection, "signal-unavailable", new SignalUnavailable { SessionId = signal.SessionId, PeerId = signal.ToDevice }));
                        break;
                    }
                    deliveries.Add((target.Connection, "signal", signal));
                    break;
                }
                default: throw new InvalidDataException("Unsupported signalling operation.");
            }
        }
        foreach (var delivery in deliveries)
            await delivery.Connection.SendAsync(delivery.Kind, delivery.Payload, ct);
    }
}
