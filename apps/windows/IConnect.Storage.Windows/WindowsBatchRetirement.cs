using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record IncomingBatchRetirementReview(string Id, string Plan, uint Items, int MetadataFiles, long MetadataBytes, UploadBatchPlanPhase Phase);

public sealed partial class WindowsBatchPlans
{
    public const int MaxDetailedPlans = 40, MaxRetiredPlans = 256;
    private const uint RetirementBatch = 32;
    private sealed record RetirementFile(string Name, string Version, long Bytes);
    private sealed record RetirementReview(IncomingBatchRetirementReview Public, RetirementFile[] Files, DateTimeOffset Expires);
    private readonly Dictionary<string, RetirementReview> retirementReviews = [];
    private static bool Bit(Row row, uint index) => (row.RetirementBits![index / 8] & (1 << (int)(index % 8))) != 0;
    private static void ValidateRetirementRow(Row row)
    {
        var h = Header(row); var bits = row.RetirementBits ?? [];
        if (row.Retired && row.Retiring || (row.Retiring || row.Retired) && !UploadBatchRules.IsTerminal(row.Phase) ||
            (row.Retiring ? bits.Length != (h.ItemCount + 7) / 8 || row.RetirementCursor > h.ItemCount * 2 || h.ItemCount % 8 != 0 && bits[^1] >> (int)(h.ItemCount % 8) != 0 : bits.Length != 0 || row.RetirementCursor != 0))
            throw new InvalidDataException("Invalid receiver-history retirement state.");
        if (row.Retiring && !row.EffectsStarted && bits.Any(x => x != 0)) throw new InvalidDataException("An unstarted receipt has unexpected child ownership.");
    }
    private RetirementFile[] RetirementFiles(Row row)
    {
        fileJournals.ValidateRoot(); using var root = SecureWindowsDirectory.OpenRoot(directory); var id = Header(row).Id;
        var names = root.EnumerateNames(default).Where(x => x.Name.StartsWith(id + ".", StringComparison.Ordinal)).Take(UploadBatchRules.MaxItems * 2 + 3).ToArray();
        if (names.Length > UploadBatchRules.MaxItems * 2 + 2 || names.Any(x => x.IsDirectory || !MetadataName(x.Name) || x.Name.EndsWith(".new", StringComparison.Ordinal))) throw new IOException("Unexpected receiver history objects were preserved.");
        return names.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => { using var file = root.OpenChild(x.Name); return new RetirementFile(x.Name, file.Info.Version, file.Info.Size); }).ToArray();
    }
    private void InspectRetirement(Row row)
    {
        if (!UploadBatchRules.IsTerminal(row.Phase) || row.Retiring || row.Retired || publishedBatchWork.ContainsKey(Header(row).Id) || fileReceivers.Values.Any(x => x.Batch == Header(row).Id && !x.Uploads.IsIdle)) throw new IOException("Finish or stop this plan and wait for verification before removing item history.");
        if (records.Values.Count(x => x.Retiring || x.Retired) >= MaxRetiredPlans) throw new IOException("Compact receipt storage is full. After all plans finish, preserve and reset closed history before removing more item receipts.");
        if (UploadBatchRules.HasManifest(row.Phase))
        {
            var saved = Inventory(row); if (!Outcomes(row, saved.Manifest, saved.Children, row.Phase == UploadBatchPlanPhase.UploadPlanCompleted, true).AsSpan().SequenceEqual(row.Outcomes ?? [])) throw new IOException("Final item outcomes changed. Existing copies and history were kept.");
        }
        else
        {
            var files = RetirementFiles(row); var id = Header(row).Id;
            if (files.Any(x => x.Name != id + ".json" && x.Name != AttemptsName(id))) throw new IOException("An unstarted plan has unexpected child metadata.");
            if (ReadAttempts(row) is { } attempts && attempts.Bits.Any(x => x != 0)) throw new IOException("An unstarted plan has unexpected attempt ownership.");
        }
    }
    public IncomingBatchRetirementReview ReviewRetirement(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); var row = records[id]; InspectRetirement(row); var files = RetirementFiles(row);
            foreach (var expired in retirementReviews.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) retirementReviews.Remove(expired);
            if (retirementReviews.Count >= UploadBatchRules.MaxPlans) retirementReviews.Remove(retirementReviews.Keys.First());
            var value = new IncomingBatchRetirementReview(ProtocolRules.NewToken(), id, Header(row).ItemCount, files.Length, files.Sum(x => x.Bytes), row.Phase);
            retirementReviews[value.Id] = new(value, files, DateTimeOffset.UtcNow.AddMinutes(2)); return value;
        }
    });
    public bool ConfirmRetirement(IncomingBatchRetirementReview confirmation) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            Ready(); if (!retirementReviews.Remove(confirmation.Id, out var review) || review.Public != confirmation || review.Expires < DateTimeOffset.UtcNow) throw new IOException("This receipt review changed, expired or was already used.");
            var row = records[confirmation.Plan]; InspectRetirement(row); if (!RetirementFiles(row).SequenceEqual(review.Files)) throw new IOException("Receiver history changed after review. It was kept.");
            var bits = ReadAttempts(row)?.Bits ?? new byte[(Header(row).ItemCount + 7) / 8]; var encoded = UploadBatchRules.HasManifest(row.Phase) ? AcceptedEncoded(row) : [];
            row = row with { Retiring = true, RetirementBits = bits, RetirementCursor = bits.All(x => x == 0) ? Header(row).ItemCount * 2 : 0 };
            Put(row, encoded); EndAssembly(Header(row).Id); return ContinueRetirementCore(row);
        }
    });
    private byte[] AcceptedEncoded(Row row) { _ = AcceptedManifest(row, out var encoded); return encoded; }
    private void ValidateRetirementInventory(Row row)
    {
        var id = Header(row).Id; var files = RetirementFiles(row).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (row.Retired) { if (files.Count != 1 || !files.Contains(id + ".json")) throw new InvalidDataException("A compact receipt has unexpected item metadata."); return; }
        var manifest = UploadBatchRules.HasManifest(row.Phase) ? AcceptedManifest(row, out _) : null; var n = Header(row).ItemCount; var cursor = row.RetirementCursor;
        var expected = new HashSet<string>(StringComparer.Ordinal) { id + ".json", AttemptsName(id) };
        for (uint index = 0; index < n; index++)
        {
            var child = ChildName(id, index); var journal = WindowsBatchUploadJournals.Name(id, index); var owns = Bit(row, index);
            var needsChild = owns && (cursor < n || index >= cursor - n); var needsFile = owns && manifest is not null && !manifest.Items[(int)index].IsDirectory && cursor < n && index >= cursor;
            var childWindow = cursor >= n && index >= cursor - n && index < Math.Min(n, cursor - n + RetirementBatch);
            var fileWindow = cursor < n && index >= cursor && index < Math.Min(n, cursor + RetirementBatch);
            if (needsChild) { expected.Add(child); if (!childWindow && !files.Contains(child)) throw new IOException("Future child receipts are missing outside the interrupted retirement window."); }
            if (needsFile) { expected.Add(journal); if (!fileWindow && !files.Contains(journal)) throw new IOException("Future file journals are missing outside the interrupted retirement window."); }
        }
        if (files.Any(x => !expected.Contains(x)) || cursor != n * 2 && !files.Contains(AttemptsName(id)) && row.EffectsStarted) throw new IOException("Unexpected or missing receiver ownership metadata was kept.");
        if (files.Contains(AttemptsName(id)) && ReadAttempts(row) is { } attempts && !attempts.Bits.AsSpan().SequenceEqual(row.RetirementBits)) throw new IOException("The retirement attempt inventory changed.");
    }
    public bool ContinueRetirement(string id) => grants.WithAuthorization(() =>
    { lock (gate) { Ready(); var row = records[id]; if (!row.Retiring) throw new IOException("This plan has no saved item-history retirement intent."); return ContinueRetirementCore(row); } });
    private bool ContinueRetirementCore(Row row)
    {
        ValidateRetirementInventory(row); var h = Header(row); var n = h.ItemCount; var cursor = row.RetirementCursor;
        var manifest = UploadBatchRules.HasManifest(row.Phase) ? AcceptedManifest(row, out _) : null; var encoded = UploadBatchRules.HasManifest(row.Phase) ? AcceptedEncoded(row) : [];
        var end = cursor < n ? Math.Min(n, cursor + RetirementBatch) : Math.Min(n * 2, cursor + RetirementBatch);
        var bindings = new Dictionary<uint, DirectoryChild>();
        DirectoryChild Bind(uint index)
        { if (bindings.TryGetValue(index, out var found)) return found; var child = ReadDirectoryChild(row, manifest!, index) ?? throw new IOException("A retained child binding is missing."); bindings[index] = child; return child; }
        for (var step = cursor; step < end; step++)
        {
            var index = step < n ? step : step - n; if (!Bit(row, index)) continue;
            if (step < n)
            {
                if (manifest!.Items[(int)index].IsDirectory || !File.Exists(Path.Combine(directory, WindowsBatchUploadJournals.Name(h.Id, index)))) continue;
                var child = Bind(index); var parent = manifest.Items[(int)index].Parent; while (parent >= 0) { _ = Bind((uint)parent); parent = manifest.Items[parent].Parent; }
                var file = FileInventory(row, manifest, child, bindings); if (WindowsUploads.ScopedPartialExists(file.Scope, context.Owner)) throw new IOException("Private file staging remains. It was kept together with its ownership journal.");
                fileJournals.RemoveRetired(WindowsBatchUploadJournals.Name(h.Id, index));
            }
            else
            {
                var name = ChildName(h.Id, index); if (!File.Exists(Path.Combine(directory, name))) continue;
                _ = Bind(index); using var root = SecureWindowsDirectory.OpenRoot(directory); using var child = root.OpenMutableChild(name); child.Delete();
            }
        }
        row = row with { RetirementCursor = end }; Put(row, encoded); if (end < n * 2) return false;
        using (var root = SecureWindowsDirectory.OpenRoot(directory))
        {
            var attempts = AttemptsName(h.Id); if (File.Exists(Path.Combine(directory, attempts))) { _ = ReadAttempts(row); using var file = root.OpenMutableChild(attempts); file.Delete(); }
        }
        if (RetirementFiles(row).Any(x => x.Name != h.Id + ".json")) throw new IOException("Unexpected receiver history objects were preserved; retirement is paused.");
        row = row with { Retiring = false, Retired = true, RetirementCursor = 0, RetirementBits = [] }; Put(row); directoryBytes.Remove(h.Id); manifestCache.Remove(h.Id);
        foreach (var key in fileReceivers.Where(x => x.Value.Batch == h.Id).Select(x => x.Key).ToArray()) fileReceivers.TryRemove(key, out _); return true;
    }
    public void ResetClosedHistory() => grants.WithAuthorization(() =>
    {
        lock (gate) { Ready(); if (records.Values.Any(x => !UploadBatchRules.IsTerminal(x.Phase) || x.Retiring)) throw new IOException("Finish every active plan and its local history cleanup before resetting closed receipts."); ArchiveHistory(); }
        return true;
    });
}
