using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

internal sealed partial class NativeUploadEndpoint
{
    internal const string BatchFolder = "Native folder";
    internal const string BatchText = "Generated nested text.\nSalama! 文書\n";
    internal const string BatchUnicodeFile = "Habari-文書.txt";
    internal static int BatchBytes => PayloadBytes + Encoding.UTF8.GetByteCount(BatchText);
    private WindowsBatchSources? batchSources;
    private WindowsBatchSender? batchSender;
    private OutgoingBatchSend? batchCompleted;
    private string batchOriginal = "", batchOriginalHash = "", batchSeedHash = "";
    private int batchBegins;
    private bool batchLost;
    private bool batchPublishedChecked;
    private bool batchPublishedCanceled;
    private bool batchFolderMoveVerified;
    public bool ExerciseFolderMoveOnSend { get; init; }
    private readonly bool batchListing;

    public void PrepareBatchDataset()
    {
        if (batchOriginal.Length != 0) throw new InvalidOperationException("The batch dataset is already prepared.");
        batchOriginal = Path.Combine(Path.GetDirectoryName(source)!, BatchFolder);
        Directory.CreateDirectory(Path.Combine(batchOriginal, "Empty")); Directory.CreateDirectory(Path.Combine(batchOriginal, "Reports"));
        File.WriteAllText(Path.Combine(batchOriginal, "Reports", BatchUnicodeFile), BatchText, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(batchOriginal, "payload.bin"), RandomNumberGenerator.GetBytes(PayloadBytes));
        File.WriteAllBytes(Path.Combine(batchOriginal, "zero.bin"), []);
        var seed = Path.Combine(destination, BatchFolder); Directory.CreateDirectory(seed); File.WriteAllText(Path.Combine(seed, "owner.txt"), ExistingBytes);
        batchOriginalHash = Tree(batchOriginal).Hash; batchSeedHash = Tree(seed).Hash;
    }

    public string[] BatchResultNames => batchCompleted is null ? throw new InvalidOperationException("Finish the batch first.") :
        Enumerable.Range(0, 6).Select(i => batchSender!.ItemResult(batchCompleted.Id, i).Name).ToArray();

