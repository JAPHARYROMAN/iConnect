using IConnect.Protocol;

namespace IConnect.Core;

/// <summary>Bounded, short-lived coordinator observations. Contains no trust or storage authority.</summary>
public sealed class PresenceTracker(string ownerId, TimeProvider? timeProvider = null)
{
    public const string Capability = "presence-v1";
    public const int MaxPeers = 128;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReportAge = TimeSpan.FromSeconds(25);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private PresenceUpdate? pending;
    private long sentAt;
    private HashSet<string> available = [];

    public static void Validate(PresenceUpdate update, string ownerId)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(update.RequestId) || update.PeerIds.Count > MaxPeers ||
            (!update.Enabled && update.PeerIds.Count != 0)) throw new InvalidDataException("Invalid availability request.");
        string? previous = null;
        foreach (var id in update.PeerIds)
        {
            if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigitLower(c)) || id == ownerId ||
                previous is not null && StringComparer.Ordinal.Compare(previous, id) >= 0)
                throw new InvalidDataException("Invalid availability scope.");
            previous = id;
        }
    }

    // Caller serializes this tracker with its connection/trust state.
    public PresenceUpdate? Next(bool enabled, IEnumerable<string> peerIds)
    {
        var ids = enabled ? peerIds.Order(StringComparer.Ordinal).ToArray() : [];
        var changed = pending is null || pending.Enabled != enabled || !pending.PeerIds.SequenceEqual(ids);
        if (!changed && (!enabled || clock.GetElapsedTime(sentAt) < PollInterval)) return null;
        var update = new PresenceUpdate { RequestId = ProtocolRules.NewToken(), Enabled = enabled };
        update.PeerIds.AddRange(ids); Validate(update, ownerId);
        if (changed) available.Clear();
        pending = update.Clone(); sentAt = clock.GetTimestamp();
        return update;
    }

    public void Accept(PresenceSnapshot snapshot)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(snapshot.RequestId) || snapshot.Peers.Count > MaxPeers)
            throw new InvalidDataException("Invalid availability report.");
        if (pending is null || snapshot.RequestId != pending.RequestId) return; // Superseded request.
        if (snapshot.Peers.Count != pending.PeerIds.Count || !snapshot.Peers.Select(x => x.PeerId).SequenceEqual(pending.PeerIds))
            throw new InvalidDataException("Availability report changed its scope.");
        if (clock.GetElapsedTime(sentAt) >= ReportAge) { available.Clear(); return; }
        available = snapshot.Peers.Where(x => x.Available && pending.Enabled).Select(x => x.PeerId).ToHashSet(StringComparer.Ordinal);
        reportedAt = sentAt; // Delayed reports cannot extend their lifetime.
    }
    private long? reportedAt;
    public IReadOnlySet<string> Available => reportedAt is { } at && clock.GetElapsedTime(at) < ReportAge
        ? available.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
    public void Clear() { pending = null; available.Clear(); reportedAt = null; }
}
