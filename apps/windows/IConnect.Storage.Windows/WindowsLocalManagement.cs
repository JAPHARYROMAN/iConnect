using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record LocalManagementReview(string Id, FileOperation Operation, string Name, string Source, string Destination, long ExpiresAt);
public sealed record LocalManagementChange(MutationInfo Receipt, string Source, string Destination);

/// <summary>Reviewed owner actions with no network host, pairing or sharing approval.</summary>
public sealed class WindowsLocalManagement : IDisposable
{
    private sealed record Catalog(int Schema, StorageGrant[] Roots);
    private sealed record Review(LocalManagementReview Value, FileRequest Request, StorageGrant Source, string[] Path,
        string Identity, bool Directory, StorageGrant Destination, long Expires);
    private sealed class Authority(WindowsLocalManagement owner) : IWindowsRecoveryAuthority
    {
        public event Action? Changed { add { } remove { } }
        public string OwnerScope => "local-file-management";
        public TimeProvider Clock => TimeProvider.System;
        public T WithAuthorization<T>(Func<T> action) => owner.Authorized(action);
        public IReadOnlyList<StorageGrant> ForPeer(string peer) => peer.Length == 0 ? owner.roots.Values.ToArray() : [];
        public StorageGrant Get(string id, string peer) => peer.Length == 0 && owner.roots.TryGetValue(id, out var root) ? root : throw new StorageException(FileStatus.AccessDenied, "This local directory reference is unavailable.");
        public void RequirePublicLocation(string canonicalPath) => owner.Public(canonicalPath);
        public void ValidateLocation(StorageGrant grant, IReadOnlyList<string> segments) => ValidateAccess(grant, segments);
        public void ValidateAccess(StorageGrant grant, IReadOnlyList<string> segments)
        {
            if (segments.Count > 128) throw new StorageException(FileStatus.Unsupported, "This location exceeds the management depth limit.");
            foreach (var part in segments) StorageRules.ValidateName(part);
            owner.Public(grant.CanonicalPath.TrimEnd('\\') + (segments.Count == 0 ? "" : "\\" + string.Join('\\', segments)));
        }
    }
    private readonly object gate = new();
    private readonly SecureWindowsDirectory root, lease;
    private readonly string volume, volumeIdentity, profileIdentity, privateProfile, recordsPath;
    private readonly string[] profileSegments;
    private readonly Authority authority;
    private readonly StateRecovery recovery;
    private readonly WindowsMutations mutations;
    private readonly WindowsRecovery deleted;
    private readonly Dictionary<string, StorageGrant> roots = [];
    private readonly Dictionary<string, Review> reviews = [];
    private string recordsVersion = "";
    private bool disposed;
    public event Action? Changed;
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public WindowsLocalManagement(string profile)
    {
        profile = Path.GetFullPath(profile); Directory.CreateDirectory(profile); var drive = Path.GetPathRoot(profile)!;
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0])) throw new IOException("Choose a normal local profile directory.");
        using (var opened = SecureWindowsDirectory.OpenRoot(drive)) { volume = opened.Info.CanonicalPath; volumeIdentity = opened.Info.Identity; }
        profileSegments = profile[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        using var ownerProfile = SecureWindowsDirectory.OpenPrivatePath(volume, volumeIdentity, profileSegments);
        privateProfile = ownerProfile.Info.CanonicalPath; profileIdentity = ownerProfile.Info.Identity;
        try { root = ownerProfile.CreateDirectory("local-management"); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { root = ownerProfile.OpenChild("local-management", true); }
        try
        {
            try { lease = root.CreateFile("owner.lock"); }
            catch (StorageException ex) when (ex.NativeError is 80 or 183) { lease = root.OpenWritableMetadataChild("owner.lock"); }
            recordsPath = Path.Combine(root.Info.CanonicalPath, "directories.json"); recovery = new(root.Info.CanonicalPath); authority = new(this);
            if (StateRecovery.Journals(root.Info.CanonicalPath).Any()) try
            {
                var saved = StateRecovery.Read<Catalog>(recordsPath, 8 * 1024 * 1024); if (saved.Schema != 1 || saved.Roots.Length > 512) throw new IOException("Invalid local management locations.");
                foreach (var item in saved.Roots)
                {
                    if (!ProtocolRules.TokenPattern().IsMatch(item.Id) || item.PeerId.Length != 0 || !SecureWindowsDirectory.IsLocalVolumePath(item.CanonicalPath) || item.RootIdentity.Length is < 1 or > 256 ||
                        item.DisplayPath.Length is < 3 or > 8192 || item.DisplayPath[1] != ':' || item.DisplayPath[2] != '\\' || item.Permissions != StoragePermission.Manage || item.Exclusions.Length != 0 || item.ExpiresAt is not null || !item.Enabled || !roots.TryAdd(item.Id, item))
                        throw new IOException("Invalid local management location.");
                }
                using var record = root.OpenChild("directories.json"); recordsVersion = record.Info.Version;
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { roots.Clear(); recovery.Add(recordsPath, "Local management directory references need review. Sources, operation history and recovery contents were kept; new changes are blocked."); }
            mutations = new(authority, root.Info.CanonicalPath, local: true); deleted = new(authority, root.Info.CanonicalPath, local: true);
            if (!recovery.Needed && mutations.RecoveryIssues.Count == 0 && mutations.ReferencedRoots().Any(id => !roots.ContainsKey(id)))
                recovery.Block("Local management directory references are missing. Restore the private profile before making new changes. Saved deleted items remain available for owner recovery.");
        }
        catch { lease?.Dispose(); root.Dispose(); throw; }
    }
    private T Authorized<T>(Func<T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var profile = SecureWindowsDirectory.OpenPrivatePath(volume, volumeIdentity, profileSegments);
            if (profile.Info.Identity != profileIdentity || profile.Info.CanonicalPath != privateProfile) throw new IOException("The private profile changed. Restart before managing local files.");
            return action();
        }
    }
    private void Public(string path)
    { if (SecureWindowsDirectory.IsWithin(path, privateProfile)) throw new StorageException(FileStatus.AccessDenied, "Private iConnect files and progress cannot be changed through the explorer."); }
    private void Healthy()
    {
        recovery.RequireHealthy();
        if (recordsVersion.Length == 0)
        { if (File.Exists(recordsPath) || File.Exists(recordsPath + ".new") || File.Exists(recordsPath + ".bak")) throw new IOException("Local directory references changed. Restart and review them before making another change."); }
        else { using var record = root.OpenChild("directories.json"); if (record.Info.Version != recordsVersion || File.Exists(recordsPath + ".new") || File.Exists(recordsPath + ".bak")) throw new IOException("Local directory references changed. Restart and review them before making another change."); }
        if (mutations.RecoveryIssues.Count != 0 || deleted.RecoveryIssues.Count != 0) throw new IOException("Review damaged local management history before making another change.");
    }
    private StorageGrant Remember(SecureWindowsDirectory folder, string display)
    {
        Public(folder.Info.CanonicalPath);
        var existing = roots.Values.FirstOrDefault(x => x.RootIdentity == folder.Info.Identity && x.CanonicalPath == folder.Info.CanonicalPath); if (existing is not null) return existing;
        if (roots.Count >= 512) throw new IOException("Local management directory references reached their limit. Review retained management history.");
        var value = new StorageGrant(ProtocolRules.NewToken(), "", "Local directory", display, folder.Info.CanonicalPath, folder.Info.Identity, StoragePermission.Manage, [], null);
        var next = new Catalog(1, roots.Values.Append(value).ToArray()); var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("Local directory references exceed their storage limit.");
        using (var output = new FileStream(recordsPath + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        AtomicStateFile.Commit(recordsPath + ".new", recordsPath); using (var record = root.OpenChild("directories.json")) recordsVersion = record.Info.Version;
        roots.Add(value.Id, value); return value;
    }
    private static SecureWindowsDirectory Selected(LocalCopySelection selected)
    {
        var opened = SecureWindowsDirectory.OpenPath(selected.Grant.CanonicalPath, selected.Grant.RootIdentity, selected.Segments, selected.IsDirectory);
        if (opened.Info.Identity != selected.Identity || opened.Info.Version != selected.Version || opened.Info.IsDirectory != selected.IsDirectory)
        { opened.Dispose(); throw new IOException("The selected item changed. Refresh and review it again."); }
        return opened;
    }
    private (StorageGrant Root, string[] Path) Source(LocalCopySelection selected)
    {
        if (selected.Segments.Length == 0) throw new IOException("Local shortcut roots and drives are protected. Open the item's parent folder to manage it.");
        using var item = Selected(selected); Public(item.Info.CanonicalPath);
        using var parent = SecureWindowsDirectory.OpenPath(selected.Grant.CanonicalPath, selected.Grant.RootIdentity, selected.Segments[..^1], true);
        return (Remember(parent, Path.GetDirectoryName(selected.SourceLocation)!), [selected.Segments[^1]]);
    }
    public LocalManagementReview ReviewRename(LocalCopySelection selected, string name) => Prepare(FileOperation.Rename, selected, null, name);
    public LocalManagementReview ReviewMove(LocalCopySelection selected, string destination, string? name = null) => Prepare(FileOperation.Move, selected, destination, name ?? selected.Name);
    public LocalMoveDestination InspectMoveDestination(LocalCopySelection selected, string destination) => Authorized(() =>
    {
        Healthy();
        using var original = Selected(selected);
        using var folder = StorageGrantStore.OpenLocalDestination(destination);
        Public(original.Info.CanonicalPath);
        Public(folder.Info.CanonicalPath);
        return new LocalMoveDestination(Path.GetFullPath(destination), folder.Info.CanonicalPath, folder.Info.Identity,
            !WindowsLocalCopies.SameVolume(original.Info.CanonicalPath, folder.Info.CanonicalPath));
    });

    internal MutationInfo RecoverVerifiedOriginal(string rootPath, string rootIdentity, string[] path, string display,
        string identity, DownloadJob copy, Action requireCurrent, CancellationToken cancellation) =>
        RecoverVerifiedSource(rootPath, rootIdentity, path, display, identity, copy.SourceVersion, false,
            original => WindowsDownloadManager.VerifyFile(original, copy, cancellation), requireCurrent, cancellation);

    internal MutationInfo RecoverVerifiedFolder(string parentPath, string parentIdentity, string name, string display,
        string identity, string version, Action<SecureWindowsDirectory> verifyTree, Action requireCurrent, CancellationToken cancellation) =>
        RecoverVerifiedSource(parentPath, parentIdentity, [name], display, identity, version, true, verifyTree, requireCurrent, cancellation);

    internal bool HasSourceRecovery(string id) => Authorized(() => deleted.ListLocal().Any(x => x.Id == id));

    internal MutationInfo RecoverVerifiedOutgoingFile(OutgoingMoveSource binding, SecureWindowsDirectory heldSource,
        string recoveryId, Action requireCurrent, CancellationToken cancellation) =>
        RecoverVerifiedSource(binding.ParentPath, binding.ParentIdentity, [binding.Name], binding.DisplayPath,
            binding.Identity, binding.Version, false, null, requireCurrent, cancellation, recoveryId, heldSource);

    internal MutationInfo RecoverVerifiedOutgoingFolder(OutgoingFolderMoveSource binding, SecureWindowsDirectory heldSource,
        string recoveryId, Action<SecureWindowsDirectory> verifyBefore, Action<SecureWindowsDirectory> verifySaved,
        Action requireCurrent, CancellationToken cancellation) =>
        RecoverVerifiedSource(binding.ParentPath, binding.ParentIdentity, [binding.Name], binding.DisplayPath,
            binding.Identity, binding.Version, true, verifyBefore, requireCurrent, cancellation, recoveryId, heldSource, verifySaved);

    private MutationInfo RecoverVerifiedSource(string rootPath, string rootIdentity, string[] path, string display,
        string identity, string version, bool directory, Action<SecureWindowsDirectory>? verifySource, Action requireCurrent,
        CancellationToken cancellation, string? recoveryId = null, SecureWindowsDirectory? heldSource = null,
        Action<SecureWindowsDirectory>? verifySavedSource = null) => Authorized(() =>
    {
        Healthy();
        using var parent = SecureWindowsDirectory.OpenPath(rootPath, rootIdentity, path[..^1], true);
        var source = Remember(parent, Path.GetDirectoryName(display)!);
        var request = new FileRequest
        {
            RequestId = ProtocolRules.NewToken(), Operation = FileOperation.Delete, NodeId = ProtocolRules.NewToken(),
            ExpectedVersion = version, MutationId = recoveryId ?? MutationRules.NewId(), MutationEpoch = Context(true)
        };
        var result = deleted.Delete("", request, () => new(source, [path[^1]], identity, directory), cancellation,
            verifySource is null ? null : original =>
            {
                requireCurrent();
                verifySource(original);
                requireCurrent();
            }, requireCurrent, directory ? saved =>
            {
                (verifySavedSource ?? verifySource!)(saved);
                requireCurrent();
            } : null, heldSource);
        WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalFileChange,
            result.Phase == MutationPhase.ChangeSucceeded ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.Review, Operation: FileOperation.Move));
        Notify();
        return result;
    });
    public LocalManagementReview ReviewDelete(LocalCopySelection selected) => Prepare(FileOperation.Delete, selected, null, selected.Name);
    public LocalManagementReview ReviewCreateFolder(LocalCopySelection selectedFolder, string name) => Prepare(FileOperation.CreateDirectory, selectedFolder, null, name);
    private LocalManagementReview Prepare(FileOperation operation, LocalCopySelection selected, string? destination, string name) => Authorized(() =>
    {
        Healthy(); StorageRules.ValidateName(name); StorageGrant source, target; string[] path;
        if (operation == FileOperation.CreateDirectory)
        {
            if (!selected.IsDirectory) throw new IOException("Open or select a destination folder first.");
            using var folder = Selected(selected); source = target = Remember(folder, selected.SourceLocation); path = [];
        }
        else
        {
            (source, path) = Source(selected); target = source;
            if (operation == FileOperation.Move) { using var folder = StorageGrantStore.OpenLocalDestination(destination!); target = Remember(folder, Path.GetFullPath(destination!)); }
        }
        authority.ValidateAccess(target, [name]);
        if (operation == FileOperation.Move)
        {
            if (!WindowsLocalCopies.SameVolume(source.CanonicalPath, target.CanonicalPath)) throw new IOException("Use the verified move workflow for files on different volumes.");
            if (selected.IsDirectory && SecureWindowsDirectory.IsWithin(target.CanonicalPath, source.CanonicalPath.TrimEnd('\\') + "\\" + path[0])) throw new IOException("A folder cannot be moved into itself.");
        }
        var context = Context(operation == FileOperation.Delete);
        var request = new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = operation, NodeId = operation == FileOperation.CreateDirectory ? "" : ProtocolRules.NewToken(),
            DestinationId = ProtocolRules.NewToken(), Name = name, ExpectedVersion = selected.Version, MutationId = MutationRules.NewId(), MutationEpoch = context };
        foreach (var expired in reviews.Where(x => x.Value.Expires < Environment.TickCount64).Select(x => x.Key).ToArray()) reviews.Remove(expired);
        if (reviews.Count >= 8) throw new IOException("Finish an earlier local management review first.");
        var value = new LocalManagementReview(ProtocolRules.NewToken(), operation, name, selected.SourceLocation, Path.Combine(target.DisplayPath, name), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
        reviews.Add(value.Id, new(value, request, source, path, selected.Identity, selected.IsDirectory, target, Environment.TickCount64 + 5 * 60_000)); return value;
    });
    private string Context(bool recoveryContext) => recoveryContext ? deleted.Query("", new() { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.RecoveryContext }).MutationEpoch :
        mutations.Query("", new() { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.MutationContext }).MutationEpoch;
    public MutationInfo Confirm(LocalManagementReview value, CancellationToken ct = default) => Authorized(() =>
    {
        Healthy(); if (!reviews.Remove(value.Id, out var review) || review.Value != value || review.Expires < Environment.TickCount64) throw new IOException("This local review expired, changed or was already used. Review the current item again.");
        ct.ThrowIfCancellationRequested(); var request = review.Request; MutationInfo result;
        if (request.Operation == FileOperation.Delete)
            result = deleted.Delete("", request, () => { using var item = OpenSource(review); if (review.Directory) Scan(review, ct); return new(review.Source, review.Path, item.Info.Identity, review.Directory); }, ct);
        else result = mutations.Execute("", request, () =>
        {
            using var destination = OpenRoot(review.Destination); authority.ValidateAccess(review.Destination, [request.Name]);
            if (request.Operation != FileOperation.CreateDirectory) { using var item = OpenSource(review); if (review.Directory) Scan(review, ct); }
            return new(request.MutationId, "", request.Operation, MutationRules.Fingerprint(request), request.Operation == FileOperation.CreateDirectory ? null : new(review.Source.Id, review.Source.RootIdentity, review.Path, review.Identity),
                new(review.Destination.Id, review.Destination.RootIdentity, [], destination.Info.Identity), request.Name, request.ExpectedVersion, request.Operation == FileOperation.CreateDirectory || review.Directory);
        }, () =>
        {
            ct.ThrowIfCancellationRequested(); using var destination = OpenRoot(review.Destination);
            if (request.Operation == FileOperation.CreateDirectory) { using var made = destination.CreateDirectory(request.Name); return new(request.Name, made.Info.Identity); }
            using var item = OpenSource(review, mutation: true); item.RenameTo(destination, request.Name); return new(request.Name, item.Info.Identity);
        });
        WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalFileChange, result.Phase == MutationPhase.ChangeSucceeded ? WindowsActivityOutcome.Completed : result.Phase == MutationPhase.ChangeFailed ? WindowsActivityOutcome.Refused : WindowsActivityOutcome.Review, Operation: request.Operation));
        Notify(); return result;
    });
    private static SecureWindowsDirectory OpenRoot(StorageGrant grant) => SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, [], true);
    private SecureWindowsDirectory OpenSource(Review review, bool mutation = false)
    {
        authority.ValidateAccess(review.Source, review.Path); var item = SecureWindowsDirectory.OpenPath(review.Source.CanonicalPath, review.Source.RootIdentity, review.Path, review.Directory, mutation: mutation, readData: false);
        if (item.Info.Identity != review.Identity || item.Info.Version != review.Request.ExpectedVersion) { item.Dispose(); throw new IOException("The reviewed item changed. Refresh and review it again."); } return item;
    }
    private void Scan(Review review, CancellationToken ct)
    {
        var pending = new Queue<string[]>(); pending.Enqueue(review.Path); var count = 0;
        while (pending.TryDequeue(out var path))
        {
            ct.ThrowIfCancellationRequested(); if (path.Length > review.Path.Length + 32 || ++count > 4096) throw new IOException("The folder exceeds the management scan bounds.");
            authority.ValidateAccess(review.Source, path); using var folder = SecureWindowsDirectory.OpenPath(review.Source.CanonicalPath, review.Source.RootIdentity, path, true);
            foreach (var child in folder.EnumerateNames(ct))
            {
                if (++count > 4096) throw new IOException("The folder exceeds 4,096 scanned entries."); var segments = path.Append(child.Name).ToArray(); authority.ValidateAccess(review.Source, segments);
                using var item = SecureWindowsDirectory.OpenPath(review.Source.CanonicalPath, review.Source.RootIdentity, segments, child.IsDirectory, readData: false); if (child.IsDirectory) pending.Enqueue(segments);
            }
        }
    }
    public IReadOnlyList<LocalManagementChange> History => Authorized(() => { Healthy(); return mutations.LocalRecords().OrderByDescending(x => MutationRules.CreatedAt(x.Id)).Select(x =>
        new LocalManagementChange(WindowsMutations.Info(x), x.Source is null ? "" : Path.Combine(roots[x.Source.Grant].DisplayPath, Path.Combine(x.Source.Path)), Path.Combine(roots[x.Destination.Grant].DisplayPath, Path.Combine(x.Destination.Path), x.Name))).ToArray(); });
    public IReadOnlyList<LocalRecoveryItem> Deleted => Authorized(() => deleted.ListLocalRecords());
    public MutationInfo Restore(string id, string name = "") => Authorized(() => { var result = deleted.RestoreLocal(id, name); RecoveryActivity(result.Phase == MutationPhase.ChangeSucceeded ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.Review); Notify(); return result; });
    public AlternateRestoreReview ReviewAlternateRestore(string id, string directory, string name) =>
        Authorized(() => deleted.PrepareAlternateRestoreLocal(id, directory, name));
    public MutationInfo ConfirmAlternateRestore(string token) => Authorized(() =>
    {
        var result = deleted.ConfirmAlternateRestoreLocal(token);
        RecoveryActivity(result.Phase == MutationPhase.ChangeSucceeded ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.Review);
        Notify(); return result;
    });
    public RecoveryReview ReviewPurge(string id) => Authorized(() => deleted.PreparePurgeLocal(id));
    public RecoveryPurgeResult Purge(string token, string confirmation) => Authorized(() => { var result = deleted.PurgeLocal(token, confirmation); RecoveryActivity(result.Complete ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.Review); Notify(); return result; });
    public RecoveryInspection InspectDeleted(string id) => Authorized(() => deleted.InspectLocal(id));
    public RecoveryVaultReview ReviewEmptyVault(string id) => Authorized(() => deleted.PrepareEmptyVaultLocal(id));
    public void RemoveEmptyVault(string token) => Authorized(() => { deleted.RemoveEmptyVaultLocal(token); Notify(); return true; });
    public string ArchiveAbsent(string token, string destination) => Authorized(() =>
    {
        var exported = deleted.ArchiveAbsentLocal(token, destination);
        Notify(); return exported;
    });
    public LocalRecoveryItem ConfirmLocations(string token) => Authorized(() => { var result = deleted.ConfirmLocationsLocal(token); Notify(); return result; });
    public RecoveryReview ReviewRemainingRestore(string id) => Authorized(() => deleted.PrepareRemainingRestoreLocal(id));
    public MutationInfo RestoreRemaining(string token, string name, string confirmation) => Authorized(() => { var result = deleted.RestoreRemainingLocal(token, name, confirmation); Notify(); return result; });
    public void Dismiss(string id) => Authorized(() => { deleted.DismissRestoredLocal(id); Notify(); return true; });
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues => Authorized(() => recovery.Issues.Select(x => x with { CanArchive = false }).Concat(mutations.RecoveryIssues).Concat(deleted.RecoveryIssues).ToArray());
    public int PreservedRecords => Authorized(() => recovery.ArchivedCount + mutations.PreservedRecords + new StateRecovery(Path.Combine(root.Info.CanonicalPath, "deleted-items")).ArchivedCount);
    public void Preserve(string id) => Authorized(() =>
    {
        if (recovery.Issues.Any(x => x.Id == id)) throw new IOException("Directory references must be restored before old local management history can be used. Files and saved recovery contents were kept.");
        if (mutations.RecoveryIssues.Any(x => x.Id == id)) mutations.ArchiveRecovery(id); else deleted.ArchiveRecovery(id); reviews.Clear(); Notify(); return true;
    });
    public void ClearPreserved() => Authorized(() => { mutations.ClearPreservedRecords(); new StateRecovery(Path.Combine(root.Info.CanonicalPath, "deleted-items")).ClearArchives(); Notify(); return true; });
    private void RecoveryActivity(WindowsActivityOutcome outcome) => WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalFileRecovery, outcome));
    private void Notify() { if (Changed is not { } listeners) return; foreach (Action listener in listeners.GetInvocationList()) try { listener(); } catch (Exception) { } }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; reviews.Clear(); lease.Dispose(); root.Dispose(); } }
}
