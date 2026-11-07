using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

namespace IConnect.Connectivity;

public sealed class PeerSession : IDisposable
{
    private readonly DeviceIdentity identity;
    private readonly byte[] remoteKey;
    private readonly bool initiator;
    private readonly byte[] nonce = RandomNumberGenerator.GetBytes(32);
    private readonly TaskCompletionSource authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim transferLock = new(1);
    private readonly string inbox;
    private readonly Action<string> report;
    private byte[]? remoteNonce;
    private bool proofSent;
    private bool proofAccepted;
    private bool readySent;
    private bool remoteReady;
    private const string ReadyCapability = "ready-v1";
    private TestFileReceiver? receiver;
    private FileStream? receiveFile;
    private string? receiveId;
    private string receiveIdentity = "";
    private long receiveSize;
    private string? sendId;
    private TaskCompletionSource<long>? acknowledgement;
    private TaskCompletionSource<TransferResult>? result;
    private int disposed;
    private int receivedFiles;
    private readonly FileRpcPeer fileRpc;
    private readonly FileTrafficPolicy? traffic;
    private readonly bool createdFolderReferences;
    private readonly bool publishedUploadChecks;
    private readonly bool folderBatchReception;
    private readonly HashSet<string> remoteCapabilities = [];
    public NativeRtc Rtc { get; }
    public string Id { get; }
    public string RemoteId { get; }
    public Task Authenticated => authenticated.Task;
    public bool IsInitiator => initiator;
    public bool IsClosed => Volatile.Read(ref disposed) != 0;
    private bool CanRequestFiles => !IsClosed && authenticated.Task.IsCompletedSuccessfully && remoteCapabilities.Contains(FileRpcPeer.Capability);
    public bool AcceptsFolderBatches => CanRequestFiles && remoteCapabilities.Contains(UploadBatchRules.Capability);
    public bool AcceptsPublishedBatchChecks => AcceptsFolderBatches && remoteCapabilities.Contains(UploadBatchRules.PublishedCheckCapability);
    public bool AcceptsPublishedUploadChecks => CanRequestFiles && remoteCapabilities.Contains(UploadRules.Capability) &&
        remoteCapabilities.Contains(UploadRules.PublishedCheckCapability);

