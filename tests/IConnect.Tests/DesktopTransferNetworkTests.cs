using IConnect.AppModel;
using IConnect.Core;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Unavailable_network_settings_pause_remote_content_without_blocking_the_local_explorer()
    {
        using var held = new WindowsTransferNetworkSettings(state);
        await using var model = new DesktopController(state, action => action());
        Assert.Null(model.TransferNetwork); Assert.NotEmpty(model.TransferNetworkIssue); Assert.Throws<StorageException>(model.NetworkTraffic.RequireContent);
        var documents = Path.Combine(directory, "generated-local-documents"); Directory.CreateDirectory(documents); File.WriteAllText(Path.Combine(documents, "salama.txt"), "Generated local data");
        await model.LocalFiles.AddAsync(documents); await model.LocalFiles.BrowseAsync(Assert.Single(model.LocalFiles.Rows).Entry);
        Assert.Equal("salama.txt", Assert.Single(model.LocalFiles.Rows).Entry.Name); Assert.Empty(model.Grants); Assert.False(model.Connected);
    }
    [Fact]
    public async Task Controller_uses_the_saved_owner_policy_and_reopen_keeps_network_permissions_separate_from_storage()
    {
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.NotNull(model.TransferNetwork); Assert.Same(model.TransferNetwork.Policy, model.NetworkTraffic);
            model.TransferNetwork.Set(new(131072, true)); Assert.Throws<StorageException>(model.NetworkTraffic.RequireContent);
        }
        await using var reopened = new DesktopController(state, action => action()); Assert.Equal(new(131072, true), reopened.TransferNetwork!.Preferences);
        reopened.NetworkTraffic.ObserveNetwork(TransferNetworkCost.Unrestricted); reopened.NetworkTraffic.RequireContent(); Assert.Empty(reopened.Grants); Assert.Empty(reopened.Peers);
    }
}
