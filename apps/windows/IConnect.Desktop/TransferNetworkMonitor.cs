using IConnect.Core;
using Windows.Networking.Connectivity;

namespace IConnect.Desktop;

/// <summary>Conservative connected-profile cost; it does not identify the chosen WebRTC route.</summary>
internal sealed class TransferNetworkMonitor : IDisposable
{
    private readonly object gate = new();
    private readonly Action<TransferNetworkCost> publish;
    private readonly Func<TransferNetworkCost> read;
    private readonly System.Threading.Timer timer;
    private bool disposed;
    private bool subscribed;
    private bool reading;
    public TransferNetworkMonitor(Action<TransferNetworkCost> publish) : this(publish, ReadCost, true) { }
    internal TransferNetworkMonitor(Action<TransferNetworkCost> publish, Func<TransferNetworkCost> read, bool watchSystem)
    {
        this.publish = publish; this.read = read;
        if (watchSystem) try { NetworkInformation.NetworkStatusChanged += Changed; subscribed = true; } catch (Exception) { }
        timer = new(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }
    private void Changed(object sender) => Refresh();
    private void Refresh()
    {
        lock (gate) { if (disposed || reading) return; reading = true; }
        try
        {
            TransferNetworkCost result; try { result = read(); } catch (Exception) { result = TransferNetworkCost.Unknown; }
            lock (gate) { if (!disposed) try { publish(result); } catch (Exception) { } }
        }
        finally { lock (gate) reading = false; }
    }
    private static TransferNetworkCost ReadCost()
    {
        var profiles = NetworkInformation.GetConnectionProfiles().Where(x => x.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None).ToArray();
        if (profiles.Length == 0) return TransferNetworkCost.Unknown;
        var costs = profiles.Select(x => x.GetConnectionCost()).ToArray();
        return costs.Any(x => x.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || x.Roaming || x.OverDataLimit || x.ApproachingDataLimit || x.BackgroundDataUsageRestricted)
            ? TransferNetworkCost.Metered : costs.All(x => x.NetworkCostType == NetworkCostType.Unrestricted) ? TransferNetworkCost.Unrestricted : TransferNetworkCost.Unknown;
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        if (subscribed) try { NetworkInformation.NetworkStatusChanged -= Changed; } catch (Exception) { }
        timer.Dispose();
    }
}
