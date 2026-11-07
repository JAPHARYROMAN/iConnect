using IConnect.Protocol;
using System.Text;

namespace IConnect.Core;

public enum ExplorerSort { Name, Size, Modified, Type }
public enum ExplorerLayout { List, Grid }
public sealed record ExplorerRow(FileEntry Entry, FileEntry[] Route)
{
    public string[] SourcePath => Route.Skip(1).Select(x => x.Name).ToArray();
    public string RelativePath => string.Join(" / ", SourcePath);
    public string Type => Entry.IsDirectory ? "Folder" : Path.GetExtension(Entry.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : "File";
}
public sealed record ExplorerSnapshot(ExplorerRow[] Rows, bool Complete, int Visited, string Notice = "");

/// <summary>Bounded, permission-checked discovery. Display pages are independent of transport pages.</summary>
public static class ExplorerCatalog
{
    public const int PageSize = 50, MaxVisited = 8192, MaxMatches = 2048, MaxRequests = 2048, MaxDepth = 32;
    private sealed class Budget
    {
        public int Requests, Visited, Bytes;
        public bool Stopped;
        public string Notice = "";
        public void Stop(string notice) { Stopped = true; Notice = notice; }
    }
    public static async Task<ExplorerSnapshot> LoadAsync(FileEntry[] directoryRoute, TransferManifests.Request request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var budget = new Budget(); var rows = new List<ExplorerRow>();
        try
        {
            if (directoryRoute.Length == 0)
            {
                var roots = await Send(new FileRequest { Operation = FileOperation.ListRoots }, request, budget, timeout.Token);
                var grants = new HashSet<string>();
                foreach (var entry in roots.Entries)
                {
                    if (!entry.IsRoot || !grants.Add(entry.GrantId)) throw new InvalidDataException("The source returned ambiguous approved locations.");
                    rows.Add(new(entry.Clone(), [entry.Clone()])); budget.Visited++;
                }
                return new(rows.ToArray(), roots.Continuation.Length == 0, budget.Visited, roots.Continuation.Length == 0 ? "" : "The source returned an incomplete location list.");
            }
            ValidateRoute(directoryRoute);
            await ReadDirectory(directoryRoute, request, budget, row => rows.Add(row), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (!budget.Stopped) budget.Stop("Discovery timed out. Results are incomplete; refresh or choose a narrower scope."); }
        return new(rows.ToArray(), !budget.Stopped, budget.Visited, budget.Notice);
    }
    public static async Task<ExplorerSnapshot> SearchAsync(FileEntry[] scope, string query, TransferManifests.Request request, CancellationToken ct)
    {
        ValidateRoute(scope); query = query.Trim();
        if (query.Length is < 1 or > 128 || query.Any(char.IsControl)) throw new ArgumentException("Enter 1–128 visible characters to search filenames.");
        var needle = Fold(query); var budget = new Budget(); var rows = new List<ExplorerRow>();
        var pending = new Queue<FileEntry[]>(); pending.Enqueue(scope);
        var directories = new HashSet<string>(StringComparer.Ordinal); var unavailable = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (pending.TryDequeue(out var route) && !budget.Stopped)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!directories.Add(route[^1].Id)) throw new InvalidDataException("The source repeated a directory reference.");
                try
                {
                    await ReadDirectory(route, request, budget, row =>
                    {
                        if (Fold(row.Entry.Name).Contains(needle, StringComparison.Ordinal))
                        {
                            rows.Add(row);
                            if (rows.Count >= MaxMatches) budget.Stop("The search reached its result limit. Results are incomplete; use a narrower query.");
                        }
                        if (row.Entry.IsDirectory)
                        {
                            if (row.Route.Length > MaxDepth + 1) { unavailable++; return; }
                            pending.Enqueue(row.Route);
                        }
                    }, timeout.Token);
                }
                catch (StorageException ex) when (ex.Status is FileStatus.AccessDenied or FileStatus.NotFound or FileStatus.StaleReference or FileStatus.Unsupported)
                { unavailable++; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (!budget.Stopped) budget.Stop("Search timed out. Results are incomplete; use a narrower scope."); }
        var notice = budget.Notice;
        if (unavailable > 0) notice += (notice.Length == 0 ? "" : " ") + $"{unavailable} folders were unavailable or exceeded the depth limit; results are incomplete.";
        return new(rows.ToArray(), !budget.Stopped && unavailable == 0, budget.Visited, notice);
    }
    private static async Task ReadDirectory(FileEntry[] route, TransferManifests.Request request, Budget budget, Action<ExplorerRow> accept, CancellationToken ct)
    {
        var directory = route[^1];
        var metadata = (await Send(new FileRequest { Operation = FileOperation.GetMetadata, NodeId = directory.Id }, request, budget, ct)).Entries.SingleOrDefault();
        if (metadata is null || !SameFolder(metadata, directory))
            throw new InvalidDataException("The selected folder changed. Refresh its parent.");
        route = [.. route.Take(route.Length - 1), metadata]; directory = metadata;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<string>(StringComparer.Ordinal);
        var continuation = "";
        try
        {
        for (var page = 0; page < 256 && !budget.Stopped; page++)
        {
            var response = await Send(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = directory.Id, Continuation = continuation }, request, budget, ct);
            continuation = response.Continuation;
            foreach (var entry in response.Entries)
            {
                if (entry.IsRoot || entry.ParentId != directory.Id || entry.GrantId != directory.GrantId || !names.Add(entry.Name) || !ids.Add(entry.Id))
                    throw new InvalidDataException("The source returned ambiguous or unrelated directory items.");
                if (++budget.Visited > MaxVisited) { budget.Stop("The discovery limit was reached. Results and sorting are limited to loaded items."); break; }
                budget.Bytes += entry.CalculateSize() + route.Sum(x => x.CalculateSize());
                if (budget.Bytes > 16 * 1024 * 1024) { budget.Stop("The metadata limit was reached. Results are incomplete."); break; }
                accept(new(entry.Clone(), [.. route, entry.Clone()]));
                if (budget.Stopped) break;
            }
            if (continuation.Length == 0) break;
            if (page == 255) budget.Stop("This folder exceeded the page limit. Results are incomplete.");
        }
        if (!budget.Stopped)
        {
            var after = (await Send(new FileRequest { Operation = FileOperation.GetMetadata, NodeId = directory.Id }, request, budget, ct)).Entries.SingleOrDefault();
            if (after is null || !SameFolder(after, directory) || after.Version != directory.Version)
                throw new StorageException(FileStatus.StaleReference, "The directory changed during discovery. Refresh it.");
        }
        }
        finally
        {
            if (continuation.Length != 0)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await request(new FileRequest { Operation = FileOperation.CloseListing, NodeId = directory.Id, Continuation = continuation }, cleanup.Token).WaitAsync(cleanup.Token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }
    private static async Task<FileResponse> Send(FileRequest value, TransferManifests.Request request, Budget budget, CancellationToken ct)
    {
        if (++budget.Requests > MaxRequests) { budget.Stop("The request limit was reached. Results are incomplete."); throw new OperationCanceledException(ct); }
        return await TransferManifests.Checked(request, value, ct);
    }
    public static ExplorerRow[] Sort(IEnumerable<ExplorerRow> rows, ExplorerSort sort, bool descending)
    {
        var comparer = Comparer<ExplorerRow>.Create((left, right) =>
        {
            var compare = sort switch { ExplorerSort.Size => left.Entry.Size.CompareTo(right.Entry.Size), ExplorerSort.Modified => left.Entry.ModifiedAt.CompareTo(right.Entry.ModifiedAt),
                ExplorerSort.Type => StringComparer.Ordinal.Compare(Fold(left.Type), Fold(right.Type)), _ => StringComparer.Ordinal.Compare(Fold(left.Entry.Name), Fold(right.Entry.Name)) };
            if (compare == 0) compare = StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
            if (compare == 0) compare = StringComparer.Ordinal.Compare(left.Entry.Id, right.Entry.Id);
            return descending ? -compare : compare;
        });
        return rows.OrderBy(x => !x.Entry.IsDirectory).ThenBy(x => x, comparer).ToArray();
    }
    public static ExplorerRow[] NormalizeSelection(IEnumerable<ExplorerRow> selection)
    {
        var rows = selection.ToArray();
        return rows.Where(row => !rows.Any(parent => parent.Entry.Id != row.Entry.Id && parent.Entry.IsDirectory && parent.Entry.GrantId == row.Entry.GrantId &&
            parent.SourcePath.Length < row.SourcePath.Length && row.SourcePath.Take(parent.SourcePath.Length).SequenceEqual(parent.SourcePath))).ToArray();
    }
    public static void ValidateRoute(FileEntry[] route)
    {
        if (route.Length is < 1 or > MaxDepth + 1 || !route[0].IsRoot || route.Any(x => !x.IsDirectory) || route.Select(x => x.Id).Distinct().Count() != route.Length)
            throw new InvalidDataException("Choose an approved folder as the discovery scope.");
        for (var i = 1; i < route.Length; i++) if (route[i].GrantId != route[0].GrantId || route[i].ParentId != route[i - 1].Id)
            throw new InvalidDataException("The folder scope escaped its approved grant.");
    }
    private static bool SameFolder(FileEntry left, FileEntry right) => left.Id == right.Id && left.GrantId == right.GrantId &&
        left.IsDirectory && left.IsRoot == right.IsRoot && left.ParentId == right.ParentId && left.Name == right.Name;
    private static string Fold(string value) => value.Normalize(NormalizationForm.FormC).ToUpperInvariant();
}
