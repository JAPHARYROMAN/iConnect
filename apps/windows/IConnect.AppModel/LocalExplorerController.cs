using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text;

namespace IConnect.AppModel;

/// <summary>Local file inspection has its own lifetime and never opens a peer connection.</summary>
public sealed partial class LocalExplorerController : IAsyncDisposable
{
    private readonly string directory;
    private LocalFileLocations? source;
    private CancellationTokenSource? discovery;
    private Task? pending;
    private int revision;
    private bool disposed;
    private ExplorerRow[] catalog = [];
    private readonly HashSet<string> selectedIds = [];
    private CancellationTokenSource? batchPlanning;
    public IReadOnlyCollection<string> SelectedIds => selectedIds.ToArray();
    public IReadOnlyList<LocalFileLocation> Locations => source?.All ?? [];
    public IReadOnlyList<FileEntry> Breadcrumbs { get; private set; } = [];
    public IReadOnlyList<ExplorerRow> Rows => catalog.Skip(Page * ExplorerCatalog.PageSize).Take(ExplorerCatalog.PageSize).ToArray();
    public ExplorerRow? Selected { get; private set; }
    public ExplorerSort Sort { get; private set; }
    public bool Descending { get; private set; }
    public ExplorerLayout Layout { get; private set; }
    public int Page { get; private set; }
    public int Pages => Math.Max(1, (catalog.Length + ExplorerCatalog.PageSize - 1) / ExplorerCatalog.PageSize);
    public int Count => catalog.Length;
    public bool Busy { get; private set; }
    public bool Healthy => source is not null;
    public string Error { get; private set; } = "";
    public string Notice { get; private set; } = "";
    public string Query { get; private set; } = "";
    public PreviewSession FilePreview { get; }
    public WindowsLocalCopies? Copies { get; }
    public string CopyIssue { get; } = "";
    public WindowsLocalBatchCopies? BatchCopies { get; }
    public string BatchCopyIssue { get; } = "";
    public WindowsLocalManagement? Management { get; }
    public string ManagementIssue { get; } = "";
    private Task? managementWork;
    private readonly SemaphoreSlim managementGate = new(1, 1);
    public bool ManagementBusy => managementWork is { IsCompleted: false };
    public bool ManagementLoaded { get; private set; }
    public string ManagementNotice { get; private set; } = "";
    public IReadOnlyList<LocalManagementChange> ManagementHistory { get; private set; } = [];
    public IReadOnlyList<LocalRecoveryItem> DeletedItems { get; private set; } = [];
    public IReadOnlyList<StateRecoveryIssue> ManagementRecoveryIssues { get; private set; } = [];
    public int ManagementPreserved { get; private set; }
    public string Preview => FilePreview.View.Text;
    public FileEntry? Details { get; private set; }
    public event Action? Changed;
    public LocalExplorerController(string directory, IPreviewRenderer? previewRenderer = null)
    {
        this.directory = directory;
        FilePreview = new(previewRenderer); FilePreview.Changed += () => { if (!disposed) Changed?.Invoke(); };
        try { source = new(directory); }
        catch (Exception) { Error = "Local folder shortcuts could not be opened. Reset these shortcuts to start fresh. Sharing approvals are separate."; }
        try { Copies = new(directory); Copies.PayloadChanged += row => { if (!disposed) localPayloadRates.Observe(row, false); }; Copies.Changed += () => { if (!disposed) Changed?.Invoke(); }; }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { CopyIssue = "Local copy progress could not be opened. Files were kept. Restore access to private app storage and restart; browsing and sharing approvals are separate."; }
        try { BatchCopies = new(directory); BatchCopies.PayloadChanged += row => { if (!disposed) localPayloadRates.Observe(row, true); }; BatchCopies.Changed += () => { if (!disposed) Changed?.Invoke(); }; }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { BatchCopyIssue = "Local batch progress could not be opened. Files were kept. Restore access to private app storage and restart."; }
        try { Management = new(directory); Management.Changed += () => { if (!disposed) Changed?.Invoke(); }; }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { ManagementIssue = "Local management history could not be opened. Files and recovery contents were kept; restore private app storage access and restart."; }
    }
    public async Task<LocalCopySelection> CaptureManagementAsync(bool currentFolder = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Busy || ManagementBusy) throw new IOException("Wait for local discovery or management to finish.");
        var active = RequireSource(); var entry = (currentFolder ? Breadcrumbs.LastOrDefault() : Selected?.Entry)?.Clone() ?? throw new IOException("Select one local item, or open a folder first.");
        if (currentFolder)
        {
            var response = await active.RequestAsync(new() { Operation = FileOperation.GetMetadata, NodeId = entry.Id }, ct);
            StorageRules.ValidateResponse(response); if (response.Status != FileStatus.Ok) throw new IOException(response.Message); entry = response.Entries.Single();
        }
        return await active.CaptureBatchItemAsync(entry, ct);
    }
    public Task<T> ManageAsync<T>(Func<WindowsLocalManagement, T> action) =>
        ManageWorkAsync(manager => Task.FromResult(action(manager)));

    public async Task<T> ManageWorkAsync<T>(Func<WindowsLocalManagement, Task<T>> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var manager = Management ?? throw new IOException(ManagementIssue);
        if (!managementGate.Wait(0)) throw new IOException("Wait for the current local change to finish.");
        try { var task = Task.Run(() => action(manager)); managementWork = task; NotifyManagement(); return await task; }
        finally { managementGate.Release(); NotifyManagement(); }
    }
    public async Task RefreshManagementAsync()
    {
        if (disposed || Management is null) return;
        try
        {
            var snapshot = await ManageAsync(manager =>
            {
                var issues = manager.RecoveryIssues; var history = Array.Empty<LocalManagementChange>(); var deleted = Array.Empty<LocalRecoveryItem>(); var notices = new List<string>();
                try { if (issues.Count == 0) history = manager.History.ToArray(); } catch (IOException ex) { notices.Add(ex.Message); }
                try { deleted = manager.Deleted.ToArray(); } catch (IOException ex) { notices.Add(ex.Message); }
                return (Issues: issues, History: history, Deleted: deleted, Preserved: manager.PreservedRecords, Notice: string.Join(" ", notices));
            });
            ManagementRecoveryIssues = snapshot.Issues; ManagementHistory = snapshot.History; DeletedItems = snapshot.Deleted; ManagementPreserved = snapshot.Preserved; ManagementNotice = snapshot.Notice;
        }
        catch (Exception ex) { ManagementNotice = ex.Message; }
        finally { ManagementLoaded = true; NotifyManagement(); }
    }
    private void NotifyManagement()
    { if (disposed || Changed is not { } listeners) return; foreach (Action listener in listeners.GetInvocationList()) try { listener(); } catch (Exception) { } }
    public async Task RefreshAfterManagementAsync()
    { if (!disposed && source is not null) await BrowseAsync(Breadcrumbs.LastOrDefault()); }
    public async Task<LocalCopySelection[]> CaptureBatchSelectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Busy || selectedIds.Count is < 1 or > 128) throw new InvalidOperationException("Select between one and 128 current files or folders.");
        var entries = catalog.Where(x => selectedIds.Contains(x.Entry.Id)).Select(x => x.Entry.Clone()).ToArray(); var active = RequireSource();
        var result = await Task.Run(async () =>
        {
            var captured = new List<LocalCopySelection>(); foreach (var entry in entries) { ct.ThrowIfCancellationRequested(); captured.Add(await active.CaptureBatchItemAsync(entry, ct)); } return captured.ToArray();
        }, ct);
        ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested(); return result;
    }
    public Task<string> PrepareBatchAsync(IReadOnlyList<LocalCopySelection> selections, CancellationToken ct = default) =>
        PrepareBatchWorkAsync((copies, token) => copies.PrepareAsync(selections, token), ct);

    public Task<string> PrepareFolderMoveAsync(LocalCopySelection selected, LocalMoveDestination target, string name, CancellationToken ct = default) =>
        PrepareBatchWorkAsync((copies, token) => Task.Run(() => copies.PrepareFolderMoveAsync(selected, target, name, token), token), ct);

    private async Task<string> PrepareBatchWorkAsync(Func<WindowsLocalBatchCopies, CancellationToken, Task<string>> prepare, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var copies = BatchCopies ?? throw new IOException(BatchCopyIssue);
        if (batchPlanning is not null) throw new IOException("Wait for the current local plan.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); batchPlanning = cancel;
        try { return await prepare(copies, cancel.Token); }
        finally { batchPlanning = null; if (!disposed) Changed?.Invoke(); }
    }
    public void CancelBatchPlanning() => batchPlanning?.Cancel();
    public async Task<LocalCopySelection> CaptureCopyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Busy || Selected is not { Entry.IsDirectory: false } selected) throw new InvalidOperationException("Select one current local file first.");
        var active = RequireSource();
        var selection = await Task.Run(() => active.CaptureCopyAsync(selected.Entry, ct), ct);
        ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
        return selection;
    }
    public async Task<string> PrepareCopyAsync(CancellationToken ct = default) => await PrepareCopyAsync(await CaptureCopyAsync(ct), ct);
    public async Task<string> PrepareCopyAsync(LocalCopySelection selection, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var copies = Copies ?? throw new IOException(CopyIssue);
        return await Task.Run(() => copies.Prepare(selection), ct);
    }
    public async Task AddAsync(string path)
    {
        var active = RequireSource(); await Task.Run(() => active.Add(path)); await BrowseAsync();
    }
    public async Task RemoveAsync(string id)
    {
        var active = RequireSource(); Cancel(); ++revision; await Task.Run(() => active.Remove(id)); await BrowseAsync();
    }
    public async Task ResetAsync()
    {
        if (source is not null) throw new InvalidOperationException("Remove individual local shortcuts instead.");
        var path = Path.Combine(directory, "local-locations.json");
        if (File.Exists(path)) File.Move(path, path + ".recovery-" + ProtocolRules.NewToken());
        source = new(directory); await BrowseAsync();
    }
    public Task BrowseAsync(FileEntry? entry = null)
    {
        var route = Array.Empty<FileEntry>();
        if (entry is not null)
        {
            var index = Breadcrumbs.ToList().FindIndex(x => x.Id == entry.Id);
            route = index >= 0 ? Breadcrumbs.Take(index + 1).ToArray() : catalog.Single(x => x.Entry.Id == entry.Id && x.Entry.IsDirectory).Route;
        }
        return Discover(route, "", false);
    }
    public Task SearchAsync(string query)
    {
        if (Breadcrumbs.Count == 0) throw new InvalidOperationException("Open a local folder before searching.");
        return Discover(Breadcrumbs.ToArray(), query, true);
    }
    private Task Discover(FileEntry[] route, string query, bool search)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var active = RequireSource();
        discovery?.Cancel(); discovery?.Dispose(); var cancellation = discovery = new(); var token = cancellation.Token; var current = ++revision;
        Busy = true; Error = ""; FilePreview.Clear(); Details = null; Selected = null; selectedIds.Clear(); Changed?.Invoke();
        return pending = Run();
        async Task Run()
        {
            try
            {
                var result = await Task.Run(() => search ? ExplorerCatalog.SearchAsync(route, query, active.RequestAsync, token) : ExplorerCatalog.LoadAsync(route, active.RequestAsync, token), token);
                if (current != revision || disposed) return;
                Breadcrumbs = route; Query = search ? query.Trim() : ""; Page = 0; catalog = ExplorerCatalog.Sort(result.Rows, Sort, Descending); Notice = result.Notice;
                if (route.Length == 0 && catalog.Length < Locations.Count) Notice += " Some saved folders are unavailable or were replaced. Choose the folder again to review its current identity.";
            }
            catch (OperationCanceledException) { if (current == revision && !disposed) Error = "Local discovery canceled. Previous results were kept."; }
            catch (Exception ex) { if (current == revision && !disposed) Error = ex.Message; }
            finally { if (current == revision && !disposed) { Busy = false; Changed?.Invoke(); } }
        }
    }
    public void Select(string? id) => SelectMany(id is null ? [] : [id]);
    public void SelectMany(IEnumerable<string> ids)
    {
        var wanted = ids.Distinct().ToArray(); if (wanted.Length > 128 || wanted.Any(id => catalog.All(x => x.Entry.Id != id))) throw new InvalidOperationException("Select at most 128 loaded items.");
        var old = Selected?.Entry.Id; selectedIds.Clear(); selectedIds.UnionWith(wanted); Selected = wanted.Length == 1 ? catalog.Single(x => x.Entry.Id == wanted[0]) : null;
        if (old != Selected?.Entry.Id) FilePreview.Clear();
    }
    public void SetSort(ExplorerSort sort, bool descending) { Sort = sort; Descending = descending; catalog = ExplorerCatalog.Sort(catalog, sort, descending); Page = 0; Changed?.Invoke(); }
    public void SetLayout(ExplorerLayout layout) { Layout = layout; Changed?.Invoke(); }
    public void SetPage(int page) { Page = Math.Clamp(page, 0, Pages - 1); Changed?.Invoke(); }
    public void Cancel() => discovery?.Cancel();
    public async Task InspectAsync(bool text)
    {
        var row = Selected ?? throw new InvalidOperationException("Select one local item first."); var active = RequireSource(); var current = revision;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (text)
        {
            await FilePreview.OpenAsync(row.Entry, (value,token)=>active.RequestAsync(value,token),
                () => !disposed && current==revision && Selected?.Entry.Id==row.Entry.Id, timeout.Token);
        }
        else {
            var result=await active.RequestAsync(new() { Operation=FileOperation.GetMetadata,NodeId=row.Entry.Id },timeout.Token);
            StorageRules.ValidateResponse(result); if(disposed || current!=revision || Selected?.Entry.Id!=row.Entry.Id)return;
            if(result.Status!=FileStatus.Ok)throw new IOException(result.Message); Details=result.Entries.Single();
        }
        Changed?.Invoke();
    }
    private LocalFileLocations RequireSource() => source ?? throw new IOException(Error);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; localPayloadRates.Clear(); batchPlanning?.Cancel(); FilePreview.Clear(); ++revision; discovery?.Cancel(); if (pending is not null) await pending;
        discovery?.Dispose(); if (source is not null) await Task.Run(source.Dispose);
        if (Copies is not null) await Copies.DisposeAsync();
        if (BatchCopies is not null) await BatchCopies.DisposeAsync();
        if (managementWork is not null) try { await managementWork; } catch (Exception) { }
        Management?.Dispose();
    }
}
