using IConnect.Core;

namespace IConnect.Storage.Windows;

/// <summary>One profile's private outgoing inventory and preparation/sending workers.</summary>
public sealed class OutgoingCopyResources
{
    internal const string Single = "single", Batch = "batch";
    internal sealed record Reservation(string Id, string Peer, long Bytes, long Pending);
    private sealed class Store
    {
        public object? Owner;
        public bool Loaded, Uncertain;
        public long RetainedBytes;
        public Dictionary<string, Reservation> Rows = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Store> stores;
    private readonly SemaphoreSlim workers = new(2, 2);
    private string? profile;
    public OutgoingCopyResources() : this([Single, Batch]) { }
    private OutgoingCopyResources(string[] kinds) => stores = kinds.ToDictionary(x => x, _ => new Store());
    internal static OutgoingCopyResources Isolated(string kind) => new([kind]);
    public long ReservedBytes { get { lock (gate) return stores.Values.Sum(x => x.Rows.Values.Sum(r => r.Bytes) + x.RetainedBytes); } }
    // Unreadable preserved ownership cannot assign bytes to a peer. Charge them to every peer.
    public long ReservedForPeer(string peer) { lock (gate) return stores.Values.Sum(x => x.Rows.Values.Where(r => r.Peer == peer).Sum(r => r.Bytes) + x.RetainedBytes); }
    public int ActiveWorkers => 2 - workers.CurrentCount;
    public bool InventoryReady { get { lock (gate) return stores.Values.All(x => x.Loaded && !x.Uncertain); } }
    internal Participant Open(string kind, string location)
    {
        lock (gate)
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            if (profile is not null && !profile.Equals(canonical, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Outgoing resource accounting belongs to another profile.");
            if (!stores.TryGetValue(kind, out var store) || store.Owner is not null) throw new InvalidOperationException("This outgoing inventory is already open or is not configured.");
            profile = canonical; var token = new object(); store.Owner = token; store.Loaded = false;
            return new(this, kind, token);
        }
    }
    internal sealed class Participant(OutgoingCopyResources budget, string kind, object token) : IDisposable
    {
        private Store Owned()
        {
            var store = budget.stores[kind];
            if (!ReferenceEquals(store.Owner, token)) throw new ObjectDisposedException("Outgoing inventory");
            return store;
        }
        private static Dictionary<string, Reservation> Validate(IEnumerable<Reservation> values)
        {
            var rows = new Dictionary<string, Reservation>();
            foreach (var row in values)
            {
                if (!UploadBatchRules.CanonicalToken(row.Id) || row.Peer.Length != 64 || !row.Peer.All(char.IsAsciiHexDigitLower) || row.Bytes is < 0 or > UploadRules.PeerQuota ||
                    row.Pending < 0 || row.Pending > row.Bytes || !rows.TryAdd(row.Id, row)) throw new InvalidDataException("Invalid outgoing resource reservation.");
            }
            if (rows.Count > UploadBatchRules.MaxPlans) throw new InvalidDataException("Too many private outgoing reservations.");
            return rows;
        }
        internal void Recover(IEnumerable<Reservation> values, bool uncertain, long retainedBytes = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
            var rows = Validate(values);
            lock (budget.gate) { var store = Owned(); store.Rows = rows; store.Loaded = true; store.Uncertain = uncertain; store.RetainedBytes = retainedBytes; }
        }
        internal void Replace(IEnumerable<Reservation> values, Func<long>? availableSpace = null)
        {
            var rows = Validate(values);
            lock (budget.gate)
            {
                var store = Owned();
                var growth = rows.Values.Any(row => !store.Rows.TryGetValue(row.Id, out var old) || row.Peer != old.Peer || row.Bytes > old.Bytes || row.Pending > old.Pending);
                if (growth)
                {
                    if (budget.stores.Values.Any(x => !x.Loaded || x.Uncertain)) throw new IOException("Review the private outgoing inventories before preparing another copy.");
                    var all = budget.stores.Where(x => x.Key != kind).SelectMany(x => x.Value.Rows.Values).Concat(rows.Values).ToArray();
                    var retained = budget.stores.Values.Sum(x => x.RetainedBytes);
                    if (all.Sum(x => x.Bytes) + retained > UploadRules.PeerQuota * 4 || all.GroupBy(x => x.Peer).Any(x => x.Sum(r => r.Bytes) + retained > UploadRules.PeerQuota))
                        throw new IOException("Shared private outgoing byte reservations are full. Review retained file or folder copies.");
                    // Read free space while holding the same lock that admits reservations.
                    // Separate check-then-reserve operations can overbook competing stores.
                    if (availableSpace is not null && rows.Values.Any(row => row.Pending > (store.Rows.GetValueOrDefault(row.Id)?.Pending ?? 0)) &&
                        all.Sum(x => x.Pending) > Math.Max(0, availableSpace() - 64L * 1024 * 1024))
                        throw new IOException("Not enough private disk space for the shared outgoing reservations.");
                }
                store.Rows = rows;
            }
        }
        internal void Uncertain() { lock (budget.gate) Owned().Uncertain = true; }
        internal long OtherPending(string? id = null)
        {
            lock (budget.gate) { _ = Owned(); return budget.stores.Sum(store => store.Value.Rows.Values.Where(row => store.Key != kind || row.Id != id).Sum(row => row.Pending)); }
        }
        internal IDisposable? TryWorker() { lock (budget.gate) { _ = Owned(); return budget.TryWorker(); } }
        public void Dispose()
        {
            lock (budget.gate)
            {
                var store = budget.stores[kind]; if (!ReferenceEquals(store.Owner, token)) return;
                // Copies still exist on disk. Retain their reservation until the next owner loads them.
                store.Owner = null; store.Loaded = false;
            }
        }
    }
    public IDisposable? TryWorker() => workers.Wait(0) ? new Worker(workers) : null;
    private sealed class Worker(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) semaphore.Release(); }
    }
}
