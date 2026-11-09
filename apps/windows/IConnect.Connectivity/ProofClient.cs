using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

namespace IConnect.Connectivity;

public sealed class ProofClient : IAsyncDisposable
{
    public sealed record TrustedPeer(string Id, string Name, string PublicKey, string ApprovalEpoch = "");
    public enum PeerLinkPhase { Unknown, Connecting, Authenticated, Ended, Failed, Canceled, Unavailable, Revoked }
    public sealed record PeerLinkState(string PeerId, bool Authenticated, string Message, PeerLinkPhase Phase = PeerLinkPhase.Unknown);
    private readonly DeviceIdentity identity;
    private readonly SignalConnection connection;
    private readonly Uri server;
    private readonly string directory;
    private readonly string? stun;
    private readonly Action<string> report;
    private readonly Func<string, IFileService>? fileServiceFactory;
    private readonly FileTrafficPolicy? traffic;
    private readonly object gate = new();
    private readonly TrustedPeerStore peers;
    private readonly bool ownsTrust;
    private readonly bool sharePresence;
    private readonly PresenceTracker presence;
    private Task presenceRun = Task.CompletedTask;
    public bool PresenceSupported => connection.SupportsPresence;
    public IReadOnlySet<string> AvailablePeers { get { lock (gate) return presence.Available.Where(id => peers.TryGet(id, out _)).ToHashSet(); } }
    public event Action? PresenceChanged;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> jobs = [];
    private readonly HashSet<string> seenSignals = [];
    private readonly HashSet<string> sessionIds = [];
    private readonly HashSet<string> discardedSessions = [];
    private string? token;
    private long invitationExpiry;
    private byte[]? expectedHost;
    private bool pairingHost;
    private bool locallyApproved;
    private Pairing? pending;
    private PeerSession? session;
    private bool disposed;
    public IReadOnlyCollection<TrustedPeer> Peers => peers.All;
    public Pairing? PendingPairing => pending?.Clone();
    public event Action<string>? InvitationCreated;
    public event Action<Pairing>? PairingRequested;
    public event Action<TrustedPeer>? Paired;
    public event Action<PeerLinkState>? SessionStateChanged;
    public Task? SessionAuthenticated => session?.Authenticated;
    public bool AcceptsFolderBatches { get { lock (gate) return session?.AcceptsFolderBatches == true; } }
    public bool AcceptsPublishedBatchChecks { get { lock (gate) return session?.AcceptsPublishedBatchChecks == true; } }
    public bool AcceptsPublishedBatchCheckCancellation { get { lock (gate) return session?.AcceptsPublishedBatchCheckCancellation == true; } }
    public bool AcceptsPublishedUploadChecks { get { lock (gate) return session?.AcceptsPublishedUploadChecks == true; } }
    public bool PairingActive => pending is { } pairing && pairing.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || token is not null && invitationExpiry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool SignallingRefreshNeeded { get { lock (gate) return sessionIds.Count >= 32 || seenSignals.Count >= 1024; } }

