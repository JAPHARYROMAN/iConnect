using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record IncomingOperationReview(string Id, string Receipt, string Peer, string Name, long Size, long Checkpoint,
    string Location, string PublishedName, long CurrentExpiresAt, long ProposedExpiresAt, long ReviewExpiresAt);

public sealed partial class WindowsUploads
{
    private sealed record OperationProof(Row Row, string Grant, string Epoch, int Generation, Func<bool> Current, long Started, long Wall);
    private sealed record OperationOffer(IncomingOperationReview Value, OperationProof Proof, string FileVersion);
    private sealed record OperationFile(OperationProof Proof, SecureWindowsDirectory Root, SecureWindowsDirectory File) : IDisposable
    { public void Dispose() { File.Dispose(); Root.Dispose(); } }
    private readonly Dictionary<string, OperationOffer> operationReviews = [];
    private readonly SemaphoreSlim operationReviewSlot = new(1, 1);
    private int operationReviewGeneration;
    private static long OperationStart(Row row) => row.OperationApprovedAt == 0 ? MutationRules.CreatedAt(row.Id) : row.OperationApprovedAt;
    private static long OperationExpiry(Row row) => Math.Min(StorageRules.MaxTimestamp, OperationStart(row) + MutationRules.Lifetime);
    private void RequireOperationCurrent(Row row)
    {
        var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds(); var at = OperationStart(row);
        if (at <= 0 || at < now - MutationRules.Lifetime || at > now + 300_000)
            throw new StorageException(FileStatus.StaleReference, "This incoming copy's 24-hour write window expired or the clocks disagree. Ask the receiving owner to review this existing receipt. Resume does not require another copy.");
    }
    private void RequireOperationRow(Row row)
    {
        Ready();
        if (scope is not null || verifying.ContainsKey(row.Id) || row.TemporaryIdentity.Length == 0 ||
            !(row.Phase == UploadPhase.UploadReceiving && row.FinalName.Length == 0 ||
              row.Offset == row.Size && row.FinalName.Length != 0 && row.Phase is UploadPhase.UploadFinalizing or UploadPhase.UploadReview))
            throw new IOException("Choose an idle ordinary incoming receipt with an identified partial or recorded publication. Batch, stopped and completed receipts keep their existing workflows.");
        if (grants.Clock.GetUtcNow().ToUnixTimeMilliseconds() <= OperationExpiry(row))
            throw new IOException("This receipt's write window has not expired. Refresh its status and resume the same receipt explicitly.");
    }
    private string OperationGrant(Row row) => JsonSerializer.Serialize(Authorize(row));
    // Called under current owner authorization and the incoming-store gate. Long reads run outside both locks.
    private void RequireOperationProof(OperationProof proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds(); var elapsed = grants.Clock.GetElapsedTime(proof.Started);
        if (!proof.Current() || proof.Generation != operationReviewGeneration || proof.Epoch != cache.Epoch ||
            now < proof.Wall || now - proof.Wall >= 300_000 || elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromMinutes(5) ||
            cache.Rows.SingleOrDefault(x => x.Id == proof.Row.Id) != proof.Row)
            throw new IOException("This incoming-window review changed, expired or lost owner context. Review the current receipt again.");
        RequireOperationRow(proof.Row);
        if (OperationGrant(proof.Row) != proof.Grant) throw new IOException("The receiving approval changed. Review the current receipt again.");
    }
    private OperationFile OpenOperationFile(OperationProof proof, CancellationToken ct)
    {
        RequireOperationProof(proof, ct); var root = Open(proof.Row);
        try
        {
            SecureWindowsDirectory file;
            try { file = root.OpenReceivingPart(proof.Row.Temporary, mutation: true); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound && proof.Row.FinalName.Length != 0)
            { file = root.OpenChild(proof.Row.FinalName); }
            try { RequireFile(proof.Row, file, proof.Row.Offset); return new(proof, root, file); }
            catch { file.Dispose(); throw; }
        }
        catch { root.Dispose(); throw; }
    }
    private async Task<T> CheckOperationAsync<T>(Func<T> action, CancellationToken ct)
    {
        if (!operationReviewSlot.Wait(0)) throw new IOException("Finish or cancel the current incoming-window byte check first.");
        try { return await Task.Run(action, ct); }
        finally { operationReviewSlot.Release(); }
    }
    /// <summary>Local owner review only. No file RPC creates or confirms an offer.</summary>
    public Task<IncomingOperationReview> ReviewOperationAsync(string id, Func<bool> current, CancellationToken ct = default) => CheckOperationAsync(() =>
    {
        using var opened = grants.WithAuthorization(() => { lock (gate)
        {
            Ready(); var row = cache.Rows.Single(x => x.Id == id); RequireOperationRow(row);
            var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (now > StorageRules.MaxTimestamp - MutationRules.Lifetime) throw new IOException("The device clock cannot represent a new write window.");
            var proof = new OperationProof(row, OperationGrant(row), cache.Epoch, operationReviewGeneration, current, grants.Clock.GetTimestamp(), now);
            return OpenOperationFile(proof, ct);
        } });
        void Check() => grants.WithAuthorization(() => { lock (gate) { RequireOperationProof(opened.Proof, ct); return true; } });
        VerifyPrefix(opened.Proof.Row, opened.File, ct, Check);
        return grants.WithAuthorization(() => { lock (gate)
        {
            RequireOperationProof(opened.Proof, ct); var row = opened.Proof.Row; var now = opened.Proof.Wall;
            foreach (var old in operationReviews.Values.Where(x => x.Proof.Row.Id == id || grants.Clock.GetElapsedTime(x.Proof.Started) >= TimeSpan.FromMinutes(5)).ToArray())
                operationReviews.Remove(old.Value.Id);
            if (operationReviews.Count >= 128) throw new IOException("Cancel an earlier incoming-window review first.");
            var value = new IncomingOperationReview(ProtocolRules.NewToken(), id, row.Peer, row.Name, row.Size, row.Offset,
                row.Destination.CanonicalRoot.TrimEnd('\\') + "\\" + string.Join('\\', row.Destination.Path), row.FinalName,
                OperationExpiry(row), now + MutationRules.Lifetime, now + 300_000);
            operationReviews.Add(value.Id, new(value, opened.Proof, opened.File.Info.Version)); return value;
        } });
    }, ct);
    public Task ConfirmOperationAsync(IncomingOperationReview value, CancellationToken ct = default) => CheckOperationAsync(() =>
    {
        using var opened = grants.WithAuthorization(() => { lock (gate)
        {
            if (!operationReviews.Remove(value.Id, out var offer) || offer.Value != value) throw new IOException("This incoming-window review is unavailable or changed.");
            var result = OpenOperationFile(offer.Proof, ct);
            if (result.File.Info.Version == offer.FileVersion) return result;
            result.Dispose(); throw new IOException("The reviewed partial or published file changed. Its bytes were kept; review it again.");
        } });
        void Check() => grants.WithAuthorization(() => { lock (gate) { RequireOperationProof(opened.Proof, ct); return true; } });
        VerifyPrefix(opened.Proof.Row, opened.File, ct, Check);
        return grants.WithAuthorization(() => { lock (gate)
        {
            RequireOperationProof(opened.Proof, ct);
            Put(opened.Proof.Row with { OperationApprovedAt = opened.Proof.Wall });
            checkedPrefixes.Remove(opened.Proof.Row.Id); // The normal receiver rechecks the prefix again on resume.
            return true;
        } });
    }, ct);
    public void CancelOperationReview(string id) { lock (gate) operationReviews.Remove(id); }
    public void InvalidateOperationReviews() { lock (gate) { operationReviewGeneration++; operationReviews.Clear(); } }
}
