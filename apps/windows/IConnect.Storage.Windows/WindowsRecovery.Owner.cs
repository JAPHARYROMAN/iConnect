using System.Security.Cryptography;
using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record LocalRecoveryItem(DeletedInfo Item, string OriginalLocation, string SavedLocation, string RestoredLocation,
    string State, bool CanPurge, bool CanRestoreRemaining, bool CanDismiss);
public sealed record RecoveryReview(string Token, string Name, string OriginalLocation, string SavedLocation,
    int Files, int Folders, long Bytes, bool HasEarlierPurge, long ExpiresAt);
public sealed record RecoveryInspection(string Token, LocalRecoveryItem Item, string OriginalStatus, string SavedStatus,
    string RestoredStatus, bool CanConfirm, bool IsAbsent, bool CanArchiveAbsent);
public sealed record RecoveryPurgeResult(bool Complete, int RemovedEntries, string Message);
public sealed record AlternateRestoreReview(string Token, string SavedLocation, string Destination);
public sealed record RecoveryVaultReview(string? Token, string Location);

public sealed partial class WindowsRecovery
{
    private enum PurgePhase { Started, Review, Complete }
    private sealed record PurgeRecord(string Id, PurgePhase Phase, string Fingerprint, int Files, int Folders, long Bytes,
        int RemovedEntries = 0, bool EarlierAttempt = false);
    private enum ReviewKind { Purge, RemainingRestore, Locations, AlternateRestore, EmptyVault }
    private sealed record RestoreLocation(string CanonicalPath, string Identity, string DisplayPath);
    private sealed record RestorePlan(RestoreLocation Location, string Name);
    private enum Presence { Matches, Missing, Changed, Unavailable }
    private sealed record Probe(Presence State, string Identity = "", string Version = "");
    private sealed record Locations(Probe Original, Probe Saved, Probe Restored);
    private sealed record Node(int Parent, string Name, int Depth, string Identity, string Version, bool Directory, long Size, uint Attributes);
    private sealed record Review(ReviewKind Kind, string ItemId, string ItemStamp, string Owner, string Epoch, long Started,
        string? TreeStamp = null, Locations? Locations = null, RestorePlan? Restore = null);
    private sealed class CapturedTree : IDisposable
    {
        public readonly List<Node> Nodes = [];
        public readonly List<SecureWindowsDirectory> Entries = [];
        public readonly List<SecureWindowsDirectory> Ancestors = [];
        public void Dispose()
        { for (var i = Entries.Count - 1; i >= 0; i--) Entries[i].Dispose(); foreach (var item in Ancestors) item.Dispose(); }
    }
    private readonly Dictionary<string, Review> ownerReviews = [];
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(5);
    private static string Stamp<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void ValidatePurge(PurgeRecord? purge)
    {
        if (purge is null) return;
        var created = MutationRules.CreatedAt(purge.Id);
        if (!Enum.IsDefined(purge.Phase) || !Hex(purge.Fingerprint) || created is < 0 or > StorageRules.MaxTimestamp ||
            purge.Files < 0 || purge.Folders < 0 || (long)purge.Files + purge.Folders is < 1 or > 4096 ||
            purge.Bytes is < 0 or > StorageRules.MaxTransferSize || purge.RemovedEntries < 0 || purge.RemovedEntries > purge.Files + purge.Folders ||
            purge.Phase == PurgePhase.Complete && purge.RemovedEntries != purge.Files + purge.Folders)
            throw new InvalidDataException("Invalid permanent-deletion record.");
    }
    private static bool Settled(Item item) => item.State == DeletedState.DeletedRestored || item.Purge?.Phase == PurgePhase.Complete;
    private LocalRecoveryItem OwnerInfo(Item item)
    {
        var original = System.IO.Path.Combine(new[] { item.Grant.DisplayPath }.Concat(item.Path).ToArray());
        var saved = System.IO.Path.Combine(item.Grant.DisplayPath, item.Vault, item.Slot);
        var restored = item.RestoredName.Length == 0 ? "" : item.RestoredDestination is { } destination
            ? Path.Combine(destination.DisplayPath, item.RestoredName)
            : Path.Combine(new[] { item.Grant.DisplayPath }.Concat(item.Path[..^1]).Append(item.RestoredName).ToArray());
        var state = item.Purge?.Phase == PurgePhase.Complete ? "Permanently deleted" :
            item.State == DeletedState.DeletedRestored ? item.Purge is null ? "Restored or original confirmed" : "Remaining data restored" :
            item.Purge is not null ? "Permanent deletion needs review" : item.State == DeletedState.DeletedStored ? "Saved for recovery" : "Needs review";
        return new(Info(item), original, saved, restored, state,
            item.Purge?.Phase != PurgePhase.Complete && item.State is DeletedState.DeletedStored or DeletedState.DeletedReview,
            item.Purge is { Phase: not PurgePhase.Complete } && item.State is DeletedState.DeletedReview or DeletedState.DeletedStored,
            Settled(item));
    }
    public IReadOnlyList<LocalRecoveryItem> ListLocalRecords() => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            _ = ListLocal();
            return (IReadOnlyList<LocalRecoveryItem>)catalog.Items.OrderByDescending(x => MutationRules.CreatedAt(x.Id)).Select(OwnerInfo).ToArray();
        }
    });
    private Item LocalItem(string id) => catalog.Items.SingleOrDefault(x => x.Id == id) ?? throw new IOException("This saved item is unavailable.");
    private string AddReview(Item item, ReviewKind kind, string? treeStamp = null, Locations? locations = null, RestorePlan? restore = null)
    {
        foreach (var token in ownerReviews.Where(x => grants.Clock.GetElapsedTime(x.Value.Started) >= ReviewLifetime).Select(x => x.Key).ToArray()) ownerReviews.Remove(token);
        if (ownerReviews.Count >= 8) ownerReviews.Remove(ownerReviews.MinBy(x => x.Value.Started).Key);
        var id = ProtocolRules.NewToken(); ownerReviews.Add(id, new(kind, item.Id, Stamp(item), grants.OwnerScope, catalog.Epoch, grants.Clock.GetTimestamp(), treeStamp, locations, restore)); return id;
    }
    private (Item Item, Review Review) TakeReview(string token, ReviewKind kind)
    {
        if (!ownerReviews.Remove(token, out var review) || review.Kind != kind || grants.Clock.GetElapsedTime(review.Started) >= ReviewLifetime ||
            review.Owner != grants.OwnerScope || review.Epoch != catalog.Epoch)
            throw new IOException("This review expired or the owner profile changed. Review this item again.");
        var item = LocalItem(review.ItemId);
        if (Stamp(item) != review.ItemStamp) throw new IOException("This record changed after review. Review it again.");
        return (item, review);
    }
    private CapturedTree CaptureSaved(Item item, bool mutation)
    {
        var tree = new CapturedTree();
        try
        {
            var root = Root(item); tree.Ancestors.Add(root); var vault = Vault(item, root); tree.Ancestors.Add(vault);
            var source = vault.OpenRecoveryItem(item.Slot, item.Directory, mutation); tree.Entries.Add(source);
            if (source.Info.Identity != item.Identity) throw new StorageException(FileStatus.StaleReference, "The saved item was replaced. Its contents were kept.");
            long bytes = 0;
            void Visit(SecureWindowsDirectory current, int parent, string name, int depth, bool alreadyAdded)
            {
                if (!alreadyAdded) tree.Entries.Add(current);
                if (tree.Nodes.Count >= 4096 || depth > 32) throw new StorageException(FileStatus.Unsupported, "This saved tree exceeds 4,096 entries or 32 levels. Review it with local filesystem tools.");
                grants.RequirePublicLocation(current.Info.CanonicalPath);
                var index = tree.Nodes.Count; var snapshot = current.Info;
                tree.Nodes.Add(new(parent, name, depth, snapshot.Identity, snapshot.Version, snapshot.IsDirectory, snapshot.Size, snapshot.Attributes));
                if (!snapshot.IsDirectory)
                { bytes = checked(bytes + snapshot.Size); if (bytes > StorageRules.MaxTransferSize) throw new IOException("This saved tree exceeds the supported review size."); return; }
                // An extra name is bounded before sorting; a directory cannot force an unbounded materialization.
                var names = current.EnumerateNames(default).Take(4097 - tree.Nodes.Count).ToArray();
                if (names.Length + tree.Nodes.Count > 4096) throw new IOException("This saved tree exceeds the review entry limit.");
                foreach (var child in names.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    StorageRules.ValidateName(child.Name);
                    var opened = current.OpenManagementChild(child.Name, child.IsDirectory, mutation);
                    Visit(opened, index, child.Name, depth + 1, false);
                }
            }
            Visit(source, -1, item.Slot, 0, true); return tree;
        }
        catch { tree.Dispose(); throw; }
    }
    private RecoveryReview PrepareTreeReview(string id, ReviewKind kind) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var item = LocalItem(id); var local = OwnerInfo(item);
            if (kind == ReviewKind.Purge ? !local.CanPurge : !local.CanRestoreRemaining) throw new IOException("This saved item is not available for that action. Review its locations first.");
            using var tree = CaptureSaved(item, false); var token = AddReview(item, kind, Stamp(tree.Nodes));
            return new RecoveryReview(token, item.Path[^1], local.OriginalLocation, local.SavedLocation,
                tree.Nodes.Count(x => !x.Directory), tree.Nodes.Count(x => x.Directory), tree.Nodes.Where(x => !x.Directory).Sum(x => x.Size),
                item.Purge is not null, grants.Clock.GetUtcNow().Add(ReviewLifetime).ToUnixTimeMilliseconds());
        }
    });
    public RecoveryReview PreparePurgeLocal(string id) => PrepareTreeReview(id, ReviewKind.Purge);
    public RecoveryReview PrepareRemainingRestoreLocal(string id) => PrepareTreeReview(id, ReviewKind.RemainingRestore);
    private void RequireSettledVault(Item item)
    {
        if (!Settled(item) || catalog.Receipts.Any(x => x.Item == item.Id && x.Phase == MutationPhase.ChangePending))
            throw new IOException("Resolve the saved item and any pending operations before reviewing its recovery folder for removal.");
    }
    private void RequireEmptyVault(SecureWindowsDirectory vault)
    {
        grants.RequirePublicLocation(vault.Info.CanonicalPath);
        if (vault.EnumerateNames(default).Any() || vault.HasDataStreams())
            throw new IOException("The recovery folder contains entries or data streams. It was kept.");
    }
    public RecoveryVaultReview PrepareEmptyVaultLocal(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready();
            var item = LocalItem(id);
            RequireSettledVault(item);
            using var root = Root(item);
            SecureWindowsDirectory opened;
            try { opened = Vault(item, root); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound)
            { return new RecoveryVaultReview(null, Path.Combine(root.Info.CanonicalPath, item.Vault)); }
            using var vault = opened;
            RequireEmptyVault(vault);
            var token = AddReview(item, ReviewKind.EmptyVault, treeStamp: vault.Info.Version);
            return new RecoveryVaultReview(token, vault.Info.CanonicalPath);
        }
    });
    public void RemoveEmptyVaultLocal(string token) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready();
            var (item, review) = TakeReview(token, ReviewKind.EmptyVault);
            RequireSettledVault(item);
            using var root = Root(item);
            using (var vault = Vault(item, root, mutation: true))
            {
                RequireEmptyVault(vault);
                if (vault.Info.Version != review.TreeStamp)
                    throw new IOException("The recovery folder changed after review. Review it again.");
                // The native disposition also rejects a directory that acquired a child during the check.
                vault.Delete();
            }
            try { using var remaining = root.OpenRecoveryDirectory(item.Vault); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { return true; }
            throw new IOException("Windows accepted removal, but an object remains at the recorded path. Review the current location.");
        }
    });
    private static bool ValidRestoreLocation(RestoreLocation location, Item item) =>
        SecureWindowsDirectory.IsLocalVolumePath(location.CanonicalPath) &&
        WindowsLocalCopies.SameVolume(location.CanonicalPath, item.Grant.CanonicalPath) && Identity(location.Identity) &&
        location.DisplayPath.Length is >= 3 and <= 8192 && char.IsAsciiLetter(location.DisplayPath[0]) &&
        location.DisplayPath[1] == ':' && location.DisplayPath[2] == '\\' && !location.DisplayPath.Any(char.IsControl);
    private void ValidateRestoreTarget(string canonicalPath, StorageGrant historical)
    {
        var segments = canonicalPath[49..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (StorageGrantStore.IsSensitiveSegments(segments) || StorageGrantStore.IsSystemTree(segments))
            throw new StorageException(FileStatus.AccessDenied, "Choose a location outside private and operating-system folders.");
        grants.RequirePublicLocation(canonicalPath);
        var prefix = historical.CanonicalPath.TrimEnd('\\') + "\\";
        if (canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            grants.ValidateAccess(historical, canonicalPath[prefix.Length..].Split('\\'));
    }
    private SecureWindowsDirectory OpenRestoreParent(RestoreLocation location, bool useDisplayPath = false)
    {
        grants.RequirePublicLocation(location.CanonicalPath);
        var parent = useDisplayPath ? StorageGrantStore.OpenLocalDestination(location.DisplayPath) :
            SecureWindowsDirectory.OpenRoot(location.CanonicalPath);
        if (parent.Info.Identity == location.Identity &&
            parent.Info.CanonicalPath.Equals(location.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            return parent;
        parent.Dispose();
        throw new StorageException(FileStatus.StaleReference, "The reviewed destination folder changed. The saved copy was kept.");
    }
    private static bool SavedSlotMissing(Item item, SecureWindowsDirectory vault)
    {
        try { using var saved = vault.OpenRecoveryItem(item.Slot, item.Directory); return false; }
        catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { return true; }
    }
    public AlternateRestoreReview PrepareAlternateRestoreLocal(string id, string directory, string name) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var item = LocalItem(id);
            if (!Info(item).CanRestore) throw new IOException("Only an intact saved item is available for this restore.");
            StorageRules.ValidateName(name);
            var full = Path.GetFullPath(directory);
            using var parent = StorageGrantStore.OpenLocalDestination(full);
            var location = new RestoreLocation(parent.Info.CanonicalPath, parent.Info.Identity, full);
            if (!ValidRestoreLocation(location, item))
                throw new StorageException(FileStatus.Unsupported, "Choose a folder on the same local volume as the saved item.");
            ValidateRestoreTarget(Path.Combine(location.CanonicalPath, name), item.Grant);
            using var tree = CaptureSaved(item, false);
            var token = AddReview(item, ReviewKind.AlternateRestore, Stamp(tree.Nodes), restore: new(location, name));
            return new AlternateRestoreReview(token, OwnerInfo(item).SavedLocation, Path.Combine(full, name));
        }
    });
    public MutationInfo ConfirmAlternateRestoreLocal(string token) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var (item, review) = TakeReview(token, ReviewKind.AlternateRestore);
            var plan = review.Restore!;
            string reviewedVersion;
            using (var tree = CaptureSaved(item, false))
            {
                if (Stamp(tree.Nodes) != review.TreeStamp) throw new IOException("The saved contents changed after review. Review them again.");
                reviewedVersion = tree.Nodes[0].Version;
            }
            TrimReceipts(item.Peer);
            return RestoreCore(item, item.Grant, plan.Name, new(MutationRules.NewId(), item.Peer, item.Owner, FileOperation.Restore,
                new string('0', 64), item.Id, plan.Name, Destination: plan.Location), default, reviewedVersion);
        }
    });
    public string ExportLocalRecord(string id, string destination) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready();
            return WriteLocalExport(LocalItem(id), destination);
        }
    });
    public static string RecoveryExportName(string id) => "iConnect-Recovery-" + id + ".json";
    private string WriteLocalExport(Item item, string destination, Locations? absent = null)
    {
        using var folder = StorageGrantStore.OpenLocalDestination(destination);
        grants.RequirePublicLocation(folder.Info.CanonicalPath);
        if (!SecureWindowsDirectory.IsLocalVolumePath(folder.Info.CanonicalPath))
            throw new StorageException(FileStatus.Unsupported, "Choose a folder on a local Windows volume for this private export.");
        var name = RecoveryExportName(item.Id);
        var path = Path.Combine(Path.GetFullPath(destination), name);
        grants.RequirePublicLocation(Path.Combine(folder.Info.CanonicalPath, name));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1, ExportedAt = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Notice = "Private local recovery metadata; no file content or private device key. Paths and peer identifiers can be sensitive. This export does not authorize file operations.",
            Item = item, Receipts = catalog.Receipts.Where(x => x.Item == item.Id).ToArray(),
            AbsentAtRecordedLocations = absent is null ? null : new
            {
                Original = PresenceText(absent.Original), Saved = PresenceText(absent.Saved),
                Restored = item.RestoredName.Length == 0 ? "No restore destination was recorded" : PresenceText(absent.Restored)
            }
        }, new JsonSerializerOptions { WriteIndented = true });
        using var output = folder.CreateFile(name);
        try { output.Write(0, bytes); output.Flush(); }
        catch { output.Delete(); throw; } // Only the newly allocated no-replace export is removed on failure.
        return path;
    }
    public RecoveryPurgeResult PurgeLocal(string token, string confirmation) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (confirmation != "DELETE") throw new IOException("Type DELETE to confirm permanent deletion of the reviewed saved copy.");
            Ready(); var (item, review) = TakeReview(token, ReviewKind.Purge);
            using var tree = CaptureSaved(item, true);
            if (Stamp(tree.Nodes) != review.TreeStamp) throw new IOException("The saved contents changed after review. Nothing was deleted; prepare a new review.");
            var purge = new PurgeRecord(MutationRules.NewId(), PurgePhase.Started, review.TreeStamp!, tree.Nodes.Count(x => !x.Directory),
                tree.Nodes.Count(x => x.Directory), tree.Nodes.Where(x => !x.Directory).Sum(x => x.Size), EarlierAttempt: item.Purge is not null);
            item = item with { Purge = purge, State = DeletedState.DeletedReview, Message = "Permanent deletion was started locally. An interrupted outcome requires owner review." };
            Save(catalog with { Items = catalog.Items.Where(x => x.Id != item.Id).Append(item).ToArray() });
            var removed = 0;
            try
            {
                foreach (var index in Enumerable.Range(0, tree.Nodes.Count).OrderByDescending(i => tree.Nodes[i].Depth).ThenBy(i => tree.Nodes[i].Name, StringComparer.Ordinal))
                {
                    var node = tree.Nodes[index]; var entry = tree.Entries[index];
                    if (node.Directory && entry.EnumerateNames(default).Any()) throw new IOException("A saved folder contains new or undeleted entries. Those entries were kept.");
                    entry.Delete(); entry.Dispose(); removed++;
                }
                item = item with { Purge = purge with { Phase = PurgePhase.Complete, RemovedEntries = removed }, Message = "The reviewed saved copy was permanently deleted on this computer." };
                Save(catalog with { Items = catalog.Items.Where(x => x.Id != item.Id).Append(item).ToArray() });
                return new RecoveryPurgeResult(true, removed, item.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item = item with { Purge = purge with { Phase = PurgePhase.Review, RemovedEntries = removed },
                    Message = "Permanent deletion did not finish. Some entries may have been removed. Review the saved location before another action." };
                Save(catalog with { Items = catalog.Items.Where(x => x.Id != item.Id).Append(item).ToArray() });
                return new RecoveryPurgeResult(false, removed, item.Message);
            }
        }
    });
    public MutationInfo RestoreRemainingLocal(string token, string name, string confirmation) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (confirmation != "RESTORE REMAINS") throw new IOException("Type RESTORE REMAINS to confirm that earlier deletion cannot be undone.");
            Ready(); var (item, review) = TakeReview(token, ReviewKind.RemainingRestore);
            using (var tree = CaptureSaved(item, false))
                if (Stamp(tree.Nodes) != review.TreeStamp) throw new IOException("The remaining data changed after review. Review it again.");
            TrimReceipts(item.Peer);
            // This explicit local action acknowledges incomplete contents; remote restore remains disabled.
            item = item with { State = DeletedState.DeletedStored };
            var result = RestoreCore(item, item.Grant, name, new(MutationRules.NewId(), item.Peer, item.Owner, FileOperation.Restore,
                new string('0', 64), item.Id, name.Length == 0 ? item.Path[^1] : name), default);
            return result;
        }
    });
    private Locations ProbeLocations(Item item)
    {
        Probe Inspect(Func<SecureWindowsDirectory> openParent, Func<SecureWindowsDirectory, SecureWindowsDirectory> openEntry)
        {
            try
            {
                using var parent = openParent();
                try
                {
                    using var entry = openEntry(parent);
                    return new(entry.Info.Identity == item.Identity ? Presence.Matches : Presence.Changed, entry.Info.Identity, entry.Info.Version);
                }
                catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { return new(Presence.Missing); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(Presence.Unavailable); }
        }
        var saved = Inspect(() =>
        {
            using var root = Root(item); return Vault(item, root);
        }, vault => vault.OpenRecoveryItem(item.Slot, item.Directory));
        Probe AtName(string name) => Inspect(() => Parent(item, item.Grant), parent => parent.OpenManagementChild(name, item.Directory));
        var restored = item.RestoredName.Length == 0 ? new(Presence.Missing) : item.RestoredDestination is { } destination
            ? Inspect(() => OpenRestoreParent(destination), parent => parent.OpenManagementChild(item.RestoredName, item.Directory))
            : AtName(item.RestoredName);
        return new(AtName(item.Path[^1]), saved, restored);
    }
    private static string PresenceText(Probe probe) => probe.State switch
    { Presence.Matches => "Recorded object found", Presence.Missing => "No item at the recorded location", Presence.Changed => "A different object occupies this location", _ => "Location unavailable or changed; identity could not be checked" };
    private static bool Absent(Locations locations) => locations.Saved.State == Presence.Missing &&
        locations.Original.State is Presence.Missing or Presence.Changed &&
        locations.Restored.State is Presence.Missing or Presence.Changed;
    public RecoveryInspection InspectLocal(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var item = LocalItem(id); var locations = ProbeLocations(item);
            var canConfirm = item.Purge is null && (locations.Saved.State == Presence.Matches && locations.Original.State is Presence.Missing or Presence.Changed ||
                locations.Saved.State == Presence.Missing && (locations.Original.State == Presence.Matches || locations.Restored.State == Presence.Matches));
            var absent = Absent(locations);
            return new RecoveryInspection(AddReview(item, ReviewKind.Locations, locations: locations), OwnerInfo(item), PresenceText(locations.Original),
                PresenceText(locations.Saved), item.RestoredName.Length == 0 ? "No restore destination was recorded" : PresenceText(locations.Restored),
                canConfirm, absent, absent && !RetainForReplay(item));
        }
    });
    public string ArchiveAbsentLocal(string token, string destination) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var (item, review) = TakeReview(token, ReviewKind.Locations);
            var locations = ProbeLocations(item);
            if (locations != review.Locations || !Absent(locations))
                throw new IOException("The recorded locations do not confirm absence. Review the current locations.");
            if (RetainForReplay(item))
                throw new IOException("Keep this record for 24 hours after its last operation and resolve any pending operations before archiving.");
            var exported = WriteLocalExport(item, destination, locations);
            if (ProbeLocations(item) != locations)
                throw new IOException("The locations changed during export. The exported metadata and active record were kept.");
            RemoveLocalRecord(item);
            return exported;
        }
    });
    public LocalRecoveryItem ConfirmLocationsLocal(string token) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var (item, review) = TakeReview(token, ReviewKind.Locations); var locations = ProbeLocations(item);
            if (locations != review.Locations) throw new IOException("The locations changed after review. Inspect them again.");
            if (item.Purge is not null) throw new IOException("Permanent deletion was attempted. Review or restore only the remaining data explicitly.");
            if (locations.Saved.State == Presence.Matches && locations.Original.State is Presence.Missing or Presence.Changed)
                item = item with { State = DeletedState.DeletedStored, Message = "The owner confirmed the recorded saved object. Existing original-folder items remain untouched." };
            else if (locations.Saved.State == Presence.Missing && (locations.Original.State == Presence.Matches || locations.Restored.State == Presence.Matches))
                item = item with { State = DeletedState.DeletedRestored, Message = "The owner confirmed the recorded object at its original or restore location. No filesystem change was performed." };
            else throw new IOException("These locations do not prove a recoverable or present copy. Existing metadata and files were kept for manual review.");
            Save(catalog with { Items = catalog.Items.Where(x => x.Id != item.Id).Append(item).ToArray() }); return OwnerInfo(item);
        }
    });
}
