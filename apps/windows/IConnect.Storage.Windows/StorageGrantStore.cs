using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record StorageGrant(string Id, string PeerId, string Label, string DisplayPath,
    string CanonicalPath, string RootIdentity, StoragePermission Permissions,
    string[] Exclusions, long? ExpiresAt, bool Enabled = true);

public sealed class StorageGrantStore : IWindowsRecoveryAuthority
{
    private readonly string path;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    internal TimeProvider Clock => clock;
    TimeProvider IWindowsRecoveryAuthority.Clock => Clock;
    string IWindowsRecoveryAuthority.OwnerScope => OwnerScope;
    void IWindowsRecoveryAuthority.RequirePublicLocation(string canonicalPath) => RequirePublicLocation(canonicalPath);
    void IWindowsRecoveryAuthority.ValidateAccess(StorageGrant grant, IReadOnlyList<string> segments) => ValidateAccess(grant, segments);
    internal IAttachmentChecks AttachmentChecks { get; init; } = new WindowsAttachmentChecks();
    private Dictionary<string, StorageGrant> grants;
    private WindowsMutations? mutations;
    private WindowsRecovery? deleted;
    private WindowsUploads? uploads;
    private WindowsBatchPlans? batchPlans;
    private readonly SemaphoreSlim incomingVerification = new(2, 2);
    private sealed class IncomingSlot(SemaphoreSlim slots) : IDisposable
    { private int released; public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) slots.Release(); } }
    internal IDisposable? ReserveIncomingVerification() => incomingVerification.Wait(0) ? new IncomingSlot(incomingVerification) : null;
    private WindowsOwnerBinding? ownerBinding;
    private string ownerEpoch = "";
    internal WindowsMutations Mutations => mutations ??= new(this, Path.GetDirectoryName(path)!);
    public WindowsRecovery Deleted { get { lock (gate) return deleted ??= new(this, Path.GetDirectoryName(path)!); } }
    public WindowsUploads Uploads { get { lock (gate) return uploads ??= new(this, Path.GetDirectoryName(path)!); } }
    public WindowsBatchPlans BatchPlans { get { lock (gate) return batchPlans ??= new(this, Path.GetDirectoryName(path)!); } }
    internal string OwnerScope => ownerBinding is null ? "local" : ownerBinding.OwnerId + ":" + ownerEpoch;
    internal void RequirePublicLocation(string canonicalPath) => ownerBinding?.RequirePublicLocation(canonicalPath);
    public IReadOnlyList<StateRecoveryIssue> MutationRecoveryIssues { get { lock (gate) return Mutations.RecoveryIssues; } }
    public int MutationPreservedRecords { get { lock (gate) return Mutations.PreservedRecords; } }
    public void ArchiveMutationRecovery(string id) { lock (gate) Mutations.ArchiveRecovery(id); }
    public void ClearMutationPreservedRecords() { lock (gate) Mutations.ClearPreservedRecords(); }
    public event Action? Changed;
    public Action<WindowsActivityEvent>? Activity { get; set; }
    internal void RecordActivity(WindowsActivityEvent? value)
        => WindowsActivity.Publish(Activity, value);
    public void BindOwnerPolicy(WindowsOwnerBinding binding)
    {
        lock (gate)
        {
            if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(path)!)).Equals(binding.ProfileDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Remote storage and trust must use the same owner profile.");
            if (ownerBinding is not null) { ownerBinding.Require(ownerEpoch); if (ownerBinding.OwnerId != binding.OwnerId || ownerEpoch != binding.Epoch) throw new InvalidDataException("Storage belongs to another owner generation."); return; }
            ownerBinding = binding; ownerEpoch = binding.Epoch; grants = Read(); ownerBinding.Require(ownerEpoch);
            uploads?.OwnerScopeChanged();
            batchPlans?.OwnerScopeChanged();
        }
    }
    internal T WithAuthorization<T>(Func<T> operation) { lock (gate) return ownerBinding is null ? operation() : ownerBinding.Authorized(ownerEpoch, operation); }
    T IWindowsFileAuthority.WithAuthorization<T>(Func<T> operation) => WithAuthorization(operation);
    void IWindowsFileAuthority.ValidateLocation(StorageGrant grant, IReadOnlyList<string> segments) => ValidateAccess(grant, segments);
    internal void ValidateAccess(StorageGrant grant, IReadOnlyList<string> segments)
    {
        ValidateLocation(grant, segments);
        ownerBinding?.RequirePublicLocation(grant.CanonicalPath.TrimEnd('\\') + (segments.Count == 0 ? "" : "\\" + string.Join('\\', segments)));
    }

    public StorageGrantStore(string directory, TimeProvider? timeProvider = null, WindowsOwnerBinding? ownerBinding = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "storage-grants.json");
        this.ownerBinding = ownerBinding; ownerEpoch = ownerBinding?.Epoch ?? "";
        if (ownerBinding is not null && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(ownerBinding.ProfileDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Storage and identity must use the same local profile.");
        grants = Read();
    }
    private Dictionary<string, StorageGrant> Read()
    {
        if (ownerBinding?.Healthy == false) return [];
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Storage grant configuration exceeds its limit.");
            var loaded = ownerBinding is null ? File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, StorageGrant>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Storage grant configuration is invalid.") : [] : ownerBinding.ReadRecords<StorageGrant>(path, 4 * 1024 * 1024, ownerEpoch);
            if (loaded.Count > 512) throw new InvalidDataException("Too many storage approvals.");
            foreach (var pair in loaded)
            {
                var grant = pair.Value;
                if (grant is null || pair.Key != grant.Id || !ProtocolRules.TokenPattern().IsMatch(pair.Key) ||
                    grant.PeerId is null || grant.PeerId.Length != 64 || grant.PeerId.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                    grant.CanonicalPath is null || !SecureWindowsDirectory.IsLocalVolumePath(grant.CanonicalPath) ||
                    grant.RootIdentity is null || grant.RootIdentity.Length is < 1 or > 256 || grant.RootIdentity.Any(char.IsControl) ||
                    grant.DisplayPath is null || grant.DisplayPath.Length is < 3 or > 8192 || grant.DisplayPath[1] != ':' || grant.DisplayPath[2] != '\\' ||
                    grant.Exclusions is null || grant.Exclusions.Length > 512 || grant.ExpiresAt is < 0 or > StorageRules.MaxTimestamp)
                    throw new InvalidDataException("Storage grant configuration is invalid.");
                ValidateLabel(grant.Label); ValidatePermissions(grant.Permissions);
                foreach (var excluded in grant.Exclusions) if (excluded != NormalizeExclusion(excluded)) throw new InvalidDataException("Invalid storage exclusion.");
            }
            return loaded;
        }
        catch (Exception ex) when (ownerBinding is not null && (StateRecovery.IsRecordFailure(ex) || ex is StorageException))
        { ownerBinding.RejectMetadata("Storage approvals need local owner recovery. " + ex.Message); return []; }
    }
    public void ReloadOwnerScope()
    {
        lock (gate)
        {
            ownerEpoch = ownerBinding?.Epoch ?? ""; ownerBinding?.Require(ownerEpoch); grants = Read();
            uploads?.OwnerScopeChanged();
            batchPlans?.OwnerScopeChanged();
        }
        Changed?.Invoke();
    }
    private bool Current => ownerBinding is null || ownerBinding.Healthy && ownerBinding.Epoch == ownerEpoch;
    public IReadOnlyList<StorageGrant> All { get { lock (gate) return Current ? grants.Values.Select(Clone).ToArray() : []; } }
    public static SecureWindowsDirectory OpenLocalDestination(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' ||
            IsSensitiveSegments(full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Skip(1)))
            throw new StorageException(FileStatus.AccessDenied, "Choose a normal local folder for downloaded content.");
        var root = SecureWindowsDirectory.OpenRoot(full);
        var segments = root.Info.CanonicalPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Skip(2).ToArray();
        if (IsSensitiveSegments(segments) || IsSystemTree(segments))
        { root.Dispose(); throw new StorageException(FileStatus.AccessDenied, "Private and operating-system locations cannot receive remote content."); }
        return root;
    }
    public IReadOnlyList<StorageGrant> ForPeer(string peerId)
    {
        lock (gate) return !Current ? [] : grants.Values.Where(x => x.PeerId == peerId && x.Enabled && PublicRoot(x) &&
            (x.ExpiresAt is null || x.ExpiresAt > clock.GetUtcNow().ToUnixTimeMilliseconds()))
            .Select(Clone).ToArray();
    }

    public StorageGrant Add(string peerId, string localDirectory, StoragePermission permissions = StoragePermission.ReadOnly,
        string? label = null, IEnumerable<string>? exclusions = null, DateTimeOffset? expiresAt = null, bool enabled = true)
    {
        if (peerId.Length != 64 || peerId.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Choose a paired device.", nameof(peerId));
        var full = Path.GetFullPath(localDirectory);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new StorageException(FileStatus.Unsupported, "Choose a local drive or folder, not a network or device path.");
        if (IsSensitiveSegments(full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1)))
            throw new StorageException(FileStatus.AccessDenied, "Credential and application-private locations cannot be shared.");
        using var root = SecureWindowsDirectory.OpenRoot(full);
        ownerBinding?.RequirePublicLocation(root.Info.CanonicalPath);
        var canonicalSegments = root.Info.CanonicalPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(2).ToArray();
        if (IsSensitiveSegments(canonicalSegments) || IsSystemTree(canonicalSegments))
            throw new StorageException(FileStatus.AccessDenied, "Credential, private, and operating-system locations cannot be shared.");
        var displayLabel = label ?? (Path.GetFileName(full.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : full);
        ValidateLabel(displayLabel);
        ValidatePermissions(permissions);
        if (expiresAt is { } expiry && expiry <= clock.GetUtcNow()) throw new ArgumentException("Access expiry must be in the future.");
        var normalized = (exclusions ?? []).Select(NormalizeExclusion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length > 512) throw new ArgumentException("Choose at most 512 excluded paths.");
        var grant = new StorageGrant(ProtocolRules.NewToken(), peerId, displayLabel, full, root.Info.CanonicalPath,
            root.Info.Identity, permissions, normalized,
            expiresAt?.ToUnixTimeMilliseconds(), enabled);
        lock (gate)
        {
            if (grants.Count >= 512) throw new StorageException(FileStatus.ResourceExhausted, "The storage grant limit was reached.");
            if (grants.Values.Count(x => x.PeerId == peerId) >= 32)
                throw new StorageException(FileStatus.ResourceExhausted, "This device already has 32 shared locations.");
            var updated = new Dictionary<string, StorageGrant>(grants) { [grant.Id] = grant };
            Save(updated); grants = updated;
        }
        Changed?.Invoke();
        return Clone(grant);
    }

    public StorageGrant Get(string grantId, string peerId)
    {
        lock (gate)
        {
            ownerBinding?.Require(ownerEpoch);
            if (!grants.TryGetValue(grantId, out var grant) || !grant.Enabled || grant.PeerId != peerId ||
                (grant.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow().ToUnixTimeMilliseconds()))
                throw new StorageException(FileStatus.AccessDenied, "Storage access is unavailable, expired, or revoked.");
            ValidateAccess(grant, []);
            return Clone(grant);
        }
    }

    public void Update(string grantId, StoragePermission permissions, IEnumerable<string> exclusions,
        DateTimeOffset? expiresAt, bool enabled, string? label = null)
    {
        ValidatePermissions(permissions);
        if (label is not null) ValidateLabel(label);
        var normalized = exclusions.Select(NormalizeExclusion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length > 512) throw new ArgumentException("Choose at most 512 excluded paths.");
        lock (gate)
        {
            if (!grants.TryGetValue(grantId, out var grant)) throw new KeyNotFoundException("Storage grant is unavailable.");
            var updated = new Dictionary<string, StorageGrant>(grants) { [grantId] = grant with { Permissions = permissions,
                Exclusions = normalized, ExpiresAt = expiresAt?.ToUnixTimeMilliseconds(), Enabled = enabled, Label = label ?? grant.Label } };
            Save(updated); grants = updated;
        }
        Changed?.Invoke();
    }

    public void Remove(string grantId)
    {
        lock (gate)
        {
            var updated = new Dictionary<string, StorageGrant>(grants); updated.Remove(grantId);
            Save(updated); grants = updated;
        }
        Changed?.Invoke();
    }
    public void RevokePeer(string peerId)
    {
        lock (gate)
        {
            var updated = grants.Where(x => x.Value.PeerId != peerId).ToDictionary(x => x.Key, x => x.Value);
            Save(updated); grants = updated;
        }
        Changed?.Invoke();
    }

    public static void ValidateLocation(StorageGrant grant, IReadOnlyList<string> segments)
    {
        foreach (var segment in segments) StorageRules.ValidateName(segment);
        if (segments.Count > 128 || IsSensitiveSegments(segments))
            throw new StorageException(FileStatus.AccessDenied, "This location is excluded from remote access.");
        if (IsSystemTree(grant.CanonicalPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(2).Concat(segments).ToArray()))
            throw new StorageException(FileStatus.AccessDenied, "Operating-system locations are excluded from remote access.");
        var relative = string.Join('\\', segments);
        if (grant.Exclusions.Any(excluded => relative.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(excluded + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new StorageException(FileStatus.AccessDenied, "The owner excluded this location.");
        // Drive roots keep system/metadata trees outside normal remote operations.
        if (Path.GetPathRoot(grant.DisplayPath)?.Equals(grant.DisplayPath, StringComparison.OrdinalIgnoreCase) == true && segments.Count > 0 &&
            (segments[0].StartsWith('$') || new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "Recovery", "Config.Msi" }
                .Contains(segments[0], StringComparer.OrdinalIgnoreCase)))
            throw new StorageException(FileStatus.AccessDenied, "Operating-system locations are excluded from remote access.");
    }
    private bool PublicRoot(StorageGrant grant)
    { try { ownerBinding?.RequirePublicLocation(grant.CanonicalPath); return true; } catch (StorageException) { return false; } }

    internal static bool IsSensitiveSegments(IEnumerable<string> segments) => segments.Any(x =>
        x.StartsWith(".iconnect-", StringComparison.OrdinalIgnoreCase) ||
        new[] { "AppData", ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".codex", ".git", "NTUSER.DAT", "UsrClass.dat" }
            .Contains(x, StringComparer.OrdinalIgnoreCase));
    internal static bool IsSystemTree(IReadOnlyList<string> segments) => segments.Count > 0 &&
        (segments[0].StartsWith('$') || new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "Recovery", "Config.Msi" }
            .Contains(segments[0], StringComparer.OrdinalIgnoreCase));
    private static string NormalizeExclusion(string value)
    {
        var segments = value.Replace('/', '\\').Split('\\');
        foreach (var segment in segments) StorageRules.ValidateName(segment);
        return string.Join('\\', segments);
    }
    private static void ValidateLabel(string value)
    {
        if (value.Length is < 1 or > 80 || value.Any(char.IsControl)) throw new ArgumentException("Choose a short, readable storage label.");
    }
    private static void ValidatePermissions(StoragePermission value)
    {
        if ((value & ~StoragePermission.Manage) != 0) throw new ArgumentException("Unknown storage permission.");
    }
    private static StorageGrant Clone(StorageGrant grant) => grant with { Exclusions = (string[])grant.Exclusions.Clone() };
    private void Save(Dictionary<string, StorageGrant> updated)
    {
        if (ownerBinding is not null) { ownerBinding.SaveRecords(path, ownerEpoch, updated); return; }
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, updated, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        AtomicStateFile.Commit(temporary, path);
    }
}
