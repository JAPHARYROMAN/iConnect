using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadSenderTests
{
    private static readonly byte[] MoveBytes = "Generated original for a reviewed move."u8.ToArray();

    private static async Task<OutgoingCopy> PrepareCompletedMove(Fixture fixture)
    {
        await fixture.Initialize();
        File.WriteAllBytes(fixture.SourcePath, MoveBytes);
        var id = await fixture.Sender.PrepareMoveAsync(fixture.SourcePath, fixture.Authority);
        await fixture.Sender.SendAsync(id, fixture.Target, fixture.Request);
        var row = Assert.Single(fixture.Sender.All);
        Assert.Equal(SendingPhase.Completed, row.Phase);
        Assert.Equal(MoveBytes, File.ReadAllBytes(fixture.SourcePath));
        return row;
    }

    private static async Task<FileResponse> ReadBack(Fixture fixture, OutgoingCopy copy, FileRequest request, CancellationToken token)
    {
        Assert.Equal(FileOperation.VerifyPublishedUpload, request.Operation);
        Assert.Equal(copy.RemoteId, request.TransferId);
        Assert.Equal(copy.RemoteEpoch, request.MutationEpoch);
        var response = await fixture.Request(new()
        { Operation = FileOperation.UploadStatus, TransferId = request.TransferId, MutationEpoch = request.MutationEpoch }, token);
        var digest = SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Destination, copy.FinalName)));
        if (!CryptographicOperations.FixedTimeEquals(digest, response.Upload.Sha256.Span))
            return new() { RequestId = response.RequestId, Status = FileStatus.Conflict, Message = "The published bytes changed." };
        response.PublishedUploadCheck = new() { Challenge = request.MutationId, Verified = true };
        return response;
    }

    [Fact]
    public async Task Restart_requires_new_move_review_and_keeps_snapshot_and_restorable_original()
    {
        await using var fixture = new Fixture();
        var copy = await PrepareCompletedMove(fixture);
        using var manager = new WindowsLocalManagement(fixture.SenderPath);
        var previous = fixture.Sender.ReviewMove(copy.Id, manager);
        await fixture.Sender.DisposeAsync();
        fixture.Sender = new(fixture.SenderPath, authority => fixture.Current && authority == fixture.Authority);
        await Assert.ThrowsAsync<IOException>(() => fixture.Sender.FinishMoveAsync(previous, manager,
            (request, token) => ReadBack(fixture, copy, request, token), () => true));
        var review = fixture.Sender.ReviewMove(copy.Id, manager);
        Assert.Equal(fixture.SourcePath, review.Source);
        Assert.Equal(copy.FinalName, review.FinalName);
        var result = await fixture.Sender.FinishMoveAsync(review, manager, async (request, token) =>
        {
            Assert.True(File.Exists(fixture.SourcePath));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(fixture.SourcePath, [0]));
            return await ReadBack(fixture, copy, request, token);
        }, () => true);
        Assert.Equal(MutationPhase.ChangeSucceeded, result.Phase);
        Assert.False(File.Exists(fixture.SourcePath));
        var row = Assert.Single(fixture.Sender.All);
        Assert.True(row.SourceRecovered);
        Assert.Equal(result.Id, row.SourceRecoveryId);
        Assert.Equal(MoveBytes, File.ReadAllBytes(Path.Combine(fixture.Destination, copy.FinalName)));
        Assert.Equal(MoveBytes, File.ReadAllBytes(Path.Combine(fixture.SenderPath, "outgoing-uploads", copy.Id + ".bin")));
        var saved = Assert.Single(manager.Deleted);
        Assert.Equal(result.Id, saved.Item.Id);
        Assert.Equal(MutationPhase.ChangeSucceeded, manager.Restore(saved.Item.Id).Phase);
        Assert.Equal(MoveBytes, File.ReadAllBytes(fixture.SourcePath));
        Assert.Throws<IOException>(() => fixture.Sender.ReviewMove(copy.Id, manager));

        // A crash can leave the outgoing completion marker unwritten after the recovery receipt commits.
        await fixture.Sender.DisposeAsync();
        var journal = Path.Combine(fixture.SenderPath, "outgoing-uploads", "journal.json");
        var json = JsonNode.Parse(File.ReadAllText(journal))!;
        json[0]!["SourceRecovered"] = false;
        File.WriteAllText(journal, json.ToJsonString());
        fixture.Sender = new(fixture.SenderPath, authority => fixture.Current && authority == fixture.Authority);
        Assert.Throws<IOException>(() => fixture.Sender.ReviewMove(copy.Id, manager));
        Assert.Equal(MoveBytes, File.ReadAllBytes(fixture.SourcePath));
    }

    [Theory]
    [InlineData("changed-source")]
    [InlineData("replaced-source")]
    [InlineData("changed-destination")]
    [InlineData("lost-proof")]
    [InlineData("ended-session")]
    public async Task Move_keeps_original_when_source_or_current_receiver_evidence_fails(string failure)
    {
        await using var fixture = new Fixture();
        var copy = await PrepareCompletedMove(fixture);
        using var manager = new WindowsLocalManagement(fixture.SenderPath);
        var review = fixture.Sender.ReviewMove(copy.Id, manager);
        if (failure == "changed-source") File.AppendAllText(fixture.SourcePath, "changed");
        if (failure == "replaced-source")
        {
            File.Move(fixture.SourcePath, fixture.SourcePath + ".previous");
            File.WriteAllBytes(fixture.SourcePath, MoveBytes);
        }
        if (failure == "changed-destination") File.AppendAllText(Path.Combine(fixture.Destination, copy.FinalName), "changed");
        var calls = 0;
        var sessionCurrent = true;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Sender.FinishMoveAsync(review, manager, async (request, token) =>
        {
            calls++;
            if (failure == "lost-proof") throw new IOException("Generated lost proof reply.");
            var response = await ReadBack(fixture, copy, request, token);
            if (failure == "ended-session") sessionCurrent = false;
            return response;
        }, () => sessionCurrent));
        Assert.Equal(failure.EndsWith("-source", StringComparison.Ordinal) ? 0 : 1, calls);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Empty(manager.Deleted);
        var row = Assert.Single(fixture.Sender.All);
        Assert.False(row.SourceRecovered);
        Assert.Empty(row.SourceRecoveryId);
        await Assert.ThrowsAsync<IOException>(() => fixture.Sender.FinishMoveAsync(review, manager, fixture.Request, () => true));
    }
}
