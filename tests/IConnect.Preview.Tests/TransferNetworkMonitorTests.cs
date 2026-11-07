using IConnect.Core;
using IConnect.Desktop;
using Windows.Networking.Connectivity;
using Xunit;
using Xunit.Abstractions;

namespace IConnect.Preview.Tests;

public sealed class TransferNetworkMonitorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Native_Windows_cost_queries_and_monitor_run_without_opening_a_window()
    {
        var profiles = NetworkInformation.GetConnectionProfiles();
        foreach (var profile in profiles.Where(x => x.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None))
        {
            var cost = profile.GetConnectionCost(); Assert.InRange((int)cost.NetworkCostType, 0, 3);
            _ = cost.Roaming; _ = cost.OverDataLimit; _ = cost.ApproachingDataLimit; _ = cost.BackgroundDataUsageRestricted;
        }
        var seen = new TaskCompletionSource<TransferNetworkCost>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new TransferNetworkMonitor(value => seen.TrySetResult(value)); var value = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Enum.IsDefined(value)); output.WriteLine("Native Windows connected-network cost observation: " + value + ". No interface identifiers or names recorded.");
    }
    [Fact]
    public async Task A_slow_cost_query_cannot_block_shutdown_or_publish_a_late_observation()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var published = 0;
        var monitor = new TransferNetworkMonitor(_ => Interlocked.Increment(ref published), () =>
        { entered.TrySetResult(); release.Wait(); exited.TrySetResult(); return TransferNetworkCost.Unrestricted; }, false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Run(monitor.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, Volatile.Read(ref published));
        }
        finally { release.Set(); monitor.Dispose(); }
    }
}
