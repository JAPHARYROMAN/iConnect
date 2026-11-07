using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalCopyTests
{
    [Fact]
    public async Task Verified_move_draft_and_copy_reopen_before_explicit_original_recovery()
    {
        var original = Path.Combine(source, Name);
        var payload = File.ReadAllBytes(original);
        File.WriteAllText(original + ":fixture", "retained stream");
        var selection = await Capture();
        using var management = new WindowsLocalManagement(profile);
        var target = management.InspectMoveDestination(selection, destination);
        Assert.False(target.AcrossVolumes);
        const string name = "moved-文書.txt";
        string id;
        await using (var first = new WindowsLocalCopies(profile, Accepted))
            id = first.PrepareVerifiedMove(selection, target, name);
        LocalMoveCompletion staleReview;
        await using (var reopened = new WindowsLocalCopies(profile, Accepted))
        {
            Assert.Equal(Path.Combine(destination, name), Assert.Single(reopened.Jobs).MoveDestination);
            Assert.Throws<IOException>(() => reopened.ChooseDestination(id, source, DownloadCollision.KeepBoth));
            reopened.StartVerifiedMoveCopy(id);
            Assert.Equal(DownloadPhase.Completed, (await Finish(reopened, id)).Phase);
            Assert.Equal(payload, File.ReadAllBytes(original));
            staleReview = reopened.ReviewMoveCompletion(id);
        }
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        Assert.Throws<IOException>(() => copies.FinishVerifiedMove(staleReview, management));
        var review = copies.ReviewMoveCompletion(id);
        Assert.Equal(MutationPhase.ChangeSucceeded, copies.FinishVerifiedMove(review, management).Phase);
        Assert.False(File.Exists(original));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(destination, name)));
        var saved = Assert.Single(management.Deleted);
        Assert.Equal(payload, File.ReadAllBytes(saved.SavedLocation));
        Assert.Equal("retained stream", File.ReadAllText(saved.SavedLocation + ":fixture"));
        Assert.Throws<IOException>(() => copies.FinishVerifiedMove(review, management));
        Assert.Equal(MutationPhase.ChangeSucceeded, management.Restore(saved.Item.Id).Phase);
        Assert.Equal(payload, File.ReadAllBytes(original));
        Assert.Empty(new StorageGrantStore(profile).All);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_refuses_changed_bytes_even_with_preserved_size_and_write_time(bool changeOriginal)
    {
        using var management = new WindowsLocalManagement(profile);
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        var id = await PrepareMoveCopy(copies, management);
        var review = copies.ReviewMoveCompletion(id);
        var original = Path.Combine(source, Name);
        var changed = changeOriginal ? original : Path.Combine(destination, Name);
        var written = File.GetLastWriteTimeUtc(changed);
        var bytes = File.ReadAllBytes(changed);
        bytes[0] ^= 1;
        File.WriteAllBytes(changed, bytes);
        File.SetLastWriteTimeUtc(changed, written);

        Assert.Throws<InvalidDataException>(() => copies.FinishVerifiedMove(review, management));
        Assert.Empty(management.Deleted);
        Assert.True(File.Exists(original));
        Assert.Equal(bytes, File.ReadAllBytes(changed));
    }

    [Fact]
    public async Task Verified_move_refuses_replaced_destination_and_no_replace_collision()
    {
        using var management = new WindowsLocalManagement(profile);
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        var selection = await Capture();
        var stale = management.InspectMoveDestination(selection, destination);
        Directory.Move(destination, Path.Combine(sandbox, "previous-destination"));
        Directory.CreateDirectory(destination);
        Assert.Throws<IOException>(() => copies.PrepareVerifiedMove(selection, stale, Name));
        var id = copies.PrepareVerifiedMove(selection, management.InspectMoveDestination(selection, destination), Name);
        File.WriteAllText(Path.Combine(destination, Name), "existing destination");
        copies.StartVerifiedMoveCopy(id);
        Assert.NotEqual(DownloadPhase.Completed, (await Finish(copies, id)).Phase);
        Assert.Throws<IOException>(() => copies.ReviewMoveCompletion(id));
        Assert.Equal("existing destination", File.ReadAllText(Path.Combine(destination, Name)));
        Assert.True(File.Exists(Path.Combine(source, Name)));
        Assert.Empty(management.Deleted);
    }

    [Fact]
    public async Task Verified_move_rejects_ambiguous_receipts_and_consumes_cancelled_review()
    {
        using var management = new WindowsLocalManagement(profile);
        await using var copies = new WindowsLocalCopies(profile, Accepted);
        var id = await PrepareMoveCopy(copies, management);
        var review = copies.ReviewMoveCompletion(id);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => copies.FinishVerifiedMove(review, management, canceled.Token));
        Assert.Throws<IOException>(() => copies.FinishVerifiedMove(review, management));

        review = copies.ReviewMoveCompletion(id);
        File.WriteAllText(Path.Combine(profile, "local-copies", "receiving", "downloads", id + ".json.new"), "pending write");
        Assert.Throws<IOException>(() => copies.FinishVerifiedMove(review, management));
        Assert.True(File.Exists(Path.Combine(source, Name)));
        Assert.True(File.Exists(Path.Combine(destination, Name)));
        Assert.Empty(management.Deleted);
    }

    private async Task<string> PrepareMoveCopy(WindowsLocalCopies copies, WindowsLocalManagement management)
    {
        var selection = await Capture();
        var id = copies.PrepareVerifiedMove(selection, management.InspectMoveDestination(selection, destination), Name);
        copies.StartVerifiedMoveCopy(id);
        Assert.Equal(DownloadPhase.Completed, (await Finish(copies, id)).Phase);
        return id;
    }
}
