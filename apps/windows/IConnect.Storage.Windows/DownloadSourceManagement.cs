using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record ServingSource(string Id, string Peer, string Name, long Size, long Prepared, DownloadState State, long ExpiresAt, bool Preparing, bool Expired);

public sealed partial class DownloadSourceStore
{
    public event Action? Changed;
    public bool Preparing { get { lock (gate) return workers.Count != 0; } }
    public IReadOnlyList<ServingSource> All
    {
        get { lock (gate)
        {
            var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            return sources.Values.Select(x => new ServingSource(x.Id, x.Peer, x.Segments[^1], x.Size, x.Prepared, x.State, x.Expires,
                workers.ContainsKey(x.Id), x.Expires <= now)).Reverse().ToArray();
        } }
    }
    private void NotifySources()
    {
        foreach (var observer in Changed?.GetInvocationList() ?? [])
            try { ((Action)observer)(); } catch (Exception) { }
    }
    /// <summary>Local owner cancellation of one prepared read, never deletion or a change to its grant.</summary>
    public async Task StopOwnedAsync(string id)
    {
        try
        {
        var stopping = grants.WithAuthorization(() => { lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
            if (!sources.ContainsKey(id)) return Task.CompletedTask;
            var worker = workers.GetValueOrDefault(id); worker?.Cancellation.Cancel();
            sources.Remove(id); Save(); return worker?.Task ?? Task.CompletedTask;
        } });
        await stopping;
        }
        finally { NotifySources(); }
    }
    /// <summary>Removes only expired operational approvals. Source files and receiver copies stay untouched.</summary>
    public async Task<int> RemoveExpiredAsync()
    {
        try
        {
        var result = grants.WithAuthorization(() => { lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
            var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            var tasks = sources.Values.Where(x => x.Expires <= now).Select(x => workers.GetValueOrDefault(x.Id)?.Task).OfType<Task>().ToArray();
            var removed = PurgeExpired(); if (removed != 0) Save(); return (Removed: removed, Tasks: tasks);
        } });
        await Task.WhenAll(result.Tasks); return result.Removed;
        }
        finally { NotifySources(); }
    }
}
