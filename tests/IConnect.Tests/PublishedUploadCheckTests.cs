using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadTests
{
    private static async Task<UploadInfo> PublishForCheck(Fixture fixture)
    {
        await fixture.Initialize();
        var bytes = "Generated published contents."u8.ToArray();
        var pending = await fixture.Begin(Fixture.Descriptor("published.bin", bytes));
        await fixture.Write(pending, bytes);
        var published = await fixture.Finish(pending);
        Assert.Equal(UploadPhase.UploadCompleted, published.Phase);
        return published;
    }

    private static FileRequest PublishedCheckRequest(Fixture fixture, UploadInfo published) => new()
    {
        Operation = FileOperation.VerifyPublishedUpload, TransferId = published.Id, MutationEpoch = fixture.Epoch,
        MutationId = ProtocolRules.NewToken()
    };

    private static async Task<FileResponse> CollectPublishedCheck(Fixture fixture, FileRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var response = await fixture.Raw(request);
            if (response.Status != FileStatus.Ok || response.PublishedUploadCheck.Verified) return response;
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task Published_check_is_single_use_and_leaves_the_receipt_and_file_intact()
    {
        await using var fixture = new Fixture(StoragePermission.Write | StoragePermission.Create);
        var published = await PublishForCheck(fixture);
        var request = PublishedCheckRequest(fixture, published);
        var journal = Path.Combine(fixture.Profile, "incoming-uploads", "journal.json");
        var before = File.ReadAllBytes(journal);
        var pending = await fixture.Call(request);
        Assert.False(pending.PublishedUploadCheck.Verified);
        Assert.Equal(request.MutationId, pending.PublishedUploadCheck.Challenge);
        var checkedCopy = await CollectPublishedCheck(fixture, request);
        Assert.Equal(FileStatus.Ok, checkedCopy.Status);
        Assert.True(checkedCopy.PublishedUploadCheck.Verified);
        Assert.Equal(published, checkedCopy.Upload);
        Assert.Empty(checkedCopy.Content);
        await fixture.Grants.Uploads.WhenIdleAsync();
        Assert.False((await fixture.Call(request)).PublishedUploadCheck.Verified);
        Assert.Equal(before, File.ReadAllBytes(journal));
        Assert.Equal("Generated published contents.", File.ReadAllText(Path.Combine(fixture.Destination, published.FinalName)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Published_check_refuses_changed_bytes_or_identical_replacement(bool replace)
    {
        await using var fixture = new Fixture();
        var published = await PublishForCheck(fixture);
        var target = Path.Combine(fixture.Destination, published.FinalName);
        if (replace)
        {
            var old = Path.Combine(fixture.Root, "previous");
            File.Move(target, old);
            File.Copy(old, target);
        }
        else
        {
            var bytes = File.ReadAllBytes(target);
            bytes[0] ^= 1;
            File.WriteAllBytes(target, bytes);
        }
        var contents = File.ReadAllBytes(target);
        var request = PublishedCheckRequest(fixture, published);
        Assert.False((await fixture.Call(request)).PublishedUploadCheck.Verified);
        var result = await CollectPublishedCheck(fixture, request);
        Assert.Equal(FileStatus.Conflict, result.Status);
        Assert.Null(result.PublishedUploadCheck);
        Assert.Equal(contents, File.ReadAllBytes(target));
        Assert.Equal(UploadPhase.UploadCompleted, Assert.Single(fixture.Grants.Uploads.All).Phase);
    }

    [Fact]
    public async Task Published_check_requires_current_scope_and_grant()
    {
        await using var fixture = new Fixture();
        var published = await PublishForCheck(fixture);
        var request = PublishedCheckRequest(fixture, published);
        Assert.Equal(FileStatus.NotFound, (await fixture.Raw(request, new string('d', 64))).Status);
        Assert.False((await fixture.Call(request)).PublishedUploadCheck.Verified);
        using (var otherScope = new WindowsFileService(fixture.Grants))
        {
            request.RequestId = ProtocolRules.NewToken();
            var refused = await otherScope.ExecuteAsync(fixture.Peer, request, default);
            Assert.Equal(FileStatus.Conflict, refused.Status);
        }
        fixture.Service.Dispose();
        await fixture.Restart();
        Assert.False((await fixture.Call(request)).PublishedUploadCheck.Verified);
        fixture.Grants.Update(fixture.Grant.Id, StoragePermission.ReadOnly, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await fixture.Raw(request)).Status);
    }
}
