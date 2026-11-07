using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record BatchSenderResetReview(string Id, int Files, long Bytes, int UnrecognizedFiles);
public sealed record BatchSenderArchiveRemovalReview(string Id, string Archive, int Files, long Bytes, int UnrecognizedFiles);
public sealed record BatchSenderRecoveryArchive(string Id, bool Removing, int Removed, int ReviewedFiles);

public sealed partial class WindowsBatchSender
{
    private const string StoreName = "outgoing-batch-jobs", ArchivePrefix = "outgoing-batch-sender-backup-";
    private const int MaxRecoveryArchives = 8, ArchiveRemovalBatch = 64;
    private const int MaxRecoveryEntries = MaxRecords * (UploadBatchRules.MaxItems + 1) * 3 + 1;
    private const long MaxRecoveryBytes = 3L * MaxRecords * (ResultsLimit + RecordLimit);
    private sealed record RecoveryFile(string Name, string Version, long Size);
    private sealed record ResetReview(BatchSenderResetReview Value, string Owner, string Root, RecoveryFile[] Files, long Expires);
    private sealed record ArchiveReview(BatchSenderArchiveRemovalReview Value, string Owner, string Root, RecoveryFile[] Files, long Expires);
    private sealed class ArchiveDeletion(ArchiveReview review)
    { public ArchiveReview Review { get; } = review; public int Cursor { get; set; } }
    private readonly Dictionary<string, ResetReview> resetReviews = [];
    private readonly Dictionary<string, ArchiveReview> archiveReviews = [];
    private readonly Dictionary<string, ArchiveDeletion> archiveDeletions = [];
    private string[] recoveryArchives = [];

