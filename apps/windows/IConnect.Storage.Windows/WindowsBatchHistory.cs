using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public enum BatchHistoryAction { Archive, Restore, Purge }
public sealed record BatchHistoryReview(string Id, string Job, BatchHistoryAction Action, string Source, int Results, long Bytes, bool ReceiverMayHaveCopies);

public sealed partial class WindowsBatchSender
{
    public const int MaxHistory = 32;
    private const int MaxRecords = UploadBatchRules.MaxPlans + MaxHistory, PurgeBatch = 64;
    private sealed record HistoryFile(string Name, string Version, long Size);
    private sealed record HistoryReview(BatchHistoryReview Public, string Parent, HistoryFile[] Files, DateTimeOffset Expires);
    private readonly Dictionary<string, HistoryReview> historyReviews = [];
    private void ValidateSlotCounts()
    {
        if (rows.Values.Count(x => x.Archived) > MaxHistory || rows.Values.Count(x => !x.Archived) > UploadBatchRules.MaxPlans ||
            rows.Values.Where(x => !x.Archived).GroupBy(x => x.Authority.Peer).Any(x => x.Count() > UploadBatchRules.MaxPeerPlans)) throw new InvalidDataException("Outgoing history or active slots exceed their limit.");
    }
    private void OwnHistory(OutgoingBatchSend row)
    { Ready(); if (!localOwner(row.Authority)) throw new IOException("Review the local owner identity before changing this history."); if (work.ContainsKey(row.Id)) throw new IOException("Pause this batch and wait for its current request before reviewing history."); }
    public bool SourceInUse(string id) { lock (gate) return rows.Values.Any(x => x.Source == id && (work.ContainsKey(x.Id) || !x.Archived && x.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped))); }
    public BatchSourceRemovalReview ReviewSourceRemoval(string id)
    {
        lock (gate) { Ready(); if (SourceInUse(id)) throw new IOException("Archive or finish the retained send before reviewing its private source copies."); return sources.ReviewRemoval(id); }
    }
    public void RemoveSourceReviewed(BatchSourceRemovalReview review)
    {
        lock (gate) { Ready(); if (SourceInUse(review.Plan)) throw new IOException("The private source belongs to a current send. Pause and archive that job before removing its private copies."); sources.RemoveReviewed(review); }
    }
    private HistoryFile[] HistoryFiles(OutgoingBatchSend row, bool validateResults = false)
    {
        var files = new List<HistoryFile>();
        for (var index = 0; index <= row.Next && index < row.Items; index++)
        {
            var name = ResultName(row.Id, index);
            if (!File.Exists(Path.Combine(directory, name))) { if (index < row.Next) throw new IOException("Saved item results are missing. They were kept for review."); continue; }
            string version; long size; using (var file = root.OpenChild(name)) { version = file.Info.Version; size = file.Info.Size; }
            if (size > ResultLimit) throw new IOException("A saved item result exceeds its limit.");
            if (validateResults) _ = ReadResult(row, index, index < row.Next);
            using (var checkedFile = root.OpenChild(name)) if (checkedFile.Info.Version != version) throw new IOException("An item result changed during review.");
            files.Add(new(name, version, size));
        }
        var names = root.EnumerateNames(default).Where(x => x.Name.StartsWith(row.Id + ".", StringComparison.Ordinal) && x.Name != row.Id + ".json").Take(UploadBatchRules.MaxItems + 1).ToArray();
        var expected = files.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Length != files.Count || names.Any(x => x.IsDirectory || !expected.Contains(x.Name))) throw new IOException("Unexpected history objects were kept for review.");
        return files.ToArray();
    }
    private void HistoryActionAllowed(OutgoingBatchSend row, BatchHistoryAction action)
    {
        if (!Enum.IsDefined(action) || row.Purging || action == BatchHistoryAction.Archive && (row.Archived || rows.Values.Count(x => x.Archived) >= MaxHistory) ||
            action != BatchHistoryAction.Archive && !row.Archived) throw new IOException("This history action is unavailable. Review its current state.");
        if (action == BatchHistoryAction.Restore && (rows.Values.Count(x => !x.Archived) >= UploadBatchRules.MaxPlans || rows.Values.Count(x => !x.Archived && x.Authority.Peer == row.Authority.Peer) >= UploadBatchRules.MaxPeerPlans)) throw new IOException("Active queue slots are full. Archive another inactive job first.");
        if (action == BatchHistoryAction.Restore && row.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped) && rows.Values.Any(x => !x.Archived && x.Source == row.Source && x.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped))) throw new IOException("Another active job already uses this private source. Review it before restoring this original intent.");
    }
    public BatchHistoryReview ReviewHistory(string id, BatchHistoryAction action)
    {
        lock (gate)
        {
            var row = rows[id]; OwnHistory(row); HistoryActionAllowed(row, action); var files = HistoryFiles(row, action == BatchHistoryAction.Restore);
            foreach (var expired in historyReviews.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) historyReviews.Remove(expired);
            if (historyReviews.Count >= UploadBatchRules.MaxPlans) historyReviews.Remove(historyReviews.Keys.First());
            var value = new BatchHistoryReview(ProtocolRules.NewToken(), id, action, row.Source, files.Length, files.Sum(x => x.Size), row.Header is { Length: > 0 });
            historyReviews[value.Id] = new(value, versions[id], files, DateTimeOffset.UtcNow.AddMinutes(2)); return value;
        }
    }
    public bool ConfirmHistory(BatchHistoryReview confirmation)
    {
        lock (gate)
        {
            Ready(); if (!historyReviews.Remove(confirmation.Id, out var review) || review.Public != confirmation || review.Expires < DateTimeOffset.UtcNow || !rows.TryGetValue(confirmation.Job, out var row) || versions[row.Id] != review.Parent) throw new IOException("This history review changed, expired or was already used. Review it again.");
            OwnHistory(row); HistoryActionAllowed(row, confirmation.Action);
            var files = HistoryFiles(row, confirmation.Action == BatchHistoryAction.Restore); if (!files.SequenceEqual(review.Files)) throw new IOException("Item history changed after review. It was kept.");
            if (confirmation.Action != BatchHistoryAction.Purge) { Put(row with { Archived = confirmation.Action == BatchHistoryAction.Archive }); return true; }
            Put(row with { Purging = true, PurgeCount = files.Length }); return ContinuePurge(row.Id);
        }
    }
    private void ValidatePurgeInventory(OutgoingBatchSend row)
    {
        var end = Math.Min(row.PurgeCount, row.PurgeCursor + PurgeBatch);
        for (var index = end; index < row.PurgeCount; index++) if (!File.Exists(Path.Combine(directory, ResultName(row.Id, index)))) throw new IOException("History outside the interrupted cleanup window is missing.");
    }
    public bool ContinuePurge(string id)
    {
        lock (gate)
        {
            var row = rows[id]; OwnHistory(row); if (!row.Archived || !row.Purging) throw new IOException("This job has no saved history-removal intent."); ValidatePurgeInventory(row);
            var end = Math.Min(row.PurgeCount, row.PurgeCursor + PurgeBatch); var batch = new List<HistoryFile>();
            for (var index = row.PurgeCursor; index < end; index++)
            {
                var name = ResultName(id, index); if (!File.Exists(Path.Combine(directory, name))) continue;
                string version; long size; using (var file = root.OpenChild(name)) { version = file.Info.Version; size = file.Info.Size; }
                if (size > ResultLimit) throw new IOException("Unexpected history metadata was kept."); batch.Add(new(name, version, size));
            }
            foreach (var saved in batch)
            {
                using var file = root.OpenMutableChild(saved.Name); if (file.Info.Version != saved.Version) throw new IOException("History changed during cleanup. It was kept."); file.Delete();
            }
            row = Put(row with { PurgeCursor = end }); resultBytes[id] = Math.Max(0, resultBytes.GetValueOrDefault(id) - batch.Sum(x => x.Size));
            if (end != row.PurgeCount) return false;
            if (root.EnumerateNames(default).Any(x => x.Name.StartsWith(id + ".", StringComparison.Ordinal) && x.Name != id + ".json")) throw new IOException("Unexpected history objects were preserved; cleanup is paused.");
            using (var file = root.OpenMutableChild(id + ".json")) { if (file.Info.Version != versions[id]) throw new IOException("The history intent changed. It was kept."); file.Delete(); }
            rows.Remove(id); versions.Remove(id); resultBytes.Remove(id); NotifyChanged(); return true;
        }
    }
}
