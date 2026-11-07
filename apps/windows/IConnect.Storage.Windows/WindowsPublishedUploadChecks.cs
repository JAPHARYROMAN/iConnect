using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsUploads
{
    private sealed class PublishedCheck(Row row, string epoch, string requestScope, string challenge)
    {
        public Row Row { get; } = row;
        public string Epoch { get; } = epoch;
        public string Scope { get; } = requestScope;
        public string Challenge { get; } = challenge;
        public TaskCompletionSource Collected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? FinishedAt { get; set; }
        public bool Verified { get; set; }
    }
    private static readonly TimeSpan PublishedCheckLifetime = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, PublishedCheck> publishedChecks = [];

    private FileResponse VerifyPublished(Row row, FileRequest request, string requestScope, FileResponse response, CancellationToken ct)
    {
        if (scope is not null || requestScope.Length == 0)
            throw new StorageException(FileStatus.Unsupported, "Published-copy checks apply to ordinary incoming copies.");
        if (!UploadBatchRules.CanonicalToken(request.MutationId) || request.Upload is not null || request.Content.Length != 0 || request.Offset != 0)
            throw new StorageException(FileStatus.InvalidRequest, "A fresh published-copy challenge is required.");
        if (row.Phase != UploadPhase.UploadCompleted || row.SecurityPolicy != ReceivedFileSecurity.PolicyVersion ||
            row.FinalName.Length == 0 || row.TemporaryIdentity.Length == 0)
            throw new StorageException(FileStatus.Conflict, "Choose a completed published copy with a recorded identity and attachment check.");

        foreach (var check in publishedChecks.Values.Where(x => !cache.Rows.Contains(x.Row) || x.FinishedAt is { } end &&
                     grants.Clock.GetElapsedTime(end) >= PublishedCheckLifetime).ToArray())
        {
            publishedChecks.Remove(check.Row.Id);
            check.Collected.TrySetResult();
        }
        if (publishedChecks.TryGetValue(row.Id, out var previous) && previous.Challenge == request.MutationId &&
            previous.Scope == requestScope && previous.Row == row && previous.Epoch == cache.Epoch)
        {
            if (previous.FinishedAt is not null)
            {
                try
                {
                    if (!previous.Verified || !verifying.TryGetValue(row.Id, out var active) || active.Cancellation.IsCancellationRequested)
                        throw new StorageException(FileStatus.Conflict, "The published copy could not be verified. Its receipt was kept.");
                    RequireSavedPublishedReceipt(row);
                    response.Upload = Info(row);
                    response.PublishedUploadCheck = new() { Challenge = previous.Challenge, Verified = true };
                    return response;
                }
                finally
                {
                    publishedChecks.Remove(row.Id);
                    previous.Collected.TrySetResult();
                }
            }
        }
        else
        {
            if (verifying.ContainsKey(row.Id))
                throw new StorageException(FileStatus.Conflict, "A check for this copy is already running.");
            RequireSavedPublishedReceipt(row);
            var slot = grants.ReserveIncomingVerification() ??
                throw new StorageException(FileStatus.ResourceExhausted, "Incoming verification is busy. Retry shortly.");
            var check = new PublishedCheck(row, cache.Epoch, requestScope, request.MutationId);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10) + PublishedCheckLifetime);
            var work = new Work(cancellation, slot);
            publishedChecks[row.Id] = check;
            verifying.TryAdd(row.Id, work);
            work.Task = Task.Run(() => VerifyPublishedAsync(check, work));
        }
        response.Upload = Info(row);
        response.PublishedUploadCheck = new() { Challenge = request.MutationId };
        return response;
    }

    private async Task VerifyPublishedAsync(PublishedCheck check, Work work)
    {
        var row = check.Row;
        var token = work.Cancellation.Token;
        try
        {
            using var root = grants.WithAuthorization(() =>
            {
                lock (gate) { RequireCurrent(); return Open(row); }
            });
            using var file = root.OpenChild(row.FinalName);
            RequireFile(row, file, row.Size);
            WindowsDownloadManager.VerifyFile(file, row.Size, row.Hash, token);
            grants.WithAuthorization(() =>
            {
                lock (gate)
                {
                    RequireCurrent();
                    RequireSavedPublishedReceipt(row);
                    check.Verified = true;
                    check.FinishedAt = grants.Clock.GetTimestamp();
                    return true;
                }
            });
            // The exact file remains open without sharing writes or deletion until collection.
            await check.Collected.Task.WaitAsync(PublishedCheckLifetime, token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException || StateRecovery.IsRecordFailure(ex))
        {
            lock (gate)
            {
                if (publishedChecks.GetValueOrDefault(row.Id) == check)
                {
                    if (check.Verified) publishedChecks.Remove(row.Id);
                    else check.FinishedAt = grants.Clock.GetTimestamp();
                }
            }
        }
        finally
        {
            lock (gate)
            {
                verifying.TryRemove(row.Id, out _);
                work.Slot.Dispose();
                work.Cancellation.Dispose();
            }
        }

        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (publishedChecks.GetValueOrDefault(row.Id) != check || cache.Epoch != check.Epoch ||
                cache.Rows.SingleOrDefault(x => x.Id == row.Id) != row)
                throw new IOException("The published-copy check context changed.");
            Authorize(row);
        }
    }

    private void RequireSavedPublishedReceipt(Row row)
    {
        if (File.Exists(path + ".new") || Directory.Exists(path + ".new") || File.Exists(path + ".bak") || Directory.Exists(path + ".bak"))
            throw new IOException("Incoming-copy history needs review.");
        var saved = StateRecovery.Read<Cache>(path, 2 * 1024 * 1024);
        if (saved.Epoch != cache.Epoch || saved.OwnerScope != cache.OwnerScope || saved.Scope != cache.Scope ||
            !JsonSerializer.SerializeToUtf8Bytes(saved.Rows.SingleOrDefault(x => x.Id == row.Id)).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(row)))
            throw new IOException("The recorded published-copy receipt changed.");
    }

    private void InvalidatePublishedChecks() => CancelPublishedChecks(null);

    internal void CancelPublishedChecks(string? requestScope)
    {
        lock (gate)
        {
            foreach (var check in publishedChecks.Values.Where(x => requestScope is null || x.Scope == requestScope).ToArray())
            {
                publishedChecks.Remove(check.Row.Id);
                if (verifying.TryGetValue(check.Row.Id, out var work)) work.Cancellation.Cancel();
            }
        }
    }
}
