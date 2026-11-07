using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalCopyTests : IDisposable
{
    private readonly string sandbox, source, destination, profile;
    private const string Name = "local-文書.txt";
    private sealed class Checks(Func<string, string, CancellationToken, Task> action) : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken ct) => action(path, identity, ct); }
    private static readonly IAttachmentChecks Accepted = new Checks((_, _, _) => Task.CompletedTask);
    public LocalCopyTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "local-copy-tests", ProtocolRules.NewToken());
        source = Path.Combine(sandbox, "source"); destination = Path.Combine(sandbox, "destination"); profile = Path.Combine(sandbox, "profile");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, Name), "Generated owner-selected local file.\nSalama!\n");
        File.WriteAllBytes(Path.Combine(source, "payload.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17));
    }
    private async Task<LocalCopySelection> Capture(string name = Name)
    {
        using var browser = new LocalFileLocations(profile); browser.Add(source);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var file = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == name);
        return await browser.CaptureCopyAsync(file);
    }
    private static async Task<DownloadJob> Finish(WindowsLocalCopies copies, string id)
    { await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20)); return copies.Jobs.Single(x => x.Id == id).Transfer!; }
    private string Record(string id) => Path.Combine(profile, "local-copies", "sources", id + ".json");
    [Fact]
    public async Task Offline_copy_keeps_original_and_collision_and_never_creates_remote_authority()
    {
        var selection = await Capture(); var expected = File.ReadAllBytes(Path.Combine(source, Name)); var events = new List<WindowsActivityEvent>();
        File.WriteAllText(Path.Combine(destination, Name), "existing destination data");
        await using var copies = new WindowsLocalCopies(profile, Accepted) { Activity = value => { lock (events) events.Add(value); } };
        var id = copies.Prepare(selection);
        using (var shortcuts = new LocalFileLocations(profile)) shortcuts.Remove(shortcuts.All.Single().Id);
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); var row = await Finish(copies, id);
        Assert.Equal(DownloadPhase.Completed, row.Phase); Assert.Empty(row.PeerId); Assert.Equal("local-文書 (2).txt", row.FinalName);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(destination, row.FinalName))); Assert.Equal(expected, File.ReadAllBytes(Path.Combine(source, Name)));
        Assert.Equal("existing destination data", File.ReadAllText(Path.Combine(destination, Name)));
        Assert.Contains("ZoneId=4", File.ReadAllText(Path.Combine(destination, row.FinalName) + ":Zone.Identifier"));
        Assert.False(File.Exists(Path.Combine(source, Name) + ":Zone.Identifier"));
        var grants = new StorageGrantStore(profile); Assert.Empty(grants.All); Assert.False(File.Exists(Path.Combine(profile, "trusted-peers.json")));
        using var remote = new WindowsFileService(grants);
        var refused = await remote.ExecuteAsync(new string('f', 64), new() { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.GetMetadata, NodeId = id }, default);
        Assert.Equal(FileStatus.StaleReference, refused.Status);
        var complete = events.Single(x => x.Outcome == WindowsActivityOutcome.Completed);
        Assert.Equal(WindowsActivityKind.LocalCopy, complete.Kind); Assert.Empty(complete.Peer); Assert.Equal(expected.Length, complete.Bytes);
        var history = JsonSerializer.Serialize(events); Assert.DoesNotContain(Name, history); Assert.DoesNotContain(source, history); Assert.DoesNotContain(id, history);
        copies.Remove(id); Assert.Empty(copies.Jobs); Assert.True(File.Exists(Path.Combine(destination, row.FinalName)));
    }
    [Fact]
    public async Task Draft_survives_restart_waits_for_an_owner_destination_and_preserves_source_context()
    {
        var selection = await Capture(); string id;
        await using (var first = new WindowsLocalCopies(profile, Accepted)) { id = first.Prepare(selection); Assert.Null(first.Jobs.Single().Transfer); }
        await using var reopened = new WindowsLocalCopies(profile, Accepted);
        Assert.Equal(id, reopened.Jobs.Single().Id); Assert.Equal(Path.Combine(source, Name), reopened.Jobs.Single().SourceLocation); Assert.Null(reopened.Jobs.Single().Transfer);
        reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        Assert.Equal(DownloadPhase.Completed, (await Finish(reopened, id)).Phase);
    }
    [Fact]
    public async Task Changed_source_and_replaced_source_root_are_refused_without_publication()
    {
        var selection = await Capture();
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        var changed = copies.Prepare(selection); File.AppendAllText(Path.Combine(source, Name), " owner change");
        copies.ChooseDestination(changed, destination, DownloadCollision.KeepBoth); Assert.NotEqual(DownloadPhase.Completed, (await Finish(copies, changed)).Phase);
        Assert.Empty(Directory.GetFiles(destination));
        var selectedNow = await Capture(); var replaced = copies.Prepare(selectedNow);
        var old = Path.Combine(sandbox, "previous-source"); Assert.Equal(sandbox, Path.GetDirectoryName(old)); Directory.Move(source, old);
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, Name), "replacement root");
        copies.ChooseDestination(replaced, destination, DownloadCollision.KeepBoth); Assert.NotEqual(DownloadPhase.Completed, (await Finish(copies, replaced)).Phase);
        Assert.Empty(Directory.GetFiles(destination));
    }
    [Theory]
    [InlineData(DownloadCollision.Skip, DownloadPhase.Skipped)]
    [InlineData(DownloadCollision.Fail, DownloadPhase.Interrupted)]
    public async Task Collision_policy_keeps_existing_destination(DownloadCollision collision, DownloadPhase phase)
    {
        var selection = await Capture(); File.WriteAllText(Path.Combine(destination, Name), "keep original destination");
        await using var copies = new WindowsLocalCopies(profile, Accepted); var id = copies.Prepare(selection);
        copies.ChooseDestination(id, destination, collision); var row = await Finish(copies, id);
        Assert.Equal(phase, row.Phase); Assert.Equal("keep original destination", File.ReadAllText(Path.Combine(destination, Name)));
        Assert.Single(Directory.GetFiles(destination, "*.txt"));
    }
    [Fact]
    public async Task Blocked_attachment_check_can_pause_restart_and_finish_without_the_original()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new Checks(async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var selection = await Capture("payload.bin"); var expected = File.ReadAllBytes(Path.Combine(source, "payload.bin")); string id;
        await using (var copies = new WindowsLocalCopies(profile, checks))
        {
            id = copies.Prepare(selection); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); copies.Pause(id);
            var row = await Finish(copies, id); Assert.Equal(DownloadPhase.Paused, row.Phase); Assert.Equal(0, row.SecurityPolicy); Assert.NotEmpty(row.FinalName);
        }
        File.Delete(Path.Combine(source, "payload.bin"));
        await using var reopened = new WindowsLocalCopies(profile, Accepted);
        await reopened.Resume(id).WaitAsync(TimeSpan.FromSeconds(15)); var result = reopened.Jobs.Single().Transfer!;
        Assert.Equal(DownloadPhase.Completed, result.Phase); Assert.True(result.SecurityPolicy > 0);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(destination, result.FinalName)));
    }
    [Fact]
    public async Task Recheck_is_local_only_and_cannot_rewrite_a_changed_published_copy()
    {
        await using var copies = new WindowsLocalCopies(profile, Accepted); var id = copies.Prepare(await Capture());
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); var row = await Finish(copies, id);
        Assert.Equal(DownloadPhase.Completed, row.Phase); var path = Path.Combine(destination, row.FinalName); File.WriteAllText(path, "owner edited published file");
        await copies.Recheck(id).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(DownloadPhase.Completed, copies.Jobs.Single().Transfer!.Phase); Assert.Equal(0, copies.Jobs.Single().Transfer!.SecurityPolicy);
        Assert.Equal("owner edited published file", File.ReadAllText(path));
    }
    [Fact]
    public async Task Damaged_source_metadata_is_preserved_before_new_admission_and_not_adopted()
    {
        var selection = await Capture(); string id;
        await using (var first = new WindowsLocalCopies(profile, Accepted)) id = first.Prepare(selection);
        File.WriteAllText(Record(id), "{damaged local source");
        await using var reopened = new WindowsLocalCopies(profile, Accepted);
        var issue = Assert.Single(reopened.RecoveryIssues); Assert.True(issue.CanArchive);
        Assert.Throws<IOException>(() => reopened.Prepare(selection)); reopened.Preserve(issue.Id);
        Assert.Empty(reopened.RecoveryIssues); Assert.Equal(1, reopened.PreservedRecords); Assert.Empty(reopened.Jobs);
        Assert.Equal("{damaged local source", File.ReadAllText(Path.Combine(profile, "local-copies", "sources", "recovery", issue.Id, id + ".json")));
        reopened.ClearPreserved(); Assert.Equal(0, reopened.PreservedRecords); Assert.True(File.Exists(Path.Combine(source, Name)));
    }
    [Fact]
    public async Task Active_metadata_tampering_refuses_dispatch_and_changed_archive_review_is_rejected()
    {
        var selection = await Capture(); string id;
        await using (var copies = new WindowsLocalCopies(profile, Accepted))
        {
            id = copies.Prepare(selection); File.AppendAllText(Record(id), " ");
            Assert.Throws<IOException>(() => copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth)); Assert.Empty(Directory.GetFiles(destination));
        }
        File.WriteAllText(Record(id), "{bad");
        await using var reopened = new WindowsLocalCopies(profile, Accepted); var issue = reopened.RecoveryIssues.Single();
        File.AppendAllText(Record(id), "new owner bytes"); Assert.Throws<IOException>(() => reopened.Preserve(issue.Id)); Assert.True(File.Exists(Record(id)));
    }
    [Fact]
    public async Task Native_lease_refuses_a_second_queue_and_source_admission_is_bounded()
    {
        var selection = await Capture();
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        Assert.ThrowsAny<IOException>(() => new WindowsLocalCopies(profile, Accepted));
        var ids = Enumerable.Range(0, 8).Select(_ => copies.Prepare(selection)).ToArray();
        Assert.Throws<IOException>(() => copies.Prepare(selection)); Assert.Equal(8, copies.Jobs.Count);
        foreach (var id in ids) await copies.Cancel(id); Assert.Empty(copies.Jobs);
    }
    [Fact]
    public async Task Throwing_observers_cannot_change_a_committed_copy_or_leave_it_active()
    {
        await using var copies = new WindowsLocalCopies(profile, Accepted); copies.Changed += () => throw new IOException("generated observer refusal");
        copies.Activity = _ => throw new IOException("generated history refusal");
        var id = copies.Prepare(await Capture()); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        Assert.Equal(DownloadPhase.Completed, (await Finish(copies, id)).Phase); Assert.False(copies.Active(id)); copies.Remove(id);
    }
    [Fact]
    public async Task Explorer_capture_binds_the_reviewed_source_even_after_selection_changes()
    {
        await using var browser = new LocalExplorerController(profile); await browser.AddAsync(source); await browser.BrowseAsync(browser.Rows.Single().Entry);
        browser.Select(browser.Rows.Single(x => x.Entry.Name == Name).Entry.Id); var reviewed = await browser.CaptureCopyAsync();
        browser.Select(browser.Rows.Single(x => x.Entry.Name == "payload.bin").Entry.Id);
        var id = await browser.PrepareCopyAsync(reviewed); Assert.Equal(Name, browser.Copies!.Jobs.Single().Name);
        await browser.Copies.Cancel(id); Assert.Empty(browser.Copies.Jobs); Assert.Empty(new StorageGrantStore(profile).All);
    }
    [Fact]
    public async Task Actual_Windows_attachment_worker_verifies_a_local_copy_without_pairing()
    {
        await using var copies = new WindowsLocalCopies(profile); var id = copies.Prepare(await Capture());
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); var row = await Finish(copies, id);
        Assert.Equal(DownloadPhase.Completed, row.Phase); Assert.True(row.SecurityPolicy > 0);
        Assert.Empty(new StorageGrantStore(profile).All);
    }
    [Fact]
    public async Task Closing_an_active_copy_pauses_it_and_restart_never_resumes_automatically()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new WindowsLocalCopies(profile, new Checks(async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }));
        string id;
        try
        {
            id = copies.Prepare(await Capture()); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await copies.DisposeAsync(); }
        await using var reopened = new WindowsLocalCopies(profile, Accepted);
        var paused = reopened.Jobs.Single().Transfer!; Assert.Equal(DownloadPhase.Paused, paused.Phase); Assert.False(reopened.Active(id)); Assert.Equal(0, paused.SecurityPolicy);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, Name)), File.ReadAllBytes(Path.Combine(destination, paused.FinalName)));
        await reopened.Resume(id).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(DownloadPhase.Completed, reopened.Jobs.Single().Transfer!.Phase);
    }
    [Fact]
    public async Task Concurrent_cancel_and_close_finish_cleanup_without_removing_a_published_file()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new WindowsLocalCopies(profile, new Checks(async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }));
        string id;
        try
        {
            id = copies.Prepare(await Capture()); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var cancel = copies.Cancel(id); await copies.DisposeAsync(); await cancel.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await copies.DisposeAsync(); }
        await using var reopened = new WindowsLocalCopies(profile, Accepted); var row = reopened.Jobs.Single().Transfer!;
        Assert.Equal(DownloadPhase.Interrupted, row.Phase); Assert.True(File.Exists(Path.Combine(destination, row.FinalName)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, Name)), File.ReadAllBytes(Path.Combine(destination, row.FinalName)));
        await reopened.Resume(id).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(DownloadPhase.Completed, reopened.Jobs.Single().Transfer!.Phase);
    }
    [Fact]
    public async Task A_captured_source_does_not_allow_a_later_hard_link_to_bypass_native_read_checks()
    {
        var selection = await Capture(); var alias = Path.Combine(source, "generated-alias.txt");
        Assert.True(CreateHardLinkW(alias, Path.Combine(source, Name), 0), "The disposable NTFS hard-link fixture could not be created.");
        await using var copies = new WindowsLocalCopies(profile, Accepted); var id = copies.Prepare(selection);
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.NotEqual(DownloadPhase.Completed, (await Finish(copies, id)).Phase);
        Assert.Empty(Directory.GetFiles(destination)); File.Delete(alias);
        await copies.Resume(id).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(DownloadPhase.Completed, copies.Jobs.Single().Transfer!.Phase);
    }
    [Fact]
    public async Task Normal_remote_receiver_cannot_adopt_a_local_owner_receipt()
    {
        string id;
        await using (var copies = new WindowsLocalCopies(profile, Accepted))
        { id = copies.Prepare(await Capture()); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(DownloadPhase.Completed, (await Finish(copies, id)).Phase); }
        await using var remote = new WindowsDownloadManager(Path.Combine(profile, "local-copies", "receiving"), Accepted);
        Assert.Empty(remote.Jobs); Assert.Single(remote.RecoveryIssues);
        var entry = new FileEntry { Id = ProtocolRules.NewToken(), Name = Name, Size = 0, Version = new string('a', 64) };
        Assert.Throws<InvalidDataException>(() => remote.Start("", entry, destination, DownloadCollision.KeepBoth, (_, _) => throw new IOException("A requester must not run.")));
        Assert.True(File.Exists(Path.Combine(profile, "local-copies", "receiving", "downloads", id + ".json")));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
    public void Dispose()
    {
        var full = Path.GetFullPath(sandbox);
        if (!full.Contains(Path.DirectorySeparatorChar + ".local" + Path.DirectorySeparatorChar + "local-copy-tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Unexpected test cleanup target.");
        Directory.Delete(full, true);
    }
}
