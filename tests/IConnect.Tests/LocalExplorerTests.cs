using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class LocalExplorerTests : IDisposable
{
    private readonly string sandbox, source, state;
    private readonly string peer = new('e', 64);
    public LocalExplorerTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        sandbox = Path.Combine(root!.FullName, ".local", "local-explorer-tests", ProtocolRules.NewToken()); source = Path.Combine(sandbox, "documents"); state = Path.Combine(sandbox, "owner");
        Directory.CreateDirectory(Path.Combine(source, "Reports")); Directory.CreateDirectory(Path.Combine(source, ".ssh"));
        File.WriteAllText(Path.Combine(source, "Reports", "Habari-文書.txt"), "Owner-only local preview.\nSalama!\n");
        File.WriteAllText(Path.Combine(source, ".ssh", "dummy.txt"), "generated fixture, not credentials");
        for (var i = 0; i < 110; i++) File.WriteAllText(Path.Combine(source, $"page-{i:D3}.txt"), new string('x', i + 1));
    }
    [Fact]
    public async Task Offline_local_explorer_pages_sorts_searches_and_previews_without_sharing_a_folder()
    {
        await using var browser = new LocalExplorerController(state);
        await browser.AddAsync(source); Assert.Single(browser.Locations); var root = Assert.Single(browser.Rows).Entry;
        await browser.BrowseAsync(root); Assert.Equal(112, browser.Count); Assert.Equal(3, browser.Pages);
        browser.SetPage(2); Assert.Equal(12, browser.Rows.Count);
        browser.SetSort(ExplorerSort.Size, true); browser.SetLayout(ExplorerLayout.Grid);
        Assert.Equal("page-109.txt", browser.Rows.First(x => !x.Entry.IsDirectory).Entry.Name);
        await browser.SearchAsync("HABARI"); var match = Assert.Single(browser.Rows); Assert.Equal("Reports / Habari-文書.txt", match.RelativePath);
        browser.Select(match.Entry.Id); await browser.InspectAsync(true); Assert.Contains("Owner-only local preview.", browser.Preview);
        await browser.InspectAsync(false); Assert.Equal(match.Entry.Name, browser.Details!.Name);
        Assert.Empty(new StorageGrantStore(state).All); Assert.False(File.Exists(Path.Combine(state, "trusted-peers.json")));
        await browser.SearchAsync("Reports"); await browser.BrowseAsync(Assert.Single(browser.Rows).Entry); Assert.Equal(2, browser.Breadcrumbs.Count);
        Assert.Single(browser.Rows); Assert.Empty(browser.Error);
    }
    [Fact]
    public async Task Local_sensitive_names_do_not_bypass_remote_exclusions_or_make_local_references_usable_remotely()
    {
        using var local = new LocalFileLocations(state); var location = local.Add(source);
        var root = (await local.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var found = await ExplorerCatalog.SearchAsync([root], "dummy", local.RequestAsync, default); var privateFile = Assert.Single(found.Rows).Entry;
        var grants = new StorageGrantStore(state); using var remote = new WindowsFileService(grants);
        async Task<FileResponse> Rpc(FileRequest request, CancellationToken ct)
        { var value = request.Clone(); value.RequestId = ProtocolRules.NewToken(); return await remote.ExecuteAsync(peer, value, ct); }
        Assert.Empty((await Rpc(new() { Operation = FileOperation.ListRoots }, default)).Entries);
        Assert.Equal(FileStatus.StaleReference, (await Rpc(new() { Operation = FileOperation.ReadRange, NodeId = privateFile.Id, ExpectedVersion = privateFile.Version, Length = 32 }, default)).Status);
        grants.Add(peer, source); var remoteRoot = (await Rpc(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        Assert.Empty((await ExplorerCatalog.SearchAsync([remoteRoot], "dummy", Rpc, default)).Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.RequestAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = root.Id, Name = "unexpected" }, default));
        Assert.False(Directory.Exists(Path.Combine(source, "unexpected")));
        local.Remove(location.Id); Assert.Single(grants.All);
        Assert.Equal(FileStatus.AccessDenied, (await local.RequestAsync(new() { Operation = FileOperation.GetMetadata, NodeId = privateFile.Id }, default)).Status);
    }
    [Fact]
    public async Task Restart_keeps_the_selected_root_identity_and_replacement_requires_reselection()
    {
        string id;
        using (var first = new LocalFileLocations(state)) { id = first.Add(source).Id; }
        Directory.Move(source, source + "-previous"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "replacement.txt"), "different root");
        using var restarted = new LocalFileLocations(state);
        Assert.Equal(id, restarted.All.Single().Id); Assert.Empty((await restarted.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries);
        restarted.Add(source); var root = (await restarted.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        Assert.Equal("replacement.txt", (await restarted.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single().Name);
    }
    [Fact]
    public async Task Failed_shortcut_save_does_not_publish_a_location_and_damaged_shortcuts_have_local_recovery()
    {
        using (var local = new LocalFileLocations(state))
        {
            var blocker = Path.Combine(state, "local-locations.json.new"); Directory.CreateDirectory(blocker);
            Assert.ThrowsAny<Exception>(() => local.Add(source)); Assert.Empty(local.All); Directory.Delete(blocker);
        }
        var grants = new StorageGrantStore(state); var shared = grants.Add(peer, source);
        var path = Path.Combine(state, "local-locations.json"); File.WriteAllText(path, "{damaged");
        await using var browser = new LocalExplorerController(state); Assert.False(browser.Healthy);
        await browser.ResetAsync(); Assert.True(browser.Healthy); Assert.Empty(browser.Locations);
        Assert.Single(Directory.GetFiles(state, "local-locations.json.recovery-*")); Assert.Equal(shared.Id, new StorageGrantStore(state).All.Single().Id);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { new LocalFileLocation(ProtocolRules.NewToken(), "Unsafe shortcut", source, "\\\\server\\share", "identity") }));
        Assert.Throws<InvalidDataException>(() => new LocalFileLocations(state));
    }
    public void Dispose() => Directory.Delete(sandbox, true);
}
