using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Core;

public sealed record TransferSelection(FileEntry Entry, string[] SourcePath);
public sealed record TransferItem(int Parent, string GrantId, string[] SourcePath, string Name, bool Directory,
    long Size, string Version, string Error = "");
public sealed record TransferManifest(TransferItem[] Items)
{
    public long Bytes => Items.Where(x => !x.Directory && x.Error.Length == 0).Sum(x => x.Size);
    public int Files => Items.Count(x => !x.Directory);
    public int Directories => Items.Count(x => x.Directory);
}

/// <summary>A bounded copy plan, never a remote pathname accepted by a filesystem API.</summary>
public static class TransferManifests
{
    public const int MaxItems = 4096, MaxDepth = 32, MaxPages = 256, MaxBytes = 4 * 1024 * 1024;
    public delegate Task<FileResponse> Request(FileRequest request, CancellationToken ct);

    public static async Task<TransferManifest> BuildAsync(IReadOnlyList<TransferSelection> selections, Request request, CancellationToken ct)
    {
        if (selections.Count is < 1 or > 128) throw new InvalidDataException("Select between 1 and 128 items.");
        var result = new List<TransferItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            ct.ThrowIfCancellationRequested();
            if (!names.Add(selection.Entry.Name)) throw new InvalidDataException("Selected names collide on this destination. Select them separately.");
            var response = await Checked(request, new FileRequest { Operation = FileOperation.GetMetadata, NodeId = selection.Entry.Id }, ct);
            var entry = response.Entries.SingleOrDefault() ?? throw new InvalidDataException("The selected item is unavailable.");
            if (entry.Id != selection.Entry.Id || entry.GrantId != selection.Entry.GrantId || entry.Name != selection.Entry.Name ||
                entry.IsDirectory != selection.Entry.IsDirectory || entry.Version != selection.Entry.Version)
                throw new InvalidDataException("The selected source changed. Refresh it before planning a transfer.");
            await Visit(entry, selection.SourcePath, -1);
        }
        var manifest = new TransferManifest(result.ToArray()); Validate(manifest); return manifest;

        async Task Visit(FileEntry entry, string[] path, int parent)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count == MaxItems || path.Length > MaxDepth || path.Sum(x => x.Length) > 2048 || !ids.Add(entry.Id))
                throw new InvalidDataException("The source tree repeats items or exceeds the transfer planning limits.");
            var error = "";
            try { StorageRules.ValidateName(entry.Name); foreach (var component in path) StorageRules.ValidateName(component); }
            catch (StorageException) { error = "This name cannot be safely represented on the destination."; }
            var required = entry.IsDirectory ? StoragePermission.List : StoragePermission.Read;
            if ((entry.Permissions & (uint)required) == 0) error = "This item is not approved for copying.";
            if (entry.Size > StorageRules.MaxTransferSize) error = "This file exceeds the supported transfer size.";
            var index = result.Count;
            result.Add(new(parent, entry.GrantId, path.ToArray(), entry.Name, entry.IsDirectory, entry.Size, entry.Version, error));
            if (!entry.IsDirectory || error.Length != 0) return;
            try
            {
                var children = await ListAsync(request, entry, ct);
                foreach (var child in children.OrderBy(x => x.Name, StringComparer.Ordinal))
                    await Visit(child, [.. path, child.Name], index);
                var after = (await Checked(request, new FileRequest { Operation = FileOperation.GetMetadata, NodeId = entry.Id }, ct)).Entries.SingleOrDefault();
                if (after is null || after.Id != entry.Id || after.GrantId != entry.GrantId || after.Version != entry.Version || !after.IsDirectory)
                    throw new IOException("The folder changed while it was being scanned. Plan it again.");
            }
            catch (StorageException ex)
            {
                // A denied/unavailable branch is explicit; malformed or unbounded trees abort the plan.
                result[index] = result[index] with { Error = SafeError(ex.Message) };
            }
        }
    }

    public static async Task<FileEntry[]> ListAsync(Request request, FileEntry directory, CancellationToken ct)
    {
        var entries = new List<FileEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.Ordinal);
        var continuation = "";
        for (var page = 0; page < MaxPages; page++)
        {
            var response = await Checked(request, new FileRequest { Operation = FileOperation.ListDirectory, NodeId = directory.Id, Continuation = continuation }, ct);
            foreach (var entry in response.Entries)
            {
                if (entry.IsRoot || entry.ParentId != directory.Id || entry.GrantId != directory.GrantId || !names.Add(entry.Name) || !ids.Add(entry.Id))
                    throw new InvalidDataException("The source returned ambiguous or unrelated directory entries.");
                entries.Add(entry);
                if (entries.Count > MaxItems) throw new InvalidDataException("This directory exceeds the transfer planning limit.");
            }
            continuation = response.Continuation;
            if (continuation.Length == 0) return entries.ToArray();
        }
        throw new InvalidDataException("The source exceeded the directory page limit.");
    }

    public static void Validate(TransferManifest manifest)
    {
        if (manifest.Items is null || manifest.Items.Length is < 1 or > MaxItems) throw new InvalidDataException("Invalid transfer manifest size.");
        var destinations = new HashSet<(int, string)>(); long bytes = 0;
        for (var i = 0; i < manifest.Items.Length; i++)
        {
            var item = manifest.Items[i];
            if (item is null || item.Parent < -1 || item.Parent >= i || item.SourcePath is null || item.SourcePath.Length > MaxDepth ||
                item.SourcePath.Any(x => x is null || x.Length is < 1 or > 255 || x.Any(char.IsControl)) || item.SourcePath.Sum(x => x.Length) > 2048 ||
                !ProtocolRules.TokenPattern().IsMatch(item.GrantId) || item.Name.Length is < 1 or > 255 || item.Name.Any(char.IsControl) ||
                item.Version.Length != 64 || item.Version.Any(c => !char.IsAsciiHexDigitLower(c)) || item.Size < 0 ||
                item.Error.Length > 512 || item.Error.Any(char.IsControl) ||
                !destinations.Add((item.Parent, item.Name.ToUpperInvariant()))) throw new InvalidDataException("Invalid or ambiguous transfer manifest.");
            if (item.Parent >= 0)
            {
                var parent = manifest.Items[item.Parent];
                if (!parent.Directory || parent.GrantId != item.GrantId || !item.SourcePath.SequenceEqual(parent.SourcePath.Append(item.Name)))
                    throw new InvalidDataException("A manifest item escaped its selected folder.");
            }
            else if (item.SourcePath.Length != 0 && item.SourcePath[^1] != item.Name)
                throw new InvalidDataException("Invalid selected source location.");
            if (item.Error.Length == 0)
            {
                StorageRules.ValidateName(item.Name); foreach (var component in item.SourcePath) StorageRules.ValidateName(component);
                if (item.Size > StorageRules.MaxTransferSize || !item.Directory && (bytes += item.Size) > StorageRules.MaxTransferSize)
                    throw new InvalidDataException("The batch exceeds the supported transfer size.");
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(manifest).Length > MaxBytes) throw new InvalidDataException("The transfer manifest exceeds its storage limit.");
    }
    public static async Task<FileResponse> Checked(Request request, FileRequest value, CancellationToken ct)
    {
        var response = await request(value, ct); StorageRules.ValidateResponse(response);
        if (response.Status != FileStatus.Ok) throw new StorageException(response.Status, response.Message.Length == 0 ? "The source denied this operation." : response.Message);
        return response;
    }
    public static string SafeError(string text) => new(text.Where(c => !char.IsControl(c)).Take(512).ToArray());
}

