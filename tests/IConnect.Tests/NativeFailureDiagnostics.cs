using Xunit.Abstractions;

namespace IConnect.Tests;

/// <summary>Test-only numeric transport context; never emit SDP, candidates or authentication material.</summary>
internal static class NativeFailureDiagnostics
{
    private static readonly string[] ContextKeys =
    [
        "NativeConnectionState", "NativeChannelAttached", "NativeChannelOpened", "NativeIceState",
        "NativePeerId", "NativeChannelId", "NativeConnectionHistory"
    ];

    internal static void Write(ITestOutputHelper output, string stage, Exception failure, params (string Name, Task Work)[] tasks)
    {
        try { WriteCore(output, stage, failure, tasks); }
        catch (Exception) { /* Reporting must not replace the original failure being rethrown by the caller. */ }
    }

    private static string Bounded(string text) => text.Length <= 8192 ? text : text[..8192] + " [truncated]";

    private static void WriteCore(ITestOutputHelper output, string stage, Exception failure, (string Name, Task Work)[] tasks)
    {
        output.WriteLine($"Primary native failure before cleanup ({stage}): {Bounded(failure.ToString())}");
        var pending = new Queue<(string Source, Exception Failure)>();
        pending.Enqueue(("primary", failure));
        foreach (var (name, work) in tasks)
        {
            output.WriteLine($"Native task before cleanup: {name}={work.Status}");
            if (work.Exception is { } fault) pending.Enqueue((name, fault));
        }
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0 && seen.Count < 16)
        {
            var (source, error) = pending.Dequeue();
            if (!seen.Add(error)) continue;
            if (!ReferenceEquals(error, failure)) output.WriteLine($"Nested native failure ({source}): {Bounded(error.ToString())}");
            foreach (var key in ContextKeys)
                if (error.Data.Contains(key)) output.WriteLine($"Native failure context ({source}; {error.GetType().Name}): {key}={Bounded(Convert.ToString(error.Data[key], System.Globalization.CultureInfo.InvariantCulture) ?? "")}");
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(16)) pending.Enqueue((source + "/inner", inner));
            else if (error.InnerException is { } inner) pending.Enqueue((source + "/inner", inner));
        }
    }
}
