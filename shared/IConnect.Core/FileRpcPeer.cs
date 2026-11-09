using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

// Transport-neutral file RPC. Incoming I/O never blocks the channel's message
// reader, and both incoming work and outstanding requests are bounded.
public sealed class FileRpcPeer : IDisposable
{
    public const string Capability = "files-v1";
    private readonly string peerId;
    private readonly Action<Frame> send;
    private readonly IFileService? service;
    private readonly FileTrafficPolicy? traffic;
    private readonly Channel<FileRequest> incoming = Channel.CreateBounded<FileRequest>(16);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FileResponse>> pending = new();
    private readonly SemaphoreSlim credits = new(16);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private int disposed;
    public Task Completion => worker;

    public FileRpcPeer(string peerId, Action<Frame> send, IFileService? service = null, FileTrafficPolicy? traffic = null)
    {
        this.peerId = peerId; this.send = send; this.service = service; this.traffic = traffic;
        worker = Task.Run(ProcessAsync);
    }

    public async Task<FileResponse> RequestAsync(FileRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var copy = request.Clone();
        await credits.WaitAsync(linked.Token);
        var id = ProtocolRules.NewToken();
        try
        {
            if (traffic is not null) await traffic.BeforeRequestAsync(copy, true, linked.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            copy.RequestId = id;
            var completion = new TaskCompletionSource<FileResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, completion)) throw new IOException("Could not allocate a file request.");
            traffic?.RequireRequest(copy);
            send(ProtocolRules.Pack("file-request", copy));
            return await completion.Task.WaitAsync(linked.Token);
        }
        finally { pending.TryRemove(id, out _); credits.Release(); }
    }

    public bool Handle(Frame frame)
    {
        if (frame.Kind == "file-response")
        {
            var response = FileResponse.Parser.ParseFrom(frame.Payload);
            StorageRules.ValidateResponse(response);
            if (pending.TryRemove(response.RequestId, out var completion)) completion.TrySetResult(response);
            return true; // A late response after local cancellation is safely discarded.
        }
        if (frame.Kind != "file-request") return false;
        var request = FileRequest.Parser.ParseFrom(frame.Payload);
        if (!ProtocolRules.TokenPattern().IsMatch(request.RequestId)) throw new InvalidDataException("Invalid file request identifier.");
        (service as IPublishedBatchCheckCancellation)?.InterruptPublishedBatchCheck(peerId, request);
        if (!incoming.Writer.TryWrite(request))
            send(ProtocolRules.Pack("file-response", new FileResponse
            { RequestId = request.RequestId, Status = FileStatus.ResourceExhausted, Message = "Too many file operations. Retry shortly." }));
        return true;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var request in incoming.Reader.ReadAllAsync(lifetime.Token))
            {
                FileResponse response;
                try
                {
                    if (traffic is not null) await traffic.BeforeRequestAsync(request, false, lifetime.Token);
                    response = service is null
                        ? new FileResponse { RequestId = request.RequestId, Status = FileStatus.AccessDenied, Message = "No storage is shared with this device." }
                        : await service.ExecuteAsync(peerId, request, lifetime.Token);
                    if (traffic is not null && response.Content.Length != 0) traffic.RequireContent();
                }
                catch (StorageException ex) { response = new() { RequestId = request.RequestId, Status = ex.Status, Message = ex.Message }; }
                if (!lifetime.IsCancellationRequested) send(ProtocolRules.Pack("file-response", response));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            foreach (var completion in pending.Values) completion.TrySetException(new IOException("The file session ended."));
        }
        finally { service?.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); incoming.Writer.TryComplete();
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        // The worker owns service cleanup, after any current filesystem call exits.
        _ = worker.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
