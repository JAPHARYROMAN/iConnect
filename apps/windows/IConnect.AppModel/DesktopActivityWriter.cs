using System.Threading.Channels;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

/// <summary>Bounded serving-event sink. Never performs journal I/O in a permission/provider callback.</summary>
internal sealed class DesktopActivityWriter : IAsyncDisposable
{
    private sealed record Captured(long At, WindowsActivityEvent Event);
    private sealed record Command(Captured? Event = null, string Action = "", int Days = 30, TaskCompletionSource? Reply = null);
    private readonly LocalActivityStore store;
    private readonly Action changed;
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim commands = new(1);
    private readonly Channel<Command> queue = Channel.CreateBounded<Command>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Task work;
    private bool closed;
    private long dropped;
    private long nextOverflowNotice;
    public long Dropped => Interlocked.Read(ref dropped);
    public DesktopActivityWriter(LocalActivityStore store, Action changed)
    { this.store = store; this.changed = changed; work = Task.Run(Run); }
    private void Notify() { try { changed(); } catch (Exception) { } }
    public void Record(WindowsActivityEvent value)
    {
        lock (lifecycle)
        {
            if (closed || store.Days == 0 && store.Issue.Length == 0) return;
            value = value.Sanitized();
            if (Enum.IsDefined(value.Kind) && Enum.IsDefined(value.Outcome) && queue.Writer.TryWrite(new(new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), value)))) return;
        }
        Interlocked.Increment(ref dropped);
        var now = Environment.TickCount64; var due = Interlocked.Read(ref nextOverflowNotice);
        if (now >= due && Interlocked.CompareExchange(ref nextOverflowNotice, now + 100, due) == due) Notify();
    }
    public Task Flush() => Control("flush");
    public Task Configure(int days) { if (!LocalActivityStore.ValidDays(days)) throw new ArgumentException("Choose a supported retention period."); return Control("configure", days); }
    public Task Clear() => Control("clear");
    public Task Reset() => Control("reset");
    private async Task Control(string action, int days = 30)
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await commands.WaitAsync();
        try { lock (lifecycle) ObjectDisposedException.ThrowIf(closed, this); await queue.Writer.WriteAsync(new(Action: action, Days: days, Reply: reply)); }
        finally { commands.Release(); }
        await reply.Task;
    }
    private async Task Run()
    {
        await foreach (var first in queue.Reader.ReadAllAsync())
        {
            var batch = new List<Command> { first };
            if (first.Event is not null)
            {
                await Task.Delay(100);
                while (batch.Count < 64 && queue.Reader.TryRead(out var next)) batch.Add(next);
            }
            var pending = new List<Captured>();
            void FlushPending()
            {
                if (pending.Count == 0) return;
                try { store.AddObserved(Entries(pending)); }
                catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Interlocked.Add(ref dropped, pending.Count); }
                pending.Clear(); Notify();
            }
            foreach (var command in batch)
            {
                if (command.Event is { } value) { pending.Add(value); continue; }
                FlushPending();
                try
                {
                    switch (command.Action)
                    {
                        case "configure": store.Configure(command.Days); break;
                        case "clear": store.Clear(); break;
                        case "reset": store.PreserveAndReset(); break;
                        case "flush": if (store.Issue.Length != 0) throw new IOException(store.Issue); break;
                    }
                    command.Reply?.TrySetResult();
                }
                catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { command.Reply?.TrySetException(ex); }
                Notify();
                if (command.Action == "close") return;
            }
            FlushPending();
        }
    }
    private static IReadOnlyList<ActivityEntry> Entries(List<Captured> values)
    {
        var rows = new List<ActivityEntry>();
        for (var index = 0; index < values.Count;)
        {
            var first = values[index++]; var count = 1; var bytes = first.Event.Bytes;
            while (index < values.Count && values[index].At >= first.At && values[index].At - first.At <= 1000 &&
                (values[index].Event with { Bytes = -1 }) == (first.Event with { Bytes = -1 }))
            {
                var next = values[index++].Event.Bytes; count++;
                bytes = bytes < 0 || next < 0 || bytes > long.MaxValue - next ? -1 : bytes + next;
            }
            var value = first.Event;
            var category = value.Kind switch { WindowsActivityKind.FileRequest => "Incoming requests", WindowsActivityKind.IncomingCopy or WindowsActivityKind.BatchFile or WindowsActivityKind.BatchReceive => "Incoming copies",
                WindowsActivityKind.FileChange => "Remote changes", WindowsActivityKind.LocalFileChange => "Local changes", WindowsActivityKind.CoordinatorConnection or WindowsActivityKind.DirectConnection => "Connections",
                WindowsActivityKind.LocalBatchPlan or WindowsActivityKind.LocalBatchCopy or WindowsActivityKind.LocalCopy or WindowsActivityKind.Download or WindowsActivityKind.DownloadBatchFile or WindowsActivityKind.DownloadBatch or WindowsActivityKind.PrivateCopy or WindowsActivityKind.FileSend or WindowsActivityKind.BatchPrepare or WindowsActivityKind.BatchSend => "Transfers", _ => "Local recovery" };
            var action = value.Kind switch
            {
                WindowsActivityKind.IncomingCopy => "Incoming file publication", WindowsActivityKind.BatchFile => "Incoming batch file publication",
                WindowsActivityKind.BatchReceive => "Incoming batch", WindowsActivityKind.Download => "Downloaded copy verification", WindowsActivityKind.DownloadBatchFile => "Downloaded batch-file verification",
                WindowsActivityKind.DownloadBatch => "Folder download", WindowsActivityKind.PrivateCopy => "Private file preparation", WindowsActivityKind.FileSend => "Send prepared file",
                WindowsActivityKind.LocalCopy => "Local file copy verification",
                WindowsActivityKind.LocalBatchPlan => "Local file and folder planning",
                WindowsActivityKind.LocalBatchCopy => "Local file and folder copy verification",
                WindowsActivityKind.LocalBatchRecovery => "Local batch progress recovery",
                WindowsActivityKind.LocalFileChange => Operation(value.Operation),
                WindowsActivityKind.LocalFileRecovery => "Local file recovery",
                WindowsActivityKind.BatchPrepare => "Private batch preparation", WindowsActivityKind.BatchSend => "Send prepared batch",
                WindowsActivityKind.ProgressPreservation => "Preserve unreadable progress", WindowsActivityKind.ProgressBackupRemoval => "Remove preserved progress metadata",
                WindowsActivityKind.IncomingPrivateCleanup => "Remove incoming private staging", WindowsActivityKind.IncomingHistoryReset => "Reset incoming file history",
                WindowsActivityKind.BatchHistoryReset => "Reset incoming batch history", WindowsActivityKind.ReceivingPartCleanup => "Remove reviewed receiving partials",
                WindowsActivityKind.SenderStoreReset => "Preserve and reset sender store", WindowsActivityKind.SourceStoreReset => "Preserve and reset private-source store",
                WindowsActivityKind.SenderArchiveCleanup => "Remove reviewed sender archive", WindowsActivityKind.SourceArchiveCleanup => "Remove reviewed source archive",
                WindowsActivityKind.ReceiverHistoryRetirement => "Retire receiver item history", WindowsActivityKind.SenderHistoryCleanup => "Manage retained sending history",
                WindowsActivityKind.PrivateSourceCleanup => "Remove reviewed private copies", WindowsActivityKind.ReceivedCopyCheck => "Recheck recorded received copy",
                WindowsActivityKind.SenderHistoryArchive => "Archive sending history", WindowsActivityKind.SenderHistoryRestore => "Restore original sending intent",
                WindowsActivityKind.SenderHistoryPurge => "Remove reviewed sending history", WindowsActivityKind.FileChange => Operation(value.Operation),
                WindowsActivityKind.CoordinatorConnection => "Coordinator connection", WindowsActivityKind.DirectConnection => "Direct device connection", _ => Operation(value.Operation)
            };
            var outcome = value.Outcome switch
            { WindowsActivityOutcome.Accepted => "accepted", WindowsActivityOutcome.Requested => "requested", WindowsActivityOutcome.Refused => "refused", WindowsActivityOutcome.Completed => "completed",
                WindowsActivityOutcome.Canceled => "canceled", WindowsActivityOutcome.Review => "needs review", WindowsActivityOutcome.Paused => "paused", WindowsActivityOutcome.Skipped => "skipped",
                WindowsActivityOutcome.CompletedWithIssues => "finished with skipped or failed items", WindowsActivityOutcome.Ended => "ended", _ => "failed" };
            var description = action + " · " + outcome + (count > 1 ? $" · {count} events" : "") + (bytes >= 0 ? $" · {bytes} bytes" : "");
            rows.Add(new(ProtocolRules.NewToken(), first.At, category, description, value.Peer.Length == 0 ? null : value.Peer));
        }
        return rows;
    }
    private static string Operation(FileOperation? value) => value switch
    {
        FileOperation.ListRoots or FileOperation.ListDirectory => "List approved folders", FileOperation.GetMetadata => "Read item details",
        FileOperation.ReadRange or FileOperation.DownloadRead => "Read file bytes", FileOperation.Search => "Search approved folders",
        FileOperation.CreateDirectory or FileOperation.CreateBatchDirectory => "Create receiving folder", FileOperation.Rename => "Rename item",
        FileOperation.Copy => "Copy item", FileOperation.Move => "Move item", FileOperation.Delete => "Recoverable delete", FileOperation.Restore => "Restore saved item",
        FileOperation.PrepareDownload => "Prepare file download", FileOperation.ReleaseDownload => "Release sending ticket", FileOperation.CloseListing => "Close folder listing",
        FileOperation.MutationContext or FileOperation.MutationStatus or FileOperation.ListMutations => "Review file changes",
        FileOperation.RecoveryContext or FileOperation.RecoveryStatus or FileOperation.ListDeleted => "Review saved items",
        FileOperation.UploadContext or FileOperation.ListUploads => "Review receiving locations or receipts", FileOperation.PrepareUpload or FileOperation.PrepareBatchFile => "Prepare incoming file",
        FileOperation.UploadWrite or FileOperation.WriteBatchFile => "Receive file bytes", FileOperation.FinishUpload or FileOperation.FinishBatchFile => "Request incoming file finalization",
        FileOperation.CancelUpload or FileOperation.CancelBatchFile => "Cancel incoming file", FileOperation.ResolveCreatedDirectory => "Recover created folder reference",
        FileOperation.UploadBatchContext => "Review batch receiving context", FileOperation.BeginUploadBatch => "Admit incoming batch", FileOperation.WriteUploadManifest => "Receive batch manifest",
        FileOperation.SealUploadBatch => "Validate incoming batch plan", FileOperation.CancelUploadBatch or FileOperation.StopUploadBatch => "Stop incoming batch",
        FileOperation.FinishUploadBatch => "Request incoming batch finalization", FileOperation.DownloadStatus or FileOperation.UploadStatus or
            FileOperation.UploadBatchStatus or FileOperation.UploadBatchItemStatus => "Review transfer status", _ => "Unsupported file operation"
    };
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource? reply = null;
        await commands.WaitAsync();
        try
        {
            bool first; lock (lifecycle) { first = !closed; closed = true; }
            if (first) { reply = new(TaskCreationOptions.RunContinuationsAsynchronously); await queue.Writer.WriteAsync(new(Action: "close", Reply: reply)); }
        }
        finally { commands.Release(); }
        if (reply is not null) await reply.Task;
        await work; queue.Writer.TryComplete();
    }
}
