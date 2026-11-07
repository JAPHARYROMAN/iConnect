using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using IConnect.AppModel;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Connection_activity_records_rejected_coordinator_handshake_without_claiming_direct_authentication()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").Configure(app => app.Run(context => { context.Response.StatusCode = 403; return Task.CompletedTask; })));
        factory.UseKestrel(0); using var http = factory.CreateClient();
        await using var model = new DesktopController(state, action => action()); model.SaveSettings(new("NEVER-LOG-DEVICE", http.BaseAddress!.AbsoluteUri));
        await Assert.ThrowsAnyAsync<Exception>(model.ConnectAsync); await model.FlushActivity();
        Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        Assert.Contains(model.Activity, x => x.Description == "Coordinator connection · requested"); Assert.Contains(model.Activity, x => x.Description == "Coordinator connection · failed");
        Assert.DoesNotContain(model.Activity, x => x.Description.StartsWith("Direct device connection"));
        var text = File.ReadAllText(Path.Combine(state, "activity.json")); Assert.DoesNotContain("NEVER-LOG", text); Assert.DoesNotContain(http.BaseAddress.Authority, text);
    }

    [Fact]
    public async Task Connection_activity_owner_cancel_during_handshake_keeps_one_canceled_outcome_and_no_authentication()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").Configure(app => app.Run(async context => { arrived.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); })));
        factory.UseKestrel(0); using var http = factory.CreateClient(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var model = new DesktopController(state, action => action()); model.SaveSettings(new("Cancellation fixture", http.BaseAddress!.AbsoluteUri));
        var connecting = model.ConnectAsync(); await arrived.Task.WaitAsync(deadline.Token); await model.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting); await model.FlushActivity();
        Assert.Single(model.Activity, x => x.Description == "Coordinator connection · canceled");
        Assert.DoesNotContain(model.Activity, x => x.Description is "Coordinator connection · failed" or "Coordinator connection · completed" || x.Description.StartsWith("Direct device connection"));
        Assert.False(model.Connected); Assert.False(model.Connecting); Assert.False(model.PeerAuthenticated);
    }

    [Fact]
    public Task Connection_activity_rejects_a_real_native_channel_with_an_unpinned_device_identity_before_ready() => RejectedNativeIdentity(null, null);

    [Fact]
    public async Task Signed_offer_rejected_by_native_transport_keeps_coordinator_and_rejects_replay()
    {
        using var peer = DeviceIdentity.Ephemeral("Native rejection fixture");
        SeedOwnerTrust(peer);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var model = new DesktopController(state, action => action());
        model.SaveSettings(new("Desktop fixture", http.BaseAddress!.AbsoluteUri));
        await model.ConnectAsync();
        await using var signaling = await SignalConnection.ConnectAsync(http.BaseAddress, peer, deadline.Token);
        // This has a valid signed envelope and fingerprint but no WebRTC media section or ICE credentials.
        var offer = new Signal
        {
            SessionId = ProtocolRules.NewToken(), FromDevice = peer.Id, ToDevice = model.DeviceId, Kind = "offer",
            Body = "a=fingerprint:sha-256 " + string.Join(':', Enumerable.Repeat("ab", 32)) + "\r\n",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))
        };
        offer.Signature = ByteString.CopyFrom(peer.Sign(ProtocolRules.SignalTranscript(offer)));
        await signaling.SendAsync("signal", offer, deadline.Token);
        while (!model.Activity.Any(x => x.Description == "Direct device connection · failed"))
        {
            Assert.True(model.Connected, model.Diagnostic);
            await model.FlushActivity();
            await Task.Delay(15, deadline.Token);
        }
        Assert.True(model.Connected);
        Assert.False(model.PeerAuthenticated);
        Assert.False(model.PeerConnecting);
        var invitation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.InvitationCreated += uri => invitation.TrySetResult(uri);
        await model.CreateInvitationAsync();
        Assert.StartsWith("iconnect://pair", await invitation.Task.WaitAsync(deadline.Token));
        await signaling.SendAsync("signal", offer, deadline.Token);
        while (model.Connected) await Task.Delay(15, deadline.Token);
        Assert.False(model.PeerAuthenticated);
    }

    [Theory]
    [InlineData("candidate", "replay")]
    [InlineData("answer", "replay")]
    [InlineData("candidate", "unknown")]
    [InlineData("answer", "unknown")]
    public Task Late_signed_messages_for_a_closed_native_session_keep_the_coordinator_and_still_reject_bad_context(string kind, string invalid) => RejectedNativeIdentity(kind, invalid);

    private async Task RejectedNativeIdentity(string? lateKind, string? invalid)
    {
        using var advertised = DeviceIdentity.Ephemeral("NEVER-LOG-ADVERTISED-NAME"); using var impostor = DeviceIdentity.Ephemeral("Different native identity"); SeedOwnerTrust(advertised);
        var key = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        using var owner = DeviceIdentity.OpenWindows(key, "Desktop fixture");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development")); factory.UseKestrel(0); using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var model = new DesktopController(state, action => action()); model.SaveSettings(new("Desktop fixture", http.BaseAddress!.AbsoluteUri)); await model.ConnectAsync();
        await using var signaling = await SignalConnection.ConnectAsync(http.BaseAddress, advertised, deadline.Token);
        await model.ConnectDeviceAsync(advertised.Id);
        Signal? offer = null;
        while (offer is null) { var frame = await signaling.ReceiveAsync(deadline.Token); Assert.NotNull(frame); if (frame.Kind == "signal") { var candidate = Signal.Parser.ParseFrom(frame.Payload); if (candidate.Kind == "offer") offer = candidate; } }
        // Signaling uses the pinned key; the actual native channel presents a different device key.
        using var remote = new PeerSession(impostor, owner.PublicKey, offer.SessionId, false, Path.Combine(directory, "wrong-key-inbox"), _ => { });
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlyCandidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivingSession = remote;
        PeerSession? successor = null; Task successorRun = Task.CompletedTask; Task successorSending = Task.CompletedTask;
        var remoteRun = remote.RunAsync(deadline.Token);
        async Task Forward(PeerSession source)
        {
            await foreach (var value in source.Rtc.Signals.ReadAllAsync(deadline.Token))
            {
                if (value.Kind == "answer" && source.Id == offer.SessionId) answer.TrySetResult(value.Body);
                var signal = new Signal { SessionId = source.Id, FromDevice = advertised.Id, ToDevice = owner.Id, Kind = value.Kind, Body = value.Body, Mid = value.Mid,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) };
                signal.Signature = ByteString.CopyFrom(advertised.Sign(ProtocolRules.SignalTranscript(signal))); await signaling.SendAsync("signal", signal, deadline.Token);
            }
        }
        var sending = Task.Run(() => Forward(remote));
        var receiving = Task.Run(async () =>
        {
            while (await signaling.ReceiveAsync(deadline.Token) is { } frame)
            {
                if (frame.Kind == "invitation") Volatile.Read(ref remoteMarker).TrySetResult();
                if (frame.Kind != "signal") continue;
                var value = Signal.Parser.ParseFrom(frame.Payload); var target = Volatile.Read(ref receivingSession);
                if (value.SessionId != target.Id || target.IsClosed) continue;
                if (value.Kind is "candidate" or "answer" && target.Rtc.TryApplyRemoteSignal(value.Kind, value.Body, value.Mid) &&
                    value.Kind == "candidate" && ReferenceEquals(target, remote))
                    earlyCandidate.TrySetResult();
            }
        });
        try
        {
            // Deliver an actual candidate before applying the already received offer.
            await earlyCandidate.Task.WaitAsync(deadline.Token);
            Assert.Empty(remote.Rtc.RemoteFingerprint);
            Assert.True(remote.Rtc.TryApplyRemoteSignal("offer", offer.Body, offer.Mid));
            await remote.Rtc.Opened.WaitAsync(deadline.Token);
            while (!model.Activity.Any(x => x.Description == "Direct device connection · failed")) { await model.FlushActivity(); await Task.Delay(15, deadline.Token); }
            Assert.True(model.Connected); Assert.False(model.PeerAuthenticated); Assert.False(model.PeerConnecting);
            Assert.Contains("Unexpected peer identity or challenge.", model.Diagnostic);
            Assert.Contains(model.Activity, x => x.Description == "Direct device connection · requested" && x.PeerId == advertised.Id);
            Assert.DoesNotContain(model.Activity, x => x.Description == "Direct device connection · completed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.BrowseAsync());
            Assert.DoesNotContain("NEVER-LOG", File.ReadAllText(Path.Combine(state, "activity.json")));
            if (lateKind is not null)
            {
                remote.Dispose();
                var late = new Signal { SessionId = offer.SessionId, FromDevice = advertised.Id, ToDevice = owner.Id, Kind = lateKind, Mid = "0",
                    Body = lateKind == "answer" ? await answer.Task.WaitAsync(deadline.Token) : "candidate:fixture 1 UDP 123 192.0.2.1 9999 typ host",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) };
                late.Signature = ByteString.CopyFrom(advertised.Sign(ProtocolRules.SignalTranscript(late)));
                async Task Barrier(Signal packet)
                {
                    var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref remoteMarker, marker);
                    await signaling.SendAsync("signal", packet, deadline.Token);
                    // This invitation is processed after forwarding the packet to the owner.
                    await signaling.SendAsync("invite", new Notice(), deadline.Token); await marker.Task.WaitAsync(deadline.Token);
                    var invitation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Created(string value) => invitation.TrySetResult(value);
                    model.InvitationCreated += Created;
                    try { await model.CreateInvitationAsync(); Assert.StartsWith("iconnect://pair", await invitation.Task.WaitAsync(deadline.Token)); }
                    finally { model.InvitationCreated -= Created; }
                }
                await Barrier(late);
                Assert.True(model.Connected); Assert.False(model.PeerAuthenticated); Assert.False(model.PeerConnecting);
                successor = new PeerSession(advertised, owner.PublicKey, ProtocolRules.NewToken(), true, Path.Combine(directory, "successor-inbox"), _ => { });
                Volatile.Write(ref receivingSession, successor); successorRun = successor.RunAsync(deadline.Token); successorSending = Task.Run(() => Forward(successor)); successor.Rtc.Offer();
                await successor.Authenticated.WaitAsync(deadline.Token); while (!model.PeerAuthenticated) await Task.Delay(15, deadline.Token);
                var during = late.Clone(); during.Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)); during.Signature = ByteString.CopyFrom(advertised.Sign(ProtocolRules.SignalTranscript(during)));
                await Barrier(during); Assert.True(model.PeerAuthenticated);
                Assert.Empty((await successor.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, deadline.Token)).Entries);
                var forged = during.Clone(); forged.Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
                var damaged = forged.Signature.ToByteArray(); damaged[^1] ^= 1; forged.Signature = ByteString.CopyFrom(damaged);
                var clientField = typeof(DesktopController).GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var handle = typeof(ProofClient).GetMethod("HandleSignalAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var rejected = Assert.Throws<System.Reflection.TargetInvocationException>(() => handle.Invoke(clientField.GetValue(model), [forged, deadline.Token]));
                Assert.IsType<InvalidDataException>(rejected.InnerException);
                if (invalid == "unknown") { late.SessionId = ProtocolRules.NewToken(); late.Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)); late.Signature = ByteString.CopyFrom(advertised.Sign(ProtocolRules.SignalTranscript(late))); }
                await signaling.SendAsync("signal", late, deadline.Token);
                while (model.Connected) await Task.Delay(15, deadline.Token);
                Assert.False(model.PeerAuthenticated);
            }
        }
        finally
        {
            deadline.Cancel(); remote.Dispose(); successor?.Dispose();
            foreach (var work in new[] { remoteRun, sending, receiving, successorRun, successorSending }) try { await work; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }

    private sealed class ChangedReceiptService(WindowsFileService inner) : IFileService
    {
        internal bool OmitMoveReceipt { get; set; }
        internal bool RefuseChangeHistory { get; set; }
        internal int HistoryRefusals { get; private set; }
        public async Task<FileResponse> ExecuteAsync(string peer, FileRequest request, CancellationToken token)
        {
            if (RefuseChangeHistory && request.Operation == FileOperation.ListMutations) { HistoryRefusals++; return new() { RequestId = request.RequestId, Status = FileStatus.AccessDenied, Message = "Generated history-refresh refusal" }; }
            var result = await inner.ExecuteAsync(peer, request, token); if (OmitMoveReceipt && request.Operation == FileOperation.Move) result.Mutation = null; return result;
        }
        public void Dispose() => inner.Dispose();
    }
}