/// <summary>Rebinds session-local references through an approved grant and exact component names.</summary>
public sealed class ManifestSourceResolver(TransferManifests.Request request)
{
    private readonly Dictionary<string, FileEntry> cache = new(StringComparer.Ordinal);
    public async Task<FileEntry> ResolveAsync(TransferItem item, CancellationToken ct)
    {
        var key = item.GrantId;
        if (!cache.TryGetValue(key, out var node))
        {
            FileEntry? selected = null; var continuation = ""; var seen = new HashSet<string>();
            for (var page = 0; page < TransferManifests.MaxPages; page++)
            {
                var roots = await TransferManifests.Checked(request, new FileRequest { Operation = FileOperation.ListRoots, Continuation = continuation }, ct);
                if (roots.Entries.Any(x => !x.IsRoot || !seen.Add(x.GrantId))) throw new InvalidDataException("The source returned ambiguous roots.");
                selected = roots.Entries.SingleOrDefault(x => x.GrantId == item.GrantId);
                if (selected is not null || roots.Continuation.Length == 0) break;
                continuation = roots.Continuation;
            }
            node = selected ?? throw new IOException("The approved source folder is unavailable. Reconnect and check its permissions.");
            cache[key] = node;
        }
        foreach (var name in item.SourcePath)
        {
            key += "/" + RulesKey(name);
            if (cache.TryGetValue(key, out var found)) { node = found; continue; }
            var children = await TransferManifests.ListAsync(request, node, ct);
            foreach (var child in children) cache[key[..(key.LastIndexOf('/') + 1)] + RulesKey(child.Name)] = child;
            node = children.SingleOrDefault(x => x.Name == name) ?? throw new IOException("A planned source item moved or is no longer available.");
            cache[key] = node;
        }
        var metadata = (await TransferManifests.Checked(request, new FileRequest { Operation = FileOperation.GetMetadata, NodeId = node.Id }, ct)).Entries.SingleOrDefault();
        if (metadata is null || metadata.Id != node.Id || metadata.GrantId != item.GrantId || metadata.Name != item.Name ||
            metadata.IsDirectory != item.Directory || !item.Directory && (metadata.Size != item.Size || metadata.Version != item.Version))
            throw new InvalidDataException("The planned source changed. Start a new transfer after reviewing it.");
        return metadata;
    }
    private static string RulesKey(string name) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name));
}