    public IReadOnlyList<BatchSenderRecoveryArchive> RecoveryArchives
    {
        get { lock (gate) return recoveryArchives.Select(id => archiveDeletions.TryGetValue(id, out var deletion)
            ? new BatchSenderRecoveryArchive(id, true, deletion.Cursor, deletion.Review.Files.Length)
            : new BatchSenderRecoveryArchive(id, false, 0, 0)).ToArray(); }
    }
    public string RecoveryArchiveLocation(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!recoveryArchives.Contains(id, StringComparer.Ordinal)) throw new IOException("This preserved sender store is no longer available.");
            return Path.Combine(Path.GetDirectoryName(directory)!, ArchivePrefix + id);
        }
    }
    private static SecureWindowsDirectory OpenLease(SecureWindowsDirectory parent, string name)
    {
        try { return parent.CreateFile(name); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { return parent.OpenWritableMetadataChild(name); }
    }
    private (SecureWindowsDirectory Root, SecureWindowsDirectory Lease) OpenStore()
    {
        SecureWindowsDirectory node;
        try { node = recoveryProfile.CreateDirectory(StoreName); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { node = recoveryProfile.OpenChild(StoreName, true); }
        using (node)
        {
            var opened = SecureWindowsDirectory.OpenRoot(directory);
            try
            {
                if (opened.Info.Identity != node.Info.Identity) throw new IOException("Sender storage changed while opening it.");
                return (opened, OpenLease(opened, "owner.lock"));
            }
            catch { opened.Dispose(); throw; }
        }
    }
    private string RecoveryOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ownerReady()) throw new InvalidOperationException("Review the current local identity before sender recovery.");
        var stamp = recoveryOwner?.Invoke() ?? "";
        if (stamp.Length != 108 || stamp[64] != '.' || !Hex(stamp[..64]) || !UploadBatchRules.CanonicalToken(stamp[65..]))
            throw new InvalidOperationException("A current local owner generation is required for sender recovery.");
        return stamp;
    }
    private static bool KnownRecoveryName(string name)
    {
        if (name == "owner.lock") return true;
        if (name.EndsWith(".new", StringComparison.Ordinal) || name.EndsWith(".bak", StringComparison.Ordinal)) name = name[..^4];
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var stem = name[..^5]; if (UploadBatchRules.CanonicalToken(stem)) return true;
        var split = stem.IndexOf('.');
        return split > 0 && UploadBatchRules.CanonicalToken(stem[..split]) && int.TryParse(stem[(split + 1)..], out var index) &&
            index >= 0 && index < UploadBatchRules.MaxItems && stem[(split + 1)..] == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private RecoveryFile[] RecoveryInventory(SecureWindowsDirectory folder, bool active)
    {
        var names = folder.EnumerateNames(default).Take(MaxRecoveryEntries + 1).ToArray();
        if (names.Length > MaxRecoveryEntries || names.Any(x => x.IsDirectory))
            throw new IOException("The sender store contains unexpected folders or too many entries. Everything was kept for local inspection.");
        var files = new List<RecoveryFile>(names.Length); long bytes = 0;
        foreach (var entry in names.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            SecureWindowsDirectory.Snapshot info;
            if (active && entry.Name == "owner.lock") info = SecureWindowsDirectory.Inspect(lease.Handle);
            else { using var file = folder.OpenManagementChild(entry.Name, false); info = file.Info; }
            bytes = checked(bytes + info.Size); if (bytes > MaxRecoveryBytes) throw new IOException("The sender store exceeds its recovery size limit. Its files were kept.");
            files.Add(new(entry.Name, info.Version, info.Size));
        }
        return files.ToArray();
    }
    private void RefreshRecoveryArchives()
    {
        var names = recoveryProfile.EnumerateNames(default).Where(x => x.Name.StartsWith(ArchivePrefix, StringComparison.Ordinal)).Take(MaxRecoveryArchives + 1).ToArray();
        if (names.Length > MaxRecoveryArchives) throw new IOException("Too many preserved sender stores. Review the private profile.");
        foreach (var entry in names)
        {
            if (!entry.IsDirectory || !UploadBatchRules.CanonicalToken(entry.Name[ArchivePrefix.Length..])) throw new IOException("An unrecognized preserved sender store was kept.");
            using var archive = recoveryProfile.OpenManagementChild(entry.Name, true);
        }
        recoveryArchives = names.Select(x => x.Name[ArchivePrefix.Length..]).Order(StringComparer.Ordinal).ToArray();
    }
    private SecureWindowsDirectory OpenRecoveryArchive(string id, bool mutation = false)
    {
        if (!UploadBatchRules.CanonicalToken(id) || !recoveryArchives.Contains(id, StringComparer.Ordinal)) throw new IOException("This preserved sender store is no longer available.");
        return recoveryProfile.OpenManagementChild(ArchivePrefix + id, true, mutation);
    }
    private static long ReviewExpiry() => Environment.TickCount64 + 120_000;
    private void EnsureRecoveryStore()
    {
        if (root.Handle.IsClosed || lease.Handle.IsClosed) { lease.Dispose(); root.Dispose(); (root, lease) = OpenStore(); }
    }
    public BatchSenderResetReview ReviewStoreReset()
    {
        lock (gate)
        {
            var owner = RecoveryOwner();
            if (!recovery.Needed || work.Count != 0) throw new IOException("Pause batch sending and review the blocked sender store first.");
            EnsureRecoveryStore(); RefreshRecoveryArchives();
            if (recoveryArchives.Length >= MaxRecoveryArchives) throw new IOException("Review and remove a preserved sender store before making another archive.");
            var files = RecoveryInventory(root, true);
            var value = new BatchSenderResetReview(ProtocolRules.NewToken(), files.Count(x => x.Name != "owner.lock"), files.Sum(x => x.Size), files.Count(x => !KnownRecoveryName(x.Name)));
            resetReviews.Clear(); resetReviews.Add(value.Id, new(value, owner, root.Info.Identity, files, ReviewExpiry())); return value;
        }
    }
    public string ConfirmStoreReset(BatchSenderResetReview confirmation)
    {
        lock (gate)
        {
            var owner = RecoveryOwner();
            if (!resetReviews.Remove(confirmation.Id, out var review) || review.Value != confirmation || review.Owner != owner || review.Expires < Environment.TickCount64 || work.Count != 0 || !recovery.Needed)
                throw new IOException("The sender recovery review changed or expired. Review it again.");
            EnsureRecoveryStore(); RefreshRecoveryArchives();
            if (recoveryArchives.Length >= MaxRecoveryArchives || root.Info.Identity != review.Root || !RecoveryInventory(root, true).SequenceEqual(review.Files))
                throw new IOException("The sender store changed after review. Its files were kept.");
            var archiveId = ProtocolRules.NewToken();
            if (sources.RecoveryIssues.Count == 0)
                foreach (var source in sources.All.Where(x => x.Phase == BatchSourcePhase.Ready)) sources.NoteReceiverIntent(source.Id, source.Authority, source.Hash);
            if (RecoveryOwner() != owner) throw new IOException("The local owner changed during recovery review. Its files were kept.");
            try
            {
                // The profile-wide lease stays held while the directory's own lease is released.
                lease.Dispose(); root.Dispose();
                using (var old = recoveryProfile.OpenManagementChild(StoreName, true, mutation: true))
                {
                    if (old.Info.Identity != review.Root || !RecoveryInventory(old, false).SequenceEqual(review.Files)) throw new IOException("The sender store changed before preservation.");
                    old.RenameTo(recoveryProfile, ArchivePrefix + archiveId);
                }
                (root, lease) = OpenStore(); rows.Clear(); versions.Clear(); resultBytes.Clear(); historyReviews.Clear();
                recovery = new(directory); RefreshRecoveryArchives(); NotifyChanged(); return archiveId;
            }
            catch
            {
                rows.Clear(); versions.Clear(); resultBytes.Clear(); historyReviews.Clear();
                recovery.Block("Sender recovery was interrupted. Preserved stores and private snapshots were kept; restore access and review recovery again.");
                try { EnsureRecoveryStore(); RefreshRecoveryArchives(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { }
                NotifyChanged(); throw;
            }
        }
    }
    public BatchSenderArchiveRemovalReview ReviewArchiveRemoval(string id)
    {
        lock (gate)
        {
            var owner = RecoveryOwner(); RefreshRecoveryArchives();
            if (archiveDeletions.TryGetValue(id, out var active))
            {
                if (active.Review.Owner == owner) throw new IOException("Continue the already reviewed archive removal first.");
                archiveDeletions.Remove(id);
            }
            using var archive = OpenRecoveryArchive(id); var files = RecoveryInventory(archive, false);
            var value = new BatchSenderArchiveRemovalReview(ProtocolRules.NewToken(), id, files.Length, files.Sum(x => x.Size), files.Count(x => !KnownRecoveryName(x.Name)));
            archiveReviews.Remove(id); archiveReviews[id] = new(value, owner, archive.Info.Identity, files, ReviewExpiry()); return value;
        }
    }
    public bool ConfirmArchiveRemoval(BatchSenderArchiveRemovalReview confirmation)
    {
        lock (gate)
        {
            var owner = RecoveryOwner();
            if (!archiveReviews.Remove(confirmation.Archive, out var review) || review.Value != confirmation || review.Owner != owner || review.Expires < Environment.TickCount64)
                throw new IOException("The archive removal review changed or expired. Review it again.");
            using (var archive = OpenRecoveryArchive(confirmation.Archive))
                if (archive.Info.Identity != review.Root || !RecoveryInventory(archive, false).SequenceEqual(review.Files)) throw new IOException("The preserved sender store changed. Its files were kept.");
            archiveDeletions.Add(confirmation.Archive, new(review)); return ContinueArchiveRemoval(confirmation.Archive);
        }
    }
    public bool ContinueArchiveRemoval(string id)
    {
        lock (gate)
        {
            var owner = RecoveryOwner();
            if (!archiveDeletions.TryGetValue(id, out var deletion) || deletion.Review.Owner != owner)
            {
                archiveDeletions.Remove(id); NotifyChanged();
                throw new IOException("Review the remaining archive files under the current local identity.");
            }
            using var archive = OpenRecoveryArchive(id, mutation: true);
            if (archive.Info.Identity != deletion.Review.Root)
            { archiveDeletions.Remove(id); NotifyChanged(); throw new IOException("The preserved sender store changed. Its remaining files were kept."); }
            var end = Math.Min(deletion.Review.Files.Length, deletion.Cursor + ArchiveRemovalBatch);
            try
            {
                while (deletion.Cursor < end)
                {
                    var expected = deletion.Review.Files[deletion.Cursor];
                    using (var file = archive.OpenManagementChild(expected.Name, false, mutation: true))
                    { if (file.Info.Version != expected.Version) throw new IOException("An archived file changed. Its remaining files were kept."); file.Delete(); }
                    deletion.Cursor++;
                }
                if (deletion.Cursor != deletion.Review.Files.Length) { NotifyChanged(); return false; }
                if (archive.EnumerateNames(default).Any()) throw new IOException("Reviewed files were removed; additional archive entries were kept. Review the remaining store again.");
                archive.Delete(); archive.Dispose(); archiveDeletions.Remove(id); RefreshRecoveryArchives(); NotifyChanged(); return true;
            }
            catch { archiveDeletions.Remove(id); NotifyChanged(); throw; }
        }
    }
}
