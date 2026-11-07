using IConnect.Protocol;

namespace IConnect.Core;

public enum TransferNetworkCost { Unknown, Unrestricted, Metered }
public sealed record TransferNetworkPreferences(long BytesPerSecond = 0, bool PauseOnMetered = false)
{
    public static void Validate(TransferNetworkPreferences value)
    { if (value.BytesPerSecond != 0 && value.BytesPerSecond is < 65536 or > 67108864) throw new InvalidDataException("Choose unlimited bandwidth or a limit between 64 KiB/s and 64 MiB/s."); }
}

/// <summary>One owner-wide payload budget per direction; metadata and cleanup remain usable.</summary>
public sealed class FileTrafficPolicy
{
    private readonly object gate = new();
    private readonly SemaphoreSlim sending = new(1), receiving = new(1);
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private TransferNetworkPreferences preferences = new();
    private TransferNetworkCost cost;
    private long observed;
    private bool observedNetwork, healthy = true;
    private const string Paused = "Remote file content is paused on a metered or unknown network. Review Transfer network controls in Settings, then explicitly resume the transfer.";
    public FileTrafficPolicy() : this(TimeProvider.System, (duration, ct) => Task.Delay(duration, ct)) { }
    internal FileTrafficPolicy(TimeProvider clock, Func<TimeSpan, CancellationToken, Task> delay) { this.clock = clock; this.delay = delay; }
    public void Configure(TransferNetworkPreferences value, bool healthy = true)
    { TransferNetworkPreferences.Validate(value); lock (gate) { preferences = value; this.healthy = healthy; } }
    public void ObserveNetwork(TransferNetworkCost value)
    { if (!Enum.IsDefined(value)) throw new ArgumentException("Invalid network cost."); lock (gate) { cost = value; observed = clock.GetTimestamp(); observedNetwork = true; } }
    public TransferNetworkCost NetworkCost { get { lock (gate) return CurrentCost(); } }
    private TransferNetworkCost CurrentCost() => !observedNetwork || clock.GetElapsedTime(observed) > TimeSpan.FromSeconds(5) ? TransferNetworkCost.Unknown : cost;
    private long Limit()
    {
        lock (gate)
        {
            if (!healthy) throw new StorageException(FileStatus.AccessDenied, "Transfer network settings need review on this computer. File content is paused; local files were kept.");
            if (preferences.PauseOnMetered && CurrentCost() != TransferNetworkCost.Unrestricted) throw new StorageException(FileStatus.AccessDenied, Paused);
            return preferences.BytesPerSecond;
        }
    }
    public static bool IsContentOperation(FileOperation operation) => operation is FileOperation.ReadRange or FileOperation.PrepareDownload or FileOperation.DownloadRead or
        FileOperation.PrepareUpload or FileOperation.UploadWrite or FileOperation.BeginUploadBatch or FileOperation.WriteUploadManifest or FileOperation.SealUploadBatch or
        FileOperation.CreateBatchDirectory or FileOperation.PrepareBatchFile or FileOperation.WriteBatchFile;
    public void RequireContent() => _ = Limit();
    public void RequireRequest(FileRequest request)
    { if (IsContentOperation(request.Operation) || request.Content.Length != 0) RequireContent(); }
    public async Task BeforeRequestAsync(FileRequest request, bool outgoing, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Content.Length > StorageRules.MaxReadRange) throw new StorageException(FileStatus.InvalidRequest, "The file payload exceeds its range limit.");
        var reading = request.Operation is FileOperation.ReadRange or FileOperation.DownloadRead;
        if (reading && request.Length > StorageRules.MaxReadRange) throw new StorageException(FileStatus.InvalidRequest, "Choose a bounded file range.");
        RequireRequest(request);
        if (request.Content.Length != 0) await PaceAsync(request.Content.Length, outgoing, ct);
        if (reading && request.Length != 0) await PaceAsync((int)request.Length, !outgoing, ct);
        RequireRequest(request);
    }
    public async Task PaceAsync(int bytes, bool outgoing, CancellationToken ct)
    {
        if (bytes is < 0 or > StorageRules.MaxReadRange) throw new StorageException(FileStatus.InvalidRequest, "Choose a bounded file payload.");
        if (bytes == 0) { RequireContent(); return; }
        var lane = outgoing ? sending : receiving; await lane.WaitAsync(ct);
        try
        {
            var remaining = (double)bytes;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested(); var rate = Limit(); if (rate == 0) return;
                var duration = TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(Math.Min(.1, remaining / rate) * TimeSpan.TicksPerSecond))); var start = clock.GetTimestamp();
                await delay(duration, ct);
                var currentRate = Limit(); if (currentRate == 0) return;
                remaining -= Math.Max(0, clock.GetElapsedTime(start).TotalSeconds) * Math.Min(rate, currentRate);
            }
            RequireContent();
        }
        finally { lane.Release(); }
    }
}
