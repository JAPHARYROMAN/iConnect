using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record DownloadSourceReview(string Id, string Job, string Name, long Size, long Checkpoint,
    string OldVersion, string NewVersion, string Hash, bool OriginalHashKnown, long SourceExpiresAt, long ReviewExpiresAt);

public sealed partial class WindowsDownloadManager
{
    private sealed record PendingSource(DownloadSourceReview Value, string Peer, string Node, string JobStamp, DownloadInfo Info, Request Request, Func<bool> Current, long Started,
        WindowsBatchDownloads.SourceAuthority? Authority);
    private readonly Dictionary<string, PendingSource> sourceReviews = [];
    private readonly Dictionary<string, TaskCompletionSource> sourcePreparations = [];
    private readonly HashSet<(string Peer, string Ticket)> freshTickets = [];
    private readonly CancellationTokenSource sourceRecoveryLifetime = new();
    private static string SourceStamp(DownloadJob job) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job)));
    private void RequireSourceRecovery(DownloadJob job, string peer, WindowsBatchDownloads.SourceAuthority? authority = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
        if (authority is not null) authority.Require(job);
        if (localCopy || (authority is null && (batchOnly || job.BatchId.Length != 0)) || job.PeerId != peer || running.ContainsKey(job.Id) ||
            job.Phase is not (DownloadPhase.Paused or DownloadPhase.Interrupted) || job.FinalName.Length != 0 || job.Offset == job.Size && job.Hash.Length == 64 || job.Offset != 0 && job.Hash.Length != 64)
            throw new IOException("Choose an idle, unfinished standalone remote download for this device. Batch and local copies retain their own source authority.");
    }
    public Task<DownloadSourceReview> PrepareSourceReviewAsync(string id, string peer, FileEntry selected, Request request, Func<bool> current, CancellationToken ct = default) =>
        PrepareSourceReviewCoreAsync(id, peer, selected, request, current, null, ct);
    internal Task<DownloadSourceReview> PrepareBatchSourceReviewAsync(string id, string peer, FileEntry selected, Request request, Func<bool> current,
        WindowsBatchDownloads.SourceAuthority authority, CancellationToken ct) => PrepareSourceReviewCoreAsync(id, peer, selected, request, current, authority, ct);
    private async Task<DownloadSourceReview> PrepareSourceReviewCoreAsync(string id, string peer, FileEntry selected, Request request, Func<bool> current,
        WindowsBatchDownloads.SourceAuthority? authority, CancellationToken ct)
    {
        DownloadJob original; TaskCompletionSource fence; PendingSource[] expired;
        selected = selected.Clone();
        lock (gate)
        {
            original = jobs[id]; RequireSourceRecovery(original, peer, authority);
            if (!current()) throw new IOException("The source connection changed. Select the current source again.");
            if (selected.IsDirectory || selected.Name != original.Name || selected.Size != original.Size || !ProtocolRules.TokenPattern().IsMatch(selected.Id))
                throw new IOException("Select a current file with the retained download's name and size.");
            expired = sourceReviews.Values.Where(x => clock.GetElapsedTime(x.Started) > TimeSpan.FromMinutes(5)).ToArray();
            foreach (var row in expired) sourceReviews.Remove(row.Value.Id);
            if (sourcePreparations.ContainsKey(id) || sourceReviews.Values.Any(x => x.Value.Job == id) || sourcePreparations.Count + sourceReviews.Count >= 8) throw new IOException("Finish an earlier source review first.");
            fence = new(TaskCreationOptions.RunContinuationsAsynchronously); sourcePreparations.Add(id, fence);
        }
        string ticket = ""; var retained = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sourceRecoveryLifetime.Token);
        async Task<FileResponse> Rpc(FileRequest value)
        { linked.Token.ThrowIfCancellationRequested(); if (!current()) throw new IOException("The source connection changed. Prepare a new review."); return await Checked(request, value, linked.Token); }
        try
        {
            foreach (var row in expired) { await ReleaseSourceAsync(row.Info.Id, row.Request); lock (gate) freshTickets.Remove((row.Peer, row.Info.Id)); }
            var metadata = await Rpc(new() { Operation = FileOperation.GetMetadata, NodeId = selected.Id });
            var actual = metadata.Entries.Single();
            if (actual.Id != selected.Id || actual.Name != selected.Name || actual.Size != selected.Size || actual.Version != selected.Version || actual.IsDirectory)
                throw new IOException("The selected source changed. Refresh it and review again.");
            var info = (await Rpc(new() { Operation = FileOperation.PrepareDownload, NodeId = actual.Id, ExpectedVersion = actual.Version })).Download
                ?? throw new InvalidDataException("The source did not return a preparation descriptor.");
            lock (gate)
            {
                if (jobs.Values.Any(x => x.PeerId == peer && x.Ticket == info.Id) || !freshTickets.Add((peer, info.Id)))
                    throw new IOException("The source reused another receiving ticket. Existing approvals were kept.");
                ticket = info.Id;
            }
            while (true)
            {
                if (info.Id != ticket || info.Name != actual.Name || info.Version != actual.Version || info.Size != original.Size || info.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
                    throw new IOException("The fresh source descriptor changed or expired. Earlier progress was kept.");
                if (info.State != DownloadState.DownloadPreparing) break;
                await Task.Delay(300, linked.Token);
                info = (await Rpc(new() { Operation = FileOperation.DownloadStatus, TransferId = ticket })).Download ?? throw new InvalidDataException("The source descriptor is missing.");
            }
            if (info.State != DownloadState.DownloadReady || info.Sha256.Length != 32) throw new IOException("The fresh source did not finish preparation.");
            var hash = Convert.ToHexStringLower(info.Sha256.Span);
            if (original.Hash.Length != 0 && original.Hash != hash) throw new IOException("The selected source's full hash differs. Earlier bytes and the destination were kept; choose the original content.");
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                RequireSourceRecovery(jobs[id], peer, authority);
                if (!current() || SourceStamp(jobs[id]) != SourceStamp(original)) throw new IOException("The download or source connection changed while preparing its review.");
                var value = new DownloadSourceReview(ProtocolRules.NewToken(), id, original.Name, original.Size, original.Offset, original.SourceVersion, actual.Version, hash,
                    original.Hash.Length == 64, info.ExpiresAt, clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds());
                sourceReviews.Add(value.Id, new(value, peer, actual.Id, SourceStamp(original), info.Clone(), request, current, clock.GetTimestamp(), authority)); retained = true; return value;
            }
        }
        finally
        {
            if (!retained && ticket.Length != 0) { await ReleaseSourceAsync(ticket, request); lock (gate) freshTickets.Remove((peer, ticket)); }
            lock (gate) sourcePreparations.Remove(id); fence.TrySetResult();
        }
    }
    public Task ConfirmSourceReviewAsync(DownloadSourceReview value, CancellationToken ct = default) => ConfirmSourceReviewCoreAsync(value, null, ct);
    internal Task ConfirmBatchSourceReviewAsync(DownloadSourceReview value, Action<DownloadJob> beforeAdoption, CancellationToken ct) =>
        ConfirmSourceReviewCoreAsync(value, beforeAdoption, ct);
    private async Task ConfirmSourceReviewCoreAsync(DownloadSourceReview value, Action<DownloadJob>? beforeAdoption, CancellationToken ct)
    {
        PendingSource pending; DownloadJob? original; TaskCompletionSource fence;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sourceReviews.TryGetValue(value.Id, out pending!)) throw new IOException("This source review expired or was already used.");
            if (pending.Authority is not null && (!pending.Authority.Confirming || beforeAdoption is null)) throw new IOException("Approve this receiving source through its original batch review.");
            sourceReviews.Remove(value.Id);
            jobs.TryGetValue(pending.Value.Job, out original);
            fence = new(TaskCreationOptions.RunContinuationsAsynchronously); sourcePreparations.Add(pending.Value.Job, fence);
        }
        var adopted = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sourceRecoveryLifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (original is null) throw new IOException("The retained download was removed after this review. Its fresh source was not adopted.");
            if (pending.Value != value || clock.GetElapsedTime(pending.Started) > TimeSpan.FromMinutes(5) || !pending.Current()) throw new IOException("The source review changed, expired or lost its connection.");
            var status = (await Checked(pending.Request, new() { Operation = FileOperation.DownloadStatus, TransferId = pending.Info.Id }, linked.Token)).Download
                ?? throw new IOException("The fresh source is no longer available.");
            if (status.Id != pending.Info.Id || status.Name != pending.Info.Name || status.Size != original.Size || status.Version != pending.Info.Version ||
                status.State != DownloadState.DownloadReady || !status.Sha256.Equals(pending.Info.Sha256) || status.ExpiresAt != pending.Info.ExpiresAt || status.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
                throw new IOException("The reviewed source changed or expired. Earlier receiving progress was kept.");
            lock (gate)
            {
                if (!jobs.TryGetValue(original.Id, out var currentJob)) throw new IOException("The retained download was removed after review.");
                RequireSourceRecovery(currentJob, pending.Peer, pending.Authority);
                linked.Token.ThrowIfCancellationRequested(); if (!pending.Current() || SourceStamp(jobs[original.Id]) != pending.JobStamp) throw new IOException("The download or connection changed after review.");
                beforeAdoption?.Invoke(original);
                linked.Token.ThrowIfCancellationRequested();
                if (!pending.Current()) throw new IOException("The source connection ended before adoption.");
                // Only source authority changes. The destination, part identity, offset, chain and collision decision stay bound.
                var next = original with { Ticket = status.Id, SourceNode = pending.Node, SourceVersion = status.Version, Hash = value.Hash, SourceExpiresAt = status.ExpiresAt, Error = "Fresh source approved. Its retained prefix will be verified on resume." };
                Persist(next); jobs[next.Id] = next; requesters[next.Id] = pending.Request; adopted = true;
            }
            NotifySourceRecovery(original.Id);
            await ReleaseSourceAsync(original.Ticket, pending.Request);
        }
        finally { if (!adopted) await ReleaseSourceAsync(pending.Info.Id, pending.Request); lock (gate) { freshTickets.Remove((pending.Peer, pending.Info.Id)); sourcePreparations.Remove(pending.Value.Job); } fence.TrySetResult(); }
    }
    public async Task CancelSourceReviewAsync(string id)
    {
        PendingSource? pending;
        lock (gate) sourceReviews.Remove(id, out pending);
        if (pending is not null) { await ReleaseSourceAsync(pending.Info.Id, pending.Request); lock (gate) freshTickets.Remove((pending.Peer, pending.Info.Id)); }
    }
    private static async Task ReleaseSourceAsync(string ticket, Request request)
    {
        if (ticket.Length == 0) return; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await request(new() { Operation = FileOperation.ReleaseDownload, TransferId = ticket }, timeout.Token); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or NotSupportedException) { }
    }
    private void NotifySourceRecovery(string id)
    { if (Changed is not { } listeners) return; DownloadJob? job; lock (gate) jobs.TryGetValue(id, out job); if (job is null) return; foreach (Action<DownloadJob> listener in listeners.GetInvocationList()) try { listener(job); } catch (Exception) { } }
}