    public ProofClient(DeviceIdentity identity, SignalConnection connection, Uri server,
        string stateDirectory, Action<string> report, string? stun = null, Func<string, IFileService>? fileServiceFactory = null,
        TrustedPeerStore? trustStore = null, bool sharePresence = false, FileTrafficPolicy? traffic = null)
    {
        this.identity = identity; this.connection = connection; this.server = server;
        directory = stateDirectory; this.report = report; this.stun = stun;
        this.fileServiceFactory = fileServiceFactory;
        this.traffic = traffic;
        Directory.CreateDirectory(directory);
        ownsTrust = trustStore is null;
        peers = trustStore ?? new TrustedPeerStore(directory, identity.Id);
        peers.BindOwnerIdentity(identity.Id);
        this.sharePresence = sharePresence; presence = new(identity.Id);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var presenceTask = presenceRun = connection.SupportsPresence && sharePresence ? PresenceLoopAsync(linked.Token) : Task.CompletedTask;
        try
        {
            while (await connection.ReceiveAsync(linked.Token) is { } frame)
            {
                switch (frame.Kind)
                {
                    case "presence":
                        if (!connection.SupportsPresence || !sharePresence) throw new InvalidDataException("Unrequested availability report.");
                        lock (gate) presence.Accept(PresenceSnapshot.Parser.ParseFrom(frame.Payload));
                        PublishPresence();
                        break;
                    case "invitation":
                    {
                        var invitation = Invitation.Parser.ParseFrom(frame.Payload);
                        if (!ProtocolRules.TokenPattern().IsMatch(invitation.Token) ||
                            invitation.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
                            invitation.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())
                            throw new InvalidDataException("Invalid invitation.");
                        token = invitation.Token; expectedHost = identity.PublicKey; pairingHost = true;
                        invitationExpiry = invitation.ExpiresAt;
                        pending = null; locallyApproved = false;
                        var uri = $"iconnect://pair?server={Uri.EscapeDataString(server.AbsoluteUri)}&token={token}&key={ProtocolRules.Base64Url(identity.PublicKey)}";
                        InvitationCreated?.Invoke(uri);
                        break;
                    }
                    case "pairing":
                    {
                        var pairing = Pairing.Parser.ParseFrom(frame.Payload);
                        if (pairingHost && pairing.ExpiresAt != invitationExpiry)
                            throw new InvalidDataException("The original invitation expiry cannot be extended.");
                        PairingTrust.ValidateContext(pairing, identity.PublicKey, token ?? "", expectedHost ?? [], pairingHost, DateTimeOffset.UtcNow);
                        if (pending is not null) throw new InvalidDataException("Unexpected additional pairing request.");
                        pending = pairing;
                        PairingRequested?.Invoke(pairing.Clone());
                        break;
                    }
                    case "paired":
                    {
                        if (pending is null) throw new InvalidDataException("No locally initiated pairing.");
                        var completed = Pairing.Parser.ParseFrom(frame.Payload);
                        PairingTrust.ValidateCompletion(pending, completed, locallyApproved);
                        // Display the label validated when the owner inspected the request;
                        // coordinator-supplied labels are not part of the identity signature.
                        var remote = pairingHost ? pending.Guest : pending.Host;
                        var peer = new TrustedPeer(ProtocolRules.DeviceId(remote.PublicKey.ToByteArray()), remote.Name,
                            Convert.ToBase64String(remote.PublicKey.ToByteArray()), completed.TransactionId);
                        lock (gate) peers.Add(peer);
                        pending = null; token = null; expectedHost = null; locallyApproved = false;
                        report($"Paired with {peer.Name}. Storage still requires a separate owner-approved grant.");
                        Paired?.Invoke(peer);
                        report($"After both devices show paired, start a session with: connect {peer.Id}");
                        break;
                    }
                    case "rejected":
                        pending = null; token = null; expectedHost = null; locallyApproved = false;
                        report("Pairing rejected or cancelled.");
                        break;
                    case "signal": await HandleSignalAsync(Signal.Parser.ParseFrom(frame.Payload), linked.Token); break;
                    case "signal-unavailable":
                    {
                        var unavailable = SignalUnavailable.Parser.ParseFrom(frame.Payload);
                        if (!ProtocolRules.TokenPattern().IsMatch(unavailable.SessionId) || unavailable.PeerId.Length != 64 || unavailable.PeerId.Any(c => !char.IsAsciiHexDigitLower(c)))
                            throw new InvalidDataException("Invalid signalling status.");
                        lock (gate)
                        {
                            if (session is { } active && active.Id == unavailable.SessionId && active.RemoteId == unavailable.PeerId)
                            {
                                discardedSessions.Add(active.Id);
                                active.Dispose(); session = null;
                                PublishSessionState(new(unavailable.PeerId, false, "The paired device is unavailable. Open its app and check its network.", PeerLinkPhase.Unavailable));
                            }
                        }
                        break;
                    }
                    default: throw new InvalidDataException("Unsupported coordinator response.");
                }
            }
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
            session?.Dispose();
            lock (gate) presence.Clear();
            PublishPresence();
            try { await presenceTask; } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private HashSet<string> publishedPresence = [];
    private void PublishPresence()
    {
        bool changed;
        lock (gate)
        {
            var current = presence.Available.Where(id => peers.TryGet(id, out _)).ToHashSet();
            changed = !publishedPresence.SetEquals(current); publishedPresence = current;
        }
        if (changed) PresenceChanged?.Invoke();
    }
    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                PresenceUpdate? update;
                lock (gate) update = presence.Next(true, peers.All.Select(x => x.Id));
                if (update is not null) await connection.SendAsync("presence", update, ct);
                PublishPresence();
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch { lifetime.Cancel(); throw; }
    }

    public Task CreateInvitationAsync(CancellationToken ct = default)
    { peers.RequireCurrentOwner(); return connection.SendAsync("invite", new Notice(), ct); }

    public async Task JoinInvitationAsync(string invitationUri, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(invitationUri, UriKind.Absolute, out var invitation) || invitation.Scheme != "iconnect" || invitation.Host != "pair")
            throw new InvalidDataException("Expected an iConnect pairing invitation.");
        var fields = invitation.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
            .ToDictionary(x => x[0], x => x.Length == 2 ? Uri.UnescapeDataString(x[1]) : "");
        if (fields.Count != 3 || !fields.TryGetValue("server", out var endpoint) || new Uri(endpoint) != server ||
            !fields.TryGetValue("token", out var invitationToken) || !ProtocolRules.TokenPattern().IsMatch(invitationToken) ||
            !fields.TryGetValue("key", out var publicKey)) throw new InvalidDataException("Invitation does not match the configured server.");
        var hostKey = ProtocolRules.Decode64(publicKey);
        _ = ProtocolRules.DeviceId(hostKey);
        token = invitationToken; expectedHost = hostKey; pairingHost = false; pending = null; locallyApproved = false;
        await connection.SendAsync("claim", new Claim { Token = token, HostPublicKey = ByteString.CopyFrom(hostKey) }, ct);
    }

