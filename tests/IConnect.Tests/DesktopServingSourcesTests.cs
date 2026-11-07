using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Offline_source_inventory_stops_only_the_chosen_read_and_retains_it_across_controller_restart()
    {
        using var peer = DeviceIdentity.Ephemeral("Generated receiving device"); var owner = SeedOwnerTrust(peer);
        var shared = Path.Combine(directory, "Generated source files"); Directory.CreateDirectory(shared);
        var bytes = Encoding.UTF8.GetBytes("Generated original file kept after source stop."); var original = Path.Combine(shared, "source.txt"); File.WriteAllBytes(original, bytes);
        var grants = new StorageGrantStore(state, ownerBinding: new WindowsOwnerBinding(state, owner)); var grant = grants.Add(peer.Id, shared);
        string first, second;
        await using (var sources = new DownloadSourceStore(grants, state))
        {
            using var root = SecureWindowsDirectory.OpenRoot(shared); using var file = root.OpenChild("source.txt");
            first = sources.Prepare(peer.Id, grant.Id, ["source.txt"], file.Info.Identity, file.Info.Version, bytes.Length, "offline-fixture").Id;
            second = sources.Prepare(peer.Id, grant.Id, ["source.txt"], file.Info.Identity, file.Info.Version, bytes.Length, "offline-fixture").Id;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (sources.Preparing || sources.All.Any(x => x.State != DownloadState.DownloadReady)) await Task.Delay(10, timeout.Token);
        }
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.Equal(2, model.ServingSources.Count); Assert.All(model.ServingSources, x => Assert.Equal(peer.Id, x.Peer));
            Assert.False(model.Connected); Assert.False(model.PeerAuthenticated); model.RefreshServingSources();
            await model.StopServingSource(first); Assert.Equal(second, Assert.Single(model.ServingSources).Id);
            Assert.Equal(0, await model.RemoveExpiredServingSources()); Assert.Equal(bytes, File.ReadAllBytes(original));
            Assert.Equal(grant.Id, Assert.Single(model.Grants).Id);
        }
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.Equal(second, Assert.Single(model.ServingSources).Id); await model.StopServingSource(second); Assert.Empty(model.ServingSources);
            Assert.Equal(bytes, File.ReadAllBytes(original)); Assert.False(model.Connected);
        }
    }
}