    public PeerSession(DeviceIdentity identity, byte[] remoteKey, string sessionId, bool initiator,
        string inbox, Action<string> report, string? stun = null, bool loopbackOnly = false, IFileService? fileService = null, FileTrafficPolicy? traffic = null)
    {
        this.identity = identity;
        this.remoteKey = remoteKey;
        this.initiator = initiator;
        this.inbox = inbox;
        this.report = report;
        Id = sessionId;
        RemoteId = ProtocolRules.DeviceId(remoteKey);
        Rtc = new NativeRtc(stun, loopbackOnly);
        this.traffic = traffic;
        fileRpc = new FileRpcPeer(RemoteId, frame => Rtc.Send(frame.ToByteArray()), fileService, traffic);
        createdFolderReferences = fileService is WindowsFileService windowsFiles && windowsFiles.SupportsCreatedDirectoryReferences;
        publishedUploadChecks = fileService is WindowsFileService { SupportsPublishedUploadChecks: true };
        folderBatchReception = fileService is WindowsFileService host && host.SupportsFolderBatchReception(RemoteId);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var failed = false;
        try
        {
            await Rtc.Opened.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var hello = new SessionHello { DeviceId = identity.Id, Nonce = ByteString.CopyFrom(nonce) };
            hello.Capabilities.Add(FileRpcPeer.Capability);
            hello.Capabilities.Add(StorageRules.DownloadCapability);
            hello.Capabilities.Add(MutationRules.Capability);
            if (createdFolderReferences) hello.Capabilities.Add(MutationRules.CreatedFoldersCapability);
            hello.Capabilities.Add(RecoveryRules.Capability);
            hello.Capabilities.Add(UploadRules.Capability);
            if (publishedUploadChecks) hello.Capabilities.Add(UploadRules.PublishedCheckCapability);
            if (folderBatchReception) hello.Capabilities.Add(UploadBatchRules.Capability);
            if (folderBatchReception) hello.Capabilities.Add(UploadBatchRules.PublishedCheckCapability);
            hello.Capabilities.Add(ReadyCapability);
            Send("hello", hello);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (await Rtc.Messages.WaitToReadAsync(ct).AsTask().WaitAsync(
                authenticated.Task.IsCompletedSuccessfully ? Timeout.InfiniteTimeSpan :
                deadline > DateTimeOffset.UtcNow ? deadline - DateTimeOffset.UtcNow : TimeSpan.Zero, ct))
            {
                while (Rtc.Messages.TryRead(out var bytes)) await Handle(ProtocolRules.Parse(bytes), ct);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            authenticated.TrySetException(ex);
            acknowledgement?.TrySetException(ex);
            result?.TrySetException(ex);
            throw;
        }
        finally
        {
            Dispose();
            try { await fileRpc.Completion; }
            // The worker owns service cleanup. Its failure must not replace the
            // authentication, protocol or cancellation failure already in flight.
            catch (Exception) when (failed) { }
        }
    }

    private byte[] Transcript()
    {
        if (remoteNonce is null || Rtc.LocalFingerprint.Length == 0 || Rtc.RemoteFingerprint.Length == 0)
            throw new InvalidDataException("Incomplete session authentication context.");
        return ProtocolRules.SessionTranscript(Id,
            initiator ? identity.Id : RemoteId, initiator ? RemoteId : identity.Id,
            initiator ? Rtc.LocalFingerprint : Rtc.RemoteFingerprint,
            initiator ? Rtc.RemoteFingerprint : Rtc.LocalFingerprint,
            initiator ? nonce : remoteNonce, initiator ? remoteNonce : nonce);
    }

    private async Task Handle(Frame frame, CancellationToken cancellation)
    {
        if (frame.Kind == "hello")
        {
            var hello = SessionHello.Parser.ParseFrom(frame.Payload);
            if (remoteNonce is not null || hello.Nonce.Length != 32 || hello.DeviceId != RemoteId)
                throw new InvalidDataException("Unexpected peer identity or challenge.");
            remoteNonce = hello.Nonce.ToByteArray();
            if (hello.Capabilities.Count > 16 || hello.Capabilities.Any(x => x.Length is < 1 or > 64))
                throw new InvalidDataException("Invalid peer capabilities.");
            remoteCapabilities.UnionWith(hello.Capabilities);
            if (!remoteCapabilities.Contains(ReadyCapability)) throw new InvalidDataException("Update the peer app to support mutual session readiness.");
            Send("proof", new SessionProof { Signature = ByteString.CopyFrom(identity.Sign(Transcript())) });
            proofSent = true;
            return;
        }
        if (frame.Kind == "proof")
        {
            var proof = SessionProof.Parser.ParseFrom(frame.Payload);
            if (!proofSent || proofAccepted || !ProtocolRules.Verify(remoteKey, Transcript(), proof.Signature.ToByteArray()))
                throw new InvalidDataException("Device authentication failed.");
            _ = Rtc.SelectedPair();
            proofAccepted = true;
            Send("ready", new Notice { Message = ReadyCapability });
            readySent = true;
            PublishAuthenticated();
            return;
        }
        if (frame.Kind == "ready")
        {
            if (!proofAccepted || remoteReady || Notice.Parser.ParseFrom(frame.Payload).Message != ReadyCapability)
                throw new InvalidDataException("Unexpected peer readiness acknowledgement.");
            remoteReady = true;
            PublishAuthenticated();
            return;
        }
        if (!authenticated.Task.IsCompletedSuccessfully) throw new InvalidDataException("Authenticate before transferring.");
        if (fileRpc.Handle(frame)) return;
        switch (frame.Kind)
        {
            case "transfer-start":
            {
                traffic?.RequireContent();
                if (receiver is not null || ++receivedFiles > 4)
                    throw new InvalidDataException("The session's test-file receive limit was reached.");
                var start = TransferStart.Parser.ParseFrom(frame.Payload);
                // Validate before creating any destination. Names never become filesystem paths.
                using (var validation = new TestFileReceiver(start, Stream.Null)) { }
                Directory.CreateDirectory(inbox);
                receiveFile = new FileStream(Path.Combine(inbox, start.TransferId + ".part"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                receiveIdentity = WindowsReceivedFileSecurity.MarkPrivateStaging(receiveFile.SafeFileHandle); receiveSize = start.Size;
                receiver = new TestFileReceiver(start, receiveFile);
                receiveId = start.TransferId;
                Send("transfer-ack", new TransferAck { TransferId = start.TransferId, Offset = 0 });
                report("Receiving synthetic test file...");
                break;
            }
            case "transfer-chunk":
            {
                if (receiver is null) throw new InvalidDataException("No active transfer.");
                var chunk = TransferChunk.Parser.ParseFrom(frame.Payload);
                if (traffic is not null) await traffic.PaceAsync(chunk.Data.Length, false, cancellation);
                var offset = receiver.Append(chunk);
                Send("transfer-ack", new TransferAck { TransferId = chunk.TransferId, Offset = offset });
                break;
            }
            case "transfer-finish":
            {
                if (receiver is null || receiveFile is null || receiveId is null) throw new InvalidDataException("No active transfer.");
                var finish = TransferFinish.Parser.ParseFrom(frame.Payload);
                var digest = receiver.Finish(finish.TransferId);
                receiveFile.Flush(true);
                receiveFile.Dispose();
                receiveFile = null;
                await WindowsReceivedFileSecurity.FinishSyntheticAsync(inbox, receiveId, receiveIdentity, receiveSize, Convert.ToHexStringLower(digest), cancellation);
                receiver.Dispose();
                receiver = null;
                receiveId = null;
                Send("transfer-result", new TransferResult { TransferId = finish.TransferId, Sha256 = ByteString.CopyFrom(digest) });
                report($"Received and verified SHA-256: {Convert.ToHexStringLower(digest)}");
                break;
            }
            case "transfer-ack":
            {
                var ack = TransferAck.Parser.ParseFrom(frame.Payload);
                if (ack.TransferId != sendId || acknowledgement is null || !acknowledgement.TrySetResult(ack.Offset))
                    throw new InvalidDataException("Unexpected transfer acknowledgement.");
                break;
            }
            case "transfer-result":
            {
                var completed = TransferResult.Parser.ParseFrom(frame.Payload);
                if (completed.TransferId != sendId || result is null || !result.TrySetResult(completed))
                    throw new InvalidDataException("Unexpected transfer result.");
                break;
            }
            default: throw new InvalidDataException("Unsupported peer operation.");
        }
    }

    private void PublishAuthenticated()
    {
        if (readySent && remoteReady && authenticated.TrySetResult())
            report("Authenticated direct connection established (no relay).");
    }

    public async Task SendTestFileAsync(CancellationToken ct)
    {
        await Authenticated.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await transferLock.WaitAsync(ct);
        try
        {
            traffic?.RequireContent();
            var data = RandomNumberGenerator.GetBytes(256 * 1024);
            var digest = SHA256.HashData(data);
            sendId = ProtocolRules.NewToken();
            result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAndAck("transfer-start", new TransferStart
            {
                TransferId = sendId, Name = "iconnect-test.bin", Size = data.Length, Sha256 = ByteString.CopyFrom(digest)
            }, 0, ct);
            for (var offset = 0; offset < data.Length; offset += ProtocolRules.ChunkBytes)
            {
                var length = Math.Min(ProtocolRules.ChunkBytes, data.Length - offset);
                if (traffic is not null) await traffic.PaceAsync(length, true, ct);
                await SendAndAck("transfer-chunk", new TransferChunk
                {
                    TransferId = sendId, Offset = offset, Data = ByteString.CopyFrom(data, offset, length)
                }, offset + length, ct);
            }
            Send("transfer-finish", new TransferFinish { TransferId = sendId });
            var completed = await result.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (!CryptographicOperations.FixedTimeEquals(digest, completed.Sha256.Span))
                throw new InvalidDataException("Destination did not verify the expected checksum.");
            report($"Sent 256 KiB; destination verified SHA-256: {Convert.ToHexStringLower(digest)}");
        }
        finally { sendId = null; result = null; acknowledgement = null; transferLock.Release(); }
    }

    public async Task<FileResponse> RequestFilesAsync(FileRequest request, CancellationToken ct = default)
    {
        await Authenticated.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (UploadBatchRules.IsRpc(request.Operation) && !remoteCapabilities.Contains(UploadBatchRules.Capability))
            throw new NotSupportedException("This peer does not support folder-batch receiving.");
        if (request.Operation == FileOperation.VerifyPublishedBatch && !AcceptsPublishedBatchChecks)
            throw new NotSupportedException("This peer does not support fresh published-batch checks.");
        if (UploadRules.IsRpc(request.Operation) && !remoteCapabilities.Contains(UploadRules.Capability))
            throw new NotSupportedException("This device does not accept incoming file copies. Update it or receive from the other device's interface.");
        if (request.Operation == FileOperation.VerifyPublishedUpload && !AcceptsPublishedUploadChecks)
            throw new NotSupportedException("This device does not support fresh published-copy checks.");
        if (!remoteCapabilities.Contains(FileRpcPeer.Capability))
            throw new NotSupportedException("Update the other device to use file browsing.");
        if (MutationRules.IsMutationRpc(request.Operation) && !remoteCapabilities.Contains(MutationRules.Capability))
            throw new NotSupportedException("Update the other device to use recoverable file operations.");
        if (request.Operation == FileOperation.ResolveCreatedDirectory && !remoteCapabilities.Contains(MutationRules.CreatedFoldersCapability))
            throw new NotSupportedException("This device cannot recover created-folder references. Update it before resuming a folder copy.");
        if (RecoveryRules.IsRpc(request.Operation) && !remoteCapabilities.Contains(RecoveryRules.Capability))
            throw new NotSupportedException("This device does not offer recoverable deletion. Update it or manage its files locally.");
        if (request.Operation is >= FileOperation.PrepareDownload and <= FileOperation.ReleaseDownload &&
            !remoteCapabilities.Contains(StorageRules.DownloadCapability))
            throw new NotSupportedException("Update the other device to transfer real files.");
        return await fileRpc.RequestAsync(request, ct);
    }

    private async Task SendAndAck(string kind, IMessage message, long expected, CancellationToken ct)
    {
        acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(kind, message);
        if (await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(15), ct) != expected)
            throw new InvalidDataException("Destination progress does not match the sent data.");
    }

    private void Send(string kind, IMessage message) => Rtc.Send(ProtocolRules.Pack(kind, message).ToByteArray());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Rtc.Dispose();
        fileRpc.Dispose();
        receiver?.Dispose();
        receiveFile?.Dispose();
        authenticated.TrySetCanceled();
        acknowledgement?.TrySetCanceled();
        result?.TrySetCanceled();
        // Failed .part files are deliberately never exposed as completed files.
    }
}
