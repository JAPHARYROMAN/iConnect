using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record LocalBatchVerification(string Destination, int Files, int Folders, long Bytes);

public sealed partial class WindowsLocalBatchCopies
{
    private sealed record Verification(string Id, CancellationTokenSource Cancellation, Task Task);
    private Verification? verification;

    public bool Verifying(string id) { lock (gate) return verification?.Id == id; }

    public void CancelVerification()
    {
        lock (gate) verification?.Cancellation.Cancel();
    }

    public Task<LocalBatchVerification> VerifyCompletedAsync(string id, CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (verification is not null) throw new IOException("Wait for the current destination check or cancel it.");
            RequireContextIdle(id);
            var job = batches.Jobs.SingleOrDefault(x => x.Id == id);
            if (batches.RecoveryIssues.Count != 0 || batches.Active(id) || job is not { HasBoundDestination: true })
                throw new IOException("Choose a completed batch with a recorded identity for every destination item.");
            return RunVerification(id, ct, token => WithVerifiedDestination(job, token, (result, _) => result));
        }
    }

    private Task<T> RunVerification<T>(string id, CancellationToken ct, Func<CancellationToken, T> run)
    {
        if (verification is not null) throw new IOException("Wait for the current destination check or cancel it.");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var task = Task.Run(() =>
        {
            try { return run(cancellation.Token); }
            finally
            {
                lock (gate) { verification = null; cancellation.Dispose(); }
                Notify();
            }
        });
        verification = new(id, cancellation, task);
        Notify();
        return task;
    }

    private T WithVerifiedDestination<T>(BatchDownload job, CancellationToken ct, Func<LocalBatchVerification, Action, T> verified)
    {
        ct.ThrowIfCancellationRequested();
        using var receipt = batchRoot.OpenChild(job.Id + ".json");
        var receiptPath = receipt.Info.CanonicalPath;
        RequireSettledReceipt(receiptPath);
        var saved = StateRecovery.Read<BatchDownload>(receiptPath, 8 * 1024 * 1024);
        if (!JsonSerializer.SerializeToUtf8Bytes(saved).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(job)))
            throw new IOException("The saved batch receipt changed. Reopen the app to review it.");
        using var destination = StorageGrantStore.OpenLocalDestination(job.DisplayPath);
        if (destination.Info.Identity != job.RootIdentity ||
            !destination.Info.CanonicalPath.Equals(job.RootPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The recorded destination folder changed.");
        var privateProfile = Path.GetDirectoryName(root.Info.CanonicalPath)!;
        if (SecureWindowsDirectory.IsWithin(destination.Info.CanonicalPath, privateProfile))
            throw new IOException("The recorded destination is inside private iConnect storage.");

        var opened = new List<SecureWindowsDirectory>(job.Items.Length);
        var children = job.Items.ToLookup(x => x.Source.Parent);
        try
        {
            foreach (var item in job.Items)
            {
                ct.ThrowIfCancellationRequested();
                var parent = item.Source.Parent < 0 ? destination : opened[item.Source.Parent];
                var entry = parent.OpenChild(item.LocalName, item.Source.Directory);
                opened.Add(entry);
                if (entry.Info.Identity != (item.Source.Directory ? item.DirectoryIdentity : item.FileIdentity))
                    throw new IOException("A recorded destination item was replaced.");
                if (SecureWindowsDirectory.IsWithin(entry.Info.CanonicalPath, privateProfile) ||
                    item.Source.Directory && SecureWindowsDirectory.IsWithin(privateProfile, entry.Info.CanonicalPath))
                    throw new IOException("A destination item overlaps private iConnect storage.");
                if (!item.Source.Directory) WindowsDownloadManager.VerifyFile(entry, item.Source.Size, item.Hash, ct);
            }
            RequireCurrent();
            var folders = job.Items.Count(x => x.Source.Directory);
            return verified(new(job.DisplayPath, job.Items.Length - folders, folders, job.VerifiedBytes), RequireCurrent);
        }
        finally { for (var i = opened.Count - 1; i >= 0; i--) opened[i].Dispose(); }

        void RequireCurrent()
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < job.Items.Length; i++)
                if (job.Items[i].Source.Directory)
                    RequireChildren(opened[i], children[i].Select(x => (x.LocalName, x.Source.Directory)),
                        "A copied folder has a missing or unexpected entry.", ct);
            RequireSettledReceipt(receiptPath);
        }
    }

    private static void RequireChildren(SecureWindowsDirectory folder, IEnumerable<(string Name, bool Directory)> names,
        string message, CancellationToken ct)
    {
        var expected = names.ToHashSet();
        foreach (var entry in folder.EnumerateNames(ct))
            if (!expected.Remove((entry.Name, entry.IsDirectory))) throw new IOException(message);
        if (expected.Count != 0) throw new IOException(message);
    }

    private static void RequireSettledReceipt(string path)
    {
        foreach (var suffix in new[] { ".new", ".bak" })
            if (File.Exists(path + suffix) || Directory.Exists(path + suffix))
                throw new IOException("The batch receipt has uncommitted progress. Reopen the app to review it.");
    }
}
