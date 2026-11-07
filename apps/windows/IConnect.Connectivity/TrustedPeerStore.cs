using System.Text.Json;
using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.Connectivity;

/// <summary>Local trust is available and revocable even when the coordinator is offline.</summary>
public sealed class TrustedPeerStore : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream lease;
    private Dictionary<string, ProofClient.TrustedPeer> peers;
    private bool disposed;
    private string ownerEpoch = "";
    public WindowsOwnerBinding? OwnerBinding { get; private set; }

    public TrustedPeerStore(string directory, string? ownId = null)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "peers.json");
        lease = new FileStream(Path.Combine(directory, "peers.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (ownId is not null) { OwnerBinding = new(directory, ownId); ownerEpoch = OwnerBinding.Epoch; }
            peers = Read();
        }
        catch { lease.Dispose(); throw; }
    }
    private Dictionary<string, ProofClient.TrustedPeer> Read()
    {
        if (OwnerBinding?.Healthy == false) return [];
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Trust store exceeds limit.");
            var loaded = OwnerBinding is null ? File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ProofClient.TrustedPeer>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid trust store.") : [] : OwnerBinding.ReadRecords<ProofClient.TrustedPeer>(path, 1024 * 1024, ownerEpoch);
            if (loaded.Count > 128) throw new InvalidDataException("Too many pinned devices.");
            foreach (var pair in loaded)
            {
                var peer = pair.Value;
                if (peer is null || peer.Id != pair.Key || peer.PublicKey is null || peer.Name is null ||
                    ProtocolRules.DeviceId(Convert.FromBase64String(peer.PublicKey)) != peer.Id ||
                    peer.Name.Length is < 1 or > 64 || peer.Name.Any(char.IsControl) || peer.ApprovalEpoch.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(peer.ApprovalEpoch))
                    throw new InvalidDataException("Invalid pinned device identity.");
            }
            return loaded;
        }
        catch (Exception ex) when (OwnerBinding is not null && (StateRecovery.IsRecordFailure(ex) || ex is StorageException))
        { OwnerBinding.RejectMetadata("Pinned identities need local owner recovery. " + ex.Message); return []; }
    }
    private bool Current => OwnerBinding is null || OwnerBinding.Healthy && OwnerBinding.Epoch == ownerEpoch;
    public bool OwnerReady { get { lock (gate) return OwnerBinding is not null && Current; } }
    public void RequireCurrentOwner() { lock (gate) (OwnerBinding ?? throw new InvalidOperationException("Bind the local identity first.")).Require(ownerEpoch); }
    public IReadOnlyCollection<ProofClient.TrustedPeer> All { get { lock (gate) return Current ? peers.Values.ToArray() : []; } }
    public bool TryGet(string id, out ProofClient.TrustedPeer peer) { lock (gate) { if (!Current) { peer = null!; return false; } return peers.TryGetValue(id, out peer!); } }
    public void BindOwnerIdentity(string ownId)
    {
        lock (gate)
        {
            if (OwnerBinding is not null) { if (OwnerBinding.OwnerId != ownId) throw new InvalidDataException("Trust belongs to another local identity."); return; }
            OwnerBinding = new(Path.GetDirectoryName(path)!, ownId); ownerEpoch = OwnerBinding.Epoch; peers = Read();
        }
    }
    public void ResetOwnerPolicy()
    {
        lock (gate)
        {
            (OwnerBinding ?? throw new InvalidOperationException("Bind the Windows identity before recovery.")).Reset();
            ownerEpoch = OwnerBinding.Epoch; peers = Read();
        }
    }
    public void Add(ProofClient.TrustedPeer peer)
    {
        if (ProtocolRules.DeviceId(Convert.FromBase64String(peer.PublicKey)) != peer.Id ||
            peer.Name.Length is < 1 or > 64 || peer.Name.Any(char.IsControl) || peer.ApprovalEpoch.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(peer.ApprovalEpoch)) throw new InvalidDataException("Invalid paired device.");
        lock (gate)
        {
            var updated = new Dictionary<string, ProofClient.TrustedPeer>(peers) { [peer.Id] = peer };
            if (updated.Count > 128) throw new InvalidDataException("Remove an existing device before pairing another.");
            Save(updated); peers = updated;
        }
    }
    public void Remove(string id)
    {
        lock (gate)
        {
            if (!peers.ContainsKey(id)) throw new InvalidDataException("Unknown paired device.");
            var updated = new Dictionary<string, ProofClient.TrustedPeer>(peers);
            updated.Remove(id); Save(updated); peers = updated;
        }
    }
    public string EnsureUploadScope(string id)
    {
        lock (gate)
        {
            RequireCurrentOwner();
            if (!peers.TryGetValue(id, out var peer)) throw new InvalidDataException("Choose a current paired device.");
            if (peer.ApprovalEpoch.Length != 0) return peer.ApprovalEpoch;
            var updated = new Dictionary<string, ProofClient.TrustedPeer>(peers) { [id] = peer with { ApprovalEpoch = ProtocolRules.NewToken() } };
            Save(updated); peers = updated; return peers[id].ApprovalEpoch;
        }
    }
    public void InitializeUploadScopes()
    {
        lock (gate)
        {
            RequireCurrentOwner(); if (peers.Values.All(x => x.ApprovalEpoch.Length != 0)) return;
            var updated = peers.ToDictionary(x => x.Key, x => x.Value.ApprovalEpoch.Length == 0 ? x.Value with { ApprovalEpoch = ProtocolRules.NewToken() } : x.Value);
            Save(updated); peers = updated;
        }
    }
    private void Save(Dictionary<string, ProofClient.TrustedPeer> updated)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (OwnerBinding is not null) { OwnerBinding.SaveRecords(path, ownerEpoch, updated); return; }
        var temporary = path + ".new";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, updated, new JsonSerializerOptions { WriteIndented = true });
            output.Flush(true);
        }
        AtomicStateFile.Commit(temporary, path);
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lease.Dispose(); } }
}
