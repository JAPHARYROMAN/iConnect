using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record ReceivingPartFolder(string Id, string Location, int Files, long Bytes);
public sealed record ReceivingPartEntry(string Name, long Bytes);
public sealed record ReceivingPartRemovalReview(string Id, string Folder, int Files, long Bytes);
public sealed record ReceivingPartRemovalProgress(string Folder, int Removed, int ReviewedFiles);

/// <summary>Owner-selected, nonrecursive review of unidentified destination-side parts.</summary>
public sealed class WindowsReceivingPartRecovery(StorageGrantStore grants, WindowsDownloadManager downloads, WindowsBatchDownloads batches,
    Func<string> currentOwner, Func<bool> allowed, Func<WindowsLocalCopies?>? localCopies = null, Func<WindowsLocalBatchCopies?>? localBatches = null) : IDisposable
{
    private const string Prefix = ".iconnect-transfer-", Suffix = ".part";
    private const int ScanLimit = 32768, PartLimit = 8192, RemovalBatch = 64;
    private readonly object gate = new();
    private sealed record Selection(ReceivingPartFolder Value, string Owner, string Canonical, string Identity);
    private sealed record Entry(string Token, string Name, string Version, long Bytes);
    private sealed record Review(ReceivingPartRemovalReview Value, string Owner, string Protection, Entry[] Files, long Expires);
    private sealed class Deletion(Review review) { internal Review Review { get; } = review; internal int Cursor; }
    private Selection? selected;
    private Review? review;
    private Deletion? deletion;
    private bool disposed;

    public ReceivingPartFolder? Selected { get { lock (gate) return selected?.Value; } }
    public ReceivingPartRemovalProgress? Progress { get { lock (gate) return deletion is { } d ? new(d.Review.Value.Folder, d.Cursor, d.Review.Files.Length) : null; } }

    private string Actor(Func<bool> continuing)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!allowed() || !continuing()) throw new OperationCanceledException("Keep iConnect disconnected and this recovery screen open to continue cleanup.");
        var owner = currentOwner();
        if (owner.Length != 108 || owner[64] != '.' || owner[..64].Any(c => !char.IsAsciiHexDigitLower(c)) || !UploadBatchRules.CanonicalToken(owner[65..]))
            throw new IOException("Review the current Windows identity before partial-file cleanup.");
        return owner;
    }
    private T Guard<T>(Func<ReceivingPartProtection, T> operation)
    {
        try
        {
            return grants.WithAuthorization(() =>
                grants.BatchPlans.WithReceivingPartProtection(batch => grants.Uploads.WithReceivingPartProtection(incoming =>
                    batches.WithReceivingPartProtection(folders => downloads.WithReceivingPartProtection(local => LocalProtection(copies => LocalBatchProtection(localFolders =>
                    {
                        using var protection = new ReceivingPartProtection(); protection.Merge(batch); protection.Merge(incoming); protection.Merge(folders); protection.Merge(local); protection.Merge(copies); protection.Merge(localFolders);
                        lock (gate) return operation(protection);
                    })))))));
        }
        catch (OperationCanceledException) { throw; }
        catch { lock (gate) { review = null; deletion = null; } throw; }
    }
    private T LocalProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        if (localCopies?.Invoke() is { } copies) return copies.WithReceivingPartProtection(operation);
        if (localCopies is not null || Directory.Exists(Path.Combine(downloads.ProfileDirectory, "local-copies")))
            throw new IOException("Local copy ownership could not be validated. Restore access to its progress and restart before partial-file cleanup.");
        using var empty = new ReceivingPartProtection(); empty.Bind("no-local-copy-store"); return operation(empty);
    }
    private T LocalBatchProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        if (localBatches?.Invoke() is { } copies) return copies.WithReceivingPartProtection(operation);
        if (localBatches is not null || Directory.Exists(Path.Combine(downloads.ProfileDirectory, "local-batch-copies")))
            throw new IOException("Local batch ownership could not be validated. Restore access to its progress and restart before partial-file cleanup.");
        using var empty = new ReceivingPartProtection(); empty.Bind("no-local-batch-store"); return operation(empty);
    }
    private SecureWindowsDirectory Open(Selection selection)
    {
        var root = StorageGrantStore.OpenLocalDestination(selection.Value.Location);
        try
        {
            grants.RequirePublicLocation(root.Info.CanonicalPath);
            if (root.Info.Identity != selection.Identity || root.Info.CanonicalPath != selection.Canonical)
                throw new IOException("The selected destination folder changed. Its files were kept; choose and inspect it again.");
            return root;
        }
        catch { root.Dispose(); throw; }
    }
    private static bool PartToken(string name, out string token)
    {
        token = "";
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal)) return false;
        token = name[Prefix.Length..^Suffix.Length]; return ProtocolRules.TokenPattern().IsMatch(token);
    }
    private static Entry[] Inventory(SecureWindowsDirectory root, ReceivingPartProtection protection)
    {
        var names = root.EnumerateNames(default).Take(ScanLimit + 1).ToArray();
        if (names.Length > ScanLimit) throw new IOException("This folder exceeds the 32,768-entry recovery scan limit. Its contents were kept.");
        var files = new List<Entry>();
        foreach (var name in names.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!PartToken(name.Name, out var token) || protection.Tokens.Contains(token)) continue;
            if (name.IsDirectory) throw new IOException("A temporary-looking directory was kept. Recovery does not descend into folders.");
            using var file = root.OpenReceivingPart(token);
            if (protection.Identities.Contains(file.Info.Identity)) continue;
            if (file.Info.Size < 0 || file.Info.Size > StorageRules.MaxTransferSize || files.Count >= PartLimit)
                throw new IOException("Unidentified partial files exceed the supported recovery limits. Their contents were kept.");
            files.Add(new(token, name.Name, file.Info.Version, file.Info.Size));
        }
        return files.ToArray();
    }
    private Selection SelectionFor(string id, string owner)
    {
        if (selected is not { } selection || selection.Value.Id != id || selection.Owner != owner)
        { review = null; deletion = null; throw new IOException("Choose and inspect this folder again under the current Windows identity."); }
        return selection;
    }
    public ReceivingPartFolder Select(string folder) => Guard(protection =>
    {
        var owner = Actor(() => true); review = null; deletion = null; selected = null; var full = Path.GetFullPath(folder);
        using var root = StorageGrantStore.OpenLocalDestination(full); grants.RequirePublicLocation(root.Info.CanonicalPath);
        var files = Inventory(root, protection);
        var value = new ReceivingPartFolder(ProtocolRules.NewToken(), full, files.Length, files.Sum(x => x.Bytes));
        review = null; deletion = null; selected = new(value, owner, root.Info.CanonicalPath, root.Info.Identity); return value;
    });
    public void ClearSelection() { lock (gate) { review = null; deletion = null; selected = null; } }
    public IReadOnlyList<ReceivingPartEntry> Inspect(string folder, int start) => Guard<IReadOnlyList<ReceivingPartEntry>>(protection =>
    {
        var owner = Actor(() => true); var selection = SelectionFor(folder, owner); using var root = Open(selection);
        var files = Inventory(root, protection);
        if (start < 0 || start > files.Length) throw new ArgumentOutOfRangeException(nameof(start));
        return files.Skip(start).Take(50).Select(x => new ReceivingPartEntry(x.Name, x.Bytes)).ToArray();
    });
    public ReceivingPartRemovalReview ReviewRemoval(string folder) => Guard(protection =>
    {
        var owner = Actor(() => true); var selection = SelectionFor(folder, owner); using var root = Open(selection);
        if (deletion is { } active && active.Review.Owner == owner && active.Review.Protection == protection.Stamp)
            throw new IOException("Continue the existing reviewed cleanup before requesting another removal review.");
        deletion = null; var files = Inventory(root, protection);
        if (files.Length == 0) throw new IOException("This folder has no supported unreferenced partial files to review.");
        var value = new ReceivingPartRemovalReview(ProtocolRules.NewToken(), folder, files.Length, files.Sum(x => x.Bytes));
        review = new(value, owner, protection.Stamp, files, Environment.TickCount64 + 120_000); return value;
    });
    public bool ConfirmRemoval(ReceivingPartRemovalReview value, Func<bool>? continuing = null) => Guard(protection =>
    {
        var owner = Actor(continuing ?? (() => true)); var pending = review; review = null;
        if (pending is null || pending.Value != value || pending.Owner != owner || pending.Protection != protection.Stamp || pending.Expires < Environment.TickCount64)
            throw new IOException("This partial-file review changed, expired or was already used. Inspect and review again.");
        var selection = SelectionFor(value.Folder, owner); using var root = Open(selection);
        if (!Inventory(root, protection).SequenceEqual(pending.Files)) throw new IOException("The folder's partial files changed after review. Their files were kept.");
        deletion = new(pending); return Remove(root, protection, owner, continuing ?? (() => true));
    });
    public bool ContinueRemoval(Func<bool>? continuing = null) => Guard(protection =>
    {
        var owner = Actor(continuing ?? (() => true)); var active = deletion;
        if (active is null || active.Review.Owner != owner || active.Review.Protection != protection.Stamp)
        { deletion = null; throw new IOException("Current receiving state changed. Inspect and review the remaining partial files again."); }
        var selection = SelectionFor(active.Review.Value.Folder, owner); using var root = Open(selection);
        return Remove(root, protection, owner, continuing ?? (() => true));
    });
    private bool Remove(SecureWindowsDirectory root, ReceivingPartProtection protection, string owner, Func<bool> continuing)
    {
        var active = deletion!;
        try
        {
            var end = Math.Min(active.Review.Files.Length, active.Cursor + RemovalBatch);
            while (active.Cursor < end)
            {
                if (Actor(continuing) != owner) throw new IOException("The local Windows identity changed. Remaining files were kept.");
                var expected = active.Review.Files[active.Cursor];
                if (protection.Tokens.Contains(expected.Token)) throw new IOException("A current receipt protects this partial file. It was kept.");
                using var file = root.OpenReceivingPart(expected.Token, mutation: true);
                if (file.Info.Version != expected.Version || protection.Identities.Contains(file.Info.Identity))
                    throw new IOException("A reviewed partial file changed or belongs to a current receipt. It was kept.");
                file.Delete(); active.Cursor++;
            }
            if (active.Cursor < active.Review.Files.Length) return false;
            deletion = null; selected = null; return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { deletion = null; throw; }
    }
    public void Dispose() { lock (gate) { disposed = true; review = null; deletion = null; selected = null; } }
}