    public async Task ApproveAsync(string code, CancellationToken ct = default)
    {
        if (pending is null || code != ProtocolRules.VerificationCode(pending) ||
            pending.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidDataException("Check the other device's code; pairing must still be current.");
        locallyApproved = true;
        await connection.SendAsync("approve", new Approval
        {
            TransactionId = pending.TransactionId, Approved = true,
            Signature = ByteString.CopyFrom(identity.Sign(ProtocolRules.PairingTranscript(pending)))
        }, ct);
        report("Local approval sent. Waiting for the other device.");
    }

    public async Task RejectAsync(CancellationToken ct = default)
    {
        if (pending is null) return;
        await connection.SendAsync("approve", new Approval { TransactionId = pending.TransactionId, Approved = false }, ct);
        pending = null; token = null; expectedHost = null; locallyApproved = false;
    }

    public Task ConnectPeerAsync(string peerId, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!peers.TryGet(peerId, out var peer)) throw new InvalidDataException("Choose a paired device.");
            if (sessionIds.Count >= 64) throw new InvalidDataException("Reconnect to the coordinator before starting more direct sessions.");
            if (session is { } active && active.RemoteId==peerId && !active.Authenticated.IsCompleted) return Task.CompletedTask;
            StartSession(peer, ProtocolRules.NewToken(), true, ct);
            session!.Rtc.Offer();
        }
        return Task.CompletedTask;
    }

    private void StartSession(TrustedPeer peer, string id, bool initiator, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!peers.TryGet(peer.Id, out var current) || current != peer)
            throw new InvalidDataException("Device access was removed.");
        if(session is { } previous) { discardedSessions.Add(previous.Id); previous.Dispose(); }
        var fileService = fileServiceFactory?.Invoke(peer.Id);
        try
        {
            if (fileService is WindowsFileService windows) windows.BindOwnerPolicy(peers.OwnerBinding!, peer.Id, peers.EnsureUploadScope(peer.Id));
            session = new(identity, Convert.FromBase64String(peer.PublicKey), id, initiator,
                Path.Combine(directory, "inbox"), report, stun, fileService: fileService, traffic: traffic);
        }
        catch { fileService?.Dispose(); throw; }
        sessionIds.Add(id);
        var active = session;
        PublishSessionState(active, PeerLinkPhase.Connecting, "Connecting directly…");
        jobs.Add(Observe(async () =>
        {
            var ending = PeerLinkPhase.Ended;
            try
            {
                var running = active.RunAsync(lifetime.Token);
                async Task Announce()
                {
                    await active.Authenticated;
                    PublishSessionState(active, PeerLinkPhase.Authenticated, "Authenticated direct connection");
                }
                await Task.WhenAll(running, Announce());
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { ending = PeerLinkPhase.Canceled; throw; }
            catch (Exception) { ending = PeerLinkPhase.Failed; throw; }
            finally { lock (gate) { discardedSessions.Add(active.Id); PublishSessionState(active, ending, "Disconnected"); } }
        }));
        jobs.Add(Observe(async () =>
        {
            await foreach (var local in active.Rtc.Signals.ReadAllAsync(lifetime.Token))
            {
                var signal = new Signal
                {
                    SessionId = id, FromDevice = identity.Id, ToDevice = peer.Id, Kind = local.Kind,
                    Body = local.Body, Mid = local.Mid, Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                signal.Signature = ByteString.CopyFrom(identity.Sign(ProtocolRules.SignalTranscript(signal)));
                Task sending;
                lock(gate)
                {
                    if(!ReferenceEquals(session,active) || active.IsClosed || disposed) continue;
                    // Check the session and enqueue under the same gate used by replacement.
                    // A superseded offer/candidate must never overtake the new session's answer.
                    sending=connection.SendAsync("signal",signal,lifetime.Token);
                }
                await sending;
            }
        }));
    }

    private Task HandleSignalAsync(Signal signal, CancellationToken ct)
    {
        lock (gate) return HandleSignalLocked(signal, ct);
    }

    private Task HandleSignalLocked(Signal signal, CancellationToken ct)
    {
        if (!peers.TryGet(signal.FromDevice, out var peer)) throw new InvalidDataException("Unpaired peer requested a connection.");
        ProtocolRules.ValidateSignal(signal, peer.Id, identity.Id, Convert.FromBase64String(peer.PublicKey), DateTimeOffset.UtcNow);
        if (seenSignals.Count >= 2048 || !seenSignals.Add(ProtocolRules.Base64Url(signal.Nonce.ToByteArray())))
            throw new InvalidDataException("Replayed or excessive connection messages.");
        if(discardedSessions.Contains(signal.SessionId)) return Task.CompletedTask;
        if (session is { IsClosed: true } ended && ended.Id == signal.SessionId && ended.RemoteId == peer.Id)
        { discardedSessions.Add(ended.Id); return Task.CompletedTask; }
        if (signal.Kind == "offer")
        {
            if (sessionIds.Count >= 64 || sessionIds.Contains(signal.SessionId)) throw new InvalidDataException("Replayed session offer.");
            if(session is {IsInitiator:true} active && active.RemoteId==peer.Id && !active.Authenticated.IsCompleted && StringComparer.Ordinal.Compare(identity.Id,peer.Id)<0)
            {
                sessionIds.Add(signal.SessionId); discardedSessions.Add(signal.SessionId);
                report("Both devices connected at once. Keeping one authenticated connection attempt."); return Task.CompletedTask;
            }
            StartSession(peer, signal.SessionId, false, ct);
        }
        if (session is null || signal.SessionId != session.Id || session.RemoteId != peer.Id)
            throw new InvalidDataException("Unexpected connection session.");
        if (!session.Rtc.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid))
            discardedSessions.Add(session.Id);
        return Task.CompletedTask;
    }

    public Task SendTestFileAsync(CancellationToken ct = default) =>
        session?.SendTestFileAsync(ct) ?? throw new InvalidDataException("Connect to a paired device first.");

    public Task<FileResponse> RequestFilesAsync(FileRequest request, CancellationToken ct = default)
    {
        PeerSession active;
        lock (gate) active = session ?? throw new InvalidDataException("Connect to a paired device first.");
        return active.RequestFilesAsync(request, ct);
    }

    public Func<FileRequest, CancellationToken, Task<FileResponse>> CaptureFileRequester(string peerId)
    {
        lock (gate) return RequireFileSession(peerId).RequestFilesAsync; // Never follows a later session to a different peer.
    }
    public Func<FileRequest, CancellationToken, Task<FileResponse>> CaptureFileRequester(string peerId, out Func<bool> current)
    {
        lock (gate)
        {
            var active = RequireFileSession(peerId);
            current = () => { lock (gate) return !disposed && ReferenceEquals(active, session) && !active.IsClosed && !active.Rtc.HasEnded; };
            return active.RequestFilesAsync;
        }
    }
    private PeerSession RequireFileSession(string peerId)
    {
        var active = session ?? throw new InvalidDataException("Connect to a paired device first.");
        if (active.RemoteId != peerId || !active.Authenticated.IsCompletedSuccessfully)
            throw new InvalidDataException("Connect to the selected device first.");
        return active;
    }

    public void Revoke(string peerId)
    {
        lock (gate) RevokeLocked(peerId);
    }

    private void RevokeLocked(string peerId)
    {
        var ended = session?.RemoteId == peerId;
        if (ended) { session!.Dispose(); session = null; }
        peers.Remove(peerId);
        PublishPresence();
        if (ended) PublishSessionState(new(peerId, false, "Device revoked", PeerLinkPhase.Revoked));
        report("Device revoked locally; active access ended.");
    }

    private void PublishSessionState(PeerSession active, PeerLinkPhase phase, string message)
    {
        lock (gate) { if (ReferenceEquals(session, active)) PublishSessionState(new(active.RemoteId, phase == PeerLinkPhase.Authenticated, message, phase)); }
    }
    private void PublishSessionState(PeerLinkState state)
    {
        foreach (var observer in SessionStateChanged?.GetInvocationList() ?? [])
            try { ((Action<PeerLinkState>)observer)(state); } catch (Exception) { /* A status/history observer cannot change the session result. */ }
    }

    private async Task Observe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { report($"Session ended: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel(); presence.Clear(); session?.Dispose();
            running = jobs.ToArray();
        }
        PublishPresence();
        try { await Task.WhenAll(running); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        // RunAsync observes this failure and ends the connection.
        // Disposal must still release the socket and trust lock after that task faults.
        try { await presenceRun; } catch (Exception) when (presenceRun.IsFaulted || presenceRun.IsCanceled) { }
        await connection.DisposeAsync();
        if (ownsTrust) peers.Dispose();
        lifetime.Dispose();
    }
}
