using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace IConnect.AppModel;

[Flags]
public enum DesktopChange { Status = 1, Devices = 2, Files = 4, Grants = 8, History = 16, Settings = 32, Transfers = 64, LocalFiles = 128, Payload = 256, All = 511 }
public sealed record DesktopBatchPlan(string PeerId, int Generation, TransferManifest Manifest);
public sealed record TransferRecoveryEntry(string Area, StateRecoveryIssue Issue);
public sealed record DesktopBatchResultView(int Index, string Name, string Phase, string Message);

/// <summary>Single owner of the desktop connection, grants and local state; has no UI dependencies.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DesktopController : IAsyncDisposable
{
    private readonly string directory;
    private readonly Action<Action> dispatch;
    private readonly TrustedPeerStore trust;
    private readonly StorageGrantStore grants;
    private readonly LocalActivityStore activity;
    private readonly DesktopActivityWriter activityWriter;
    private readonly DownloadSourceStore downloadSources;
    private readonly WindowsDownloadManager downloads;
    private readonly WindowsBatchDownloads batches;
    private readonly WindowsReceivingPartRecovery receivingParts;
    private readonly WindowsUploadSender outgoing;
    private readonly WindowsBatchSources outgoingBatches;
    private readonly WindowsBatchSender outgoingBatchSender;
    private readonly OutgoingCopyResources outgoingResources = new();
    private readonly SemaphoreSlim policyGate = new(1);
    private DeviceIdentity identity;
    private ProofClient? client;
    private CancellationTokenSource? connectionLifetime;
    private Task? connectionRun;
    private int generation;
    private ExplorerRow[] catalog = [];
    private readonly Dictionary<string, ExplorerRow> selectedFiles = [];
    private readonly HashSet<string> pendingRevocations = [];
    private CancellationTokenSource? discovery;
    private int discoveryGeneration;
    private volatile bool disposed;
    public DesktopSettings Settings { get; private set; }
    public WindowsTransferNetworkSettings? TransferNetwork { get; }
    public FileTrafficPolicy NetworkTraffic { get; } = new();
    public string TransferNetworkIssue { get; } = "";
    public DesktopAvailability Availability { get; }
    public bool PeerConnecting => client?.SessionAuthenticated is { IsCompleted: false };
    public bool PairingActive => client?.PairingActive == true;
    public bool SignallingRefreshNeeded => client?.SignallingRefreshNeeded == true;
    public bool OwnerPolicyHealthy => trust.OwnerReady;
    public string OwnerPolicyNotice => trust.OwnerBinding?.Issue ?? "Windows identity binding is unavailable.";
    public string OwnerPolicyEpoch => trust.OwnerBinding?.Epoch ?? "";
    public string DeviceId => identity.Id;
    public bool Connected { get; private set; }
    public bool Connecting { get; private set; }
    public bool PeerAuthenticated { get; private set; }
    public string ActivePeerId { get; private set; } = "";
    public string Status { get; private set; } = "Not connected. Your shared locations are unavailable remotely.";
    public string Diagnostic { get; private set; } = "";
    public IReadOnlyCollection<ProofClient.TrustedPeer> Peers => trust.All;
    public string PeerAvailability(string peerId)
    {
        if (!trust.TryGet(peerId, out _)) return "Device is not paired.";
        if (!Connected) return "Coordinator disconnected · availability unknown.";
        if (!Settings.SharePresence) return "Availability reports are off. Connect to authenticate.";
        if (client?.PresenceSupported != true) return "Coordinator does not support availability reports. Connect to authenticate.";
        return client.AvailablePeers.Contains(peerId) ? "Coordinator reports available · connect to authenticate."
            : "Availability unknown · both devices must opt in and be connected. You can still try connecting.";
    }
    public IReadOnlyList<StorageGrant> Grants => grants.All;
    public IReadOnlyList<LocalRecoveryItem> LocalDeleted { get; private set; } = [];
    public bool LocalDeletedLoaded { get; private set; }
    public IReadOnlyList<DeletedInfo> RemoteDeleted { get; private set; } = [];
    public bool MoreRemoteDeleted { get; private set; }
    private int deletedOffset;
    public IReadOnlyList<ActivityEntry> Activity => activity.All;
    public int ActivityRetentionDays => activity.Days;
    public string ActivityIssue => activity.Issue;
    public bool CanResetActivity => activity.CanReset;
    public long DroppedActivityEvents => activityWriter.Dropped;
    public Task ConfigureActivity(int days) => activityWriter.Configure(days);
    public Task PreserveAndResetActivity() => activityWriter.Reset();
    internal Task FlushActivity() => activityWriter.Flush();
    public IReadOnlyList<DownloadJob> Downloads => downloads.Jobs.Where(x => x.BatchId.Length == 0).ToArray();
    public DownloadJob? DownloadSnapshot(string id) => downloads.FindJob(id);
    public IReadOnlyList<IncomingUpload> IncomingUploads => grants.Uploads.All;
    public IncomingUpload? IncomingSnapshot(string id) => grants.Uploads.FindUpload(id);
    public IReadOnlyList<OutgoingCopy> OutgoingCopies => outgoing.All;
    public OutgoingCopy? OutgoingSnapshot(string id) => outgoing.FindCopy(id);
    public IReadOnlyList<OutgoingBatchSource> PrivateOutgoingBatches => outgoingBatches.All;
    public OutgoingBatchSource? OutgoingBatchSourceSnapshot(string id) => outgoingBatches.FindSource(id);
    public IReadOnlyList<OutgoingBatchSend> OutgoingBatchSends => outgoingBatchSender.All;
    public long? OutgoingBatchCheckpoint(string id, int index) => outgoingBatchSender.ReadCheckpoint(id, index);
    public bool OutgoingBatchActive(string id) => outgoingBatchSender.Active(id);
    public bool OutgoingBatchPreparing(string id) => outgoingBatches.Active(id);
    public bool BatchReceiverSupported => PeerAuthenticated && client?.AcceptsFolderBatches == true;
    public bool PublishedBatchCheckSupported => PeerAuthenticated && client?.AcceptsPublishedBatchChecks == true;
    public bool PublishedBatchCheckCancellationSupported => PublishedBatchCheckSupported && client?.AcceptsPublishedBatchCheckCancellation == true;
    public bool PublishedUploadCheckSupported => PeerAuthenticated && client?.AcceptsPublishedUploadChecks == true;
    public bool IsCurrentBatchAuthority(UploadAuthority authority) => CurrentUploadAuthority(authority);
    public string BatchPeerName(UploadAuthority authority) => CurrentUploadAuthority(authority) ? RequirePeer(authority.Peer).Name : "Earlier pairing · " + authority.Peer[..8];
    public string IncomingBatchPeerName(string id) => Peers.SingleOrDefault(x => x.Id == id)?.Name ?? "Earlier device · " + id[..8];
    public void PauseBatchPreparation(string id) => outgoingBatches.CancelPreparation(id);
    public Task<UploadBatchManifest> InspectPrivateBatch(string id) => Task.Run(() => outgoingBatches.InspectManifest(id));
    public Task<IReadOnlyList<DesktopBatchResultView>> BatchResultPage(string id, int start) => Task.Run<IReadOnlyList<DesktopBatchResultView>>(() =>
    {
        var row = outgoingBatchSender.All.Single(x => x.Id == id);
        if (row.Purging || start < 0 || start >= row.Items) throw new InvalidOperationException("This result page is unavailable.");
        UploadBatchManifest? manifest = null;
        try { manifest = outgoingBatches.InspectManifest(row.Source); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { }
        return Enumerable.Range(start, Math.Min(50, row.Items - start)).Select(index =>
        {
            var result = index < row.Next ? outgoingBatchSender.ItemResult(id, index) : null;
            var name = manifest is not null ? BatchItemPath(manifest, index) : result?.Name is { Length: > 0 } saved ? saved : $"Item {index + 1}";
            var phase = result is not null ? (int)result.Phase : row.ReceiverOutcomes is { Length: > 0 } ? row.ReceiverOutcomes[index] : row.Attempted && index == row.Next ? 4 : 0;
            return new DesktopBatchResultView(index, name, phase switch { 1 => "Copied", 2 => "Skipped", 3 => "Issue", 4 => "Review receiver copy", _ => "Not processed" }, result?.Message ?? "");
        }).ToArray();
    });
    public static string BatchItemPath(UploadBatchManifest manifest, int index)
    {
        var names = new List<string>();
        for (var at = index; at >= 0; at = manifest.Items[at].Parent) names.Add(manifest.Items[at].Name);
        names.Reverse(); return string.Join(" / ", names);
    }
    public long PrivateOutgoingBytes => outgoingResources.ReservedBytes;
    public IReadOnlyList<FileEntry> ReceivingLocations { get; private set; } = [];
    public FileEntry? ReceivingLocation { get; private set; }
    public bool OutgoingActive(string id) => outgoing.Active(id);
    public int OrphanOutgoingCopies => outgoing.Orphans;
    private bool CurrentUploadAuthority(UploadAuthority scope) => !disposed && trust.OwnerReady && scope.Owner == identity.Id && scope.OwnerEpoch == OwnerPolicyEpoch && trust.TryGet(scope.Peer, out var peer) && peer.ApprovalEpoch == scope.Pairing;
    public Task<string> PrepareOutgoingFile(string peerId, string path)
    { _ = RequirePeer(peerId); var epoch = trust.EnsureUploadScope(peerId); return outgoing.PrepareAsync(path, new(identity.Id, OwnerPolicyEpoch, peerId, epoch)); }
    public Task<string> PrepareOutgoingMove(string peerId, string path)
    { _ = RequirePeer(peerId); var epoch = trust.EnsureUploadScope(peerId); return outgoing.PrepareMoveAsync(path, new(identity.Id, OwnerPolicyEpoch, peerId, epoch)); }
    public Task<string> PrepareOutgoingBatch(string peerId, IReadOnlyList<string> paths, UploadCollision collision)
    { _ = RequirePeer(peerId); var epoch = trust.EnsureUploadScope(peerId); return outgoingBatches.PrepareAsync(paths, new(identity.Id, OwnerPolicyEpoch, peerId, epoch), collision); }
    public string QueueOutgoingBatch(string source, bool anotherCopy = false) => outgoingBatchSender.Queue(source, anotherCopy);
    public BatchHistoryReview ReviewOutgoingBatchHistory(string id, BatchHistoryAction action) => outgoingBatchSender.ReviewHistory(id, action);
    public bool ConfirmOutgoingBatchHistory(BatchHistoryReview review) => ObserveRecovery(HistoryActivity(review.Action), () => outgoingBatchSender.ConfirmHistory(review));
    public bool ContinueOutgoingBatchPurge(string id) => ObserveRecovery(WindowsActivityKind.SenderHistoryPurge, () => outgoingBatchSender.ContinuePurge(id));
    public BatchSourceRemovalReview ReviewOutgoingBatchRemoval(string id) => outgoingBatchSender.ReviewSourceRemoval(id);
    public void RemoveOutgoingBatchReviewed(BatchSourceRemovalReview review) => ObserveRecovery(WindowsActivityKind.PrivateSourceCleanup, () => outgoingBatchSender.RemoveSourceReviewed(review));
    public IReadOnlyList<BatchSenderRecoveryArchive> OutgoingBatchRecoveryArchives => outgoingBatchSender.RecoveryArchives;
    public string OutgoingBatchRecoveryLocation(string id) => outgoingBatchSender.RecoveryArchiveLocation(id);
    public async Task<BatchSenderResetReview> ReviewBatchSenderReset()
    {
        outgoingBatchSender.PauseAll(); await outgoingBatchSender.WhenIdleAsync();
        return await Task.Run(outgoingBatchSender.ReviewStoreReset);
    }
    public Task<string> ConfirmBatchSenderReset(BatchSenderResetReview review) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SenderStoreReset, () => outgoingBatchSender.ConfirmStoreReset(review)));
    public Task<BatchSenderArchiveRemovalReview> ReviewBatchSenderArchiveRemoval(string id) => Task.Run(() => outgoingBatchSender.ReviewArchiveRemoval(id));
    public Task<bool> ConfirmBatchSenderArchiveRemoval(BatchSenderArchiveRemovalReview review) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SenderArchiveCleanup, () => outgoingBatchSender.ConfirmArchiveRemoval(review)));
    public Task<bool> ContinueBatchSenderArchiveRemoval(string id) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SenderArchiveCleanup, () => outgoingBatchSender.ContinueArchiveRemoval(id)));
    public IReadOnlyList<BatchSourceRecoveryArchive> PrivateBatchRecoveryArchives => outgoingBatches.RecoveryArchives;
    public string PrivateBatchRecoveryLocation(string id) => outgoingBatches.RecoveryArchiveLocation(id);
    public async Task<BatchSourceResetReview> ReviewPrivateBatchStoreReset()
    {
        outgoingBatchSender.PauseAll(); outgoingBatches.PauseAll();
        await outgoingBatchSender.WhenIdleAsync(); await outgoingBatches.WhenIdleAsync();
        return await Task.Run(outgoingBatchSender.ReviewSourceStoreReset);
    }
    public Task<string> ConfirmPrivateBatchStoreReset(BatchSourceResetReview review) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SourceStoreReset, () => outgoingBatchSender.ConfirmSourceStoreReset(review)));
    public Task<IReadOnlyList<BatchSourceArchiveEntry>> InspectPrivateBatchArchive(string id, int start) => Task.Run(() => outgoingBatches.InspectRecoveryArchive(id, start));
    public Task<BatchSourceArchiveRemovalReview> ReviewPrivateBatchArchiveRemoval(string id) => Task.Run(() => outgoingBatches.ReviewArchiveRemoval(id));
    public Task<bool> ConfirmPrivateBatchArchiveRemoval(BatchSourceArchiveRemovalReview review) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SourceArchiveCleanup, () => outgoingBatches.ConfirmArchiveRemoval(review)));
    public Task<bool> ContinuePrivateBatchArchiveRemoval(string id) => Task.Run(() => ObserveRecovery(WindowsActivityKind.SourceArchiveCleanup, () => outgoingBatches.ContinueArchiveRemoval(id)));
    public Task SendOutgoingBatch(string id, FileEntry? reviewedDestination = null)
    {
        var row = outgoingBatchSender.All.Single(x => x.Id == id);
        if (!PeerAuthenticated || ActivePeerId != row.Authority.Peer) throw new InvalidOperationException("Connect to the device for which this private batch was prepared.");
        if (!BatchReceiverSupported) throw new InvalidOperationException("The connected device has not enabled compatible folder/file batch receiving.");
        var destination = reviewedDestination ?? ReceivingLocation ?? Breadcrumbs.LastOrDefault(); var request = RequireClient().CaptureFileRequester(row.Authority.Peer);
        return outgoingBatchSender.SendAsync(id, destination, async (value, token) => await request(value, token), connectionLifetime!.Token);
    }
    public async Task StopOutgoingBatch(string id)
    {
        await outgoingBatchSender.PauseAndWaitAsync(id);
        var row = outgoingBatchSender.All.Single(x => x.Id == id);
        if (row.Header is not { Length: > 0 })
        {
            await outgoingBatchSender.StopAsync(id, (_, _) => throw new InvalidOperationException("An unstarted job must not contact a receiver.")); return;
        }
        if (!PeerAuthenticated || ActivePeerId != row.Authority.Peer) throw new InvalidOperationException("Reconnect to the original pairing to stop this retained receiver plan.");
        if (!BatchReceiverSupported) throw new InvalidOperationException("The connected device has not enabled compatible folder/file batch receiving.");
        var request = RequireClient().CaptureFileRequester(row.Authority.Peer);
        await outgoingBatchSender.StopAsync(id, async (value, token) => await request(value, token), connectionLifetime!.Token);
    }
    public void PauseOutgoingBatch(string id) => outgoingBatchSender.Pause(id);
    public bool OutgoingFolderMoveActive(string id) => outgoingBatchSender.FolderMoveActive(id);
    public Task<OutgoingFolderMoveReview> ReviewOutgoingFolderMoveAsync(string id, string selectedFolder) =>
        LocalFiles.ManageAsync(manager => outgoingBatchSender.ReviewFolderMove(id, selectedFolder, manager));
    public void CancelOutgoingFolderMoveReview(OutgoingFolderMoveReview review) => outgoingBatchSender.CancelFolderMoveReview(review);
    public async Task<MutationInfo> FinishOutgoingFolderMoveAsync(OutgoingFolderMoveReview review)
    {
        var row = outgoingBatchSender.All.Single(x => x.Id == review.BatchId);
        if (!PublishedBatchCheckSupported || ActivePeerId != row.Authority.Peer)
            throw new InvalidOperationException("Connect to the original paired device with saved-batch checking enabled.");
        var client = RequireClient(); var current = generation; var token = connectionLifetime!.Token;
        var request = client.CaptureFileRequester(row.Authority.Peer, out var sessionCurrent);
        try
        {
            return await LocalFiles.ManageWorkAsync(manager => outgoingBatchSender.FinishFolderMoveAsync(review, manager, request.Invoke,
                () => current == generation && ReferenceEquals(this.client, client) && sessionCurrent(), client.AcceptsPublishedBatchCheckCancellation, token));
        }
        finally { await LocalFiles.RefreshManagementAsync(); await LocalFiles.RefreshAfterManagementAsync(); }
    }
    public Task VerifyPublishedBatchAsync(string id)
    {
        var row = outgoingBatchSender.All.Single(x => x.Id == id);
        if (!PublishedBatchCheckSupported || ActivePeerId != row.Authority.Peer)
            throw new InvalidOperationException("Connect to the original paired device with saved-batch checking enabled.");
        var request = RequireClient().CaptureFileRequester(row.Authority.Peer);
        return outgoingBatchSender.VerifyPublishedAsync(id, request.Invoke, connectionLifetime!.Token,
            cancelReceiver: RequireClient().AcceptsPublishedBatchCheckCancellation);
    }
    public OutgoingBatchItemResult OutgoingBatchResult(string id, int index) => outgoingBatchSender.ItemResult(id, index);
    public async Task LoadReceivingLocations()
    {
        if (!PeerAuthenticated) throw new InvalidOperationException("Open an authenticated session first.");
        var peer = ActivePeerId; var current = generation; var request = RequireClient().CaptureFileRequester(peer);
        var result = await request(new() { Operation = FileOperation.UploadContext }, connectionLifetime!.Token);
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
        if (result.Entries.Any(x => !x.IsRoot || !x.IsDirectory || (x.Permissions & 12) != 12)) throw new InvalidDataException("The receiver returned invalid incoming locations.");
        if (current != generation || ActivePeerId != peer) return;
        ReceivingLocations = result.Entries.ToArray(); ReceivingLocation = null; Notify(DesktopChange.Transfers);
    }
    public void SelectReceivingLocation(string? id) { ReceivingLocation = id is null ? null : ReceivingLocations.Single(x => x.Id == id); Notify(DesktopChange.Transfers); }
    public Task SendOutgoing(string id)
    {
        var row = outgoing.All.Single(x => x.Id == id);
        if (!PeerAuthenticated || ActivePeerId != row.Authority.Peer) throw new InvalidOperationException("Connect to the device for which this copy was prepared.");
        var destination = ReceivingLocation ?? Breadcrumbs.LastOrDefault(); var request = RequireClient().CaptureFileRequester(row.Authority.Peer);
        return outgoing.SendAsync(id, destination, async (value, token) => await request(value, token), connectionLifetime!.Token);
    }
    public void PauseOutgoing(string id) => outgoing.Pause(id);
    public Task VerifyPublishedOutgoingAsync(string id)
    {
        var row = outgoing.All.Single(x => x.Id == id);
        if (!PublishedUploadCheckSupported || ActivePeerId != row.Authority.Peer)
            throw new InvalidOperationException("Connect to the original paired device with published-copy checking enabled.");
        var request = RequireClient().CaptureFileRequester(row.Authority.Peer);
        return outgoing.VerifyPublishedAsync(id, request.Invoke, connectionLifetime!.Token);
    }
    public Task<OutgoingMoveReview> ReviewOutgoingMoveAsync(string id) =>
        LocalFiles.ManageAsync(manager => outgoing.ReviewMove(id, manager));
    public async Task<MutationInfo> FinishOutgoingMoveAsync(OutgoingMoveReview review)
    {
        var row = outgoing.All.Single(x => x.Id == review.CopyId);
        if (!PublishedUploadCheckSupported || ActivePeerId != row.Authority.Peer)
            throw new InvalidOperationException("Connect to the original paired device with published-copy checking enabled.");
        var client = RequireClient();
        var current = generation;
        var token = connectionLifetime!.Token;
        var request = client.CaptureFileRequester(row.Authority.Peer, out var sessionCurrent);
        try
        {
            return await LocalFiles.ManageWorkAsync(manager => outgoing.FinishMoveAsync(review, manager, request.Invoke,
                () => current == generation && ReferenceEquals(this.client, client) && sessionCurrent(), token));
        }
        finally { await LocalFiles.RefreshManagementAsync(); await LocalFiles.RefreshAfterManagementAsync(); }
    }
    public Task RenameOutgoing(string id, string name) => Task.Run(() => outgoing.Rename(id, name));
    public Task AnotherOutgoing(string id) => Task.Run(() => outgoing.AnotherCopy(id));
    public Task RemoveOutgoing(string id) => ObserveRecoveryAsync(WindowsActivityKind.PrivateSourceCleanup, () => outgoing.RemoveAsync(id));
    public Task RemoveOrphanOutgoing() => Task.Run(() => ObserveRecovery(WindowsActivityKind.PrivateSourceCleanup, outgoing.RemoveOrphans));
    public Task DiscardIncomingPartial(string id) => Task.Run(async () => { await ObserveRecoveryAsync(WindowsActivityKind.IncomingPrivateCleanup, () => grants.Uploads.DiscardOwnedPartialAsync(id)); dispatch(() => Notify(DesktopChange.Transfers)); });
    public ReceivingPartFolder? ReceivingPartFolder => receivingParts.Selected;
    public ReceivingPartRemovalProgress? ReceivingPartCleanup => receivingParts.Progress;
    public async Task<ReceivingPartFolder> SelectReceivingPartFolder(string folder)
    {
        await PauseAvailabilityAsync();
        foreach (var row in batches.Jobs) batches.Pause(row.Id);
        foreach (var row in downloads.Jobs) downloads.Pause(row.Id);
        await Task.WhenAll(batches.Jobs.Select(x => batches.WaitAsync(x.Id)).Concat(downloads.Jobs.Select(x => downloads.WaitAsync(x.Id))));
        try { return await Task.Run(() => receivingParts.Select(folder)); }
        finally { Notify(DesktopChange.Transfers); }
    }
    public Task<IReadOnlyList<ReceivingPartEntry>> InspectReceivingParts(string folder, int start) => Task.Run(() => receivingParts.Inspect(folder, start));
    public Task<ReceivingPartRemovalReview> ReviewReceivingPartRemoval(string folder) => Task.Run(() => receivingParts.ReviewRemoval(folder));
    public async Task<bool> ConfirmReceivingPartRemoval(ReceivingPartRemovalReview review, Func<bool> continuing)
    { try { return await Task.Run(() => ObserveRecovery(WindowsActivityKind.ReceivingPartCleanup, () => receivingParts.ConfirmRemoval(review, continuing))); } finally { Notify(DesktopChange.Transfers); } }
    public async Task<bool> ContinueReceivingPartRemoval(Func<bool> continuing)
    { try { return await Task.Run(() => ObserveRecovery(WindowsActivityKind.ReceivingPartCleanup, () => receivingParts.ContinueRemoval(continuing))); } finally { Notify(DesktopChange.Transfers); } }
    public IReadOnlyList<IncomingBatchPlan> IncomingBatchPlans => grants.BatchPlans.All;
    public Task<IncomingBatchRetirementReview> ReviewIncomingBatchRetirement(string id) => Task.Run(() => grants.BatchPlans.ReviewRetirement(id));
    public async Task<bool> ConfirmIncomingBatchRetirement(IncomingBatchRetirementReview review)
    { var done = await Task.Run(() => ObserveRecovery(WindowsActivityKind.ReceiverHistoryRetirement, () => grants.BatchPlans.ConfirmRetirement(review))); Notify(DesktopChange.Transfers); return done; }
    public async Task<bool> ContinueIncomingBatchRetirement(string id)
    { var done = await Task.Run(() => ObserveRecovery(WindowsActivityKind.ReceiverHistoryRetirement, () => grants.BatchPlans.ContinueRetirement(id))); Notify(DesktopChange.Transfers); return done; }
    public async Task ResetClosedBatchHistory()
    { await Task.Run(() => ObserveRecovery(WindowsActivityKind.BatchHistoryReset, grants.BatchPlans.ResetClosedHistory)); Notify(DesktopChange.Transfers); }
    public async Task ArchiveIncomingBatchPlans()
    { await PauseAvailabilityAsync(); await Task.Run(() => ObserveRecovery(WindowsActivityKind.BatchHistoryReset, grants.BatchPlans.ArchiveHistory)); Notify(DesktopChange.Transfers | DesktopChange.Settings); }
    public Task CheckIncomingSecurity(string id) { trust.RequireCurrentOwner(); return ObserveRecoveryAsync(WindowsActivityKind.ReceivedCopyCheck, async () => { await grants.Uploads.CheckOwnedPublishedAsync(id); dispatch(() => Notify(DesktopChange.Transfers)); return grants.Uploads.HasCompletedAttachmentCheck(id); }); }
    public Task CheckDownloadSecurity(string id) { trust.RequireCurrentOwner(); return ObserveRecoveryAsync(WindowsActivityKind.ReceivedCopyCheck, async () => { await downloads.CheckPublishedAsync(id); return downloads.HasCompletedAttachmentCheck(id); }); }
    public async Task ArchiveIncomingHistory()
    {
        await PauseAvailabilityAsync(); await Task.Run(() => ObserveRecovery(WindowsActivityKind.IncomingHistoryReset, grants.Uploads.ArchiveHistory));
        Notify(DesktopChange.Transfers | DesktopChange.Settings);
    }
    public IReadOnlyList<DownloadJob> OrphanedBatchFiles => downloads.Jobs.Where(x => x.BatchId.Length != 0 && !batches.Jobs.Any(b => b.Id == x.BatchId)).ToArray();
    public IReadOnlyList<TransferRecoveryEntry> TransferRecovery => downloads.RecoveryIssues.Select(x => new TransferRecoveryEntry("Files", x))
        .Concat(batches.RecoveryIssues.Select(x => new TransferRecoveryEntry("Batches", x))).Concat(downloadSources.RecoveryIssues.Select(x => new TransferRecoveryEntry("Sending", x)))
        .Concat(grants.MutationRecoveryIssues.Select(x => new TransferRecoveryEntry("File operations", x)))
        .Concat(grants.Deleted.RecoveryIssues.Select(x => new TransferRecoveryEntry("Deleted items", x)))
        .Concat(grants.Uploads.RecoveryIssues.Select(x => new TransferRecoveryEntry("Incoming copies", x)))
        .Concat(grants.BatchPlans.RecoveryIssues.Select(x => new TransferRecoveryEntry("Batch plans", x)))
        .Concat(outgoing.RecoveryIssues.Select(x => new TransferRecoveryEntry("Outgoing copies", x)))
        .Concat(outgoingBatches.RecoveryIssues.Select(x => new TransferRecoveryEntry("Private batches", x)))
        .Concat(outgoingBatchSender.RecoveryIssues.Select(x => new TransferRecoveryEntry("Batch sending", x)))
        .Concat(Availability.RecoveryIssues.Select(x => new TransferRecoveryEntry("Availability", x))).ToArray();
    public int PreservedProgressRecords => downloads.PreservedRecords + batches.PreservedRecords + downloadSources.PreservedRecords + grants.MutationPreservedRecords + grants.Uploads.PreservedRecords + grants.BatchPlans.PreservedRecords + outgoing.PreservedRecords + Availability.PreservedRecords + (trust.OwnerBinding?.PreservedRecords ?? 0);
    public Task ClearPreservedProgress() => Task.Run(() => {
        try { ObserveRecovery(WindowsActivityKind.ProgressBackupRemoval, () => { downloads.ClearPreservedRecords(); batches.ClearPreservedRecords(); downloadSources.ClearPreservedRecords(); grants.ClearMutationPreservedRecords(); grants.Uploads.ClearPreservedRecords(); grants.BatchPlans.ClearPreservedRecords(); outgoing.ClearArchives(); Availability.ClearPreservedRecords(); trust.OwnerBinding?.ClearPreservedRecords(); }); }
        finally { dispatch(() => Notify(DesktopChange.Transfers)); }
    });
    public Task ArchiveTransferRecovery(string area, string id) => Task.Run(() =>
    {
        try { ObserveRecovery(WindowsActivityKind.ProgressPreservation, () => { switch (area) { case "Files": downloads.ArchiveRecovery(id); break; case "Batches": batches.ArchiveRecovery(id); break; case "Sending": downloadSources.ArchiveRecovery(id); break; case "File operations": grants.ArchiveMutationRecovery(id); break;
            case "Availability": Availability.ArchiveRecovery(id); break;
            case "Deleted items": grants.Deleted.ArchiveRecovery(id); break;
            case "Incoming copies": grants.Uploads.ArchiveRecovery(id); break;
            case "Outgoing copies": outgoing.ArchiveRecovery(id); break;
            default: throw new InvalidOperationException("This recovery item is no longer available."); } });
        } finally { dispatch(() => Notify(DesktopChange.Transfers | DesktopChange.Settings)); }
    });
    public IReadOnlyList<BatchDownload> Batches => batches.Jobs;
    public LocalExplorerController LocalFiles { get; }
    public IReadOnlyList<DownloadJob> BatchFiles(string id) => downloads.Jobs.Where(x => x.BatchId == id).ToArray();
    public IReadOnlyList<FileEntry> Entries { get; private set; } = [];
    public IReadOnlyList<FileEntry> Breadcrumbs { get; private set; } = [];
    public string Continuation { get; private set; } = "";
    public PreviewSession FilePreview { get; }
    public string Preview => FilePreview.View.Text;
    public string FileError { get; private set; } = "";
    public bool Browsing { get; private set; }
    public ExplorerSort FileSort { get; private set; }
    public ExplorerLayout FileLayout { get; private set; }
    public bool SortDescending { get; private set; }
    public int FilePage { get; private set; }
    public int FilePageCount => Math.Max(1, (catalog.Length + ExplorerCatalog.PageSize - 1) / ExplorerCatalog.PageSize);
    public int FileCount => catalog.Length;
    public bool DiscoveryComplete { get; private set; } = true;
    public string DiscoveryNotice { get; private set; } = "";
    public string SearchQuery { get; private set; } = "";
    public IReadOnlyList<ExplorerRow> SelectedFiles => selectedFiles.Values.ToArray();
    public IReadOnlyList<ExplorerRow> FileRows => catalog.Skip(FilePage * ExplorerCatalog.PageSize).Take(ExplorerCatalog.PageSize).ToArray();
    public IReadOnlyList<MutationInfo> RecentMutations { get; private set; } = [];
    public FileEntry? PendingMove { get; private set; }
    public string MutationMessage { get; private set; } = "";
    public event Action<DesktopChange>? Changed;
    public event Action<string>? InvitationCreated;
    public event Action<Pairing>? PairingRequested;

    public DesktopController(string directory, Action<Action> dispatch, bool development = false, IPreviewRenderer? previewRenderer = null)
    {
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); this.dispatch = dispatch;
        FilePreview = new(previewRenderer);
        FilePreview.Changed += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Files); });
        Directory.CreateDirectory(this.directory);
        Settings = LocalState.Read<DesktopSettings>(Path.Combine(directory, "settings.json"), 32 * 1024)
            ?? new DesktopSettings(Environment.MachineName, development ? "http://localhost:5078" : "");
        LocalState.Validate(Settings);
        identity = OpenIdentity();
        TrustedPeerStore? openedTrust = null;
        WindowsTransferNetworkSettings? openedNetwork = null;
        WindowsUploadSender? openedOutgoing = null; WindowsBatchSources? openedBatchSources = null; WindowsBatchSender? openedBatchSender = null;
        try
        {
            trust = openedTrust = new TrustedPeerStore(directory, identity.Id);
            try
            {
                TransferNetwork = openedNetwork = new WindowsTransferNetworkSettings(directory); NetworkTraffic = TransferNetwork.Policy;
                TransferNetwork.Changed += () => dispatch(() => { if (!disposed) { ResetPayloadRateSettings(); Notify(DesktopChange.Settings | DesktopChange.Transfers); } });
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
            { TransferNetworkIssue = "Transfer network settings could not be opened. Remote file content is paused; local browsing remains available. Restore access to private settings storage and restart."; NetworkTraffic.Configure(new(), healthy: false); }
            grants = new StorageGrantStore(directory, ownerBinding: trust.OwnerBinding); activity = new LocalActivityStore(directory); Availability = new DesktopAvailability(directory);
            activityBackups = new LocalActivityBackups(activity, () => DiagnosticScope, () => !disposed);
            downloadSources = new DownloadSourceStore(grants, directory);
            downloads = new WindowsDownloadManager(directory);
            batches = new WindowsBatchDownloads(directory, downloads);
            outgoing = openedOutgoing = new WindowsUploadSender(directory, CurrentUploadAuthority, () => !disposed && OwnerPolicyHealthy, outgoingResources);
            outgoingBatches = openedBatchSources = new WindowsBatchSources(directory, CurrentUploadAuthority, () => !disposed && OwnerPolicyHealthy, outgoingResources,
                () => identity.Id + "." + OwnerPolicyEpoch);
            outgoingBatchSender = openedBatchSender = new WindowsBatchSender(directory, outgoingBatches, CurrentUploadAuthority, () => !disposed && OwnerPolicyHealthy,
                authority => authority.Owner == identity.Id && authority.OwnerEpoch == OwnerPolicyEpoch, () => identity.Id + "." + OwnerPolicyEpoch);
            outgoing.MetadataChanged += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            outgoing.PayloadChanged += ObserveOutgoingPayloadProgress;
            outgoingBatches.MetadataChanged += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            outgoingBatchSender.MetadataChanged += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            downloadSources.Changed += NotifyServingSources;
            grants.Uploads.MetadataChanged += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            grants.BatchPlans.Changed += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            batches.Changed += () => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            downloads.Changed += ObservePayloadProgress;
            downloads.MetadataChanged += _ => dispatch(() => { if (!disposed) Notify(DesktopChange.Transfers); });
            LocalFiles = new LocalExplorerController(directory, previewRenderer);
            LocalFiles.Changed += () => dispatch(() => { if (!disposed) Notify(DesktopChange.LocalFiles); });
            receivingParts = new WindowsReceivingPartRecovery(grants, downloads, batches, () =>
            {
                if (disposed || trust.OwnerBinding is not { Healthy: true } binding || binding.OwnerId != identity.Id)
                    throw new IOException("Review the current Windows identity before partial-file cleanup.");
                return identity.Id + "." + binding.Epoch;
            }, () => !disposed && client is null && connectionRun is null && !Connecting && !Connected, () => LocalFiles.Copies, () => LocalFiles.BatchCopies);
            activityWriter = new(activity, () => dispatch(() => { if (!disposed) Notify(DesktopChange.History); }));
            grants.Activity = activityWriter.Record;
            downloads.Activity = batches.Activity = outgoing.Activity = outgoingBatches.Activity = outgoingBatchSender.Activity = activityWriter.Record;
            if (LocalFiles.Copies is { } localCopies) localCopies.Activity = activityWriter.Record;
            if (LocalFiles.BatchCopies is { } localBatches) localBatches.Activity = activityWriter.Record;
            StartPayloadPulse();
            if (LocalFiles.Management is { } localManagement) localManagement.Activity = activityWriter.Record;
        }
        catch { StopPayloadPulse(); openedBatchSender?.DisposeAsync().AsTask().GetAwaiter().GetResult(); openedBatchSources?.DisposeAsync().AsTask().GetAwaiter().GetResult(); openedOutgoing?.DisposeAsync().AsTask().GetAwaiter().GetResult(); openedNetwork?.Dispose(); openedTrust?.Dispose(); identity.Dispose(); throw; }
    }
    private DeviceIdentity OpenIdentity()
    {
        string Name(string path) => "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        var canonical = Name(directory);
        var aliases = new[] { canonical, Name(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar) }.Distinct().ToArray();
        var existing = aliases.Where(CngKey.Exists).ToArray();
        if (existing.Length > 1) throw new InvalidDataException("This profile has conflicting Windows identities from earlier path aliases. Preserve both keys and review the profile before reconnecting.");
        return DeviceIdentity.OpenWindows(existing.SingleOrDefault() ?? canonical, Settings.DeviceName);
    }
    private void Notify(DesktopChange change = DesktopChange.Status) => Changed?.Invoke(change);
    private void Record(string category, string description, string? peer = null)
    {
        try { activity.Add(category, description, peer); }
        catch (IOException) { Diagnostic = "Local activity could not be saved. Check available storage."; }
        catch (UnauthorizedAccessException) { Diagnostic = "Local activity could not be saved. Check the application's local permissions."; }
        Notify(DesktopChange.History);
    }
    public void SaveSettings(DesktopSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Connected || Connecting || Availability.AutomaticAllowed) throw new InvalidOperationException("Pause availability before changing connection settings.");
        LocalState.Validate(settings);
        LocalState.Write(Path.Combine(directory, "settings.json"), settings);
        Settings = settings;
        identity.Dispose(); identity = OpenIdentity();
        if (trust.OwnerBinding?.OwnerId != identity.Id)
        {
            _ = new WindowsOwnerBinding(directory, identity.Id); // Invalidates any live approval scope held by the former key.
            Notify(DesktopChange.All);
            throw new InvalidDataException("The Windows key changed. Reopen iConnect and review its saved trust and approvals.");
        }
        Record("Settings", "Connection settings updated.");
        Notify(DesktopChange.Settings | DesktopChange.Status);
    }
    public async Task ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        trust.RequireCurrentOwner();
        trust.InitializeUploadScopes();
        if (pendingRevocations.Count != 0) throw new InvalidOperationException("Device revocation needs another attempt. Keep availability paused and retry Revoke in Devices.");
        if (Connected || Connecting) return;
        if (Settings.Coordinator.Length == 0) throw new InvalidOperationException("Set the coordinator address in Settings first.");
        var origin = new Uri(Settings.Coordinator); SignalConnection.ValidateServer(origin);
        var current = ++generation;
        var lifetime = new CancellationTokenSource(); connectionLifetime = lifetime;
        Connecting = true; Status = "Connecting to the coordinator…"; Diagnostic = ""; Notify();
        activityWriter.Record(new(WindowsActivityKind.CoordinatorConnection, WindowsActivityOutcome.Requested));
        SignalConnection? connection = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            connection = await SignalConnection.ConnectAsync(origin, identity, timeout.Token);
            if (current != generation || disposed) { await connection.DisposeAsync(); return; }
            var active = new ProofClient(identity, connection, origin, directory,
                message => Post(current, () => { Diagnostic = message; Notify(); }),
                Settings.Stun.Length == 0 ? null : Settings.Stun, _ => new WindowsFileService(grants, downloadSources), trust, Settings.SharePresence, NetworkTraffic);
            client = active; connection = null;
            active.PresenceChanged += () => Post(current, () => Notify(DesktopChange.Devices));
            active.InvitationCreated += invitation => Post(current, () => InvitationCreated?.Invoke(invitation));
            active.PairingRequested += pairing => Post(current, () => PairingRequested?.Invoke(pairing));
            active.Paired += peer => Post(current, () =>
            {
                Record("Devices", "A device was paired after mutual approval.", peer.Id);
                Notify(DesktopChange.Devices | DesktopChange.Status);
            });
            active.SessionStateChanged += state => Post(current, () =>
            {
                ActivePeerId = state.PeerId; PeerAuthenticated = state.Authenticated; Status = state.Message;
                if (!state.Authenticated) ClearFiles();
                WindowsActivityOutcome? outcome = state.Phase switch
                { ProofClient.PeerLinkPhase.Connecting => WindowsActivityOutcome.Requested, ProofClient.PeerLinkPhase.Authenticated => WindowsActivityOutcome.Completed,
                    ProofClient.PeerLinkPhase.Ended => WindowsActivityOutcome.Ended, ProofClient.PeerLinkPhase.Canceled => WindowsActivityOutcome.Canceled,
                    ProofClient.PeerLinkPhase.Failed => WindowsActivityOutcome.Failed, ProofClient.PeerLinkPhase.Unavailable or ProofClient.PeerLinkPhase.Revoked => WindowsActivityOutcome.Refused, _ => null };
                if (outcome is { } observed) activityWriter.Record(new(WindowsActivityKind.DirectConnection, observed, state.PeerId));
                Notify(DesktopChange.Status | DesktopChange.Devices | DesktopChange.Files | DesktopChange.Transfers);
            });
            Connected = true; Connecting = false;
            Status = "Coordinator connected. Choose a paired device or add a new one.";
            activityWriter.Record(new(WindowsActivityKind.CoordinatorConnection, WindowsActivityOutcome.Completed));
            connectionRun = Task.Run(async () =>
            {
                var ending = WindowsActivityOutcome.Ended;
                try { await active.RunAsync(lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception) { ending = WindowsActivityOutcome.Failed; Post(current, () => Diagnostic = "The coordinator connection ended. Check the address and network, then reconnect."); }
                finally { Post(current, () => _ = EndConnectionAsync(active, current, ending)); }
            });
            Notify(DesktopChange.Status | DesktopChange.Devices);
        }
        catch (Exception error)
        {
            if (connection is not null) await connection.DisposeAsync();
            if (current == generation)
            {
                activityWriter.Record(new(WindowsActivityKind.CoordinatorConnection, error is OperationCanceledException && lifetime.IsCancellationRequested ? WindowsActivityOutcome.Canceled : WindowsActivityOutcome.Failed));
                Connecting = false; Connected = false;
                Status = "Could not connect. Check the coordinator address and network.";
                lifetime.Dispose(); connectionLifetime = null; Notify();
            }
            throw;
        }
    }
    public async Task ResumeAvailabilityAsync()
    { trust.RequireCurrentOwner(); if (pendingRevocations.Count != 0) throw new InvalidOperationException("Retry device revocation before resuming availability."); Availability.Resume(); Notify(DesktopChange.Settings); await ConnectAsync(); }
    public async Task PauseAvailabilityAsync()
    { var stopping = DisconnectAsync(); try { Availability.Pause(); } finally { await stopping; Notify(DesktopChange.Settings); } }
    public void SetAvailabilityOptions(bool background, bool startup, bool reconnect)
    {
        Availability.SetOptions(background, startup, reconnect);
        Record("Settings", "Windows availability preferences updated."); Notify(DesktopChange.Settings | DesktopChange.Status);
    }
    private void Post(int current, Action action) => dispatch(() => { if (!disposed && current == generation) action(); });
    private async Task EndConnectionAsync(ProofClient active, int current, WindowsActivityOutcome outcome)
    {
        if (current != generation || !ReferenceEquals(client, active)) return;
        await DisconnectAsync(outcome);
    }
    public Task DisconnectAsync() => DisconnectAsync(WindowsActivityOutcome.Canceled);
    private async Task DisconnectAsync(WindowsActivityOutcome outcome)
    {
        await StopIncomingOperationReviews();
        var hadCoordinator = Connected || Connecting; var hadPeer = PeerAuthenticated || PeerConnecting; var peer = ActivePeerId;
        outgoing.PauseAll(); outgoingBatchSender.PauseAll(); outgoingBatches.PauseAll(); ReceivingLocations = []; ReceivingLocation = null;
        ++generation;
        var active = client; client = null;
        var run = connectionRun; connectionRun = null;
        var lifetime = connectionLifetime; connectionLifetime = null;
        lifetime?.Cancel();
        foreach (var batch in batches.Jobs.Where(x => x.Phase == BatchPhase.Running)) batches.Pause(batch.Id);
        Connected = Connecting = PeerAuthenticated = false; ActivePeerId = ""; ClearFiles();
        if (hadPeer) activityWriter.Record(new(WindowsActivityKind.DirectConnection, outcome, peer));
        if (hadCoordinator) activityWriter.Record(new(WindowsActivityKind.CoordinatorConnection, outcome));
        Status = "Not connected. Your shared locations are unavailable remotely.";
        Notify(DesktopChange.Status | DesktopChange.Devices | DesktopChange.Files);
        if (active is not null) await active.DisposeAsync();
        if (run is not null) await run;
        grants.Uploads.PauseVerification(); await grants.Uploads.WhenIdleAsync();
        grants.BatchPlans.PauseFileVerification(); await grants.BatchPlans.WhenFilesIdleAsync();
        await outgoing.WhenIdleAsync();
        await outgoingBatchSender.WhenIdleAsync();
        await outgoingBatches.WhenIdleAsync();
        lifetime?.Dispose();
    }
    public Task CreateInvitationAsync() => RequireClient().CreateInvitationAsync(connectionLifetime!.Token);
    public Task ApproveAsync(string code) => RequireClient().ApproveAsync(code, connectionLifetime!.Token);
    public Task RejectAsync() => RequireClient().RejectAsync(connectionLifetime!.Token);
    public async Task ConnectDeviceAsync(string id)
    {
        _ = RequirePeer(id);
        if (Availability.Preferences.PeerId != id) Availability.RememberPeer(id);
        await RequireClient().ConnectPeerAsync(id, connectionLifetime!.Token);
    }
    public async Task RevokeAsync(string id)
    {
        _ = RequirePeer(id);
        pendingRevocations.Add(id);
        var stopping = DisconnectAsync(); // Immediate barrier before persistence or automatic retry can run.
        try
        {
            try { Availability.Pause(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostic = "Availability could not be saved. Keep iConnect open and retry revocation before restarting."; }
            await stopping;
            await ChangePolicyAsync(() => Task.Run(() => { grants.RevokePeer(id); trust.Remove(id); }));
            pendingRevocations.Remove(id);
        }
        catch { Diagnostic = "Device revocation could not finish. Access stays paused in this run; retry Revoke in Devices."; Notify(DesktopChange.Status | DesktopChange.Devices); throw; }
        try { Availability.ForgetPeer(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostic = "The device was revoked, but its reconnect preference could not be cleared. Automatic access is blocked."; }
        foreach (var batch in batches.Jobs.Where(x => x.PeerId == id && x.Phase is not (BatchPhase.Completed or BatchPhase.Canceled)))
            await batches.Cancel(batch.Id);
        foreach (var job in downloads.Jobs.Where(x => x.PeerId == id && x.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped)))
            await downloads.Cancel(job.Id);
        Record("Devices", "A device and its Windows storage approvals were revoked.", id);
        Notify(DesktopChange.Devices | DesktopChange.Grants);
    }
    private ProofClient RequireClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Connected && client is not null ? client : throw new InvalidOperationException("Connect to the coordinator first.");
    }
    private ProofClient.TrustedPeer RequirePeer(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return trust.TryGet(id, out var peer) ? peer : throw new InvalidDataException("Choose a paired device.");
    }
    private async Task ChangePolicyAsync(Func<Task> action)
    {
        var epoch = OwnerPolicyEpoch;
        await policyGate.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); trust.RequireCurrentOwner(); trust.OwnerBinding!.Require(epoch); await action(); }
        finally { policyGate.Release(); }
    }
    public async Task ResetOwnerPolicyAsync()
    {
        CancelDiagnosticReview();
        CancelActivityBackupReview();
        receivingParts.ClearSelection();
        var stopping = DisconnectAsync();
        try { Availability.Pause(); Availability.SetOptions(false, false, false); }
        finally { await stopping; }
        await policyGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await Task.Run(() => { trust.ResetOwnerPolicy(); grants.ReloadOwnerScope(); });
            pendingRevocations.Clear();
            Record("Devices", "Earlier Windows trust and storage approvals were archived; fresh pairing and approvals are required.");
        }
        finally { policyGate.Release(); Notify(DesktopChange.All); }
    }
    public async Task AddGrantAsync(string peer, string path, string label, StoragePermission permissions,
        string[] exclusions, DateTimeOffset? expiresAt, bool enabled = true)
    {
        await ChangePolicyAsync(async () =>
        {
            _ = RequirePeer(peer);
            await Task.Run(() => grants.Add(peer, path, permissions, label, exclusions, expiresAt, enabled));
        });
        Record("Permissions", "A Windows storage location was explicitly approved.", peer);
        Notify(DesktopChange.Grants);
    }
    public async Task UpdateGrantAsync(string id, StoragePermission permissions, string[] exclusions, DateTimeOffset? expiresAt, bool enabled, string? label = null)
    {
        await ChangePolicyAsync(async () =>
        {
            var grant = grants.All.Single(x => x.Id == id); _ = RequirePeer(grant.PeerId);
            await Task.Run(() => grants.Update(id, permissions, exclusions, expiresAt, enabled, label));
        });
        Record("Permissions", "Windows storage permissions were updated.");
        Notify(DesktopChange.Grants);
    }
    public async Task RemoveGrantAsync(string id)
    {
        await ChangePolicyAsync(() => Task.Run(() => grants.Remove(id)));
        Record("Permissions", "A Windows storage approval was removed."); Notify(DesktopChange.Grants);
    }
    public async Task BrowseAsync(FileEntry? folder = null, bool nextPage = false)
    {
        if (nextPage) { SetFilePage(FilePage + 1); return; }
        if (!PeerAuthenticated) throw new InvalidOperationException("Open a direct connection to the phone first.");
        if (Browsing) return;
        var route = FolderRoute(folder);
        await DiscoverAsync(route, "", false);
    }
    public Task SearchFilesAsync(string query)
    {
        if (Breadcrumbs.Count == 0) throw new InvalidOperationException("Open an approved folder before searching.");
        if (Browsing) throw new InvalidOperationException("Wait for discovery or cancel it first.");
        return DiscoverAsync(Breadcrumbs.ToArray(), query, true);
    }
    private FileEntry[] FolderRoute(FileEntry? folder)
    {
        if (folder is null) return [];
        var known = catalog.FirstOrDefault(x => x.Entry.Id == folder.Id) ?? selectedFiles.GetValueOrDefault(folder.Id);
        if (known is not null && known.Entry.IsDirectory) return known.Route;
        var index = Breadcrumbs.ToList().FindIndex(x => x.Id == folder.Id);
        if (index >= 0) return Breadcrumbs.Take(index + 1).ToArray();
        throw new InvalidOperationException("Refresh the folder before opening it.");
    }
    private async Task DiscoverAsync(FileEntry[] route, string query, bool search)
    {
        if (!PeerAuthenticated) throw new InvalidOperationException("Open a direct connection to the phone first.");
        var active = RequireClient(); var peer = ActivePeerId; var current = generation; var revision = ++discoveryGeneration;
        discovery?.Cancel(); discovery?.Dispose();
        var cancellation = discovery = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime!.Token);
        var discoveryToken = cancellation.Token;
        var requester = active.CaptureFileRequester(peer);
        if (!search) selectedFiles.Clear();
        Browsing = true; FileError = ""; FilePreview.Clear(); Notify(DesktopChange.Files);
        try
        {
            var result = await Task.Run(() => search ? ExplorerCatalog.SearchAsync(route, query, (value, ct) => requester(value, ct), discoveryToken)
                : ExplorerCatalog.LoadAsync(route, (value, ct) => requester(value, ct), discoveryToken), discoveryToken);
            if (current != generation || revision != discoveryGeneration || !ReferenceEquals(active, client)) return;
            Breadcrumbs = route; SearchQuery = search ? query.Trim() : ""; FilePage = 0;
            catalog = ExplorerCatalog.Sort(result.Rows, FileSort, SortDescending);
            foreach (var row in catalog.Where(x => selectedFiles.TryGetValue(x.Entry.Id, out var old) && old.Entry.Version != x.Entry.Version).ToArray()) selectedFiles.Remove(row.Entry.Id);
            DiscoveryComplete = result.Complete; DiscoveryNotice = result.Notice; ApplyFilePage();
        }
        catch (OperationCanceledException) { if (current == generation && revision == discoveryGeneration) FileError = "Discovery canceled. Previous results were kept."; }
        catch (Exception ex) { if (current == generation && revision == discoveryGeneration) FileError = ex.Message; }
        finally { if (current == generation && revision == discoveryGeneration) { Browsing = false; Notify(DesktopChange.Files); } }
    }
    public void CancelDiscovery() => discovery?.Cancel();
    public void SetFileSort(ExplorerSort sort, bool descending)
    { FileSort = sort; SortDescending = descending; catalog = ExplorerCatalog.Sort(catalog, sort, descending); FilePage = 0; ApplyFilePage(); Notify(DesktopChange.Files); }
    public void SetFileLayout(ExplorerLayout layout) { FileLayout = layout; Notify(DesktopChange.Files); }
    public void SetFilePage(int page) { FilePage = Math.Clamp(page, 0, FilePageCount - 1); ApplyFilePage(); Notify(DesktopChange.Files); }
    private void ApplyFilePage() { Entries = FileRows.Select(x => x.Entry).ToArray(); Continuation = ""; }
    public void SelectFile(string id, bool selected)
    {
        if (!selected) { selectedFiles.Remove(id); return; }
        var row = catalog.FirstOrDefault(x => x.Entry.Id == id) ?? throw new InvalidOperationException("This item is no longer in the loaded results.");
        if (selectedFiles.Count >= 128 && !selectedFiles.ContainsKey(id)) throw new InvalidOperationException("Select at most 128 items for one copy.");
        selectedFiles[id] = row;
    }
    public void ClearFileSelection() => selectedFiles.Clear();
    public async Task<FileEntry> FileDetailsAsync(FileEntry entry)
    {
        var active = RequireClient(); var current = generation;
        var response = await active.RequestFilesAsync(new FileRequest { Operation = FileOperation.GetMetadata, NodeId = entry.Id }, connectionLifetime!.Token);
        if (current != generation || !ReferenceEquals(active, client)) throw new IOException("The connection changed.");
        var metadata = response.Entries.SingleOrDefault();
        if (response.Status != FileStatus.Ok || metadata is null || metadata.Id != entry.Id || metadata.GrantId != entry.GrantId) throw new IOException("This item changed or is unavailable. Refresh it.");
        return metadata;
    }
    public Task PreviewTextAsync(FileEntry entry) => PreviewAsync(entry);
    public Task PreviewAsync(FileEntry entry)
    {
        if (!PeerAuthenticated) throw new InvalidOperationException("Connect to the phone first.");
        var active = RequireClient(); var current = generation; var view=discoveryGeneration;
        var request=active.CaptureFileRequester(ActivePeerId);
        return FilePreview.OpenAsync(entry, (value,token) => request(value,token),
            () => current==generation && view==discoveryGeneration && ReferenceEquals(active,client) && PeerAuthenticated && !disposed, connectionLifetime!.Token);
    }
    public async Task CreateFolderAsync(string name)
    {
        var current = generation; var active = RequireClient();
        var folder = Breadcrumbs.LastOrDefault() ?? throw new InvalidOperationException("Choose a destination folder first.");
        StorageRules.ValidateName(name);
        var response = await ChangeFileAsync(new FileRequest { Operation = FileOperation.CreateDirectory, DestinationId = folder.Id, Name = name });
        if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
        if (current == generation && ReferenceEquals(active, client)) await BrowseAsync(folder);
    }
    public async Task RefreshMutationsAsync()
    {
        var active = RequireClient(); var current = generation; var request = active.CaptureFileRequester(ActivePeerId); var token = connectionLifetime!.Token;
        var context = await request(new FileRequest { Operation = FileOperation.MutationContext }, token);
        if (context.Status != FileStatus.Ok) throw new IOException(context.Message);
        var response = await request(new FileRequest { Operation = FileOperation.ListMutations, MutationEpoch = context.MutationEpoch }, token);
        if (current != generation || !ReferenceEquals(active, client)) return;
        if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
        RecentMutations = response.Mutations.ToArray(); Notify(DesktopChange.Files);
    }
    public async Task UseCreatedFolderForSending(string id)
    {
        if (!PeerAuthenticated) throw new InvalidOperationException("Connect to the paired device first.");
        var original = RecentMutations.Single(x => x.Id == id && x.Operation == FileOperation.CreateDirectory && x.Phase == MutationPhase.ChangeSucceeded);
        var active = RequireClient(); var current = generation; var peer = ActivePeerId; var request = active.CaptureFileRequester(peer); var token = connectionLifetime!.Token;
        var context = await request(new() { Operation = FileOperation.MutationContext }, token);
        if (context.Status != FileStatus.Ok) throw new IOException(context.Message);
        var response = await request(new() { Operation = FileOperation.ResolveCreatedDirectory, MutationId = id, MutationEpoch = context.MutationEpoch }, token);
        if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
        var roots = await request(new() { Operation = FileOperation.UploadContext }, token);
        if (roots.Status != FileStatus.Ok) throw new IOException(roots.Message);
        StorageRules.ValidateResponse(roots);
        if (roots.Entries.Any(x => !x.IsRoot || !x.IsDirectory || (x.Permissions & 12) != 12)) throw new InvalidDataException("The receiver returned invalid incoming locations.");
        var grant = roots.Entries.SingleOrDefault(x => response.Entries.Count == 1 && x.GrantId == response.Entries[0].GrantId && x.IsRoot && (x.Permissions & 12) == 12)
            ?? throw new IOException("This created folder is not in a currently approved receiving location.");
        var entry = CreatedDirectoryReferences.Check(new(id, context.MutationEpoch, grant.GrantId, original.RequestedName), response);
        if ((entry.Permissions & 12) != 12) throw new IOException("This folder no longer permits incoming copies.");
        if (current != generation || !ReferenceEquals(active, client) || peer != ActivePeerId) return;
        ReceivingLocations = roots.Entries.Concat(new[] { entry }).ToArray(); ReceivingLocation = entry;
        MutationMessage = "Selected created folder for sending: " + grant.Name + " / " + entry.Name + ". Review Transfers before sending.";
        Notify(DesktopChange.Files | DesktopChange.Transfers);
    }
    public async Task RefreshLocalDeletedAsync()
    {
        LocalDeleted = await Task.Run(() => grants.Deleted.ListLocalRecords()); LocalDeletedLoaded = true; Notify(DesktopChange.Grants);
    }
    public async Task RestoreLocalDeletedAsync(string id, string name)
    {
        var receipt = await Task.Run(() => grants.Deleted.RestoreLocal(id, name));
        await RefreshLocalDeletedAsync();
        if (receipt.Status != FileStatus.Ok) throw new IOException(receipt.Message);
        Record("Files", "A saved item was restored on this computer.");
    }
    public Task<AlternateRestoreReview> ReviewAlternateRestoreAsync(string id, string directory, string name) =>
        Task.Run(() => grants.Deleted.PrepareAlternateRestoreLocal(id, directory, name));
    public async Task ConfirmAlternateRestoreAsync(string token)
    {
        MutationInfo receipt;
        try { receipt = await Task.Run(() => grants.Deleted.ConfirmAlternateRestoreLocal(token)); }
        finally { await RefreshLocalDeletedAsync(); }
        if (receipt.Status != FileStatus.Ok) throw new IOException(receipt.Message);
        Record("Files", "A saved item was restored to a reviewed folder on this computer.");
    }
    public async Task DismissLocalDeletedAsync(string id)
    { await Task.Run(() => grants.Deleted.DismissRestoredLocal(id)); await RefreshLocalDeletedAsync(); }
    public Task<RecoveryInspection> InspectLocalDeletedAsync(string id) => Task.Run(() => grants.Deleted.InspectLocal(id));
    public Task<RecoveryVaultReview> ReviewEmptyVaultAsync(string id) => Task.Run(() => grants.Deleted.PrepareEmptyVaultLocal(id));
    public async Task RemoveEmptyVaultAsync(string token)
    {
        await Task.Run(() => grants.Deleted.RemoveEmptyVaultLocal(token));
        await RefreshLocalDeletedAsync();
        Record("Files", "An empty recovery folder was removed after local review.");
    }
    public async Task<string> ArchiveAbsentLocalDeletedAsync(string token, string destination)
    {
        var exported = await Task.Run(() => grants.Deleted.ArchiveAbsentLocal(token, destination));
        await RefreshLocalDeletedAsync();
        Record("Files", "An absent saved-item record was exported and archived locally.");
        return exported;
    }
    public async Task ConfirmLocalDeletedAsync(string token)
    { await Task.Run(() => grants.Deleted.ConfirmLocationsLocal(token)); await RefreshLocalDeletedAsync(); }
    public Task<RecoveryReview> PrepareLocalPurgeAsync(string id) => Task.Run(() => grants.Deleted.PreparePurgeLocal(id));
    public async Task PurgeLocalDeletedAsync(string token, string confirmation)
    {
        RecoveryPurgeResult result;
        try { result = await Task.Run(() => grants.Deleted.PurgeLocal(token, confirmation)); }
        finally { await RefreshLocalDeletedAsync(); }
        Record("Files", result.Complete ? "A reviewed saved copy was permanently deleted locally." : "Local permanent deletion needs review.");
        if (!result.Complete) throw new IOException(result.Message);
    }
    public Task<RecoveryReview> PrepareRemainingRestoreAsync(string id) => Task.Run(() => grants.Deleted.PrepareRemainingRestoreLocal(id));
    public async Task RestoreRemainingAsync(string token, string name, string confirmation)
    {
        MutationInfo result;
        try { result = await Task.Run(() => grants.Deleted.RestoreRemainingLocal(token, name, confirmation)); }
        finally { await RefreshLocalDeletedAsync(); }
        Record("Files", "Remaining saved data was reviewed for local restoration.");
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
    }
    public async Task<string> ExportLocalDeletedAsync(string id, string destination)
    {
        var path = await Task.Run(() => grants.Deleted.ExportLocalRecord(id, destination));
        Record("Files", "One recovery record was exported to an owner-selected local folder."); return path;
    }
    public async Task RefreshDeletedAsync(bool nextPage = false)
    {
        var active = RequireClient(); var current = generation; var request = active.CaptureFileRequester(ActivePeerId); var token = connectionLifetime!.Token;
        var context = await request(new FileRequest { Operation = FileOperation.RecoveryContext }, token);
        if (context.Status != FileStatus.Ok) throw new IOException(context.Message);
        var offset = nextPage ? deletedOffset : 0;
        var response = await request(new FileRequest { Operation = FileOperation.ListDeleted, MutationEpoch = context.MutationEpoch, Offset = offset }, token);
        if (current != generation || !ReferenceEquals(active, client)) return;
        if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
        RemoteDeleted = nextPage ? RemoteDeleted.Concat(response.DeletedItems).DistinctBy(x => x.Id).Take(512).ToArray() : response.DeletedItems.ToArray();
        deletedOffset = offset + 32; MoreRemoteDeleted = !response.EndOfFile && deletedOffset < 512; Notify(DesktopChange.Files);
    }
    public async Task DeleteSelectedAsync(FileEntry confirmed)
    {
        var entry = SelectedFiles.Single().Entry;
        if (entry.IsRoot || !entry.Equals(confirmed)) throw new InvalidOperationException("The selection changed. Review it again.");
        var folder = Breadcrumbs.LastOrDefault();
        var result = await ChangeFileAsync(new() { Operation = FileOperation.Delete, NodeId = entry.Id, ExpectedVersion = entry.Version });
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
        if (folder is not null) await BrowseAsync(folder);
    }
    public async Task RestoreDeletedAsync(string id, string name)
    {
        if (!RemoteDeleted.Any(x => x.Id == id && x.State == DeletedState.DeletedStored)) throw new IOException("The saved item changed. Refresh it.");
        var result = await ChangeFileAsync(new() { Operation = FileOperation.Restore, DeletedId = id, Name = name });
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
        if (Breadcrumbs.LastOrDefault() is { } folder) await BrowseAsync(folder);
    }
    private async Task<FileResponse> ChangeFileAsync(FileRequest value)
    {
        var active = RequireClient(); var current = generation; var request = active.CaptureFileRequester(ActivePeerId); var token = connectionLifetime!.Token;
        var recovering = RecoveryRules.IsChange(value.Operation);
        try
        {
            var result = await DesktopFileChange.ExecuteAsync(value, ActivePeerId, request, activityWriter.Record, token);
            if (current == generation && ReferenceEquals(active, client))
            {
                MutationMessage = result.Message;
                if (result.Mutation is not null) RecentMutations = new[] { result.Mutation }.Concat(RecentMutations.Where(x => x.Id != result.Mutation.Id)).Take(32).ToArray();
                try { if (recovering) await RefreshDeletedAsync(); else await RefreshMutationsAsync(); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { MutationMessage += " Recent history could not be refreshed."; }
                Notify(DesktopChange.Files);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        { if (current == generation) { MutationMessage = "The outcome is unavailable. Reconnect and review recent changes before repeating this action."; Notify(DesktopChange.Files); } throw; }
    }
    public async Task RenameSelectedAsync(string name)
    {
        if (SelectedFiles.Count != 1) throw new InvalidOperationException("Select one item to rename.");
        var entry = SelectedFiles[0].Entry; StorageRules.ValidateName(name);
        var result = await ChangeFileAsync(new() { Operation = FileOperation.Rename, NodeId = entry.Id, ExpectedVersion = entry.Version, Name = name });
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
        await BrowseAsync(Breadcrumbs.LastOrDefault());
    }
    public void BeginMove()
    { if (SelectedFiles.Count != 1 || SelectedFiles[0].Entry.IsRoot || (SelectedFiles[0].Entry.Permissions & (uint)StoragePermission.Move) == 0) throw new InvalidOperationException("Select one movable item."); PendingMove = SelectedFiles[0].Entry.Clone(); selectedFiles.Clear(); Notify(DesktopChange.Files); }
    public void CancelMove() { PendingMove = null; Notify(DesktopChange.Files); }
    public async Task MoveHereAsync()
    {
        var entry = PendingMove ?? throw new InvalidOperationException("Select an item to move first.");
        var folder = Breadcrumbs.LastOrDefault() ?? throw new InvalidOperationException("Open the destination folder.");
        var result = await ChangeFileAsync(new() { Operation = FileOperation.Move, NodeId = entry.Id, ExpectedVersion = entry.Version, DestinationId = folder.Id, Name = entry.Name });
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message);
        PendingMove = null; await BrowseAsync(folder);
    }
    public Task ClearActivity() => activityWriter.Clear();
    public string DownloadFile(FileEntry entry, string destination, DownloadCollision collision)
    {
        var peer = ActivePeerId; var requester = RequireClient().CaptureFileRequester(peer);
        return downloads.Start(peer, entry, destination, collision, (request, ct) => requester(request, ct));
    }
    public async Task<DesktopBatchPlan> PlanBatchAsync(IReadOnlyList<FileEntry> selection)
    {
        if (!PeerAuthenticated || Browsing) throw new InvalidOperationException("Connect to the phone and finish browsing before planning a transfer.");
        var peer = ActivePeerId; var current = generation; var requester = RequireClient().CaptureFileRequester(peer);
        var rows = selection.Select(entry => selectedFiles.GetValueOrDefault(entry.Id) ?? catalog.FirstOrDefault(x => x.Entry.Id == entry.Id)
            ?? throw new InvalidOperationException("A selected item is no longer loaded. Refresh it.")).ToArray();
        var items = ExplorerCatalog.NormalizeSelection(rows).Select(x => new TransferSelection(x.Entry, x.SourcePath)).ToArray();
        Browsing = true; Continuation = ""; Notify(DesktopChange.Files);
        try
        {
            var manifest = await TransferManifests.BuildAsync(items, (value, ct) => requester(value, ct), connectionLifetime!.Token);
            if (generation != current || peer != ActivePeerId || !PeerAuthenticated) throw new IOException("The connection changed. Plan this transfer again.");
            return new(peer, current, manifest);
        }
        finally { if (generation == current) { Browsing = false; Notify(DesktopChange.Files); } }
    }
    public string StartBatch(DesktopBatchPlan plan, string destination, DownloadCollision collision)
    {
        _ = RequirePeer(plan.PeerId);
        if (plan.Generation != generation || plan.PeerId != ActivePeerId || !PeerAuthenticated) throw new IOException("The connection changed. Review a new transfer plan.");
        var requester = RequireClient().CaptureFileRequester(plan.PeerId);
        var id = batches.Start(plan.PeerId, plan.Manifest, destination, collision, (value, ct) => requester(value, ct));
        Record("Transfers", "A selected file/folder batch was queued.", plan.PeerId); return id;
    }
    public void PauseBatch(string id) => batches.Pause(id);
    public Task CancelBatch(string id) => batches.Cancel(id);
    public bool CanRemoveBatch(string id) => batches.CanRemove(id);
    public void RemoveBatch(string id) => batches.Remove(id);
    public Task ResumeBatch(string id)
    {
        var job = batches.Jobs.Single(x => x.Id == id); _ = RequirePeer(job.PeerId);
        var requester = RequireClient().CaptureFileRequester(job.PeerId);
        return batches.Resume(id, (value, ct) => requester(value, ct));
    }
    public void PauseDownload(string id) => downloads.Pause(id);
    public Task CancelDownload(string id) => downloads.Cancel(id);
    public void RemoveDownloadRecord(string id) { downloads.RemoveRecord(id); Notify(DesktopChange.Transfers); }
    public Task ResumeDownload(string id)
    {
        var job = downloads.Jobs.Single(x => x.Id == id);
        WindowsDownloadManager.Request? request = null;
        if (job.Offset != job.Size || job.Hash.Length != 64)
        { _ = RequirePeer(job.PeerId); var captured = RequireClient().CaptureFileRequester(job.PeerId); request = (value, ct) => captured(value, ct); }
        else trust.RequireCurrentOwner();
        return downloads.Resume(id, request);
    }
    private void ClearFiles()
    {
        discoveryGeneration++; discovery?.Cancel(); discovery?.Dispose(); discovery = null;
        catalog = []; selectedFiles.Clear(); FilePage = 0; SearchQuery = DiscoveryNotice = ""; DiscoveryComplete = true;
        PendingMove = null; RecentMutations = []; MutationMessage = ""; RemoteDeleted = []; MoreRemoteDeleted = false; deletedOffset = 0;
        FilePreview.Clear(); Entries = []; Breadcrumbs = []; Continuation = FileError = ""; Browsing = false;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; StopPayloadPulse(); CancelDiagnosticReview(); activityBackups.Dispose(); receivingParts.Dispose(); await DisconnectAsync();
        await outgoingBatchSender.DisposeAsync(); await outgoingBatches.DisposeAsync(); await outgoing.DisposeAsync(); await LocalFiles.DisposeAsync(); await batches.DisposeAsync(); await downloads.DisposeAsync(); await downloadSources.DisposeAsync();
        TransferNetwork?.Dispose();
        await activityWriter.DisposeAsync(); grants.Activity = null;
        await policyGate.WaitAsync();
        try { identity.Dispose(); trust.Dispose(); }
        finally { policyGate.Release(); }
    }
}
