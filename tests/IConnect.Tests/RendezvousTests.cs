using System.Net.WebSockets;
using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IConnect.Tests;

public sealed class RendezvousTests
{
    private static async Task<SignalConnection> Register(WebApplicationFactory<Program> factory, DeviceIdentity identity, CancellationToken ct, bool signalStatus = false)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/signal"), ct);
        var connection = new SignalConnection(socket);
        var hello = await connection.ReceiveAsync(ct);
        Assert.Equal("challenge", hello!.Kind);
        var nonce = Challenge.Parser.ParseFrom(hello.Payload).Nonce.ToByteArray();
        await connection.SendAsync("register", new Registration
        {
            Identity = identity.Descriptor,
            SignalStatus = signalStatus,
            Signature = ByteString.CopyFrom(identity.Sign(ProtocolRules.RegistrationTranscript(nonce)))
        }, ct);
        Assert.Equal("registered", (await connection.ReceiveAsync(ct))!.Kind);
        return connection;
    }

    [Fact]
    public async Task Pairing_requires_both_local_approvals_then_signed_signalling_and_native_transfer_work()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var hostIdentity = DeviceIdentity.Ephemeral("Host fixture");
        using var guestIdentity = DeviceIdentity.Ephemeral("Guest fixture");
        var directory = Path.Combine(Path.GetTempPath(), "iconnect-tests", ProtocolRules.NewToken());
        var hostReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new ProofClient(hostIdentity, await Register(factory, hostIdentity, ct.Token),
            new Uri("http://localhost"), Path.Combine(directory, "host"), message =>
            { if (message.StartsWith("Authenticated direct")) hostReady.TrySetResult(); });
        await using var guest = new ProofClient(guestIdentity, await Register(factory, guestIdentity, ct.Token),
            new Uri("http://localhost"), Path.Combine(directory, "guest"), message =>
            { if (message.StartsWith("Authenticated direct")) guestReady.TrySetResult(); });
        var invitation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostPairing = new TaskCompletionSource<Pairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestPairing = new TaskCompletionSource<Pairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.InvitationCreated += value => invitation.TrySetResult(value);
        host.PairingRequested += value => hostPairing.TrySetResult(value);
        guest.PairingRequested += value => guestPairing.TrySetResult(value);
        host.Paired += _ => hostPaired.TrySetResult();
        guest.Paired += _ => guestPaired.TrySetResult();
        var hostLoop = host.RunAsync(ct.Token);
        var guestLoop = guest.RunAsync(ct.Token);
        try
        {
            await host.CreateInvitationAsync(ct.Token);
            await guest.JoinInvitationAsync(await invitation.Task.WaitAsync(ct.Token), ct.Token);
            var pairings = await Task.WhenAll(hostPairing.Task, guestPairing.Task).WaitAsync(ct.Token);
            Assert.Equal(ProtocolRules.VerificationCode(pairings[0]), ProtocolRules.VerificationCode(pairings[1]));
            Assert.Throws<CryptographicException>(() => PairingTrust.ValidateCompletion(pairings[0], pairings[0], false));
            await host.ApproveAsync(ProtocolRules.VerificationCode(pairings[0]), ct.Token);
            Assert.Empty(host.Peers);
            Assert.Empty(guest.Peers);
            await guest.ApproveAsync(ProtocolRules.VerificationCode(pairings[1]), ct.Token);
            await Task.WhenAll(hostPaired.Task, guestPaired.Task).WaitAsync(ct.Token);
            Assert.Single(host.Peers); Assert.Single(guest.Peers);
            await host.ConnectPeerAsync(guestIdentity.Id, ct.Token);
            await Task.WhenAll(hostReady.Task, guestReady.Task).WaitAsync(ct.Token);
            await host.SendTestFileAsync(ct.Token);
            await guest.SendTestFileAsync(ct.Token);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "guest", "inbox"), "*.bin"));
            host.Revoke(guestIdentity.Id);
            Assert.Empty(host.Peers);
            await Assert.ThrowsAsync<InvalidDataException>(() => host.ConnectPeerAsync(guestIdentity.Id, ct.Token));
            using var http = factory.CreateClient();
            Assert.Contains("\"fileRelay\":false", await http.GetStringAsync("/health", ct.Token));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/files", ct.Token)).StatusCode);
        }
        finally
        {
            ct.Cancel();
            foreach (var task in new[] { hostLoop, guestLoop })
                try { await task; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Offline_signal_status_is_opt_in_and_still_requires_an_authentic_signal()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = DeviceIdentity.Ephemeral("Status fixture"); using var offline = DeviceIdentity.Ephemeral("Offline fixture");
        await using var connection = await Register(factory, source, deadline.Token, signalStatus: true);
        var signal = new Signal { SessionId = ProtocolRules.NewToken(), FromDevice = source.Id, ToDevice = offline.Id, Kind = "candidate",
            Body = "candidate:1 1 udp 100 192.0.2.1 1234 typ host", Mid = "0", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        signal.Signature = ByteString.CopyFrom(source.Sign(ProtocolRules.SignalTranscript(signal)));
        await connection.SendAsync("signal", signal, deadline.Token);
        var frame = await connection.ReceiveAsync(deadline.Token); Assert.Equal("signal-unavailable", frame!.Kind);
        var status = SignalUnavailable.Parser.ParseFrom(frame.Payload); Assert.Equal(signal.SessionId, status.SessionId); Assert.Equal(offline.Id, status.PeerId);
        await connection.SendAsync("invite", new Notice(), deadline.Token); Assert.Equal("invitation", (await connection.ReceiveAsync(deadline.Token))!.Kind);
        signal.Body = "candidate:2 1 udp 100 192.0.2.2 1234 typ host"; // Deliberately retain the old signature.
        await connection.SendAsync("signal", signal, deadline.Token); Assert.Null(await connection.ReceiveAsync(deadline.Token));
    }
    [Fact]
    public async Task Legacy_registration_does_not_receive_an_unknown_coordinator_response()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var source = DeviceIdentity.Ephemeral("Legacy fixture"); using var offline = DeviceIdentity.Ephemeral("Offline fixture");
        await using var connection = await Register(factory, source, deadline.Token);
        var signal = new Signal { SessionId = ProtocolRules.NewToken(), FromDevice = source.Id, ToDevice = offline.Id, Kind = "candidate",
            Body = "candidate:1 1 udp 100 192.0.2.1 1234 typ host", Mid = "0", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        signal.Signature = ByteString.CopyFrom(source.Sign(ProtocolRules.SignalTranscript(signal)));
        await connection.SendAsync("signal", signal, deadline.Token); Assert.Null(await connection.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task An_invitation_can_only_be_claimed_once()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = DeviceIdentity.Ephemeral("Owner");
        using var alice = DeviceIdentity.Ephemeral("Alice");
        using var bob = DeviceIdentity.Ephemeral("Bob");
        await using var host = await Register(factory, owner, ct.Token);
        await using var first = await Register(factory, alice, ct.Token);
        await using var second = await Register(factory, bob, ct.Token);
        await host.SendAsync("invite", new Notice(), ct.Token);
        var token = Invitation.Parser.ParseFrom((await host.ReceiveAsync(ct.Token))!.Payload).Token;
        var claim = new Claim { Token = token, HostPublicKey = ByteString.CopyFrom(owner.PublicKey) };
        await first.SendAsync("claim", claim, ct.Token);
        Assert.Equal("pairing", (await first.ReceiveAsync(ct.Token))!.Kind);
        await second.SendAsync("claim", claim, ct.Token);
        Assert.Null(await second.ReceiveAsync(ct.Token));
    }
}
