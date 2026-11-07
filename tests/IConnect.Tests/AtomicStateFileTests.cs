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
        var commit = Task.Run(() => AtomicStateFile.Commit(staged, path));
        await Task.Delay(60); Assert.Equal("earlier", File.ReadAllText(path)); reader.Dispose();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));
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
