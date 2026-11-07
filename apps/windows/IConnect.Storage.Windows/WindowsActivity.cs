using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public enum WindowsActivityKind {
    FileRequest, IncomingCopy, BatchFile, BatchReceive, Download, DownloadBatchFile, DownloadBatch, PrivateCopy, FileSend, BatchPrepare, BatchSend,
    ProgressPreservation, ProgressBackupRemoval, IncomingPrivateCleanup, IncomingHistoryReset, BatchHistoryReset, ReceivingPartCleanup,
    SenderStoreReset, SourceStoreReset, SenderArchiveCleanup, SourceArchiveCleanup, ReceiverHistoryRetirement, SenderHistoryCleanup, PrivateSourceCleanup, ReceivedCopyCheck,
    SenderHistoryArchive, SenderHistoryRestore, SenderHistoryPurge, FileChange, CoordinatorConnection, DirectConnection, LocalCopy, LocalBatchPlan, LocalBatchCopy, LocalBatchRecovery, LocalFileChange, LocalFileRecovery
}
public enum WindowsActivityOutcome { Accepted, Refused, Failed, Canceled, Completed, Review, Paused, Skipped, CompletedWithIssues, Requested, Ended }

internal static class WindowsActivity
{
    internal static void Publish(Action<WindowsActivityEvent>? sink, WindowsActivityEvent? value)
    { if (value is not null) try { sink?.Invoke(value.Sanitized()); } catch (Exception) { /* Observability cannot change an operation's result. */ } }
}

/// <summary>Compiled local event data only. Never pass requests, receipts, paths or exception text.</summary>
public sealed record WindowsActivityEvent(WindowsActivityKind Kind, WindowsActivityOutcome Outcome, string Peer = "", long Bytes = -1, FileOperation? Operation = null)
{
    public WindowsActivityEvent Sanitized() => this with
    {
        Peer = Peer is { Length: 64 } && Peer.All(char.IsAsciiHexDigitLower) ? Peer : "",
        Bytes = Bytes >= 0 ? Bytes : -1,
        Operation = Operation is { } operation && Enum.IsDefined(operation) ? operation : null
    };
    internal static WindowsActivityEvent? Request(string peer, FileOperation operation, FileStatus status)
    {
        if (status == FileStatus.Ok && operation is FileOperation.ReadRange or FileOperation.DownloadRead or FileOperation.DownloadStatus or
            FileOperation.UploadWrite or FileOperation.WriteBatchFile or FileOperation.UploadStatus or FileOperation.UploadBatchItemStatus or
            FileOperation.VerifyPublishedUpload) return null;
        return new(WindowsActivityKind.FileRequest, status switch
        { FileStatus.Ok => WindowsActivityOutcome.Accepted, FileStatus.AccessDenied or FileStatus.Unsupported => WindowsActivityOutcome.Refused, _ => WindowsActivityOutcome.Failed }, peer, Operation: operation);
    }
}
