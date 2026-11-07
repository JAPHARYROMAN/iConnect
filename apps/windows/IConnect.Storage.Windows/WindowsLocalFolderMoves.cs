using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record LocalFolderMoveReview(string Token, string BatchId, string Source, string Destination, int Files, int Folders, long Bytes);

public sealed partial class WindowsLocalBatchCopies
{
    private sealed record FolderMoveTarget(string SourceParent, string ParentIdentity, LocalMoveDestination Destination, string Name);
    private sealed record FolderMoveApproval(LocalFolderMoveReview Review, BatchDownload Copy, long Expires);
    private readonly Dictionary<string, FolderMoveApproval> folderMoveApprovals = [];

    public Task<string> PrepareFolderMoveAsync(LocalCopySelection selected, LocalMoveDestination destination, string name,
        CancellationToken ct = default)
    {
        if (!selected.IsDirectory || selected.Segments.Length == 0)
            throw new IOException("Select a folder inside a local location for a verified move.");
        StorageRules.ValidateName(name);
        using var target = StorageGrantStore.OpenLocalDestination(destination.Directory);
        if (target.Info.Identity != destination.Identity || target.Info.CanonicalPath != destination.CanonicalPath)
            throw new IOException("The reviewed destination folder changed.");
        using var parent = SecureWindowsDirectory.OpenPath(selected.Grant.CanonicalPath, selected.Grant.RootIdentity, selected.Segments[..^1], true);
        var privateProfile = Path.GetDirectoryName(root.Info.CanonicalPath)!;
        var sourcePath = Path.Combine(parent.Info.CanonicalPath, selected.Segments[^1]);
        if (SecureWindowsDirectory.IsWithin(sourcePath, privateProfile) || SecureWindowsDirectory.IsWithin(privateProfile, sourcePath) ||
            SecureWindowsDirectory.IsWithin(target.Info.CanonicalPath, privateProfile))
            throw new IOException("Choose a source and destination outside private iConnect storage.");
        var move = new FolderMoveTarget(parent.Info.CanonicalPath, parent.Info.Identity, destination, name);
        return PrepareAsync([selected], move, ct);
    }

    public string StartFolderMoveCopy(string id)
    {
        lock (gate)
        {
            var move = plans.GetValueOrDefault(id)?.Move ?? throw new IOException("This folder has no reviewed move destination.");
            return StartCopy(id, move.Destination.Directory, DownloadCollision.Fail, move);
        }
    }

