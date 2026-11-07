using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class ExplorerTests : IDisposable
{
    private readonly string sandbox, source;
    private readonly string peer = new('c', 64);
    private readonly StorageGrantStore grants;
    private readonly WindowsFileService service;
    private readonly List<FileOperation> operations = [];
    public ExplorerTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        sandbox = Path.Combine(root!.FullName, ".local", "explorer-tests", ProtocolRules.NewToken()); source = Path.Combine(sandbox, "source");
        Directory.CreateDirectory(Path.Combine(source, "Area", "Nested")); Directory.CreateDirectory(Path.Combine(source, "Area", "Empty"));
        Directory.CreateDirectory(Path.Combine(source, "Elsewhere")); Directory.CreateDirectory(Path.Combine(source, "Area", ".ssh"));
        File.WriteAllText(Path.Combine(source, "Area", "Nested", "Habari-文書.txt"), "nested");
        File.WriteAllText(Path.Combine(source, "Elsewhere", "Habari-outside.txt"), "outside");
        File.WriteAllText(Path.Combine(source, "Area", ".ssh", "Habari-secret.txt"), "excluded");
        for (var i = 0; i < 110; i++) File.WriteAllText(Path.Combine(source, "Area", $"item-{i:D3}.txt"), new string('x', 110 - i));
        grants = new StorageGrantStore(Path.Combine(sandbox, "owner")); grants.Add(peer, source, label: "Approved documents"); service = new WindowsFileService(grants);
    }
    private Task<FileResponse> Rpc(FileRequest value, CancellationToken ct)
    {
        operations.Add(value.Operation); var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); return service.ExecuteAsync(peer, request, ct);
    }
    private async Task<FileEntry[]> Scope()
    {
        var root = (await Rpc(new FileRequest { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var area = (await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == "Area");
        return [root, area];
    }
    [Fact]
    public async Task Full_directory_sorting_covers_more_than_two_transport_pages_and_preserves_route_metadata()
    {
        var scope = await Scope(); var snapshot = await ExplorerCatalog.LoadAsync(scope, Rpc, default);
        Assert.True(snapshot.Complete, snapshot.Notice); Assert.Equal(112, snapshot.Rows.Length);
        Assert.All(snapshot.Rows, row => Assert.Equal("Area", row.SourcePath[0]));
        var sorted = ExplorerCatalog.Sort(snapshot.Rows, ExplorerSort.Size, false);
        Assert.All(sorted.Take(2), row => Assert.True(row.Entry.IsDirectory));
        Assert.Equal("item-109.txt", sorted[2].Entry.Name); Assert.Equal("item-000.txt", sorted[^1].Entry.Name);
        Assert.Equal(50, sorted.Take(ExplorerCatalog.PageSize).Count());
        Assert.True(operations.Count(x => x == FileOperation.ListDirectory) >= 5);
    }
    [Fact]
    public async Task Filename_search_stays_inside_its_selected_scope_and_never_reads_content_or_excluded_items()
    {
        var snapshot = await ExplorerCatalog.SearchAsync(await Scope(), "hABaRi", Rpc, default);
        Assert.True(snapshot.Complete, snapshot.Notice); var row = Assert.Single(snapshot.Rows);
        Assert.Equal("Area / Nested / Habari-文書.txt", row.RelativePath);
        Assert.Equal(3, row.SourcePath.Length); Assert.DoesNotContain(FileOperation.ReadRange, operations); Assert.DoesNotContain(FileOperation.PrepareDownload, operations);
        Assert.All(row.Route, entry => Assert.Equal(row.Entry.GrantId, entry.GrantId));
        var nested = row.Route[..^1]; var loaded = await ExplorerCatalog.LoadAsync(nested, Rpc, default); Assert.Single(loaded.Rows);
        var selectedFolder = new ExplorerRow(nested[^1], nested);
        Assert.Same(selectedFolder, Assert.Single(ExplorerCatalog.NormalizeSelection([row, selectedFolder])));
    }
    [Fact]
    public async Task Canceling_discovery_closes_only_its_own_cursor_and_releases_native_directory_handles()
    {
        var scope = await Scope(); using var cancellation = new CancellationTokenSource();
        async Task<FileResponse> Cancel(FileRequest value, CancellationToken ct)
        {
            var result = await Rpc(value, ct);
            if (value.Operation == FileOperation.ListDirectory) cancellation.Cancel();
            return result;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorerCatalog.LoadAsync(scope, Cancel, cancellation.Token));
        Assert.Contains(FileOperation.CloseListing, operations);
        Directory.Move(Path.Combine(source, "Area"), Path.Combine(source, "Moved"));
        Assert.True(Directory.Exists(Path.Combine(source, "Moved", "Nested")));
    }
    [Fact]
    public async Task Cursor_cleanup_cannot_close_another_peer_or_a_different_node()
    {
        var scope = await Scope(); var first = await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = scope[^1].Id }, default);
        Assert.NotEmpty(first.Continuation);
        await service.ExecuteAsync(new string('d', 64), new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.CloseListing,
            NodeId = scope[^1].Id, Continuation = first.Continuation }, default);
        var next = await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = scope[^1].Id, Continuation = first.Continuation }, default);
        Assert.Equal(FileStatus.Ok, next.Status); Assert.NotEmpty(next.Continuation);
        await Rpc(new FileRequest { Operation = FileOperation.CloseListing, NodeId = scope[0].Id, Continuation = first.Continuation }, default);
        var third = await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = scope[^1].Id, Continuation = first.Continuation }, default);
        Assert.Equal(FileStatus.Ok, third.Status);
        await Rpc(new FileRequest { Operation = FileOperation.CloseListing, NodeId = scope[^1].Id, Continuation = first.Continuation }, default);
        Assert.Equal(FileStatus.StaleReference, (await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = scope[^1].Id, Continuation = first.Continuation }, default)).Status);
    }
    [Fact]
    public async Task Revoked_permissions_and_forged_parent_metadata_do_not_become_search_results()
    {
        var scope = await Scope(); grants.RevokePeer(peer);
        await Assert.ThrowsAsync<StorageException>(() => ExplorerCatalog.LoadAsync(scope, Rpc, default));
        grants.Add(peer, source, label: "Approved documents"); scope = await Scope();
        async Task<FileResponse> Forged(FileRequest value, CancellationToken ct)
        {
            var response = await Rpc(value, ct);
            if (value.Operation == FileOperation.ListDirectory && response.Entries.Count > 0) response.Entries[0].ParentId = ProtocolRules.NewToken();
            return response;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerCatalog.SearchAsync(scope, "item", Forged, default));
        Assert.Contains(FileOperation.CloseListing, operations);
        async Task<FileResponse> ForgedFolder(FileRequest value, CancellationToken ct)
        {
            var response = await Rpc(value, ct);
            if (value.Operation == FileOperation.GetMetadata) response.Entries[0].ParentId = ProtocolRules.NewToken();
            return response;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerCatalog.LoadAsync(scope, ForgedFolder, default));
    }
    [Fact]
    public async Task A_manifest_also_accepts_a_stable_cursor_across_many_pages_without_duplicate_files()
    {
        var scope = await Scope(); var plan = await TransferManifests.BuildAsync([new(scope[^1], ["Area"])], Rpc, default);
        Assert.Equal(114, plan.Items.Length); Assert.Equal(111, plan.Files);
    }
    [Fact]
    public async Task Search_has_a_bounded_result_limit_and_marks_partial_results_instead_of_claiming_completion()
    {
        var root = new FileEntry { Id = ProtocolRules.NewToken(), GrantId = ProtocolRules.NewToken(), Name = "Root", IsRoot = true, IsDirectory = true,
            Permissions = (uint)StoragePermission.ReadOnly, Version = new('a', 64) };
        var offset = 0; var closed = false; var cursor = ProtocolRules.NewToken();
        Task<FileResponse> Fake(FileRequest value, CancellationToken ct)
        {
            var result = new FileResponse { RequestId = ProtocolRules.NewToken() };
            if (value.Operation == FileOperation.GetMetadata) result.Entries.Add(root.Clone());
            if (value.Operation == FileOperation.CloseListing) closed = true;
            if (value.Operation == FileOperation.ListDirectory)
            {
                for (var i = 0; i < 32; i++) result.Entries.Add(new FileEntry { Id = ProtocolRules.NewToken(), GrantId = root.GrantId, ParentId = root.Id,
                    Name = $"match-{offset++}", Version = new('b', 64), Permissions = (uint)StoragePermission.ReadOnly });
                result.Continuation = cursor;
            }
            return Task.FromResult(result);
        }
        var snapshot = await ExplorerCatalog.SearchAsync([root], "match", Fake, default);
        Assert.False(snapshot.Complete); Assert.Equal(ExplorerCatalog.MaxMatches, snapshot.Rows.Length); Assert.Contains("limit", snapshot.Notice); Assert.True(closed);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(sandbox, true); }
}
