using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record BatchSourceResetReview(string Id, int Files, int Directories, long Bytes, int UnrecognizedEntries);
public sealed record BatchSourceArchiveRemovalReview(string Id, string Archive, int Files, int Directories, long Bytes, int UnrecognizedEntries);
public sealed record BatchSourceRecoveryArchive(string Id, bool Removing, int Removed, int ReviewedEntries);
public sealed record BatchSourceArchiveEntry(string Path, bool Directory, long Bytes, bool Recognized);

public sealed partial class WindowsBatchSources
{
    private const string SourceStoreName = "outgoing-batch-copies", SourceArchivePrefix = "outgoing-batch-source-backup-";
    private const int MaxSourceArchives = 8, SourceRemovalBatch = 64;
    private const int MaxSourceEntries = UploadBatchRules.MaxPlans * (UploadBatchRules.MaxItems * 2 + 4) * 3 + 1;
    private const long MaxSourceBytes = UploadRules.PeerQuota * 4 + 32L * 1024 * 1024;
    private readonly SecureWindowsDirectory recoveryProfile, profileLease;
    private readonly Func<string>? recoveryOwner;
    private sealed record PreservedEntry(string Parent, string Name, bool Directory, string Version, long Bytes)
    { public string Path => Parent.Length == 0 ? Name : Parent + "/" + Name; }
    private sealed record SourceReset(BatchSourceResetReview Value, string Owner, string Root, PreservedEntry[] Entries, long Expires);
    private sealed record SourceArchiveReview(BatchSourceArchiveRemovalReview Value, string Owner, string Root, PreservedEntry[] Entries, long Expires);
    private sealed class SourceDeletion(SourceArchiveReview review)
    { public SourceArchiveReview Review { get; } = review; public int Cursor { get; set; } }
    private SourceReset? sourceReset;
    private readonly Dictionary<string, SourceArchiveReview> sourceArchiveReviews = [];
    private readonly Dictionary<string, SourceDeletion> sourceDeletions = [];
    private string[] sourceArchiveIds = [];
    private long retainedSourceBytes;
    private bool recoveryCopyReview;
    private const string CopyReviewMarker = ".outgoing-batch-source-copy-review";
    private void LoadSourceCopyReview()
    {
        var found = recoveryProfile.EnumerateNames(default).Any(x => x.Name == CopyReviewMarker);
        if (found) { using var marker = recoveryProfile.OpenChild(CopyReviewMarker); if (marker.Info.Size != 0) throw new IOException("Private snapshot copy review history changed."); recoveryCopyReview = true; }
        else if (sourceArchiveIds.Length != 0) EnsureSourceCopyReview();
    }
    private void EnsureSourceCopyReview()
    {
        using var marker = SourceOpenLease(recoveryProfile, CopyReviewMarker);
        if (marker.Info.Size != 0) throw new IOException("Private snapshot copy review history changed. Its contents were kept.");
        marker.Flush(); recoveryCopyReview = true;
    }

