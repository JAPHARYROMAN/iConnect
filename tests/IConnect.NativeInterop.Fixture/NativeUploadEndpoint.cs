using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

internal sealed class NativeUploadEndpoint : IAsyncDisposable
{
    internal const int PayloadBytes = 2 * 1024 * 1024 + 17;
    private const string ExistingBytes = "Generated existing owner file; keep these bytes.";
    private readonly string profile, source, destination, expectedIncoming;
    private readonly TrustedPeerStore trust;
    private readonly StorageGrantStore grants;
    private readonly StorageGrant grant;
    private readonly UploadAuthority authority;
    private readonly string originalHash;
    private WindowsUploadSender sender;
    private FileEntry? target;
    private OutgoingCopy? completed;
    private int preparations;
    private bool lostAcknowledgement, revoked;
    public WindowsFileService Service { get; }

    public NativeUploadEndpoint(string directory, DeviceIdentity identity, Pairing pairing)
    {
        var transcript = ProtocolRules.PairingTranscript(pairing);
        if (!ProtocolRules.Verify(pairing.Host.PublicKey.ToByteArray(), transcript, pairing.HostApproval.ToByteArray()) ||
            !ProtocolRules.Verify(pairing.Guest.PublicKey.ToByteArray(), transcript, pairing.GuestApproval.ToByteArray()))
            throw new InvalidDataException("The fixture requires both actual pairing signatures.");
        var host = pairing.Host.PublicKey.Span.SequenceEqual(identity.PublicKey);
        var own = host ? pairing.Host : pairing.Guest;
        var remote = host ? pairing.Guest : pairing.Host;
        if (!own.PublicKey.Span.SequenceEqual(identity.PublicKey)) throw new InvalidDataException("Pairing does not include this endpoint.");
        profile = Path.Combine(directory, "upload-owner");
        trust = new(profile, identity.Id);
        var peerId = ProtocolRules.DeviceId(remote.PublicKey.ToByteArray());
        var epoch = ProtocolRules.NewToken();
        trust.Add(new(peerId, remote.Name, Convert.ToBase64String(remote.PublicKey.ToByteArray()), epoch));
        var binding = trust.OwnerBinding ?? throw new InvalidDataException("The fixture owner is unavailable.");
        authority = new(identity.Id, binding.Epoch, peerId, epoch);
        destination = Path.Combine(directory, "published-uploads"); Directory.CreateDirectory(destination);
        expectedIncoming = host ? "kutuma.bin" : "from-windows.bin";
        File.WriteAllText(Path.Combine(destination, expectedIncoming), ExistingBytes);
        var sources = Path.Combine(directory, "originals"); Directory.CreateDirectory(sources);
        source = Path.Combine(sources, host ? "from-windows.bin" : "kutuma.bin");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(PayloadBytes));
        originalHash = Digest(source);
        grants = new(profile, ownerBinding: binding);
        grant = grants.Add(peerId, destination, StoragePermission.Write | StoragePermission.Create, "Fixture receive-only inbox");
        Service = new(grants); Service.BindOwnerPolicy(binding, peerId, epoch);
        sender = NewSender();
    }

    private WindowsUploadSender NewSender() => new(profile, value => value == authority && trust.OwnerReady &&
        trust.TryGet(authority.Peer, out var peer) && peer.ApprovalEpoch == authority.Pairing, () => trust.OwnerReady);
    private static string Digest(string file) { using var input = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(input)); }

    public async Task SendAsync(PeerSession peer, Action<object> emit, CancellationToken ct)
    {
        if (completed is not null || target is not null || !peer.AcceptsPublishedUploadChecks)
            throw new InvalidDataException("Choose one new authenticated upload with published-copy checking.");
        var context = await peer.RequestFilesAsync(new() { Operation = FileOperation.UploadContext }, ct);
        if (context.Status != FileStatus.Ok || context.Entries.Count != 1) throw new InvalidDataException("Expected one owner-approved receiving target.");
        target = context.Entries.Single();
        var browsing = await peer.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, ct);
        if (target.Permissions != 12 || browsing.Status is not (FileStatus.Ok or FileStatus.AccessDenied) || browsing.Entries.Count != 0 || browsing.Content.Length != 0)
            throw new InvalidDataException("The fixture receiving inbox unexpectedly permits browsing.");
        var id = await sender.PrepareAsync(source, authority, ct);
        async Task<FileResponse> Request(FileRequest value, CancellationToken token)
        {
            if (value.Operation == FileOperation.PrepareUpload) preparations++;
            var response = await peer.RequestFilesAsync(value, token);
            if (value.Operation == FileOperation.UploadWrite && !lostAcknowledgement && response.Status == FileStatus.Ok)
            {
                if (response.Upload.Offset != StorageRules.MaxReadRange) throw new InvalidDataException("The actual first range was not saved.");
                lostAcknowledgement = true;
                throw new IOException("Generated loss of an accepted native range acknowledgement.");
            }
            return response;
        }
        await sender.SendAsync(id, target, Request, ct);
        var stopped = sender.All.Single();
        var accepted = await peer.RequestFilesAsync(new() { Operation = FileOperation.UploadStatus, TransferId = stopped.RemoteId, MutationEpoch = stopped.RemoteEpoch }, ct);
        if (!lostAcknowledgement || stopped.Phase != SendingPhase.Review || stopped.Offset != 0 || accepted.Status != FileStatus.Ok ||
            accepted.Upload.Id != stopped.RemoteId || accepted.Upload.Offset != StorageRules.MaxReadRange || preparations != 1)
            throw new InvalidDataException("Lost acknowledgement did not retain the original sender and receiver checkpoints.");
        emit(new { @event = "checkpoint", receipt = stopped.RemoteId, senderOffset = stopped.Offset, receiverOffset = accepted.Upload.Offset });
        await sender.DisposeAsync(); sender = NewSender();
        if (sender.All.Single().RemoteId != stopped.RemoteId) throw new InvalidDataException("Sender recreation changed the original receipt.");
        await sender.SendAsync(id, null, Request, ct);
        completed = sender.All.Single();
        if (completed.Phase != SendingPhase.Completed || completed.RemoteId != stopped.RemoteId || preparations != 1 ||
            completed.Size != PayloadBytes || Digest(source) != originalHash || completed.Hash != originalHash)
            throw new InvalidDataException("The production sender did not complete its retained private copy.");
        await sender.VerifyPublishedAsync(id, peer.RequestFilesAsync, ct);
        emit(new { @event = "sent", sha256 = originalHash, bytes = PayloadBytes, preparations, lostAcknowledgements = 1, resumedOriginalReceipt = true });
    }

    public void Revoke()
    {
        if (revoked || completed is null) throw new InvalidDataException("Finish sending before withdrawing the fixture inbox.");
        grants.Remove(grant.Id); revoked = true;
    }

    public async Task<object> CheckAsync(PeerSession peer, CancellationToken ct)
    {
        if (!revoked || completed is null || target is null) throw new InvalidDataException("Both fixture inboxes must be withdrawn first.");
        var descriptor = new UploadInfo { Id = MutationRules.NewId(), Name = "blocked-after-withdrawal.bin", Size = 0,
            Sha256 = ByteString.CopyFrom(SHA256.HashData([])), Collision = UploadCollision.UploadKeepBoth };
        var denied = await peer.RequestFilesAsync(new() { Operation = FileOperation.PrepareUpload, TransferId = descriptor.Id,
            DestinationId = target.Id, MutationEpoch = completed.RemoteEpoch, Upload = descriptor }, ct);
        if (denied.Status is not (FileStatus.AccessDenied or FileStatus.StaleReference) || denied.Upload is not null || denied.Entries.Count != 0 || denied.Content.Length != 0)
            throw new InvalidDataException("A withdrawn receiving target admitted a new upload or exposed contents.");
        var browse = await peer.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, ct);
        if (browse.Status is not (FileStatus.Ok or FileStatus.AccessDenied) || browse.Entries.Count != 0 || browse.Content.Length != 0)
            throw new InvalidDataException("Receive-only fixture authority exposed browsing.");
        await grants.Uploads.WhenIdleAsync().WaitAsync(ct);
        var incoming = grants.Uploads.All.Single();
        if (incoming.Phase != UploadPhase.UploadCompleted || incoming.Name != expectedIncoming || incoming.Size != PayloadBytes ||
            incoming.FinalName == expectedIncoming || File.ReadAllText(Path.Combine(destination, expectedIncoming)) != ExistingBytes ||
            Directory.GetFiles(destination).Length != 2 || Digest(source) != originalHash)
            throw new InvalidDataException("The exact completed copy, collision original or receipt inventory changed.");
        var saved = Path.Combine(destination, incoming.FinalName);
        if (new FileInfo(saved).Length != PayloadBytes) throw new InvalidDataException("Published file length differs.");
        var pair = peer.Rtc.SelectedPair();
        string Kind(string candidate)
        {
            var match = System.Text.RegularExpressions.Regex.Match(candidate, "(?:^| )typ (host|srflx|prflx)(?: |$)");
            return match.Success ? match.Groups[1].Value : throw new InvalidDataException("A non-direct candidate was selected.");
        }
        return new { @event = "verified", suite = "uploads", sentHash = originalHash, receivedHash = Digest(saved), bytes = PayloadBytes,
            preparations, lostAcknowledgements = 1, resumedOriginalReceipt = true, collisionOriginalKept = true, originalSourceKept = true,
            publishedCopyChecked = true, withdrawnInboxDenied = true, localType = Kind(pair.Local), remoteType = Kind(pair.Remote) };
    }

    public async ValueTask DisposeAsync()
    {
        await sender.DisposeAsync();
        grants.Uploads.PauseVerification(); await grants.Uploads.WhenIdleAsync();
        Service.Dispose(); trust.Dispose();
    }
}
