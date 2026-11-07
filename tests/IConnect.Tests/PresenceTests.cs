using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Rendezvous;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IConnect.Tests;

public sealed class PresenceTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp = 1_000;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(int milliseconds) => Interlocked.Add(ref timestamp, milliseconds);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-100); // Lease must not use wall time.
    }
    private static readonly string Owner = new('a', 64), Peer = new('b', 64), Stranger = new('c', 64);
    private sealed class FailedWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Fixture transport failure."));
    }
    private static PresenceSnapshot Report(PresenceUpdate update, bool available = true)
    {
        var result = new PresenceSnapshot { RequestId = update.RequestId };
        result.Peers.AddRange(update.PeerIds.Select(x => new PeerAvailability { PeerId = x, Available = available }));
        return result;
    }

    [Fact]
    public void Reports_expire_from_request_time_and_old_replies_cannot_revive_them()
    {
        var clock = new Clock(); var tracker = new PresenceTracker(Owner, clock);
        Assert.Empty(tracker.Available);
        var first = tracker.Next(true, [Peer])!; clock.Advance(9_999); tracker.Accept(Report(first));
        Assert.Contains(Peer, tracker.Available); Assert.Null(tracker.Next(true, [Peer]));
        clock.Advance(1); var next = tracker.Next(true, [Peer])!;
        tracker.Accept(Report(first, false)); Assert.Contains(Peer, tracker.Available);
        clock.Advance(15_000); Assert.Empty(tracker.Available);
        clock.Advance(10_000); tracker.Accept(Report(next)); Assert.Empty(tracker.Available);
    }

    [Fact]
    public void Disable_revocation_and_scope_substitution_clear_or_reject_observations()
    {
        var tracker = new PresenceTracker(Owner);
        var first = tracker.Next(true, [Peer])!; tracker.Accept(Report(first)); Assert.Contains(Peer, tracker.Available);
        var disabled = tracker.Next(false, [Peer])!; Assert.Empty(disabled.PeerIds); Assert.Empty(tracker.Available);
        tracker.Accept(Report(first)); Assert.Empty(tracker.Available); Assert.Null(tracker.Next(false, [Peer]));
        var replaced = tracker.Next(true, [Stranger])!; tracker.Accept(Report(first)); Assert.Empty(tracker.Available);
        var forged = Report(replaced); forged.Peers[0].PeerId = Peer;
        Assert.Throws<InvalidDataException>(() => tracker.Accept(forged)); Assert.Empty(tracker.Available);
        tracker.Accept(Report(replaced)); tracker.Clear(); tracker.Accept(Report(replaced)); Assert.Empty(tracker.Available);
    }

    [Fact]
    public void Presence_scopes_are_bounded_unique_canonical_and_never_contain_the_owner()
    {
        foreach (var ids in new[] { new[] { Owner }, new[] { Peer, Peer }, new[] { Stranger, Peer }, new[] { Peer.ToUpperInvariant() }, new[] { "unknown" }, Enumerable.Repeat(Peer, 129).ToArray() })
        {
            var update = new PresenceUpdate { RequestId = ProtocolRules.NewToken(), Enabled = true }; update.PeerIds.AddRange(ids);
            Assert.Throws<InvalidDataException>(() => PresenceTracker.Validate(update, Owner));
        }
        var disabled = new PresenceUpdate { RequestId = ProtocolRules.NewToken() }; disabled.PeerIds.Add(Peer);
        Assert.Throws<InvalidDataException>(() => PresenceTracker.Validate(disabled, Owner));
        var invalid = new PresenceUpdate { RequestId = "bad" }; Assert.Throws<InvalidDataException>(() => PresenceTracker.Validate(invalid, Owner));
    }

    private static WebApplicationFactory<Program> Server(Clock clock) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => { b.UseEnvironment("Development"); b.ConfigureServices(s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock); }); });
    private static async Task<SignalConnection> Register(WebApplicationFactory<Program> factory, DeviceIdentity identity, CancellationToken ct)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/signal"), ct);
        var connection = new SignalConnection(socket);
        var challenge = Challenge.Parser.ParseFrom((await connection.ReceiveAsync(ct))!.Payload);
        await connection.SendAsync("register", new Registration { Identity = identity.Descriptor,
            Signature = ByteString.CopyFrom(identity.Sign(ProtocolRules.RegistrationTranscript(challenge.Nonce.ToByteArray()))) }, ct);
        var registered = await connection.ReceiveAsync(ct); Assert.Equal("registered", registered!.Kind);
        connection.ConfirmRegistration(Notice.Parser.ParseFrom(registered.Payload), identity.Id);
        Assert.True(connection.SupportsPresence); return connection;
    }
    private static async Task<PresenceSnapshot> Update(SignalConnection connection, CancellationToken ct, params string[] ids)
    {
        var update = new PresenceUpdate { RequestId = ProtocolRules.NewToken(), Enabled = true }; update.PeerIds.AddRange(ids.Order(StringComparer.Ordinal));
        await connection.SendAsync("presence", update, ct); var frame = await connection.ReceiveAsync(ct); Assert.Equal("presence", frame!.Kind);
        var report = PresenceSnapshot.Parser.ParseFrom(frame.Payload); Assert.Equal(update.RequestId, report.RequestId);
        Assert.Equal(update.PeerIds, report.Peers.Select(x => x.PeerId)); return report;
    }

    [Fact]
    public async Task Coordinator_only_reports_mutual_opt_in_and_merges_unknown_disabled_and_unlisted_devices()
    {
        var clock = new Clock(); await using var factory = Server(clock);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var a = DeviceIdentity.Ephemeral("A"); using var b = DeviceIdentity.Ephemeral("B"); using var c = DeviceIdentity.Ephemeral("C"); using var unknown = DeviceIdentity.Ephemeral("Absent");
        await using var ca = await Register(factory, a, ct.Token); await using var cb = await Register(factory, b, ct.Token); await using var cc = await Register(factory, c, ct.Token);
        Assert.All((await Update(ca, ct.Token, b.Id, c.Id, unknown.Id)).Peers, x => Assert.False(x.Available));
        Assert.True(Assert.Single((await Update(cb, ct.Token, a.Id)).Peers).Available); // A explicitly listed B.
        var mutual = await Update(ca, ct.Token, b.Id, unknown.Id);
        Assert.True(mutual.Peers.Single(x => x.PeerId == b.Id).Available); Assert.False(mutual.Peers.Single(x => x.PeerId == unknown.Id).Available);
        Assert.False(Assert.Single((await Update(cc, ct.Token, a.Id)).Peers).Available); // A no longer lists C.
        await cb.SendAsync("presence", new PresenceUpdate { RequestId = ProtocolRules.NewToken() }, ct.Token);
        Assert.Empty(PresenceSnapshot.Parser.ParseFrom((await cb.ReceiveAsync(ct.Token))!.Payload).Peers);
        Assert.False(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        await Update(cb, ct.Token, unknown.Id); Assert.False(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        await ca.SendAsync("invite", new Notice(), ct.Token); Assert.Equal("invitation", (await ca.ReceiveAsync(ct.Token))!.Kind);
    }

    [Fact]
    public async Task Coordinator_expires_leases_and_reconnect_does_not_inherit_old_consent()
    {
        var clock = new Clock(); await using var factory = Server(clock); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var a = DeviceIdentity.Ephemeral("A"); using var b = DeviceIdentity.Ephemeral("B");
        await using var ca = await Register(factory, a, ct.Token); var cb = await Register(factory, b, ct.Token);
        await Update(ca, ct.Token, b.Id); await Update(cb, ct.Token, a.Id);
        Assert.True(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        clock.Advance(30_000); factory.Services.GetRequiredService<PairingBroker>().PruneExpired();
        Assert.False(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        Assert.True(Assert.Single((await Update(cb, ct.Token, a.Id)).Peers).Available);
        var broker = factory.Services.GetRequiredService<PairingBroker>();
        // A different connection object cannot remove or mutate this registration.
        broker.Unregister(b.Id, cb); // Client wrapper is deliberately not the server's connection object.
        Assert.True(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        await Assert.ThrowsAsync<InvalidDataException>(() => broker.HandleAsync(b.Id, cb, ProtocolRules.Pack("presence", new PresenceUpdate { RequestId = ProtocolRules.NewToken() }), ct.Token));
        await cb.DisposeAsync();
        // Wait for the in-memory server to observe close before re-registering; no external coordinator process.
        PresenceSnapshot closed;
        do { await Task.Delay(25, ct.Token); closed = await Update(ca, ct.Token, b.Id); } while (closed.Peers[0].Available);
        await using var restarted = await Register(factory, b, ct.Token);
        Assert.False(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
        await Update(restarted, ct.Token, a.Id); Assert.True(Assert.Single((await Update(ca, ct.Token, b.Id)).Peers).Available);
    }

    [Fact]
    public async Task Invalid_presence_scope_ends_only_its_authenticated_socket()
    {
        await using var factory = Server(new Clock()); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var a = DeviceIdentity.Ephemeral("A"); using var b = DeviceIdentity.Ephemeral("B");
        await using var ca = await Register(factory, a, ct.Token); await using var cb = await Register(factory, b, ct.Token);
        var invalid = new PresenceUpdate { RequestId = ProtocolRules.NewToken(), Enabled = true }; invalid.PeerIds.Add(a.Id);
        await ca.SendAsync("presence", invalid, ct.Token); Assert.Null(await ca.ReceiveAsync(ct.Token));
        await cb.SendAsync("invite", new Notice(), ct.Token); Assert.Equal("invitation", (await cb.ReceiveAsync(ct.Token))!.Kind);
    }

    [Fact]
    public async Task Windows_reports_do_not_authenticate_and_revocation_clears_local_visibility()
    {
        await using var factory = Server(new Clock()); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var a = DeviceIdentity.Ephemeral("A"); using var b = DeviceIdentity.Ephemeral("B");
        await using var cb = await Register(factory, b, ct.Token); await Update(cb, ct.Token, a.Id);
        var directory = Path.Combine(Path.GetTempPath(), "iconnect-presence", ProtocolRules.NewToken());
        using var trust = new TrustedPeerStore(directory, a.Id);
        trust.Add(new(b.Id, "B", Convert.ToBase64String(b.PublicKey)));
        await using var client = new ProofClient(a, await Register(factory, a, ct.Token), new Uri("https://localhost"), directory, _ => { }, trustStore: trust, sharePresence: true);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PresenceChanged += () => { if (client.AvailablePeers.Contains(b.Id)) observed.TrySetResult(); };
        var run = client.RunAsync(ct.Token);
        try
        {
            await observed.Task.WaitAsync(ct.Token); Assert.Contains(b.Id, client.AvailablePeers);
            Assert.Null(client.SessionAuthenticated);
            Assert.Throws<InvalidDataException>(() => client.CaptureFileRequester(b.Id));
            client.Revoke(b.Id); Assert.Empty(client.AvailablePeers);
            await Task.Delay(1_200, ct.Token);
            Assert.False(Assert.Single((await Update(cb, ct.Token, a.Id)).Peers).Available);
        }
        finally { ct.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task A_failed_presence_send_still_releases_connection_and_exclusive_trust_on_disposal()
    {
        using var identity = DeviceIdentity.Ephemeral("Failed transport fixture");
        var socket = System.Net.WebSockets.WebSocket.CreateFromStream(new FailedWriteStream(), false, null, Timeout.InfiniteTimeSpan);
        var connection = new SignalConnection(socket); var notice = new Notice { Message = identity.Id }; notice.Capabilities.Add(PresenceTracker.Capability);
        connection.ConfirmRegistration(notice, identity.Id);
        var directory = Path.Combine(Path.GetTempPath(), "iconnect-presence", ProtocolRules.NewToken());
        var client = new ProofClient(identity, connection, new Uri("https://localhost"), directory, _ => { }, sharePresence: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.RunAsync(deadline.Token));
            Assert.Contains("Fixture transport failure.", error.ToString());
        }
        finally { await client.DisposeAsync(); }
        Assert.Empty(client.AvailablePeers);
        using var reopened = new TrustedPeerStore(directory, identity.Id); Assert.Empty(reopened.All);
    }
}
