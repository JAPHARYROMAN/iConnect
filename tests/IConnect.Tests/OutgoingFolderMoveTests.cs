using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    [Fact]
    public async Task A_completed_empty_folder_can_move_to_recovery_and_restore_without_creating_a_file()
    {
        await using var f = new Fixture();
        File.Delete(Path.Combine(f.Original, "file.bin")); File.Delete(Path.Combine(f.Original, "zero.bin")); Directory.Delete(Path.Combine(f.Original, "empty"));
        var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management);
        Assert.Equal(0, review.Files); Assert.Equal(1, review.Folders); Assert.Equal(0L, review.Bytes);
        var result = await f.Sender.FinishFolderMoveAsync(review, management, f.Request, () => true, true);
        Assert.Equal(MutationPhase.ChangeSucceeded, result.Phase); Assert.False(Directory.Exists(f.Original));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.Destination, review.FinalName)));
        Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(result.Id).Phase); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Original));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Folder_source_recovery_refuses_file_only_or_multiple_root_batches(bool multipleRoots)
    {
        await using var f = new Fixture(); var extra = Path.Combine(f.Root, "other-folder"); Directory.CreateDirectory(extra);
        var paths = multipleRoots ? new[] { f.Original, extra } : new[] { Path.Combine(f.Original, "file.bin") };
        var source = await f.Sources.PrepareAsync(paths, f.Authority, UploadCollision.UploadKeepBoth); var id = f.Sender.Queue(source);
        await f.Sender.SendAsync(id, await f.Target(), f.Request); using var management = new WindowsLocalManagement(f.Profile);
        Assert.Throws<IOException>(() => f.Sender.ReviewFolderMove(id, f.Original, management));
        Assert.Empty(management.Deleted); Assert.True(Directory.Exists(f.Original)); Assert.Equal(BatchSendingPhase.Completed, f.Sender.All.Single().Phase);
    }

    [Theory]
    [InlineData("bytes")] [InlineData("new-child")]
    public async Task Remote_folder_move_preserves_late_changes_in_recovery_and_does_not_claim_verified_success(string change)
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management); var changed = false;
        var catalog = Path.Combine(f.Profile, "local-management", "deleted-items", "catalog.json");
        var receipt = await f.Sender.FinishFolderMoveAsync(review, management, f.Request, () =>
        {
            // Change the owned original after the recovery intent is durable, immediately before its effect.
            var recovery = f.Sender.All.Single().SourceRecoveryId;
            if (!changed && recovery.Length != 0 && File.Exists(catalog))
            {
                var saved = JsonNode.Parse(File.ReadAllText(catalog))!;
                if (saved["Receipts"]!.AsArray().Any(x => x!["Id"]!.GetValue<string>() == recovery && x["Phase"]!.GetValue<int>() == (int)MutationPhase.ChangePending))
                {
                    if (change == "bytes") File.AppendAllText(Path.Combine(f.Original, "file.bin"), "late owner change");
                    else File.WriteAllText(Path.Combine(f.Original, "owner-late.txt"), "late owner file");
                    changed = true;
                }
            }
            return true;
        }, true);
        Assert.True(changed); Assert.Equal(MutationPhase.ChangeReview, receipt.Phase); Assert.False(Directory.Exists(f.Original));
        Assert.False(f.Sender.All.Single().SourceRecovered); var item = Assert.Single(management.Deleted);
        if (change == "bytes") Assert.True(File.ReadAllBytes(Path.Combine(item.SavedLocation, "file.bin")).Length > f.Bytes.Length);
        else Assert.Equal("late owner file", File.ReadAllText(Path.Combine(item.SavedLocation, "owner-late.txt")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, review.FinalName, "file.bin")));
        Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(receipt.Id).Phase);
        Assert.True(Directory.Exists(f.Original)); Assert.Throws<IOException>(() => f.Sender.ReviewFolderMove(id, f.Original, management));
    }

    [Fact]
    public async Task Canceling_a_folder_move_review_ends_only_that_approval_and_keeps_the_completed_copy()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management); f.Sender.CancelFolderMoveReview(review);
        await Assert.ThrowsAsync<IOException>(() => f.Sender.FinishFolderMoveAsync(review, management, f.Request, () => true, true));
        Assert.True(Directory.Exists(f.Original)); Assert.Empty(management.Deleted);
        var again = f.Sender.ReviewFolderMove(id, f.Original, management);
        Assert.NotEqual(review.Token, again.Token); f.Sender.CancelFolderMoveReview(again);
        Assert.Equal(BatchSendingPhase.Completed, f.Sender.All.Single().Phase);
    }

    [Theory]
    [InlineData("MoveSource")] [InlineData("SourceRecoveryId")] [InlineData("SourceRecovered")]
    public async Task Missing_folder_move_history_fields_block_schema_three_without_removing_copies(string field)
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.DisposeAsync();
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!; Assert.Equal(3, saved["Schema"]!.GetValue<int>());
        saved["Row"]!.AsObject().Remove(field); File.WriteAllText(path, saved.ToJsonString()); await f.ReopenSender();
        Assert.NotEmpty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All); Assert.Single(f.Sources.All);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }

    [Fact]
    public async Task Folder_source_recovery_cannot_be_downgraded_into_legacy_sender_history()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management);
        var result = await f.Sender.FinishFolderMoveAsync(review, management, f.Request, () => true, true);
        Assert.Equal(MutationPhase.ChangeSucceeded, result.Phase); Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(result.Id).Phase);
        await f.Sender.DisposeAsync();
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"); var saved = JsonNode.Parse(File.ReadAllText(path))!;
        saved["Schema"] = 2; File.WriteAllText(path, saved.ToJsonString()); await f.ReopenSender();
        Assert.NotEmpty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All); Assert.Single(f.Sources.All); Assert.True(Directory.Exists(f.Original));
    }

    [Theory]
    [InlineData("source-bytes")] [InlineData("source-file-replacement")] [InlineData("source-folder-replacement")]
    [InlineData("source-membership")] [InlineData("source-directory-replacement")]
    [InlineData("destination-bytes")] [InlineData("destination-membership")]
    [InlineData("lost-proof")] [InlineData("session-ended")] [InlineData("revoked-owner")]
    [InlineData("bad-receiver-challenge")] [InlineData("uncommitted-sender")]
    public async Task Remote_folder_move_preserves_the_original_when_source_or_receiver_evidence_fails(string change)
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management);
        var sourceFile = Path.Combine(f.Original, "file.bin"); var received = Path.Combine(f.Destination, review.FinalName);
        switch (change)
        {
            case "source-bytes": File.AppendAllText(sourceFile, "owner change"); break;
            case "source-file-replacement": File.Move(sourceFile, Path.Combine(f.Root, "previous.bin")); File.WriteAllBytes(sourceFile, f.Bytes); break;
            case "source-folder-replacement":
                Directory.Move(f.Original, Path.Combine(f.Root, "previous-original")); Directory.CreateDirectory(f.Original);
                Directory.CreateDirectory(Path.Combine(f.Original, "empty")); File.WriteAllBytes(sourceFile, f.Bytes); File.WriteAllBytes(Path.Combine(f.Original, "zero.bin"), []); break;
            case "source-membership": File.WriteAllText(Path.Combine(f.Original, "owner.txt"), "preserve this file"); break;
            case "source-directory-replacement": Directory.Move(Path.Combine(f.Original, "empty"), Path.Combine(f.Root, "previous-empty")); Directory.CreateDirectory(Path.Combine(f.Original, "empty")); break;
            case "destination-bytes": File.AppendAllText(Path.Combine(received, "file.bin"), "changed received copy"); break;
            case "destination-membership": File.WriteAllText(Path.Combine(received, "owner.txt"), "preserve received file"); break;
            case "revoked-owner": f.Allowed = false; break;
            case "uncommitted-sender": File.WriteAllText(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json.bak"), "preserve this backup"); break;
        }
        var current = true; var proofCalls = 0;
        var failure = await Record.ExceptionAsync(async () => await f.Sender.FinishFolderMoveAsync(review, management, async (request, token) =>
        {
            var response = await f.Request(request, token);
            if (request.Operation == FileOperation.VerifyPublishedBatch)
            {
                proofCalls++;
                if (change == "lost-proof") throw new IOException("Generated lost folder-proof acknowledgement.");
                if (change == "bad-receiver-challenge") response.PublishedBatchCheck.Challenge = ProtocolRules.NewToken();
                if (change == "session-ended" && response.PublishedBatchCheck.Verified) current = false;
            }
            return response;
        }, () => current, true));
        Assert.True(failure is IOException or InvalidDataException or InvalidOperationException, $"{change}: {failure}");
        Assert.True(Directory.Exists(f.Original)); Assert.True(File.Exists(sourceFile)); Assert.Empty(management.Deleted);
        Assert.Single(f.Sources.All); Assert.False(f.Sender.Active(id));
        if (change.StartsWith("source-", StringComparison.Ordinal) || change is "revoked-owner" or "uncommitted-sender") Assert.Equal(0, proofCalls);
        if (change == "source-membership") Assert.Equal("preserve this file", File.ReadAllText(Path.Combine(f.Original, "owner.txt")));
        if (change == "destination-membership") Assert.Equal("preserve received file", File.ReadAllText(Path.Combine(received, "owner.txt")));
    }

    [Fact]
    public async Task Remote_folder_move_requires_a_fresh_review_after_restart_and_rejects_a_replaced_parent()
    {
        await using var f = new Fixture();
        var parent = Path.Combine(f.Root, "selected-parent"); Directory.CreateDirectory(parent);
        var original = Path.Combine(parent, "original"); Directory.Move(f.Original, original);
        var source = await f.Sources.PrepareAsync([original], f.Authority, UploadCollision.UploadKeepBoth);
        var id = f.Sender.Queue(source); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var old = f.Sender.ReviewFolderMove(id, original, management); await f.ReopenSender();
        await Assert.ThrowsAsync<IOException>(() => f.Sender.FinishFolderMoveAsync(old, management, f.Request, () => true, true));
        var review = f.Sender.ReviewFolderMove(id, original, management);
        Directory.Move(parent, Path.Combine(f.Root, "previous-parent")); Directory.CreateDirectory(original);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Sender.FinishFolderMoveAsync(review, management, f.Request, () => true, true));
        Assert.True(File.Exists(Path.Combine(f.Root, "previous-parent", "original", "file.bin"))); Assert.Empty(management.Deleted);
    }

    [Fact]
    public async Task Pausing_remote_folder_move_stops_its_bound_receiver_check_and_keeps_the_original()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        var finishing = f.Sender.FinishFolderMoveAsync(review, management, async (request, token) =>
        {
            var response = await f.Request(request, token);
            if (request.Operation == FileOperation.CancelPublishedBatchCheck) { stops++; return response; }
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return response;
        }, () => true, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Sender.Pause(id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True(stops > 0); Assert.False(f.Sender.Active(id));
        await f.Receiver.BatchPlans.WhenFilesIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(Directory.Exists(f.Original)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin"))); Assert.Empty(management.Deleted);
        var again = f.Sender.ReviewFolderMove(id, f.Original, management);
        Assert.Equal(MutationPhase.ChangeSucceeded, (await f.Sender.FinishFolderMoveAsync(again, management, f.Request, () => true, true)).Phase);
    }

    [Fact]
    public async Task Verified_remote_folder_move_keeps_private_and_receiver_copies_and_restores_the_complete_original()
    {
        await using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Original, "Reports"));
        File.WriteAllText(Path.Combine(f.Original, "Reports", "Habari-文書.txt"), "Generated nested original.\n");
        var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        using var management = new WindowsLocalManagement(f.Profile);
        var review = f.Sender.ReviewFolderMove(id, f.Original, management);
        Assert.Equal(f.Original, review.Source); Assert.Equal(3, review.Files); Assert.Equal(3, review.Folders);
        var proofCalls = 0;
        var receipt = await f.Sender.FinishFolderMoveAsync(review, management, async (request, token) =>
        {
            Assert.Equal(FileOperation.VerifyPublishedBatch, request.Operation); proofCalls++;
            Assert.True(Directory.Exists(f.Original));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Path.Combine(f.Original, "file.bin"), [0]));
            return await f.Request(request, token);
        }, () => true, cancelReceiver: true);
        Assert.True(receipt.Phase == MutationPhase.ChangeSucceeded, $"{receipt.Phase}: {receipt.Message}; originalExists={Directory.Exists(f.Original)}; recovery={string.Join("; ",management.Deleted.Select(x => x.Item.State+":"+x.SavedLocation))}"); Assert.True(proofCalls >= 2);
        Assert.False(Directory.Exists(f.Original));
        var row = Assert.Single(f.Sender.All); Assert.True(row.SourceRecovered); Assert.Equal(receipt.Id, row.SourceRecoveryId);
        Assert.Equal(BatchSendingPhase.Completed, row.Phase); Assert.Single(f.Sources.All);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, review.FinalName, "file.bin")));
        Assert.Equal("Generated nested original.\n", File.ReadAllText(Path.Combine(f.Destination, review.FinalName, "Reports", "Habari-文書.txt")));
        Assert.Equal(receipt.Id, Assert.Single(management.Deleted).Item.Id);
        Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(receipt.Id).Phase);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        Assert.True(Directory.Exists(Path.Combine(f.Original, "empty"))); Assert.Empty(File.ReadAllBytes(Path.Combine(f.Original, "zero.bin")));
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues);
        Assert.Throws<IOException>(() => f.Sender.ReviewFolderMove(id, f.Original, management));
        await Assert.ThrowsAsync<IOException>(() => f.Sender.FinishFolderMoveAsync(review, management, f.Request, () => true, true));
    }
}