    private static void ValidateMove(Plan plan, FolderMoveTarget move)
    {
        StorageRules.ValidateName(move.Name);
        var target = move.Destination;
        if (plan.Schema != 3 || plan.Roots.Length != 1 || plan.Roots[0].FileOnly ||
            !SecureWindowsDirectory.IsLocalVolumePath(move.SourceParent) || !SecureWindowsDirectory.IsLocalVolumePath(target.CanonicalPath) ||
            move.ParentIdentity.Length is < 1 or > 256 || target.Identity.Length is < 1 or > 256 ||
            target.Directory.Length is < 3 or > 8192 || !char.IsAsciiLetter(target.Directory[0]) ||
            target.Directory[1] != ':' || target.Directory[2] != '\\' || target.Directory.Any(char.IsControl) ||
            !Path.Combine(move.SourceParent, plan.Roots[0].Name).Equals(plan.Roots[0].Canonical, StringComparison.OrdinalIgnoreCase) ||
            SecureWindowsDirectory.IsWithin(target.CanonicalPath, plan.Roots[0].Canonical) ||
            Path.Combine(target.CanonicalPath, move.Name).Equals(plan.Roots[0].Canonical, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A folder move requires an exact, different destination outside its source tree.");
    }

    private (Plan Plan, BatchDownload Copy, FolderMoveTarget Move) RequireFolderMoveReady(string id)
    {
        Healthy();
        RequireContextIdle(id);
        var plan = plans.GetValueOrDefault(id);
        var copy = batches.Jobs.SingleOrDefault(x => x.Id == id);
        if (plan?.Move is not { } move || copy is not { HasBoundDestination: true } || batches.Active(id) ||
            copy.RootIdentity != move.Destination.Identity || copy.RootPath != move.Destination.CanonicalPath ||
            copy.DisplayPath != move.Destination.Directory || copy.MoveName != move.Name || copy.Collision != DownloadCollision.Fail ||
            copy.Items.Length != plan.Nodes.Length)
            throw new IOException("Finish the exact reviewed folder copy before reviewing source recovery.");
        RequireRecord(plan);
        var manifest = Manifest(plan);
        for (var i = 0; i < copy.Items.Length; i++)
        {
            var item = copy.Items[i];
            var expected = manifest.Items[i];
            if (item.Source with { SourcePath = expected.SourcePath } != expected ||
                !item.Source.SourcePath.SequenceEqual(expected.SourcePath) ||
                item.LocalName != (i == 0 ? move.Name : expected.Name))
                throw new IOException("The completed copy does not match the reviewed source plan.");
        }
        return (plan, copy, move);
    }

    public LocalFolderMoveReview ReviewFolderMove(string id)
    {
        lock (gate)
        {
            var (plan, copy, move) = RequireFolderMoveReady(id);
            var source = plan.Roots[0];
            using var original = SecureWindowsDirectory.OpenBoundPath(move.SourceParent, move.ParentIdentity, [(source.Name, source.Identity)]);
            if (original.Info.Version != source.Version) throw new IOException("The source folder changed after planning.");
            var review = new LocalFolderMoveReview(ProtocolRules.NewToken(), id, source.Display,
                Path.Combine(copy.DisplayPath, move.Name), copy.Items.Count(x => !x.Source.Directory),
                copy.Items.Count(x => x.Source.Directory), copy.VerifiedBytes);
            folderMoveApprovals[id] = new(review, copy, Environment.TickCount64 + 5 * 60_000);
            return review;
        }
    }

    public Task<MutationInfo> FinishFolderMoveAsync(LocalFolderMoveReview value, WindowsLocalManagement management, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!folderMoveApprovals.Remove(value.BatchId, out var approval) || approval.Review != value || approval.Expires < Environment.TickCount64)
                throw new IOException("This move review expired or was used. Review the source and destination again.");
            var (plan, copy, move) = RequireFolderMoveReady(value.BatchId);
            if (approval.Copy != copy) throw new IOException("The copy changed. Review its current receipt.");
            var version = versions[plan.Id];
            return RunVerification(plan.Id, ct, token => WithVerifiedDestination(copy, token, (_, requireDestination) =>
            {
                using var sourceRecord = sourceRoot.OpenChild(plan.Id + ".json");
                if (sourceRecord.Info.Version != version) throw new IOException("The saved source plan changed.");
                RequireSettledReceipt(sourceRecord.Info.CanonicalPath);
                var source = plan.Roots[0];
                return management.RecoverVerifiedFolder(move.SourceParent, move.ParentIdentity, source.Name, source.Display, source.Identity,
                    source.Version, original => VerifySourceTree(original, plan, copy, token), () =>
                    {
                        RequireSettledReceipt(sourceRecord.Info.CanonicalPath);
                        requireDestination();
                    }, token);
            }));
        }
    }

    private static void VerifySourceTree(SecureWindowsDirectory source, Plan plan, BatchDownload copy, CancellationToken ct)
    {
        var opened = new List<SecureWindowsDirectory>(plan.Nodes.Length) { source };
        var children = plan.Nodes.ToLookup(x => x.Parent);
        try
        {
            for (var i = 0; i < plan.Nodes.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var node = plan.Nodes[i];
                var entry = i == 0 ? source : opened[node.Parent].OpenChild(node.Name, node.Directory);
                if (i != 0) opened.Add(entry);
                if (entry.Info.Identity != node.Identity || !node.Directory && entry.Info.Version != node.Version)
                    throw new IOException("A source item changed after planning.");
                if (!node.Directory) WindowsDownloadManager.VerifyFile(entry, node.Size, copy.Items[i].Hash, ct);
            }
            for (var i = 0; i < plan.Nodes.Length; i++)
                if (plan.Nodes[i].Directory)
                    RequireChildren(opened[i], children[i].Select(x => (x.Name, x.Directory)), "A source folder's contents changed.", ct);
        }
        finally { for (var i = opened.Count - 1; i > 0; i--) opened[i].Dispose(); }
    }
}
