using System.Collections.Concurrent;
using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

// Content stays on its original volume. Only the bounded catalog lives in the owner profile.
// Remote requests only move or restore content. Permanent deletion requires a local owner review.
public sealed partial class WindowsRecovery
{
    internal sealed record Target(StorageGrant Grant, string[] Segments, string Identity, bool Directory);
    private sealed record Item(string Id, string Peer, string Owner, StorageGrant Grant, string[] Path,
        string ParentIdentity, string Identity, string Version, bool Directory, long Size, string Vault,
        string VaultIdentity, string Slot, DeletedState State = DeletedState.DeletePending,
        string RestoredName = "", string Message = "The outcome needs reconciliation.", PurgeRecord? Purge = null,
        RestoreLocation? RestoredDestination = null);
    private sealed record Receipt(string Id, string Peer, string Owner, FileOperation Operation, string Fingerprint,
        string Item, string Name, MutationPhase Phase = MutationPhase.ChangePending,
        FileStatus Status = FileStatus.Ok, string Message = "The outcome needs reconciliation.", RestoreLocation? Destination = null,
        bool RequiresSavedCheck = false);
    private sealed record Catalog(int Schema, string Epoch, Item[] Items, Receipt[] Receipts);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate;
    private readonly IWindowsRecoveryAuthority grants;
    private readonly bool local;
    private readonly string path;
    private readonly StateRecovery recovery;
    private Catalog catalog = new(1, "", [], []);

