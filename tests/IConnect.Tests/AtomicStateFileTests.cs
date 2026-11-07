using IConnect.Core;
using Xunit;

namespace IConnect.Tests;

public sealed class AtomicStateFileTests : IDisposable
{
    private readonly string directory;
    public AtomicStateFileTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        directory = Path.Combine(root!.FullName, ".local", "state-tests", ProtocolRules.NewToken());
        Directory.CreateDirectory(directory);
    }
    [Fact]
    public async Task A_temporary_reader_lock_recovers_without_rewriting_the_staged_checkpoint()
    {
        var path = Path.Combine(directory, "progress.json"); var staged = path + ".new";
        File.WriteAllText(path, "earlier"); File.WriteAllText(staged, "verified checkpoint");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var denied = Assert.ThrowsAny<Exception>(() => File.Move(staged, path, true));
        Assert.Contains(denied.HResult & 0xffff, new[] { 5, 32, 33 });
        using var started = new ManualResetEventSlim();
        var commit = Task.Factory.StartNew(() => { started.Set(); AtomicStateFile.Commit(staged, path); },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            // Release on this already-running test thread. A thread-pool continuation
            // could otherwise retain the lock past the bounded retry window under load.
            Thread.Sleep(60); Assert.Equal("earlier", File.ReadAllText(path));
        }
        finally { reader.Dispose(); await commit.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal("verified checkpoint", File.ReadAllText(path)); Assert.False(File.Exists(staged));
    }
    [Fact]
    public void A_persistent_reader_lock_keeps_both_the_previous_state_and_the_staged_checkpoint()
    {
        var path = Path.Combine(directory, "progress.json"); var staged = path + ".new";
        File.WriteAllText(path, "earlier"); File.WriteAllText(staged, "pending");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var failure = Assert.Throws<IOException>(() => AtomicStateFile.Commit(staged, path));
        Assert.NotNull(failure.InnerException); Assert.Contains("earlier saved state", failure.Message);
        Assert.Equal("earlier", File.ReadAllText(path)); Assert.Equal("pending", File.ReadAllText(staged));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
