using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

// A protection inventory, never authority to adopt an unidentified file.
internal sealed class ReceivingPartProtection : IDisposable
{
    internal readonly HashSet<string> Tokens = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Identities = new(StringComparer.Ordinal);
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal void Bind(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
    }
    internal void Add(string token, string identity)
    { Tokens.Add(token); if (identity.Length != 0) Identities.Add(identity); }
    internal string Stamp => Convert.ToHexStringLower(hash.GetCurrentHash());
    internal void Merge(ReceivingPartProtection other)
    { Bind(other.Stamp); Tokens.UnionWith(other.Tokens); Identities.UnionWith(other.Identities); }
    public void Dispose() => hash.Dispose();
}

public sealed partial class WindowsDownloadManager
{
    internal string ProfileDirectory => Path.GetDirectoryName(directory)!;
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
            if (running.Count != 0) throw new IOException("Pause downloads and wait for their work to stop before reviewing partial files.");
            using var root = SecureWindowsDirectory.OpenRoot(directory);
            if (root.Info.Identity != partRecoveryDirectoryIdentity) throw new IOException("Download metadata storage changed. Reopen and review it before partial-file cleanup.");
            var names = root.EnumerateNames(default).Take(2049).ToArray();
            if (names.Length > 2048 || names.Any(x => x.Name.EndsWith(".json.new", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Uncommitted download metadata was kept. Review saved progress before partial-file cleanup.");
            var journals = names.Where(x => x.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (journals.Length != jobs.Count || journals.Any(x => x.IsDirectory || !jobs.ContainsKey(x.Name[..^5]) || x.Name != x.Name[..^5] + ".json"))
                throw new IOException("Download ownership metadata changed. Reopen and review saved progress before partial-file cleanup.");
            using var protection = new ReceivingPartProtection(); protection.Bind(root.Info.Identity);
            foreach (var job in jobs.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var name = job.Id + ".json"; using var file = root.OpenChild(name);
                var saved = StateRecovery.Read<DownloadJob>(Path.Combine(directory, name), 256 * 1024); Validate(saved);
                // Cold loading changes only this interrupted status in memory.
                if ((saved.Phase is DownloadPhase.Preparing or DownloadPhase.Downloading or DownloadPhase.Verifying or DownloadPhase.Finalizing) &&
                    job.Phase == DownloadPhase.Interrupted && job.Error == "The application stopped. Resume to verify and continue.")
                    saved = saved with { Phase = job.Phase, Error = job.Error };
                if (saved != job) throw new IOException("A download receipt changed outside its queue. Its files were kept.");
                protection.Bind(name); protection.Bind(file.Info.Version); protection.Add(job.Id, job.TemporaryIdentity);
            }
            return operation(protection);
        }
    }
}

public sealed partial class WindowsUploads
{
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            Ready(); if (scope is not null || !IsIdle) throw new IOException("Wait for incoming-copy verification before reviewing partial files.");
            using var root = SecureWindowsDirectory.OpenRoot(directory);
            if (root.Info.Identity != partRecoveryDirectoryIdentity) throw new IOException("Incoming-copy metadata storage changed. Reopen and review it before cleanup.");
            var names = root.EnumerateNames(default).Take(2049).ToArray();
            if (names.Length > 2048) throw new IOException("Incoming metadata exceeds its recovery scan limit. Its files were kept.");
            var metadata = names.Where(x => x.Name.Contains(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (metadata.Length != 1 || metadata[0].IsDirectory || metadata[0].Name != "journal.json")
                throw new IOException("Unexpected or uncommitted incoming metadata was kept. Preserve and reset its history before cleanup.");
            using var file = root.OpenChild("journal.json");
            var saved = StateRecovery.Read<Cache>(path, 2 * 1024 * 1024); Validate(saved); ValidateScope(saved, grants.OwnerScope, null);
            if (!JsonSerializer.SerializeToUtf8Bytes(saved).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(cache)))
                throw new IOException("Incoming-copy receipts changed outside their queue. Their files were kept.");
            using var protection = new ReceivingPartProtection(); protection.Bind(root.Info.Identity); protection.Bind(cache.Epoch); protection.Bind(file.Info.Version);
            foreach (var row in cache.Rows) protection.Add(row.Temporary, row.TemporaryIdentity);
            return operation(protection);
        }
    }
    internal static void ProtectScopedReceivingParts(string journal, string owner, ReceivingPartProtection protection)
    {
        var saved = StateRecovery.Read<Cache>(journal, WindowsBatchUploadJournals.RecordLimit); Validate(saved);
        if (saved.OwnerScope != owner || !UploadBatchRules.CanonicalToken(saved.Scope) || saved.Rows.Length != 1 || saved.Rows[0].Id != saved.Scope)
            throw new IOException("A batch file journal lost its ownership binding. Its files were kept.");
        foreach (var row in saved.Rows) protection.Add(row.Temporary, row.TemporaryIdentity);
    }
}

public sealed partial class WindowsBatchDownloads
{
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
            if (running.Count != 0 || canceling.Count != 0) throw new IOException("Pause folder downloads and wait for their cleanup before reviewing partial files.");
            using var root = SecureWindowsDirectory.OpenRoot(directory);
            if (root.Info.Identity != partRecoveryDirectoryIdentity) throw new IOException("Folder-download metadata storage changed. Reopen and review it before cleanup.");
            var names = root.EnumerateNames(default).Take(2049).ToArray();
            if (names.Length > 2048 || names.Any(x => x.Name.EndsWith(".json.new", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Uncommitted folder-download metadata was kept. Review its saved progress before cleanup.");
            var journals = names.Where(x => x.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (journals.Length != jobs.Count || journals.Any(x => x.IsDirectory || !jobs.ContainsKey(x.Name[..^5]) || x.Name != x.Name[..^5] + ".json"))
                throw new IOException("Folder-download ownership changed. Reopen and review its saved progress before cleanup.");
            using var protection = new ReceivingPartProtection(); protection.Bind(root.Info.Identity);
            foreach (var job in jobs.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var name = job.Id + ".json"; using var file = root.OpenChild(name);
                var saved = StateRecovery.Read<BatchDownload>(Path.Combine(directory, name), 8 * 1024 * 1024); Validate(saved);
                if (saved.Phase == BatchPhase.Running && job.Phase == BatchPhase.Interrupted && job.Error == StoppedError)
                    saved = saved with { Phase = job.Phase, Error = job.Error };
                if (!JsonSerializer.SerializeToUtf8Bytes(saved).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(job)))
                    throw new IOException("A folder-download receipt changed outside its queue. Its files were kept.");
                protection.Bind(name); protection.Bind(file.Info.Version);
                foreach (var item in job.Items) if (item.DownloadId.Length != 0) protection.Add(item.DownloadId, item.FileIdentity);
            }
            return operation(protection);
        }
    }
}

public sealed partial class WindowsBatchPlans
{
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            Ready(); if (assemblies.Count != 0 || fileReceivers.Values.Any(x => !x.Uploads.IsIdle))
                throw new IOException("Pause batch connections and wait for verification before reviewing partial files.");
            using var protection = fileJournals.ReceivingPartProtection(context.Owner); protection.Bind(context.Epoch);
            return operation(protection);
        }
    }
}
