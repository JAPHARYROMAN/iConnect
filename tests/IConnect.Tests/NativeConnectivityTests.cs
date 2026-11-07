using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;

namespace IConnect.Tests;

public sealed class NativeConnectivityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Version_one_peers_require_readiness_and_use_only_compatible_file_operations(bool supportsReadiness)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        using var owner = DeviceIdentity.Ephemeral("Current client fixture");
        using var older = DeviceIdentity.Ephemeral("Older protocol fixture");
        var session = ProtocolRules.NewToken();
        var inbox = Path.Combine(Path.GetTempPath(), "iconnect-tests", session);
        using var current = new PeerSession(owner, older.PublicKey, session, true, inbox, _ => { }, loopbackOnly: true);
        using var legacy = new NativeRtc(loopbackOnly: true);
        var running = current.RunAsync(ct);
        async Task Bridge(NativeRtc source, NativeRtc target)
        {
            await foreach (var signal in source.Signals.ReadAllAsync(ct))
                if (!target.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid)) break;
        }
        async Task<Frame> Read(string expected)
        {
            var frame = ProtocolRules.Parse(await legacy.Messages.ReadAsync(ct));
            Assert.Equal(expected, frame.Kind);
            return frame;
        }
        void Send(string kind, IMessage payload) => legacy.Send(ProtocolRules.Pack(kind, payload).ToByteArray());
        var bridges = new[] { Bridge(current.Rtc, legacy), Bridge(legacy, current.Rtc) };
        try
        {
            current.Rtc.Offer();
            var hello = SessionHello.Parser.ParseFrom((await Read("hello")).Payload);
            Assert.Equal(owner.Id, hello.DeviceId);
            var nonce = RandomNumberGenerator.GetBytes(32);
            var oldHello = new SessionHello { DeviceId = older.Id, Nonce = ByteString.CopyFrom(nonce) };
            oldHello.Capabilities.Add(FileRpcPeer.Capability);
            if (supportsReadiness)
            {
                oldHello.Capabilities.Add("ready-v1");
                oldHello.Capabilities.Add("unknown-extension-v1");
                oldHello.Capabilities.Add(UploadRules.PublishedCheckCapability); // Incomplete advertisement lacks uploads-v1.
            }
            Send("hello", oldHello);
            if (!supportsReadiness)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => current.Authenticated.WaitAsync(ct));
                Assert.Contains("mutual session readiness", error.Message);
                Assert.False(current.AcceptsFolderBatches);
                Assert.False(current.AcceptsPublishedUploadChecks);
                Assert.False(Directory.Exists(inbox));
                return;
            }

            var transcript = ProtocolRules.SessionTranscript(session, owner.Id, older.Id,
                legacy.RemoteFingerprint, legacy.LocalFingerprint, hello.Nonce.ToByteArray(), nonce);
            var proof = SessionProof.Parser.ParseFrom((await Read("proof")).Payload);
            Assert.True(ProtocolRules.Verify(owner.PublicKey, transcript, proof.Signature.ToByteArray()));
            Send("proof", new SessionProof { Signature = ByteString.CopyFrom(older.Sign(transcript)) });
            Assert.Equal("ready-v1", Notice.Parser.ParseFrom((await Read("ready")).Payload).Message);
            Assert.False(current.Authenticated.IsCompleted);
            Send("ready", new Notice { Message = "ready-v1" });
            await current.Authenticated.WaitAsync(ct);
            Assert.False(current.AcceptsFolderBatches);
            Assert.False(current.AcceptsPublishedUploadChecks);
            foreach (var operation in new[] { FileOperation.UploadBatchContext, FileOperation.VerifyPublishedUpload, FileOperation.PrepareDownload })
                await Assert.ThrowsAsync<NotSupportedException>(() => current.RequestFilesAsync(new FileRequest { Operation = operation }, ct));

            var roots = current.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, ct);
            var request = FileRequest.Parser.ParseFrom((await Read("file-request")).Payload);
            Assert.Equal(FileOperation.ListRoots, request.Operation);
            Send("file-response", new FileResponse { RequestId = request.RequestId });
            Assert.Empty((await roots).Entries);
        }
        finally
        {
            deadline.Cancel();
            current.Dispose(); legacy.Dispose();
            foreach (var bridge in bridges)
                try { await bridge; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (IOException ex) when (ex.Message == "Direct connection ended.") { }
            try { await running; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException ex) when (ex.Message == "Direct connection ended." && current.Authenticated.IsCompletedSuccessfully) { }
            catch (InvalidDataException) when (!supportsReadiness && current.Authenticated.IsFaulted) { }
        }
    }

    [Fact]
    public async Task Real_native_peers_authenticate_and_verify_files_in_both_directions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var hostIdentity = DeviceIdentity.Ephemeral("Test Windows host");
        using var guestIdentity = DeviceIdentity.Ephemeral("Test peer");
        var directory = Path.Combine(Path.GetTempPath(), "iconnect-tests", ProtocolRules.NewToken());
        var hostInbox = Path.Combine(directory, "host");
        var guestInbox = Path.Combine(directory, "guest");
        var session = ProtocolRules.NewToken();
        using var host = new PeerSession(hostIdentity, guestIdentity.PublicKey, session, true, hostInbox, _ => { }, loopbackOnly: true);
        using var guest = new PeerSession(guestIdentity, hostIdentity.PublicKey, session, false, guestInbox, _ => { }, loopbackOnly: true);
        var hostRun = host.RunAsync(timeout.Token);
        var guestRun = guest.RunAsync(timeout.Token);
        async Task Bridge(PeerSession source, PeerSession target)
        {
            await foreach (var signal in source.Rtc.Signals.ReadAllAsync(timeout.Token))
            {
                if (signal.Kind == "candidate") target.Rtc.AddCandidate(signal.Body, signal.Mid);
                else target.Rtc.SetDescription(signal.Body, signal.Kind);
            }
        }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer();
            await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(timeout.Token);
            await host.SendTestFileAsync(timeout.Token);
            await guest.SendTestFileAsync(timeout.Token);
            foreach (var path in new[] { hostInbox, guestInbox })
            {
                var file = Assert.Single(Directory.GetFiles(path, "*.bin"));
                Assert.Equal(256 * 1024, new FileInfo(file).Length);
                Assert.Empty(Directory.GetFiles(path, "*.part"));
                Assert.Equal(32, SHA256.HashData(await File.ReadAllBytesAsync(file, timeout.Token)).Length);
            }
        }
        finally
        {
            timeout.Cancel();
            host.Dispose(); guest.Dispose();
            foreach (var task in bridges.Append(hostRun).Append(guestRun))
            {
                try { await task; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
