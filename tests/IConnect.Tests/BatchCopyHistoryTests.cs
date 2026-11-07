using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private static string SourceRecord(Fixture f, string id) => Path.Combine(f.Profile, "outgoing-batch-copies", id + ".json");
    private static void Purge(Fixture f, string id)
    {
        Archive(f, id); var done = f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Purge));
        while (!done) done = f.Sender.ContinuePurge(id);
    }
    [Fact]
    public async Task Snapshot_remembers_possible_receiver_effects_before_the_first_begin_request()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        Assert.False(source.MayHaveReceiverCopies); var checkedBegin = false;
        async Task<FileResponse> Inspect(FileRequest value, CancellationToken ct)
        {
            if (value.Operation == FileOperation.BeginUploadBatch)
            {
                checkedBegin = true; Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies);
                var saved = JsonNode.Parse(File.ReadAllText(SourceRecord(f, source.Id)))!;
                Assert.Equal(2, saved["Schema"]!.GetValue<int>()); Assert.True(saved["MayHaveReceiverCopies"]!.GetValue<bool>());
            }
            return await f.Request(value, ct);
        }
        await f.Sender.SendAsync(id, await f.Target(), Inspect); Assert.True(checkedBegin); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        var after = Assert.Single(f.Sources.All); Assert.Equal(source.Hash, after.Hash); Assert.Equal(source.Bytes, after.Bytes);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
    [Fact]
    public async Task Purging_all_job_history_cannot_remove_the_additional_copy_requirement()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        await f.Sender.SendAsync(id, await f.Target(), f.Request); var originalHeader = Assert.Single(f.Sender.All).Header!;
        Purge(f, id); Assert.Empty(f.Sender.All); await f.ReopenPrivateSources();
        Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies); var calls = f.Calls;
        Assert.Throws<IOException>(() => f.Sender.Queue(source)); Assert.Equal(calls, f.Calls);
        var another = f.Sender.Queue(source, anotherCopy: true); Assert.Equal(0, Assert.Single(f.Sender.All).Header?.Length ?? 0);
        await f.Sender.SendAsync(another, await f.Target(), f.Request);
        Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase); Assert.NotEqual(originalHeader, Assert.Single(f.Sender.All).Header);
        Assert.Equal(2, f.Begins); Assert.Equal(2, Directory.GetDirectories(f.Destination).Length);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Losing_the_entire_sender_store_keeps_copy_review_in_the_independent_snapshot()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        await f.Sender.SendAsync(id, await f.Target(), f.Request);
        await f.ReopenPrivateSources(() =>
        {
            var jobs = Path.GetFullPath(Path.Combine(f.Profile, "outgoing-batch-jobs"));
            Assert.StartsWith(Path.GetFullPath(f.Root) + Path.DirectorySeparatorChar, jobs, StringComparison.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(jobs)) File.Delete(path);
            Directory.Delete(jobs, false);
        });
        Assert.Empty(f.Sender.All); Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies);
        Assert.Throws<IOException>(() => f.Sender.Queue(source)); Assert.Equal(1, f.Begins);
        Assert.NotEmpty(f.Sender.Queue(source, anotherCopy: true));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task A_valid_retained_header_upgrades_a_legacy_snapshot_before_history_can_be_forgotten()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        await f.Sender.SendAsync(id, await f.Target(), f.Request); var calls = f.Calls;
        await f.ReopenPrivateSources(() =>
        {
            var path = SourceRecord(f, source); var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            saved.Remove("Schema"); saved.Remove("MayHaveReceiverCopies"); File.WriteAllText(path, saved.ToJsonString());
        });
        Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Sources.RecoveryIssues); Assert.Equal(calls, f.Calls);
        var upgraded = Assert.Single(f.Sources.All); Assert.Equal(2, upgraded.Schema); Assert.True(upgraded.MayHaveReceiverCopies);
        Purge(f, id); await f.ReopenPrivateSources(); Assert.Throws<IOException>(() => f.Sender.Queue(source));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_missing_or_wrongly_typed_schema_two_copy_flag_blocks_without_remote_effects(bool missing)
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        var manifest = f.Sources.ReadManifest(source.Id); var index = manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == "file.bin"));
        var target = await f.Target(); var calls = f.Calls;
        await f.ReopenPrivateSources(() =>
        {
            var path = SourceRecord(f, source.Id); var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (missing) saved.Remove("MayHaveReceiverCopies"); else saved["MayHaveReceiverCopies"] = "false";
            File.WriteAllText(path, saved.ToJsonString());
        });
        Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.Empty(f.Sources.All);
        await f.Sender.SendAsync(id, target, f.Request);
        Assert.Equal(calls, f.Calls); Assert.Equal(0, f.Begins); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Profile, "outgoing-batch-copies", source.Id, index + ".bin")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_legacy_ready_snapshot_without_any_sender_history_requires_conservative_copy_review(bool earlierOwner)
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        Purge(f, id); await f.ReopenPrivateSources(() =>
        {
            var path = SourceRecord(f, source); var old = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            old.Remove("Schema"); old.Remove("MayHaveReceiverCopies");
            if (earlierOwner) old["Authority"]!["Owner"] = new string('c', 64);
            File.WriteAllText(path, old.ToJsonString());
        });
        Assert.Empty(f.Sender.All); Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies); Assert.Equal(0, f.Calls);
        if (earlierOwner)
        {
            Assert.Throws<InvalidOperationException>(() => f.Sender.Queue(source, anotherCopy: true));
            f.Sources.RemoveReviewed(f.Sources.ReviewRemoval(source)); Assert.Empty(f.Sources.All);
        }
        else { Assert.Throws<IOException>(() => f.Sender.Queue(source)); Assert.NotEmpty(f.Sender.Queue(source, anotherCopy: true)); }
    }
    [Fact]
    public async Task Stopping_and_purging_an_unstarted_job_does_not_claim_receiver_copies_exist()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        await f.Sender.StopAsync(id, f.Request); Purge(f, id); await f.ReopenPrivateSources();
        Assert.False(Assert.Single(f.Sources.All).MayHaveReceiverCopies); Assert.Equal(0, f.Calls);
        Assert.NotEmpty(f.Sender.Queue(source));
    }
    [Fact]
    public async Task Failure_to_commit_snapshot_copy_history_prevents_the_first_receiver_admission()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        var obstruction = SourceRecord(f, source) + ".new"; File.WriteAllText(obstruction, "generated owner data");
        await f.Sender.SendAsync(id, await f.Target(), f.Request);
        Assert.Equal(0, f.Begins); Assert.Empty(f.Receiver.BatchPlans.All); Assert.NotEmpty(f.Sources.RecoveryIssues);
        Assert.Equal("generated owner data", File.ReadAllText(obstruction));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
}