    internal WindowsRecovery(StorageGrantStore grants, string directory)
        : this((IWindowsRecoveryAuthority)grants, directory, false) { }
    internal WindowsRecovery(IWindowsRecoveryAuthority grants, string directory, bool local)
    {
        this.grants = grants; this.local = local; directory = System.IO.Path.Combine(directory, "deleted-items");
        System.IO.Directory.CreateDirectory(directory); path = System.IO.Path.Combine(directory, "catalog.json");
        gate = Gates.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new()); recovery = new(directory);
        lock (gate) { try { Load(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Damaged(); } }
    }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public void ArchiveRecovery(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (recovery.Issues.Any(x => x.Id == id))
            {
                recovery.Archive(id);
                if (!recovery.Needed) Save(new(1, ProtocolRules.NewToken(), [], []));
            }
            return true;
        }
    });
    private void Damaged() => recovery.Add(path, "Deleted-item metadata needs owner review. All recovery folders and their contents were kept. Archiving this catalog loses automatic restore links; preserve the archive for manual recovery.");
    private void Ready()
    {
        recovery.RequireHealthy();
        try { Load(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Damaged(); recovery.RequireHealthy(); }
    }
    private void Load()
    {
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("Incomplete recovery catalog.");
            Save(new(1, ProtocolRules.NewToken(), [], [])); return;
        }
        var loaded = StateRecovery.Read<Catalog>(path, 8 * 1024 * 1024);
        if (loaded.Schema != 1 || !Token(loaded.Epoch) || loaded.Items.Length > 512 || loaded.Receipts.Length > 512 ||
            loaded.Items.Select(x => x.Id).Distinct().Count() != loaded.Items.Length || loaded.Receipts.Select(x => x.Id).Distinct().Count() != loaded.Receipts.Length)
            throw new InvalidDataException("Invalid recovery catalog.");
        foreach (var item in loaded.Items)
        {
            if (!Token(item.Id) || !Owner(item.Peer) || item.Owner.Length is < 1 or > 128 || !Token(item.Grant.Id) || item.Grant.PeerId != item.Peer ||
                !SecureWindowsDirectory.IsLocalVolumePath(item.Grant.CanonicalPath) || !Identity(item.Grant.RootIdentity) ||
                item.Path.Length is < 1 or > 128 || !Identity(item.ParentIdentity) || !Identity(item.Identity) || !Hex(item.Version) ||
                item.Size < 0 || !Enum.IsDefined(item.State) || !Token(item.Slot) || item.Vault != ".iconnect-trash-" + item.Id || !Identity(item.VaultIdentity) ||
                item.Message.Length > 512 || item.Message.Any(char.IsControl) || item.Grant.Exclusions is null || item.Grant.Exclusions.Length > 512 ||
                item.Grant.DisplayPath.Length is < 3 or > 8192 || item.Grant.DisplayPath[1] != ':' || item.Grant.DisplayPath[2] != '\\') throw new InvalidDataException("Invalid recovery location.");
            var created = MutationRules.CreatedAt(item.Id);
            if (created is < 0 or > StorageRules.MaxTimestamp) throw new InvalidDataException("Invalid recovery timestamp.");
            foreach (var exclusion in item.Grant.Exclusions) foreach (var name in exclusion.Split('\\')) StorageRules.ValidateName(name);
            foreach (var name in item.Path) StorageRules.ValidateName(name);
            if (item.RestoredName.Length > 0) StorageRules.ValidateName(item.RestoredName);
            ValidatePurge(item.Purge);
            if (item.RestoredDestination is { } restored &&
                (item.RestoredName.Length == 0 || !ValidRestoreLocation(restored, item)))
                throw new InvalidDataException("Invalid local restore location.");
        }
        foreach (var receipt in loaded.Receipts)
        {
            var item = loaded.Items.SingleOrDefault(x => x.Id == receipt.Item && x.Peer == receipt.Peer && x.Owner == receipt.Owner);
            if (!Owner(receipt.Peer) || !Hex(receipt.Fingerprint) || receipt.Owner.Length is < 1 or > 128 || !RecoveryRules.IsChange(receipt.Operation) ||
                item is null || receipt.RequiresSavedCheck && (!local || !item.Directory || receipt.Operation != FileOperation.Delete) ||
                receipt.Destination is { } destination &&
                (receipt.Operation != FileOperation.Restore || !ValidRestoreLocation(destination, item)))
                throw new InvalidDataException("Invalid recovery receipt.");
            MutationRules.Validate(Info(receipt)); StorageRules.ValidateName(receipt.Name);
        }
        catalog = loaded;
    }
    private static bool Token(string value) => ProtocolRules.TokenPattern().IsMatch(value);
    private static bool Hex(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    private bool Owner(string value) => local ? value.Length == 0 : Hex(value);
    private static bool Identity(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl);
    private void Save(Catalog value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("The recovery catalog is full. Existing copies were kept.");
        using (var stream = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        AtomicStateFile.Commit(path + ".new", path); catalog = value;
    }
    private void Put(Item item, Receipt receipt) => Save(catalog with
    {
        Items = catalog.Items.Where(x => x.Id != item.Id).Append(item).ToArray(),
        Receipts = catalog.Receipts.Where(x => x.Id != receipt.Id).Append(receipt).ToArray()
    });
    private StorageGrant Authorize(Item item, bool restore)
    {
        if (restore && item.Purge is not null) throw new StorageException(FileStatus.AccessDenied, "Permanent deletion was attempted on this copy. Its remaining data requires local owner review.");
        if (item.Owner != grants.OwnerScope) throw new StorageException(FileStatus.AccessDenied, "This copy belongs to an earlier owner approval. Review it locally.");
        var current = grants.Get(item.Grant.Id, item.Peer);
        StorageRules.Require(current.Permissions, StoragePermission.Delete | (restore ? StoragePermission.Create : 0));
        if (current.RootIdentity != item.Grant.RootIdentity || current.CanonicalPath != item.Grant.CanonicalPath)
            throw new StorageException(FileStatus.StaleReference, "The original approved location changed.");
        grants.ValidateAccess(current, item.Path); return current;
    }
    private SecureWindowsDirectory Root(Item item)
    {
        grants.RequirePublicLocation(item.Grant.CanonicalPath);
        var root = SecureWindowsDirectory.OpenRoot(item.Grant.CanonicalPath);
        if (root.Info.Identity != item.Grant.RootIdentity) { root.Dispose(); throw new StorageException(FileStatus.StaleReference, "The original volume or folder changed."); }
        return root;
    }
    private static SecureWindowsDirectory Vault(Item item, SecureWindowsDirectory root, bool mutation = false)
    {
        var vault = root.OpenRecoveryDirectory(item.Vault, mutation: mutation);
        if (vault.Info.Identity != item.VaultIdentity) { vault.Dispose(); throw new StorageException(FileStatus.StaleReference, "The recovery folder changed. Review it locally."); }
        return vault;
    }
    private SecureWindowsDirectory Parent(Item item, StorageGrant approval)
    {
        grants.ValidateAccess(approval, item.Path[..^1]);
        var parent = SecureWindowsDirectory.OpenPath(approval.CanonicalPath, approval.RootIdentity, item.Path[..^1], true);
        if (parent.Info.Identity != item.ParentIdentity) { parent.Dispose(); throw new StorageException(FileStatus.StaleReference, "The original parent folder changed. The saved copy was kept."); }
        return parent;
    }
    private Receipt? Existing(string peer, FileRequest request)
    {
        if (request.MutationEpoch != catalog.Epoch) throw new StorageException(FileStatus.StaleReference, "Refresh deleted items before making a change.");
        MutationRules.RequireCurrent(request.MutationId, grants.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var receipt = catalog.Receipts.SingleOrDefault(x => x.Id == request.MutationId);
        if (receipt is not null)
        {
            if (receipt.Peer != peer || receipt.Owner != grants.OwnerScope || receipt.Fingerprint != MutationRules.Fingerprint(request))
                throw new StorageException(FileStatus.Conflict, "This identifier belongs to another operation.");
            var item = catalog.Items.Single(x => x.Id == receipt.Item); _ = Authorize(item, request.Operation == FileOperation.Restore);
            return Reconcile(item, receipt);
        }
        TrimReceipts(peer); return null;
    }
    private void TrimReceipts(string peer)
    {
        var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var kept = catalog.Receipts.Where(x => MutationRules.CreatedAt(x.Id) >= now - MutationRules.Lifetime || x.Phase == MutationPhase.ChangePending).ToArray();
        if (kept.Length >= 512 || kept.Count(x => x.Peer == peer) >= 128) throw new StorageException(FileStatus.ResourceExhausted, "Recovery operation history is full. Existing copies were kept.");
        catalog = catalog with { Receipts = kept };
    }
    internal MutationInfo Delete(string peer, FileRequest request, Func<Target> prepare, CancellationToken ct,
        Action<SecureWindowsDirectory>? verifySource = null, Action? requireCurrent = null, Action<SecureWindowsDirectory>? verifySaved = null,
        SecureWindowsDirectory? heldSource = null)
    {
        lock (gate)
        {
            Ready(); var prior = Existing(peer, request); if (prior is not null) return Info(prior);
            var target = prepare(); var grant = target.Grant; var segments = target.Segments; var identity = target.Identity; var directory = target.Directory;
            StorageRules.Require(grant.Permissions, StoragePermission.Delete);
            if (segments.Length == 0) throw new StorageException(FileStatus.AccessDenied, "Approved roots cannot be deleted.");
            grants.ValidateAccess(grant, segments);
            if (catalog.Items.Length >= 512 || catalog.Items.Count(x => x.Peer == peer && x.Owner == grants.OwnerScope) >= 128)
                throw new StorageException(FileStatus.ResourceExhausted, "The saved-item limit was reached. Restore saved items and dismiss their completed records before deleting more.");
            ct.ThrowIfCancellationRequested();
            using var opened = heldSource is null ? SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, segments,
                directory, mutation: true, readData: verifySource is not null) : null;
            var source = heldSource ?? opened!;
            var actual = heldSource is null ? source.Info : SecureWindowsDirectory.Inspect(source.Handle);
            if (actual.Identity != identity || actual.Version != request.ExpectedVersion || actual.IsDirectory != directory ||
                heldSource is not null && (!local || directory && (verifySource is null || verifySaved is null || requireCurrent is null) ||
                    !actual.CanonicalPath.Equals(Path.Combine(grant.CanonicalPath, Path.Combine(segments)), StringComparison.OrdinalIgnoreCase)))
                throw new StorageException(FileStatus.StaleReference, "The item changed. Refresh it before deleting.");
            verifySource?.Invoke(source);
            using var parent = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, segments[..^1], true);
            using var root = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, [], true);
            var name = ".iconnect-trash-" + request.MutationId;
            using var vault = root.OpenRecoveryDirectory(name, create: true);
            var item = new Item(request.MutationId, peer, grants.OwnerScope, grant, segments.ToArray(), parent.Info.Identity, identity,
                source.Info.Version, directory, source.Info.Size, name, vault.Info.Identity, ProtocolRules.NewToken());
            var receipt = new Receipt(request.MutationId, peer, grants.OwnerScope, FileOperation.Delete, MutationRules.Fingerprint(request), item.Id, segments[^1],
                RequiresSavedCheck: verifySaved is not null);
            Put(item, receipt); // A failed commit can leave an empty reserved folder; the source has not moved.
            try
            {
                requireCurrent?.Invoke();
                source.RenameTo(vault, item.Slot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { source.Dispose(); vault.Dispose(); return Info(Reconcile(item, receipt)); }
            var stored = item with { State = DeletedState.DeletedStored, Message = "Saved in iConnect recovery on the original volume. No automatic expiry." };
            var completed = receipt with { Phase = MutationPhase.ChangeSucceeded,
                Message = verifySaved is null ? "Moved to iConnect recovery." : "The folder copy was verified and the original is in recovery." };
            try { verifySaved?.Invoke(source); }
            catch (Exception ex) when (ex is OperationCanceledException || StateRecovery.IsRecordFailure(ex))
            {
                completed = SavedCheckUncertain(receipt);
                stored = stored with { Message = completed.Message };
            }
            try { Put(stored, completed); return Info(completed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { source.Dispose(); vault.Dispose(); return Info(Reconcile(item, receipt)); }
        }
    }
    internal MutationInfo Restore(string peer, FileRequest request, CancellationToken ct)
    {
        lock (gate)
        {
            Ready(); var prior = Existing(peer, request); if (prior is not null) return Info(prior);
            var item = catalog.Items.SingleOrDefault(x => x.Id == request.DeletedId && x.Peer == peer) ?? throw new StorageException(FileStatus.NotFound, "This saved item is unavailable.");
            var grant = Authorize(item, true);
            return RestoreCore(item, grant, request.Name, new(request.MutationId, peer, grants.OwnerScope, FileOperation.Restore,
                MutationRules.Fingerprint(request), item.Id, request.Name.Length == 0 ? item.Path[^1] : request.Name), ct);
        }
    }
    private MutationInfo RestoreCore(Item item, StorageGrant approval, string requestedName, Receipt receipt, CancellationToken ct,
        string? reviewedVersion = null)
    {
        if (item.State != DeletedState.DeletedStored) throw new StorageException(FileStatus.Conflict, "This copy needs review or was already restored.");
        var name = requestedName.Length == 0 ? item.Path[^1] : requestedName; StorageRules.ValidateName(name);
        var destination = receipt.Destination;
        var historicalPath = destination is null ? item.Path[..^1].Append(name).ToArray() : item.Path;
        grants.ValidateAccess(approval, historicalPath);
        using var root = Root(item); using var vault = Vault(item, root);
        using var parent = destination is null ? Parent(item, approval) : OpenRestoreParent(destination, useDisplayPath: true);
        using var source = vault.OpenRecoveryItem(item.Slot, item.Directory, mutation: true);
        if (source.Info.Identity != item.Identity || reviewedVersion is not null && source.Info.Version != reviewedVersion)
            throw new StorageException(FileStatus.StaleReference, "The saved item changed. Review it locally.");
        var targetPath = destination is null ? null : Path.Combine(destination.CanonicalPath, name);
        CheckTree(source, approval, historicalPath, ct, restoreTarget: targetPath);
        ct.ThrowIfCancellationRequested();
        item = item with { State = DeletedState.RestorePending, RestoredName = name, RestoredDestination = destination };
        Put(item, receipt);
        try
        {
            source.RenameTo(parent, name);
        }
        catch (StorageException ex) when (ex.NativeError is 80 or 183)
        {
            item = item with { State = item.Purge is null ? DeletedState.DeletedStored : DeletedState.DeletedReview, RestoredName = "", RestoredDestination = null, Message = "The destination already exists. Choose another name; both copies were kept." };
            receipt = receipt with { Phase = MutationPhase.ChangeFailed, Status = FileStatus.Conflict, Message = item.Message };
            Put(item, receipt); return Info(receipt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { source.Dispose(); return Info(Reconcile(item, receipt)); }
        var completed = receipt with { Phase = MutationPhase.ChangeSucceeded, Message = "Restored without replacing any existing item." };
        var restored = item with
        {
            State = DeletedState.DeletedRestored,
            Message = item.Purge is not null ? "Only the remaining saved data was restored. Earlier permanent deletion cannot be undone." :
                destination is null ? "Restored to its original parent folder." : "Restored to the folder reviewed on this computer."
        };
        try { Put(restored, completed); return Info(completed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { source.Dispose(); return Info(Reconcile(item, receipt)); }
    }
    private void CheckTree(SecureWindowsDirectory entry, StorageGrant approval, string[] path, CancellationToken ct,
        int depth = 0, int[]? visited = null, string? restoreTarget = null)
    {
        visited ??= [0]; ct.ThrowIfCancellationRequested();
        if (++visited[0] > 4096 || depth > 32) throw new StorageException(FileStatus.Unsupported, "This folder exceeds the safe recovery scan limit.");
        grants.ValidateAccess(approval, path);
        if (restoreTarget is not null) ValidateRestoreTarget(restoreTarget, approval);
        if (!entry.Info.IsDirectory) return;
        foreach (var child in entry.EnumerateNames(ct))
        {
            var next = path.Append(child.Name).ToArray(); grants.ValidateAccess(approval, next);
            using var opened = entry.OpenChild(child.Name, child.IsDirectory);
            CheckTree(opened, approval, next, ct, depth + 1, visited,
                restoreTarget is null ? null : Path.Combine(restoreTarget, child.Name));
        }
    }
    private Receipt Reconcile(Item item, Receipt receipt)
    {
        if (receipt.Phase != MutationPhase.ChangePending) return receipt;
        if (item.Purge is not null)
        {
            receipt = receipt with { Phase = MutationPhase.ChangeReview, Status = FileStatus.Conflict, Message = "Permanent deletion was attempted locally. Review remaining data on the owning computer." };
            Put(item, receipt); return receipt;
        }
        try
        {
            using var root = Root(item); using var vault = Vault(item, root);
            if (receipt.Operation == FileOperation.Delete)
            {
                using var saved = vault.OpenRecoveryItem(item.Slot, item.Directory);
                if (saved.Info.Identity == item.Identity)
                {
                    item = item with { State = DeletedState.DeletedStored, Message = receipt.RequiresSavedCheck
                        ? "The original is in recovery. Final folder verification needs review."
                        : "The saved copy was reconciled by file identity." };
                    receipt = receipt.RequiresSavedCheck ? SavedCheckUncertain(receipt) :
                        receipt with { Phase = MutationPhase.ChangeSucceeded, Message = "The recovery move was confirmed by file identity." };
                    Put(item, receipt); return receipt;
                }
            }
            else
            {
                using var parent = receipt.Destination is { } destination ? OpenRestoreParent(destination) : Parent(item, item.Grant);
                using var restored = parent.OpenChild(receipt.Name, item.Directory);
                if (restored.Info.Identity == item.Identity && (receipt.Destination is null || SavedSlotMissing(item, vault)))
                {
                    var confirmed = item with { State = DeletedState.DeletedRestored, RestoredName = receipt.Name, RestoredDestination = receipt.Destination,
                        Message = "The restored copy was reconciled by file identity." };
                    var completed = receipt with { Phase = MutationPhase.ChangeSucceeded, Message = "The restore was confirmed by file identity." };
                    Put(confirmed, completed); return completed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        item = item with { State = DeletedState.DeletedReview, Message = "The outcome is uncertain. Review the original folder and the saved recovery copy locally before another action." };
        receipt = receipt with { Phase = MutationPhase.ChangeReview, Status = FileStatus.Conflict, Message = item.Message };
        Put(item, receipt); return receipt;
    }
    private static Receipt SavedCheckUncertain(Receipt receipt) => receipt with
    {
        Phase = MutationPhase.ChangeReview, Status = FileStatus.Conflict,
        Message = "The original is in recovery. Final folder verification needs review."
    };
    internal FileResponse Query(string peer, FileRequest request)
    {
        lock (gate)
        {
            Ready(); var response = new FileResponse { RequestId = request.RequestId, MutationEpoch = catalog.Epoch };
            if (request.Operation == FileOperation.RecoveryContext) return response;
            if (request.MutationEpoch != catalog.Epoch) throw new StorageException(FileStatus.StaleReference, "The recovery catalog changed. Refresh it.");
            if (request.Operation == FileOperation.RecoveryStatus)
            {
                var receipt = catalog.Receipts.SingleOrDefault(x => x.Id == request.MutationId && x.Peer == peer && x.Owner == grants.OwnerScope && x.Destination is null)
                    ?? throw new StorageException(FileStatus.NotFound, "No recovery receipt is available.");
                var item = catalog.Items.Single(x => x.Id == receipt.Item); _ = Authorize(item, receipt.Operation == FileOperation.Restore);
                response.Mutation = Info(Reconcile(item, receipt)); return response;
            }
            if (request.Offset is < 0 or > 512) throw new StorageException(FileStatus.InvalidRequest, "Request a bounded recovery page.");
            var rows = catalog.Items.Where(x => x.Peer == peer && x.Owner == grants.OwnerScope && x.Purge?.Phase != PurgePhase.Complete).OrderByDescending(x => MutationRules.CreatedAt(x.Id)).ThenBy(x => x.Id).ToArray();
            foreach (var row in rows.Skip((int)request.Offset).Take(32))
            {
                try
                {
                    var approval = Authorize(row, false);
                    foreach (var pending in catalog.Receipts.Where(x => x.Item == row.Id && x.Phase == MutationPhase.ChangePending).ToArray()) _ = Reconcile(catalog.Items.Single(x => x.Id == row.Id), pending);
                    var current = catalog.Items.Single(x => x.Id == row.Id);
                    var info = Info(current);
                    if (current.RestoredDestination is not null) info.RestoredName = "";
                    info.CanRestore = info.CanRestore && (approval.Permissions & StoragePermission.Create) != 0;
                    response.DeletedItems.Add(info);
                }
                catch (StorageException) { }
            }
            response.EndOfFile = request.Offset + 32 >= rows.Length; return response;
        }
    }
    // Owner actions remain available after a peer or its grant is revoked. They still bind the original filesystem identities.
    public IReadOnlyList<DeletedInfo> ListLocal() => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready();
            foreach (var receipt in catalog.Receipts.Where(x => x.Phase == MutationPhase.ChangePending).ToArray()) _ = Reconcile(catalog.Items.Single(x => x.Id == receipt.Item), receipt);
            return (IReadOnlyList<DeletedInfo>)catalog.Items.OrderByDescending(x => MutationRules.CreatedAt(x.Id)).Select(Info).ToArray();
        }
    });
    public MutationInfo RestoreLocal(string id, string name = "") => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); TrimReceipts(catalog.Items.SingleOrDefault(x => x.Id == id)?.Peer ?? "");
            var item = catalog.Items.SingleOrDefault(x => x.Id == id) ?? throw new IOException("The saved item is unavailable.");
            if (item.Purge is not null) throw new IOException("Review and explicitly restore only the remaining data after a permanent-deletion attempt.");
            // Former peer exclusions remain in effect; local recovery never grants new remote authority.
            return RestoreCore(item, item.Grant, name, new(MutationRules.NewId(), item.Peer, item.Owner, FileOperation.Restore,
                new string('0', 64), item.Id, name.Length == 0 ? item.Path[^1] : name), default);
        }
    });
    public void DismissRestoredLocal(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var item = catalog.Items.SingleOrDefault(x => x.Id == id) ?? throw new IOException("The saved item is unavailable.");
            if (!Settled(item)) throw new IOException("Only restored or permanently deleted records can be dismissed. This control never deletes saved content.");
            if (RetainForReplay(item))
                throw new IOException("Keep this record for 24 hours after its last operation to prevent replay.");
            RemoveLocalRecord(item);
            return true;
        }
    });
    private bool RetainForReplay(Item item)
    {
        var cutoff = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds() - MutationRules.Lifetime;
        return MutationRules.CreatedAt(item.Id) >= cutoff ||
            item.Purge is not null && MutationRules.CreatedAt(item.Purge.Id) >= cutoff ||
            catalog.Receipts.Any(x => x.Item == item.Id &&
                (x.Phase == MutationPhase.ChangePending || MutationRules.CreatedAt(x.Id) >= cutoff));
    }
    private void RemoveLocalRecord(Item item) => Save(catalog with
    {
        Items = catalog.Items.Where(x => x.Id != item.Id).ToArray(),
        Receipts = catalog.Receipts.Where(x => x.Item != item.Id).ToArray()
    });
    private static DeletedInfo Info(Item item) => new() { Id = item.Id, Name = item.Path[^1], IsDirectory = item.Directory, Size = item.Size,
        DeletedAt = MutationRules.CreatedAt(item.Id), State = item.State, Message = item.Message, RestoredName = item.RestoredName, CanRestore = item.State == DeletedState.DeletedStored && item.Purge is null };
    private static MutationInfo Info(Receipt receipt) => new() { Id = receipt.Id, Operation = receipt.Operation, Phase = receipt.Phase,
        Status = receipt.Status, Message = receipt.Message, RequestedName = receipt.Name, CreatedAt = MutationRules.CreatedAt(receipt.Id),
        ResultingName = receipt.Phase == MutationPhase.ChangeSucceeded ? receipt.Name : "" };
}
