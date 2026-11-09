using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;

namespace IConnect.Tests;

public sealed class NativeFailureDiagnosticsTests
{
    private sealed class Output : ITestOutputHelper
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string message) => Lines.Add(message);
        public void WriteLine(string format, params object[] args) => WriteLine(string.Format(format, args));
    }

    [Fact]
    public void Nested_channel_context_and_a_distinct_peer_task_cause_survive_without_unrelated_exception_data()
    {
        var native = new IOException("Generated native failure.");
        native.Data["NativeConnectionState"] = 3;
        native.Data["PrivateFixtureMarker"] = "Do not render this fixture value.";
        var output = new Output();
        NativeFailureDiagnostics.Write(output, "generated fixture", new ChannelClosedException(native),
            ("host signals", Task.FromException(new InvalidDataException("Generated signalling root cause."))));
        Assert.Contains(output.Lines, line => line.Contains("NativeConnectionState=3", StringComparison.Ordinal));
        Assert.True(output.Lines.Any(line => line.Contains("Generated signalling root cause.", StringComparison.Ordinal)),
            "Peer task cause must be retained before cleanup.");
        Assert.DoesNotContain(output.Lines, line => line.Contains("Do not render this fixture value.", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_context_is_deduplicated_and_exception_text_and_distinct_contexts_are_bounded()
    {
        var shared = new IOException("Generated shared failure.");
        shared.Data["NativeConnectionState"] = 3;
        var duplicate = new Output();
        NativeFailureDiagnostics.Write(duplicate, "generated duplicates", new AggregateException(Enumerable.Repeat(shared, 40)),
            ("same failure", Task.FromException(shared)));
        Assert.Single(duplicate.Lines, line => line.Contains("NativeConnectionState=3", StringComparison.Ordinal));

        var bounded = new Output();
        var children = Enumerable.Range(0, 40).Select(index =>
        {
            var failure = new IOException(index == 0 ? new string('x', 20000) : "Generated context.");
            failure.Data["NativePeerId"] = index;
            return failure;
        });
        NativeFailureDiagnostics.Write(bounded, "generated bounds", new AggregateException(children));
        Assert.InRange(bounded.Lines.Count(line => line.Contains("NativePeerId=", StringComparison.Ordinal)), 1, 16);
        Assert.True(bounded.Lines.All(line => line.Length <= 9000),
            "Each diagnostic entry must stay within the exception-text budget.");
    }
}
