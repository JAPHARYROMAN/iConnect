using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private int servingChangePending;
    private void NotifyServingSources()
    {
        if (disposed || Interlocked.CompareExchange(ref servingChangePending, 1, 0) != 0) return;
        try { dispatch(() => { try { if (!disposed) Notify(DesktopChange.Transfers); } catch (Exception) { } finally { Volatile.Write(ref servingChangePending, 0); } }); }
        catch (Exception) { Volatile.Write(ref servingChangePending, 0); }
    }
    public IReadOnlyList<ServingSource> ServingSources => downloadSources.All;
    public void RefreshServingSources() => Notify(DesktopChange.Transfers);
    public Task StopServingSource(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this); trust.RequireCurrentOwner(); var epoch = OwnerPolicyEpoch;
        return Task.Run(async () =>
        {
            try { ObjectDisposedException.ThrowIf(disposed, this); trust.OwnerBinding!.Require(epoch); await downloadSources.StopOwnedAsync(id); }
            finally { NotifyServingSources(); }
        });
    }
    public Task<int> RemoveExpiredServingSources()
    {
        ObjectDisposedException.ThrowIf(disposed, this); trust.RequireCurrentOwner(); var epoch = OwnerPolicyEpoch;
        return Task.Run(async () =>
        {
            try { ObjectDisposedException.ThrowIf(disposed, this); trust.OwnerBinding!.Require(epoch); return await downloadSources.RemoveExpiredAsync(); }
            finally { NotifyServingSources(); }
        });
    }
}
