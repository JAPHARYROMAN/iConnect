using IConnect.Core;
using System.Security.Cryptography;
using System.Text;

namespace IConnect.Storage.Windows;

public sealed record LocalBatchMissingReview(string Id, string Context, bool HasSource);

public sealed partial class WindowsLocalBatchCopies
{
    // Presence denies a fresh start. No marker contents can grant file access.
    private readonly Dictionary<string, string> decisions = [];
    private readonly Dictionary<string, string> readyDecisions = [];
    private readonly Dictionary<string, (string Kind, Task Task)> orphanWork = [];
    private sealed record MissingReview(LocalBatchMissingReview Value, string Stamp, long Expires);
    private readonly Dictionary<string, MissingReview> missingReviews = [];
    private void LoadDecisions()
    {
        var entries = decisionRoot.EnumerateNames(default).Take(513).ToArray();
        if (entries.Length > 512) recovery.Block("Local copy decisions exceed their inventory limit. Keep private progress and restore access before starting new work.");
        foreach (var entry in entries.Take(512)) try
        {
            var unused = entry.Name.EndsWith(".ready", StringComparison.Ordinal); var suffix = unused ? 6 : 8;
            if (entry.IsDirectory || !unused && !entry.Name.EndsWith(".started", StringComparison.Ordinal) || entry.Name.Length < suffix || !ProtocolRules.TokenPattern().IsMatch(entry.Name[..^suffix])) throw new IOException("Unexpected local decision entry.");
            using var file = decisionRoot.OpenChild(entry.Name); (unused ? readyDecisions : decisions).Add(entry.Name[..^suffix], file.Info.Version);
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Block("Local copy decisions could not be validated. Keep private progress and restore its normal file access before restarting. File receipt controls remain separate."); }
    }
    private void RequireDecisions()
    {
        var entries = decisionRoot.EnumerateNames(default).Take(513).ToArray();
        var expected = decisions.Select(x => (Name: x.Key + ".started", Version: x.Value)).Concat(readyDecisions.Select(x => (Name: x.Key + ".ready", Version: x.Value))).ToDictionary(x => x.Name, x => x.Version);
        if (entries.Length != expected.Count || entries.Any(x => x.IsDirectory || !expected.ContainsKey(x.Name)))
            throw new IOException("Local copy decisions changed. Restart and review retained progress; files were kept.");
        foreach (var item in expected)
        { using var file = decisionRoot.OpenChild(item.Key); if (file.Info.Version != item.Value) throw new IOException("A local copy decision changed. Restart before reviewing it."); }
    }
    private void EnsureDecision(string id)
    {
        if (!decisions.ContainsKey(id))
        {
            if (!ProtocolRules.TokenPattern().IsMatch(id) || decisions.Count >= 256) throw new IOException("Local copy decisions need review before continuing.");
            using var file = decisionRoot.CreateFile(id + ".started"); file.Flush(); decisions.Add(id, SecureWindowsDirectory.Inspect(file.Handle).Version);
        }
        RemoveReady(id);
    }
    private void CreateReady(string id)
    { using var file = decisionRoot.CreateFile(id + ".ready"); file.Flush(); readyDecisions.Add(id, SecureWindowsDirectory.Inspect(file.Handle).Version); }
    private void RemoveReady(string id)
    {
        if (!readyDecisions.TryGetValue(id, out var version)) return;
        using var file = decisionRoot.OpenMutableChild(id + ".ready"); if (file.Info.Version != version) throw new IOException("The unused draft decision changed. Restart and review it.");
        file.Delete(); readyDecisions.Remove(id);
    }
    private void RemoveDecision(string id)
    {
        if (!decisions.TryGetValue(id, out var version)) return;
        using var file = decisionRoot.OpenMutableChild(id + ".started"); if (file.Info.Version != version) throw new IOException("The local copy decision changed. Review it again.");
        file.Delete(); decisions.Remove(id);
    }
    public IReadOnlyList<DownloadJob> OrphanFiles
    {
        get { lock (gate)
        {
            var parents = batches.Jobs;
            return files.Jobs.Where(file => !parents.Any(parent => parent.Id == file.BatchId && parent.Items.Any(x => x.DownloadId == file.Id))).ToArray();
        } }
    }
    public bool OrphanActive(string id) { lock (gate) return orphanWork.ContainsKey(id); }
    public void PauseOrphan(string id) { lock (gate) { ObjectDisposedException.ThrowIf(closed, this); _ = RequireOrphan(id); files.Pause(id); } }
    private void RequireContextIdle(string id)
    {
        if (Verifying(id)) throw new IOException("Wait for destination verification or cancel it before controlling this batch.");
        if (files.Jobs.Any(x => x.BatchId == id && orphanWork.ContainsKey(x.Id)))
            throw new IOException("Pause unattached receipt work and wait for it to finish before controlling this batch.");
    }
    public Task StopOrphanAsync(string id) => RunOrphan(id, "stop");
    public Task RecheckOrphanAsync(string id) => RunOrphan(id, "check");
    private DownloadJob RequireOrphan(string id)
    {
        var row = OrphanFiles.SingleOrDefault(x => x.Id == id) ?? throw new IOException("This receipt is attached to a batch or is no longer available. Use the current batch controls.");
        if (batches.Active(row.BatchId)) throw new IOException("Pause the original batch and wait for its work to finish first.");
        return row;
    }
    private Task RunOrphan(string id, string kind)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (orphanWork.TryGetValue(id, out var current)) return current.Kind == kind ? current.Task : throw new IOException("Pause the current receipt operation and wait for it to stop first.");
            var row = RequireOrphan(id); ReceivingStamp();
            if (row.BatchId.Length != 0) EnsureDecision(row.BatchId);
            var task = Task.Run(async () =>
            {
                if (kind == "check") await files.CheckPublishedAsync(id); else await files.Cancel(id);
                var result = files.Jobs.Single(x => x.Id == id);
                WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalBatchRecovery, result.Phase == DownloadPhase.Completed ? WindowsActivityOutcome.Completed : result.Phase == DownloadPhase.Canceled && result.Error.Length == 0 ? WindowsActivityOutcome.Canceled : WindowsActivityOutcome.Review));
            });
            orphanWork.Add(id, (kind, task));
            _ = task.ContinueWith(done => { _ = done.Exception; lock (gate) orphanWork.Remove(id); Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalBatchRecovery, WindowsActivityOutcome.Requested)); Notify(); return task;
        }
    }
    public bool CanRemoveOrphan(string id)
    {
        lock (gate)
        {
            var row = OrphanFiles.SingleOrDefault(x => x.Id == id);
            return row is not null && !orphanWork.ContainsKey(id) && !batches.Active(row.BatchId) && (row.Phase is DownloadPhase.Completed or DownloadPhase.Skipped || row.Phase == DownloadPhase.Canceled && row.Error.Length == 0);
        }
    }
    public void RemoveOrphan(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this); var row = RequireOrphan(id); ReceivingStamp();
            if (!CanRemoveOrphan(id)) throw new IOException("Finish verification or safe cancellation before removing this receipt.");
            if (row.BatchId.Length != 0) EnsureDecision(row.BatchId);
            files.RemoveRecord(id); WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalBatchRecovery, WindowsActivityOutcome.Completed)); Notify();
        }
    }
    private string ReceivingStamp() => WithReceivingPartProtection(protection => protection.Stamp);
    private string MissingStamp(string id)
    {
        var stamp = ReceivingStamp(); RequireDecisions();
        if (batches.Jobs.Any(x => x.Id == id)) throw new IOException("This batch has receiving progress. Use its original controls.");
        var job = Jobs.SingleOrDefault(x => x.Id == id) ?? throw new IOException("This local context is no longer available.");
        if (!job.CopyMayExist) throw new IOException("Cancel this unstarted draft with its ordinary controls.");
        if (files.Jobs.Any(x => x.BatchId == id)) throw new IOException("Review, stop or verify and remove every retained file receipt for this context first.");
        if (plans.TryGetValue(id, out var plan)) RequireRecord(plan);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', id, stamp, versions.GetValueOrDefault(id, "no-source"), decisions.GetValueOrDefault(id, "no-decision")))));
    }
    public LocalBatchMissingReview ReviewMissing(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this); var stamp = MissingStamp(id);
            foreach (var expired in missingReviews.Where(x => x.Value.Expires < Environment.TickCount64 || x.Value.Value.Context == id).Select(x => x.Key).ToArray()) missingReviews.Remove(expired);
            if (missingReviews.Count >= 8) throw new IOException("Finish an earlier missing-progress review first.");
            var value = new LocalBatchMissingReview(ProtocolRules.NewToken(), id, plans.ContainsKey(id)); missingReviews.Add(value.Id, new(value, stamp, Environment.TickCount64 + 5 * 60_000)); return value;
        }
    }
    public void ConfirmRemoveMissing(LocalBatchMissingReview value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (!missingReviews.Remove(value.Id, out var review) || review.Value != value || review.Expires < Environment.TickCount64 || review.Stamp != MissingStamp(value.Context)) throw new IOException("This review changed, expired or was already used. Review missing progress again.");
            RemovePlan(value.Context); RemoveReady(value.Context); RemoveDecision(value.Context); WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalBatchRecovery, WindowsActivityOutcome.Completed)); Notify();
        }
    }
}
