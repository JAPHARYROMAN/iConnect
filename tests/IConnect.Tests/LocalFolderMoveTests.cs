using System.Text.Json.Nodes;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalBatchCopyTests
{
    [Fact]
    public async Task Canceled_folder_move_planning_creates_no_draft_or_copy()
    {
        var selected = Assert.Single(await Capture("Reports"));
        await using var browser = new LocalExplorerController(profile);
        var target = browser.Management!.InspectMoveDestination(selected, destination);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.PrepareFolderMoveAsync(selected, target, "Moved", cancel.Token));
        Assert.Empty(browser.BatchCopies!.Jobs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.True(Directory.Exists(selected.SourceLocation));
    }

    [Fact]
    public async Task Folder_move_survives_restart_and_preserves_the_original_tree_in_recovery()
    {
        using var management = new WindowsLocalManagement(profile);
        var selected = Assert.Single(await Capture("Reports"));
        var target = management.InspectMoveDestination(selected, destination);
        const string name = "Moved-文書";
        string id;
        await using (var draft = Open()) id = await draft.PrepareFolderMoveAsync(selected, target, name);
        LocalFolderMoveReview oldReview;
        await using (var copy = Open())
        {
            Assert.Equal(Path.Combine(destination, name), Assert.Single(copy.Jobs).MoveDestination);
            Assert.Throws<IOException>(() => copy.ChooseDestination(id, destination, DownloadCollision.Fail));
            copy.StartFolderMoveCopy(id);
            Assert.Equal(BatchPhase.Completed, (await Finish(copy, id)).Phase);
            Assert.True(Directory.Exists(selected.SourceLocation));
            oldReview = copy.ReviewFolderMove(id);
        }
        await using var reopened = Open();
        await Assert.ThrowsAsync<IOException>(() => reopened.FinishFolderMoveAsync(oldReview, management));
        var review = reopened.ReviewFolderMove(id);
        Assert.Equal(2, review.Files);
        Assert.Equal(3, review.Folders);
        var result = await reopened.FinishFolderMoveAsync(review, management);
        Assert.Equal(MutationPhase.ChangeSucceeded, result.Phase);
        Assert.False(Directory.Exists(selected.SourceLocation));
        await Assert.ThrowsAsync<IOException>(() => reopened.FinishFolderMoveAsync(review, management));
        var saved = Assert.Single(management.Deleted);
        Assert.Equal(selected.SourceLocation, saved.OriginalLocation);
        var relative = Path.Combine("Nested", "文書.txt");
        Assert.Equal(File.ReadAllBytes(Path.Combine(saved.SavedLocation, relative)), File.ReadAllBytes(Path.Combine(review.Destination, relative)));
        Assert.True(Directory.Exists(Path.Combine(review.Destination, "Empty")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(review.Destination, "zero.bin")));
        Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(saved.Item.Id).Phase);
        Assert.True(Directory.Exists(selected.SourceLocation));
    }

    [Theory]
    [InlineData("source-bytes")]
    [InlineData("source-membership")]
    [InlineData("destination-identity")]
    [InlineData("collision")]
    public async Task Folder_move_keeps_the_original_when_copy_or_recheck_fails(string change)
    {
        using var management = new WindowsLocalManagement(profile);
        await using var copies = Open();
        var selected = Assert.Single(await Capture("Reports"));
        var id = await copies.PrepareFolderMoveAsync(selected, management.InspectMoveDestination(selected, destination), "Moved");
        var output = Path.Combine(destination, "Moved");
        if (change == "collision")
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "kept.txt"), "Existing destination");
        }
        copies.StartFolderMoveCopy(id);
        var copied = await Finish(copies, id);
        if (change == "collision")
        {
            Assert.Equal(BatchPhase.CompletedWithIssues, copied.Phase);
            Assert.Throws<IOException>(() => copies.ReviewFolderMove(id));
            Assert.Equal("Existing destination", File.ReadAllText(Path.Combine(output, "kept.txt")));
        }
        else
        {
            Assert.Equal(BatchPhase.Completed, copied.Phase);
            var review = copies.ReviewFolderMove(id);
            var relative = Path.Combine("Nested", "文書.txt");
            if (change == "source-bytes") File.AppendAllText(Path.Combine(selected.SourceLocation, relative), "changed");
            if (change == "source-membership") File.WriteAllText(Path.Combine(selected.SourceLocation, "Empty", "new.txt"), "added");
            if (change == "destination-identity")
            {
                var path = Path.Combine(output, relative);
                var original = Path.Combine(sandbox, "previous-destination");
                File.Move(path, original);
                File.Copy(original, path);
            }
            await Assert.ThrowsAnyAsync<IOException>(() => copies.FinishFolderMoveAsync(review, management));
        }
        Assert.True(Directory.Exists(selected.SourceLocation));
        Assert.Empty(management.Deleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Unfinished_folder_postcheck_keeps_original_recoverable(bool restart)
    {
        var original = Path.Combine(source, "Reports");
        string id;
        using (var manager = new WindowsLocalManagement(profile))
        {
            using var parent = SecureWindowsDirectory.OpenRoot(source);
            SecureWindowsDirectory.Snapshot selected;
            using (var folder = parent.OpenChild("Reports", true)) selected = folder.Info;
            var checks = 0;
            var result = manager.RecoverVerifiedFolder(parent.Info.CanonicalPath, parent.Info.Identity, "Reports", original,
                selected.Identity, selected.Version, _ =>
                {
                    if (++checks == 2 && !restart) throw new IOException("Generated postcheck failure.");
                }, () => { }, default);
            id = result.Id;
            Assert.Equal(2, checks);
            Assert.Equal(restart ? MutationPhase.ChangeSucceeded : MutationPhase.ChangeReview, result.Phase);
            Assert.False(Directory.Exists(original));
        }
        if (restart)
        {
            var path = Path.Combine(profile, "local-management", "deleted-items", "catalog.json");
            var data = JsonNode.Parse(File.ReadAllText(path))!;
            data["Items"]![0]!["State"] = (int)DeletedState.DeletePending;
            data["Receipts"]![0]!["Phase"] = (int)MutationPhase.ChangePending;
            File.WriteAllText(path, data.ToJsonString());
        }
        using var reopened = new WindowsLocalManagement(profile);
        var saved = Assert.Single(reopened.Deleted);
        Assert.Equal(id, saved.Item.Id);
        Assert.True(saved.Item.CanRestore);
        Assert.Contains("verification", saved.Item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(saved.SavedLocation, "Nested", "文書.txt")));
        Assert.Equal(MutationPhase.ChangeSucceeded, reopened.Restore(id).Phase);
        Assert.True(Directory.Exists(original));
    }
}