    public IReadOnlyList<BatchSourceRecoveryArchive> RecoveryArchives
    {
        get { lock (gate) return sourceArchiveIds.Select(id => sourceDeletions.TryGetValue(id, out var deletion)
            ? new BatchSourceRecoveryArchive(id, true, deletion.Cursor, deletion.Review.Entries.Length) : new(id, false, 0, 0)).ToArray(); }
    }
    public string RecoveryArchiveLocation(string id)
    {
        lock (gate) { _ = SourceRecoveryOwner(); using var archive = OpenSourceArchive(id); return Path.Combine(Path.GetDirectoryName(directory)!, SourceArchivePrefix + id); }
    }
    private static SecureWindowsDirectory SourceOpenLease(SecureWindowsDirectory parent, string name)
    {
        try { return parent.CreateFile(name); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { return parent.OpenWritableMetadataChild(name); }
    }
    private (SecureWindowsDirectory, SecureWindowsDirectory) OpenSourceStore()
    {
        SecureWindowsDirectory node;
        try { node = recoveryProfile.CreateDirectory(SourceStoreName); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { node = recoveryProfile.OpenChild(SourceStoreName, true); }
        using (node)
        {
            var opened = SecureWindowsDirectory.OpenRoot(directory);
            try
            {
                if (opened.Info.Identity != node.Info.Identity) throw new IOException("Private snapshot storage changed while opening it.");
                return (opened, SourceOpenLease(opened, "owner.lock"));
            }
            catch { opened.Dispose(); throw; }
        }
    }
    private void EnsureSourceStore()
    { if (root.Handle.IsClosed || lease.Handle.IsClosed) { lease.Dispose(); root.Dispose(); (root, lease) = OpenSourceStore(); } }
    private string SourceRecoveryOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ownerReady()) throw new InvalidOperationException("Review the current local identity before private snapshot recovery.");
        var stamp = recoveryOwner?.Invoke() ?? "";
        if (stamp.Length != 108 || stamp[64] != '.' || !Hex(stamp[..64]) || !UploadBatchRules.CanonicalToken(stamp[65..]))
            throw new InvalidOperationException("A current local owner generation is required for private snapshot recovery.");
        return stamp;
    }
    private static bool KnownSourceEntry(PreservedEntry entry)
    {
        if (entry.Parent.Length == 0)
        {
            if (entry.Directory) return UploadBatchRules.CanonicalToken(entry.Name);
            if (entry.Name == "owner.lock") return true;
            var name = entry.Name;
            if (name.EndsWith(".new", StringComparison.Ordinal) || name.EndsWith(".bak", StringComparison.Ordinal)) name = name[..^4];
            return name.EndsWith(".json", StringComparison.Ordinal) && UploadBatchRules.CanonicalToken(name[..^5]);
        }
        if (!UploadBatchRules.CanonicalToken(entry.Parent) || entry.Directory) return false;
        if (entry.Name == "manifest.bin") return true;
        var suffix = Path.GetExtension(entry.Name); var number = Path.GetFileNameWithoutExtension(entry.Name);
        return suffix is ".bin" or ".json" && int.TryParse(number, out var index) && index >= 0 && index < UploadBatchRules.MaxItems && number == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private PreservedEntry[] SourceRecoveryInventory(SecureWindowsDirectory folder, bool active = false)
    {
        var entries = new List<PreservedEntry>(); long bytes = 0; var directories = 0;
        void Add(SecureWindowsDirectory parent, string path, SecureWindowsDirectory.DirectoryName name)
        {
            SecureWindowsDirectory.Snapshot info;
            if (active && path.Length == 0 && name.Name == "owner.lock") info = SecureWindowsDirectory.Inspect(lease.Handle);
            else { using var opened = parent.OpenManagementChild(name.Name, name.IsDirectory); info = opened.Info; }
            if (!info.IsDirectory) bytes = checked(bytes + info.Size);
            if (entries.Count >= MaxSourceEntries || bytes > MaxSourceBytes) throw new IOException("Private snapshot recovery exceeds its file or byte limit. Everything was kept.");
            entries.Add(new(path, name.Name, info.IsDirectory, info.IsDirectory ? info.Identity : info.Version, info.IsDirectory ? 0 : info.Size));
        }
        foreach (var name in folder.EnumerateNames(default).Take(MaxSourceEntries + 1).OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!name.IsDirectory) { Add(folder, "", name); continue; }
            if (++directories > UploadBatchRules.MaxPlans * 3) throw new IOException("Too many preserved snapshot folders. Everything was kept.");
            using (var plan = folder.OpenManagementChild(name.Name, true))
            {
                foreach (var child in plan.EnumerateNames(default).Take(MaxSourceEntries + 1).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (child.IsDirectory) throw new IOException("Unexpected nested snapshot folders were kept for local investigation.");
                    Add(plan, name.Name, child);
                }
                // Postorder permits bounded removal without descending into new or unreviewed folders.
                Add(folder, "", name);
            }
        }
        return entries.ToArray();
    }
    private void RefreshSourceArchives()
    {
        var names = recoveryProfile.EnumerateNames(default).Where(x => x.Name.StartsWith(SourceArchivePrefix, StringComparison.Ordinal)).Take(MaxSourceArchives + 1).ToArray();
        if (names.Length > MaxSourceArchives) throw new IOException("Too many preserved private snapshot stores. Review the private profile.");
        long bytes = 0;
        foreach (var name in names)
        {
            if (!name.IsDirectory || !UploadBatchRules.CanonicalToken(name.Name[SourceArchivePrefix.Length..])) throw new IOException("Unrecognized preserved private snapshots were kept.");
            using var archive = recoveryProfile.OpenManagementChild(name.Name, true);
            bytes = checked(bytes + SourceRecoveryInventory(archive).Sum(x => x.Bytes));
        }
        sourceArchiveIds = names.Select(x => x.Name[SourceArchivePrefix.Length..]).Order(StringComparer.Ordinal).ToArray(); retainedSourceBytes = bytes;
    }
    private void RefreshSourceAccounting()
    {
        try { RefreshSourceArchives(); inventory.Recover(Reservations(records.Values), recovery.Needed, retainedSourceBytes); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { inventory.Uncertain(); throw; }
    }
    private SecureWindowsDirectory OpenSourceArchive(string id, bool mutation = false)
    {
        if (!UploadBatchRules.CanonicalToken(id) || !sourceArchiveIds.Contains(id, StringComparer.Ordinal)) throw new IOException("This preserved private snapshot store is no longer available.");
        return recoveryProfile.OpenManagementChild(SourceArchivePrefix + id, true, mutation);
    }
    internal BatchSourceResetReview ReviewStoreReset()
    {
        lock (gate)
        {
            var actor = SourceRecoveryOwner();
            if (!recovery.Needed || work.Count != 0 || readers.Count != 0) throw new IOException("Stop private preparation and batch sending before reviewing the blocked snapshot store.");
            EnsureSourceStore(); RefreshSourceAccounting();
            if (sourceArchiveIds.Length >= MaxSourceArchives) throw new IOException("Review and remove a preserved snapshot store before preserving another.");
            var entries = SourceRecoveryInventory(root, true);
            var value = new BatchSourceResetReview(ProtocolRules.NewToken(), entries.Count(x => !x.Directory && !(x.Parent.Length == 0 && x.Name == "owner.lock")), entries.Count(x => x.Directory), entries.Sum(x => x.Bytes), entries.Count(x => !KnownSourceEntry(x)));
            sourceReset = new(value, actor, root.Info.Identity, entries, Environment.TickCount64 + 120_000); return value;
        }
    }
    internal string ConfirmStoreReset(BatchSourceResetReview confirmation)
    {
        lock (gate)
        {
            var actor = SourceRecoveryOwner(); var review = sourceReset; sourceReset = null;
            if (review is null || review.Value != confirmation || review.Owner != actor || review.Expires < Environment.TickCount64 || work.Count != 0 || readers.Count != 0 || !recovery.Needed)
                throw new IOException("The private snapshot recovery review changed or expired. Review it again.");
            EnsureSourceStore(); RefreshSourceAccounting();
            if (sourceArchiveIds.Length >= MaxSourceArchives || root.Info.Identity != review.Root || !SourceRecoveryInventory(root, true).SequenceEqual(review.Entries))
                throw new IOException("The private snapshot store changed after review. Everything was kept.");
            var id = ProtocolRules.NewToken();
            // Create-only empty marker: an interrupted creation is still conservative on restart.
            // It remains after archive removal, so preparing originals again keeps copy review.
            EnsureSourceCopyReview();
            try
            {
                lease.Dispose(); root.Dispose();
                using (var old = recoveryProfile.OpenManagementChild(SourceStoreName, true, mutation: true))
                {
                    if (SourceRecoveryOwner() != actor || old.Info.Identity != review.Root || !SourceRecoveryInventory(old).SequenceEqual(review.Entries))
                        throw new IOException("The private snapshots or local owner changed before preservation.");
                    old.RenameTo(recoveryProfile, SourceArchivePrefix + id);
                }
                (root, lease) = OpenSourceStore(); records.Clear(); versions.Clear(); manifests.Clear(); reviews.Clear(); recovery = new(directory);
                RefreshSourceAccounting(); NotifyChanged(); return id;
            }
            catch
            {
                records.Clear(); versions.Clear(); manifests.Clear(); reviews.Clear(); inventory.Uncertain();
                recovery.Block("Private snapshot recovery was interrupted. Preserved copies remain; restore access and review recovery again.");
                try { EnsureSourceStore(); RefreshSourceArchives(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { }
                NotifyChanged(); throw;
            }
        }
    }
    public IReadOnlyList<BatchSourceArchiveEntry> InspectRecoveryArchive(string id, int start)
    {
        lock (gate)
        {
            _ = SourceRecoveryOwner(); ArgumentOutOfRangeException.ThrowIfNegative(start);
            using var archive = OpenSourceArchive(id); var entries = SourceRecoveryInventory(archive);
            if (start > entries.Length) throw new ArgumentOutOfRangeException(nameof(start));
            return entries.Skip(start).Take(50).Select(x => new BatchSourceArchiveEntry(x.Path, x.Directory, x.Bytes, KnownSourceEntry(x))).ToArray();
        }
    }
    public BatchSourceArchiveRemovalReview ReviewArchiveRemoval(string id)
    {
        lock (gate)
        {
            var actor = SourceRecoveryOwner(); RefreshSourceAccounting();
            if (sourceDeletions.TryGetValue(id, out var deletion))
            {
                if (deletion.Review.Owner == actor) throw new IOException("Continue the already reviewed snapshot removal first.");
                sourceDeletions.Remove(id);
            }
            using var archive = OpenSourceArchive(id); var entries = SourceRecoveryInventory(archive);
            var value = new BatchSourceArchiveRemovalReview(ProtocolRules.NewToken(), id, entries.Count(x => !x.Directory), entries.Count(x => x.Directory), entries.Sum(x => x.Bytes), entries.Count(x => !KnownSourceEntry(x)));
            sourceArchiveReviews[id] = new(value, actor, archive.Info.Identity, entries, Environment.TickCount64 + 120_000); return value;
        }
    }
    public bool ConfirmArchiveRemoval(BatchSourceArchiveRemovalReview confirmation)
    {
        lock (gate)
        {
            var actor = SourceRecoveryOwner();
            if (!sourceArchiveReviews.Remove(confirmation.Archive, out var review) || review.Value != confirmation || review.Owner != actor || review.Expires < Environment.TickCount64)
                throw new IOException("The snapshot archive removal review changed or expired. Review it again.");
            using (var archive = OpenSourceArchive(confirmation.Archive))
                if (archive.Info.Identity != review.Root || !SourceRecoveryInventory(archive).SequenceEqual(review.Entries)) throw new IOException("The preserved private snapshots changed. Everything was kept.");
            sourceDeletions[confirmation.Archive] = new(review); return ContinueArchiveRemoval(confirmation.Archive);
        }
    }
    public bool ContinueArchiveRemoval(string id)
    {
        lock (gate)
        {
            var actor = SourceRecoveryOwner();
            if (!sourceDeletions.TryGetValue(id, out var deletion) || deletion.Review.Owner != actor)
            { sourceDeletions.Remove(id); NotifyChanged(); throw new IOException("Review the remaining private snapshots under the current local identity."); }
            using var archive = OpenSourceArchive(id, mutation: true);
            if (archive.Info.Identity != deletion.Review.Root) { sourceDeletions.Remove(id); throw new IOException("The preserved snapshot store changed. Its files were kept."); }
            var end = Math.Min(deletion.Review.Entries.Length, deletion.Cursor + SourceRemovalBatch);
            try
            {
                while (deletion.Cursor < end)
                {
                    if (SourceRecoveryOwner() != actor) throw new IOException("The local owner changed. Review the remaining private snapshots again.");
                    var entry = deletion.Review.Entries[deletion.Cursor];
                    using var parent = entry.Parent.Length == 0 ? null : archive.OpenManagementChild(entry.Parent, true);
                    if (parent is not null && parent.Info.Identity != deletion.Review.Entries.Single(x => x.Directory && x.Parent.Length == 0 && x.Name == entry.Parent).Version)
                        throw new IOException("A preserved snapshot folder changed. Remaining files were kept.");
                    using (var file = (parent ?? archive).OpenManagementChild(entry.Name, entry.Directory, mutation: true))
                    {
                        if ((entry.Directory ? file.Info.Identity : file.Info.Version) != entry.Version) throw new IOException("A preserved snapshot entry changed. It was kept.");
                        if (entry.Directory && file.EnumerateNames(default).Any()) throw new IOException("Additional snapshot entries were kept. Review the remaining files again.");
                        file.Delete();
                    }
                    deletion.Cursor++;
                }
                if (deletion.Cursor != deletion.Review.Entries.Length) { archive.Dispose(); RefreshSourceAccounting(); NotifyChanged(); return false; }
                if (archive.EnumerateNames(default).Any()) throw new IOException("Additional snapshot entries were kept. Review the remaining files again.");
                archive.Delete(); archive.Dispose(); sourceDeletions.Remove(id); RefreshSourceAccounting(); NotifyChanged(); return true;
            }
            catch
            {
                archive.Dispose();
                sourceDeletions.Remove(id);
                try { RefreshSourceAccounting(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { inventory.Uncertain(); }
                NotifyChanged(); throw;
            }
        }
    }
}

public sealed partial class WindowsBatchSender
{
    public BatchSourceResetReview ReviewSourceStoreReset()
    { lock (gate) { _ = RecoveryOwner(); if (work.Count != 0) throw new IOException("Pause batch sending before reviewing private snapshot recovery."); return sources.ReviewStoreReset(); } }
    public string ConfirmSourceStoreReset(BatchSourceResetReview review)
    { lock (gate) { _ = RecoveryOwner(); if (work.Count != 0) throw new IOException("Pause batch sending before preserving private snapshots."); return sources.ConfirmStoreReset(review); } }
}