    private bool BatchCurrent(UploadAuthority value) => value == authority && trust.OwnerReady &&
        trust.TryGet(authority.Peer, out var peer) && peer.ApprovalEpoch == authority.Pairing;
    private void OpenBatchStores()
    {
        batchSources = new(profile, BatchCurrent, recoveryOwner: () => authority.Owner + "." + authority.OwnerEpoch);
        batchSender = new(profile, batchSources, BatchCurrent, localOwner: value => value == authority,
            recoveryOwner: () => authority.Owner + "." + authority.OwnerEpoch);
    }
    internal static (string Hash, int Items, long Bytes) Tree(string root, IReadOnlyDictionary<string, string>? names = null)
    {
        var lines = new List<(string Relative, string Line)>(); var items = 0; long bytes = 0;
        void Walk(string path, string relative, int depth)
        {
            if (++items > 16 || depth > 8 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unexpected or redirected fixture tree.");
            var logical = names is null ? relative : names.TryGetValue(relative, out var mapped) ? mapped : throw new InvalidDataException("Unexpected saved fixture item.");
            if (Directory.Exists(path))
            {
                lines.Add((logical, "D\t" + logical + "\n"));
                foreach (var entry in Directory.EnumerateFileSystemEntries(path).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    Walk(entry, relative.Length == 0 ? Path.GetFileName(entry) : relative + "/" + Path.GetFileName(entry), depth + 1);
            }
            else
            {
                var length = new FileInfo(path).Length; bytes += length;
                lines.Add((logical, "F\t" + logical + "\t" + length.ToString(CultureInfo.InvariantCulture) + "\t" + Digest(path) + "\n"));
            }
        }
        Walk(root, "", 0);
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines.OrderBy(x => x.Relative, StringComparer.Ordinal).Select(x => x.Line))))), items, bytes);
    }
    public async Task SendBatchAsync(PeerSession peer, Action<object> emit, CancellationToken ct)
    {
        if (batchSender is not null || batchOriginal.Length == 0 || !peer.AcceptsFolderBatches || !peer.AcceptsPublishedBatchChecks || !peer.AcceptsPublishedBatchCheckCancellation) throw new InvalidDataException("Prepare one dataset and use authenticated batch peers with cancellable destination checks.");
        var context = await peer.RequestFilesAsync(new() { Operation = FileOperation.UploadContext }, ct);
        if (context.Status != FileStatus.Ok || context.Entries.Count != 1) throw new InvalidDataException("Expected one receiving inbox.");
        target = context.Entries.Single();
        var roots = await peer.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, ct);
        if (target.Permissions != (batchListing ? 13 : 12) || roots.Status is not (FileStatus.Ok or FileStatus.AccessDenied) || roots.Entries.Count != (batchListing ? 1 : 0) || roots.Content.Length != 0)
            throw new InvalidDataException("The receiving inbox permits unexpected browsing.");
        OpenBatchStores();
        var sourceId = await batchSources!.PrepareAsync([batchOriginal], authority, UploadCollision.UploadKeepBoth, ct);
        var manifest = batchSources.ReadManifest(sourceId);
        if (manifest.Items.Count != 6 || Tree(batchOriginal) is not { Items: 6 } || batchSources.All.Single().Bytes != BatchBytes)
            throw new InvalidDataException("The real private folder snapshot differs from the six-item dataset.");
        var index = manifest.Items.Select((item, i) => (item, i)).Single(x => x.item.Name == "payload.bin").i;
        var id = batchSender!.Queue(sourceId);
        var trace = new List<object>();
        async Task<FileResponse> Request(FileRequest value, CancellationToken token)
        {
            if (value.Operation == FileOperation.BeginUploadBatch) batchBegins++;
            var response = await peer.RequestFilesAsync(value, token);
            trace.Add(new { operation = value.Operation.ToString(), index = value.BatchItemIndex, status = response.Status.ToString(), response.Message,
                phase = response.Upload?.Phase.ToString(), offset = response.Upload?.Offset });
            File.WriteAllText(Path.Combine(profile, "native-batch-trace.json"), System.Text.Json.JsonSerializer.Serialize(trace));
            if (value.Operation == FileOperation.WriteBatchFile && value.BatchItemIndex == index && !batchLost && response.Status == FileStatus.Ok)
            {
                if (response.Upload is not { Offset: StorageRules.MaxReadRange }) throw new InvalidDataException("The batch range was not actually accepted.");
                batchLost = true; throw new IOException("Generated loss of an accepted native batch range reply.");
            }
            return response;
        }
        await batchSender.SendAsync(id, target, Request, ct);
        var interrupted = batchSender.All.Single(); var header = UploadBatchHeader.Parser.ParseFrom(interrupted.Header);
        var accepted = await peer.RequestFilesAsync(new() { Operation = FileOperation.UploadBatchItemStatus, TransferId = header.Id,
            MutationEpoch = header.Epoch, BatchItemIndex = (uint)index }, ct);
        if (!batchLost || interrupted.Phase != BatchSendingPhase.Review || batchSender.ReadCheckpoint(id, index) != 0 ||
            accepted.Status != FileStatus.Ok || accepted.Upload.Offset != StorageRules.MaxReadRange || batchBegins != 1)
            throw new InvalidDataException($"The batch checkpoints differ: lost={batchLost}, next={interrupted.Next}, payload={index}, local={batchSender.ReadCheckpoint(id,index)}, remote={accepted.Upload?.Offset}, status={accepted.Status}, begins={batchBegins}.");
        emit(new { @event = "checkpoint", receipt = header.Id, senderOffset = 0, receiverOffset = accepted.Upload.Offset });
        var savedHeader = interrupted.Header!.ToArray();
        await batchSender.DisposeAsync(); await batchSources.DisposeAsync(); OpenBatchStores();
        if (!batchSender!.All.Single().Header!.SequenceEqual(savedHeader)) throw new InvalidDataException("Recreation changed the original parent intent.");
        await batchSender.SendAsync(id, null, Request, ct); batchCompleted = batchSender.All.Single();
        if (batchCompleted.Phase != BatchSendingPhase.Completed || batchCompleted.Next != 6 || batchBegins != 1 ||
            !batchCompleted.Header!.SequenceEqual(savedHeader) || batchCompleted.Outcomes?.Any(x => x != 1) != false ||
            !batchCompleted.Outcomes.SequenceEqual(batchCompleted.ReceiverOutcomes!) || Tree(batchOriginal).Hash != batchOriginalHash)
            throw new InvalidDataException("The exact parent plan or complete item outcomes were not retained.");
        await CancelBatchProofAsync(peer, ct);
        await VerifyBatchAsync(peer, ct); batchPublishedChecked = true;
        if (ExerciseFolderMoveOnSend) await VerifyFolderMoveAsync(peer, ct);
        emit(new { @event = "sent", sha256 = batchOriginalHash, bytes = BatchBytes, items = 6, preparations = batchBegins, publishedBatchChecked = batchPublishedChecked, publishedBatchCanceled = batchPublishedCanceled,
            originalFolderMoveVerified = batchFolderMoveVerified, lostAcknowledgements = 1, resumedOriginalReceipt = true, names = BatchResultNames });
    }
    public async Task VerifyFolderMoveAsync(PeerSession peer, CancellationToken ct)
    {
        if (batchCompleted is null || !peer.AcceptsPublishedBatchChecks || batchFolderMoveVerified)
            throw new InvalidOperationException("Finish one native folder batch before its reviewed original move.");
        using var management = new WindowsLocalManagement(profile);
        var review = batchSender!.ReviewFolderMove(batchCompleted.Id, batchOriginal, management);
        var result = await batchSender.FinishFolderMoveAsync(review, management, peer.RequestFilesAsync,
            () => !peer.IsClosed && BatchCurrent(authority), peer.AcceptsPublishedBatchCheckCancellation, ct);
        if (result.Phase != MutationPhase.ChangeSucceeded || Directory.Exists(batchOriginal) || batchSender.All.Single().SourceRecovered != true)
            throw new InvalidDataException("The native folder move did not retain a verified source recovery transaction.");
        var saved = management.Deleted.Single(x => x.Item.Id == result.Id);
        if (Tree(saved.SavedLocation).Hash != batchOriginalHash || management.Restore(result.Id).Phase != MutationPhase.ChangeSucceeded ||
            Tree(batchOriginal).Hash != batchOriginalHash)
            throw new InvalidDataException("The native folder original did not verify and restore with its complete tree.");
        batchFolderMoveVerified = true;
    }
    public Task VerifyBatchAsync(PeerSession peer, CancellationToken ct)
    {
        if (batchCompleted is null || !peer.AcceptsPublishedBatchChecks)
            throw new InvalidOperationException("Finish this batch with a peer that supports fresh whole-batch checks.");
        return batchSender!.VerifyPublishedAsync(batchCompleted.Id, peer.RequestFilesAsync, ct, peer.AcceptsPublishedBatchCheckCancellation);
    }
    private async Task CancelBatchProofAsync(PeerSession peer, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        FileRequest? original = null;
        async Task<FileResponse> Request(FileRequest value, CancellationToken token)
        {
            var response = await peer.RequestFilesAsync(value, token);
            if (value.Operation == FileOperation.VerifyPublishedBatch)
            {
                if (original is not null || response.Status != FileStatus.Ok || response.PublishedBatchCheck is not { Verified: false, Canceled: false })
                    throw new InvalidDataException("Expected one pending native batch check before cancellation.");
                original = value.Clone(); stopping.Cancel(); stopping.Token.ThrowIfCancellationRequested();
            }
            if (value.Operation != FileOperation.CancelPublishedBatchCheck || original is null || value.MutationId != original.MutationId ||
                value.TransferId != original.TransferId || value.MutationEpoch != original.MutationEpoch || !value.BatchHeader.Equals(original.BatchHeader))
                throw new InvalidDataException("Cancellation did not retain the exact native proof scope.");
            if (response.Status == FileStatus.Ok && response.PublishedBatchCheck is { Canceled: true, Verified: false }) batchPublishedCanceled = true;
            return response;
        }
        try
        {
            await batchSender!.VerifyPublishedAsync(batchCompleted!.Id, Request, stopping.Token, cancelReceiver: true);
            throw new InvalidDataException("Stopping the native check did not cancel its caller.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ct.IsCancellationRequested) { }
        if (!batchPublishedCanceled || batchSender!.All.Single().Phase != BatchSendingPhase.Completed)
            throw new InvalidDataException("The remote batch check did not acknowledge resource release with completed history retained.");
    }
    public void RevokeBatch()
    {
        if (revoked || batchCompleted is null) throw new InvalidDataException("Finish the original batch before withdrawing its inbox.");
        grants.Remove(grant.Id); revoked = true;
    }
    public async Task<object> CheckBatchAsync(PeerSession peer, IReadOnlyList<string> receivedNames, CancellationToken ct)
    {
        if (!revoked || batchCompleted is null || target is null) throw new InvalidDataException("Both batch inboxes must be withdrawn first.");
        var header = UploadBatchHeader.Parser.ParseFrom(batchCompleted.Header); header.Id = MutationRules.NewId();
        var denied = await peer.RequestFilesAsync(new() { Operation = FileOperation.BeginUploadBatch, TransferId = header.Id,
            MutationEpoch = header.Epoch, DestinationId = target.Id, BatchHeader = header }, ct);
        if (denied.Status is not (FileStatus.AccessDenied or FileStatus.StaleReference) || denied.Batch is not null || denied.Entries.Count != 0 || denied.Content.Length != 0)
            throw new InvalidDataException("A withdrawn inbox admitted another batch.");
        await grants.BatchPlans.WhenFilesIdleAsync().WaitAsync(ct);
        var plan = grants.BatchPlans.All.Single();
        var trees = Directory.GetDirectories(destination);
        if (plan.Phase != UploadBatchPlanPhase.UploadPlanCompleted || plan.Items != 6 || plan.Bytes != BatchBytes || trees.Length != 2 ||
            Tree(Path.Combine(destination, BatchFolder)).Hash != batchSeedHash || Tree(batchOriginal).Hash != batchOriginalHash)
            throw new InvalidDataException("The completed plan, collision tree or source changed.");
        var manifest = batchSources!.ReadManifest(batchCompleted.Source);
        if (receivedNames.Count != 6) throw new InvalidDataException("The counterpart did not retain all item names.");
        var physical = new string[6]; var logical = new string[6]; var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 6; i++)
        {
            var item = manifest.Items[i]; var name = receivedNames[i]; StorageRules.ValidateName(name);
            if (batchListing && i != 0 && name != item.Name || !batchListing && name == item.Name)
                throw new InvalidDataException("The saved names differ from the receiving inbox's collision and privacy policy.");
            var stem = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
            if (!batchListing && (!name.StartsWith(stem + " (", StringComparison.Ordinal) || !item.IsDirectory && Path.GetExtension(name) != Path.GetExtension(item.Name)))
                throw new InvalidDataException("Blind receiving changed the original name text or extension.");
            physical[i] = item.Parent < 0 ? "" : physical[item.Parent].Length == 0 ? name : physical[item.Parent] + "/" + name;
            logical[i] = item.Parent < 0 ? "" : logical[item.Parent].Length == 0 ? item.Name : logical[item.Parent] + "/" + item.Name;
            mapping.Add(physical[i], logical[i]);
        }
        var published = trees.Single(x => Path.GetFileName(x) != BatchFolder);
        if (Path.GetFileName(published) != receivedNames[0]) throw new InvalidDataException("The received root differs from its retained receipt.");
        var checkedTree = Tree(published, mapping);
        string Saved(int i) => Path.Combine(published, physical[i].Replace('/', Path.DirectorySeparatorChar));
        if (checkedTree.Items != 6 || checkedTree.Bytes != BatchBytes || Directory.EnumerateFileSystemEntries(Saved(1)).Any() ||
            new FileInfo(Saved(5)).Length != 0 || File.ReadAllText(Saved(3)) != BatchText)
            throw new InvalidDataException("The complete nested, Unicode, empty-directory and zero-byte outputs differ.");
        return new { @event = "verified", suite = "batches", sentHash = batchOriginalHash, receivedHash = checkedTree.Hash,
            bytes = BatchBytes, items = 6, preparations = batchBegins, lostAcknowledgements = 1, resumedOriginalReceipt = true,
            collisionOriginalKept = true, originalSourceKept = true, emptyDirectoriesKept = true, unicodeNameTextKept = true,
            zeroLengthFilesKept = true, parentOutcomesMatch = true, blindNamesRetained = !batchListing, withdrawnInboxDenied = true, publishedBatchChecked = batchPublishedChecked, publishedBatchCanceled = batchPublishedCanceled,
            originalFolderMoveVerified = batchFolderMoveVerified };
    }
}
