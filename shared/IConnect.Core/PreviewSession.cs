using IConnect.Protocol;
using System.Security.Cryptography;

namespace IConnect.Core;

public sealed record PreviewView(string Name = "", string Text = "", PreviewFrame? Frame = null, bool Busy = false, string Error = "");

/// <summary>One foreground viewer. Navigation cancels work, clears retained input and rejects late publication.</summary>
public sealed class PreviewSession(IPreviewRenderer? renderer = null)
{
    private readonly object gate = new();
    private CancellationTokenSource? lifetime;
    private PreviewInput? cached;
    private Func<bool>? available;
    private int revision;
    private PreviewView view = new();
    public PreviewView View { get { lock (gate) return view; } }
    public event Action? Changed;
    public void Clear()
    {
        bool changed;
        lock (gate) { changed = view.Name.Length != 0; Reset(); }
        if (changed) Changed?.Invoke();
    }
    // Caller holds gate. A new selection must reset and acquire its revision atomically.
    private void Reset()
    {
        ++revision; lifetime?.Cancel(); lifetime = null; available = null;
        if (cached is not null) CryptographicOperations.ZeroMemory(cached.Bytes);
        cached = null; view = new();
    }
    public async Task OpenAsync(FileEntry entry, Func<FileRequest, CancellationToken, Task<FileResponse>> request, Func<bool> isAvailable, CancellationToken cancellation = default)
    {
        CancellationTokenSource operation; int current;
        lock (gate) { Reset(); current = revision; lifetime = operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); operation.CancelAfter(TimeSpan.FromSeconds(30)); available = isAvailable; view = new(entry.Name, Busy: true); }
        Changed?.Invoke(); PreviewInput? input = null;
        try
        {
            if (!isAvailable()) throw new OperationCanceledException();
            input = await PreviewRules.FetchAsync(entry, (value, token) => isAvailable() ? request(value, token) : throw new OperationCanceledException(), operation.Token);
            var text = input.Kind == PreviewKind.Text ? PreviewRules.Text(input) : "";
            var frame = input.Kind == PreviewKind.Text ? null : await (renderer ?? throw new IOException("Image and PDF previews are unavailable in this application host.")).RenderAsync(input, 0, operation.Token);
            lock (gate)
            {
                if (current != revision) return;
                if (operation.IsCancellationRequested || !isAvailable()) { view = new(); available = null; return; }
                if (input.Kind == PreviewKind.Pdf) { cached = input; input = null; }
                view = new(entry.Name, text, frame);
            }
        }
        catch (Exception ex) { lock (gate) if (current == revision) view = isAvailable() ? new(entry.Name, Error: ex is OperationCanceledException ? "Preview canceled or timed out." : ex.Message) : new(); }
        finally
        {
            if (input is not null) CryptographicOperations.ZeroMemory(input.Bytes);
            lock (gate) { if (ReferenceEquals(lifetime, operation)) lifetime = null; }
            operation.Dispose(); Changed?.Invoke();
        }
    }
    public async Task PageAsync(int page)
    {
        PreviewInput input; CancellationTokenSource operation; Func<bool> isAvailable; int current; PreviewView previous;
        lock (gate)
        {
            if (view.Busy || cached?.Kind != PreviewKind.Pdf || view.Frame is null || page < 0 || page >= view.Frame.Pages) return;
            isAvailable = available ?? (() => false);
            if (!isAvailable()) { Reset(); Changed?.Invoke(); return; }
            input = cached; current = revision; previous = view; lifetime = operation = new(TimeSpan.FromSeconds(20)); view = view with { Busy = true, Error = "" };
        }
        Changed?.Invoke();
        try
        {
            var frame = await renderer!.RenderAsync(input, page, operation.Token);
            lock (gate) if (current == revision)
            {
                if (operation.IsCancellationRequested || !isAvailable()) Reset();
                else view = previous with { Frame = frame };
            }
        }
        catch (Exception ex) { lock (gate) if (current == revision) { if (!isAvailable()) Reset(); else view = previous with { Error = ex is OperationCanceledException ? "Preview canceled or timed out." : ex.Message }; } }
        finally { lock (gate) { if (ReferenceEquals(lifetime, operation)) lifetime = null; } operation.Dispose(); Changed?.Invoke(); }
    }
}
