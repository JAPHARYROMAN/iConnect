using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace IConnect.Tests;

public sealed class NativeSessionFailureTests(ITestOutputHelper output)
{
    private const string CleanupError = "Generated storage cleanup failure.";
    private sealed class CleanupService(bool fail) : IFileService
    {
        public int Requests, Disposals;
        public Task<FileResponse> ExecuteAsync(string peer, FileRequest request, CancellationToken cancellation)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("An unauthenticated peer reached file storage."); }
        public void Dispose() { Interlocked.Increment(ref Disposals); if (fail) throw new IOException(CleanupError); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_readiness_remains_the_run_failure_when_storage_cleanup_also_fails(bool cleanupFails)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var host = DeviceIdentity.Ephemeral("Generated session owner");
        using var guest = DeviceIdentity.Ephemeral("Generated incompatible peer");
        var id = ProtocolRules.NewToken(); var inbox = Path.Combine(Path.GetTempPath(), "iconnect-session-failure", id);
        var service = new CleanupService(cleanupFails);
        using var peer = new PeerSession(host, guest.PublicKey, id, true, inbox, _ => { }, loopbackOnly: true, fileService: service);
        using var legacy = new NativeRtc(loopbackOnly: true);
        var run = peer.RunAsync(deadline.Token);
        async Task Bridge(NativeRtc from, NativeRtc to)
        {
            await foreach (var signal in from.Signals.ReadAllAsync(deadline.Token))
                if (!to.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid)) break;
        }
        var bridges = new[] { Bridge(peer.Rtc, legacy), Bridge(legacy, peer.Rtc) };
        try
        {
            peer.Rtc.Offer();
            Assert.Equal("hello", ProtocolRules.Parse(await legacy.Messages.ReadAsync(deadline.Token)).Kind);
            var hello = new SessionHello { DeviceId = guest.Id, Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) };
            hello.Capabilities.Add(FileRpcPeer.Capability);
            legacy.Send(ProtocolRules.Pack("hello", hello).ToByteArray());
            var authentication = await Assert.ThrowsAsync<InvalidDataException>(() => peer.Authenticated.WaitAsync(deadline.Token));
            Assert.Contains("mutual session readiness", authentication.Message);
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(deadline.Token));
            Assert.Same(authentication, failure);
            Assert.Equal(0, service.Requests); Assert.Equal(1, service.Disposals);
            Assert.False(Directory.Exists(inbox));
        }
        catch (Exception failure)
        {
            NativeFailureDiagnostics.Write(output, "missing readiness", failure,
                ("host open", peer.Rtc.Opened), ("legacy open", legacy.Opened),
                ("host authentication", peer.Authenticated), ("host run", run),
                ("host signals", bridges[0]), ("legacy signals", bridges[1]));
            throw;
        }
        finally
        {
            deadline.Cancel(); peer.Dispose(); legacy.Dispose();
            foreach (var task in bridges.Append(run))
                try { await task; } catch (Exception failure) when (failure is IOException or InvalidDataException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Storage_cleanup_failure_is_observable_when_the_message_loop_has_no_prior_failure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var host = DeviceIdentity.Ephemeral("Generated session owner");
        using var guest = DeviceIdentity.Ephemeral("Generated session guest");
        var id = ProtocolRules.NewToken(); var root = Path.Combine(Path.GetTempPath(), "iconnect-session-failure", id);
        var service = new CleanupService(true);
        using var first = new PeerSession(host, guest.PublicKey, id, true, Path.Combine(root, "host"), _ => { }, loopbackOnly: true, fileService: service);
        using var second = new PeerSession(guest, host.PublicKey, id, false, Path.Combine(root, "guest"), _ => { }, loopbackOnly: true);
        var loops = new[] { first.RunAsync(deadline.Token), second.RunAsync(deadline.Token) };
        async Task Bridge(PeerSession from, PeerSession to)
        {
            await foreach (var signal in from.Rtc.Signals.ReadAllAsync(deadline.Token))
                if (!to.Rtc.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid)) break;
        }
        var bridges = new[] { Bridge(first, second), Bridge(second, first) };
        try
        {
            first.Rtc.Offer(); await Task.WhenAll(first.Authenticated, second.Authenticated).WaitAsync(deadline.Token);
            first.Dispose();
            var failure = await Assert.ThrowsAsync<IOException>(() => loops[0].WaitAsync(deadline.Token));
            Assert.Equal(CleanupError, failure.Message); Assert.Equal(1, service.Disposals); Assert.Equal(0, service.Requests);
            Assert.False(Directory.Exists(root));
        }
        catch (Exception failure)
        {
            NativeFailureDiagnostics.Write(output, "storage cleanup", failure,
                ("host open", first.Rtc.Opened), ("guest open", second.Rtc.Opened),
                ("host authentication", first.Authenticated), ("guest authentication", second.Authenticated),
                ("host run", loops[0]), ("guest run", loops[1]),
                ("host signals", bridges[0]), ("guest signals", bridges[1]));
            throw;
        }
        finally
        {
            deadline.Cancel(); first.Dispose(); second.Dispose();
            foreach (var task in loops.Concat(bridges))
                try { await task; } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
    }
}
