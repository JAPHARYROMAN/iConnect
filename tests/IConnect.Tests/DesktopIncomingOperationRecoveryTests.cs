using Google.Protobuf;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Offline_controller_reviews_cancel_disconnect_and_approve_only_the_existing_incoming_window()
    {
        using var peer = DeviceIdentity.Ephemeral("Generated sender"); var owner = SeedOwnerTrust(peer);
        var destination = Path.Combine(directory, "Generated incoming destination"); Directory.CreateDirectory(destination);
        var clock = new UploadTests.OperationClock(); clock.Wall(TimeSpan.FromHours(-25));
        var grants = new StorageGrantStore(state, clock, new WindowsOwnerBinding(state, owner)); grants.Add(peer.Id, destination, StoragePermission.Contribute);
        var bytes = RandomNumberGenerator.GetBytes(2 * StorageRules.MaxReadRange + 17); var idBytes = RandomNumberGenerator.GetBytes(32);
        BinaryPrimitives.WriteInt64BigEndian(idBytes, clock.GetUtcNow().ToUnixTimeMilliseconds()); var id = ProtocolRules.Base64Url(idBytes);
        using (var service = new WindowsFileService(grants))
        {
            async Task<FileResponse> Call(FileRequest request)
            {
                request.RequestId = ProtocolRules.NewToken(); var response = await service.ExecuteAsync(peer.Id, request, default);
                Assert.Equal(FileStatus.Ok, response.Status); return response;
            }
            var context = await Call(new() { Operation = FileOperation.UploadContext });
            await Call(new() { Operation = FileOperation.PrepareUpload, DestinationId = Assert.Single(context.Entries).Id, TransferId = id, MutationEpoch = context.MutationEpoch,
                Upload = new() { Id = id, Name = "generated.bin", Size = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)), Collision = UploadCollision.UploadKeepBoth } });
            await Call(new() { Operation = FileOperation.UploadWrite, TransferId = id, MutationEpoch = context.MutationEpoch, Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange) });
        }
        var journal = Path.Combine(state, "incoming-uploads", "journal.json"); var before = File.ReadAllBytes(journal); var part = Assert.Single(Directory.GetFiles(destination));
        await using var model = new DesktopController(state, action => action()); Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        var cancelled = await model.ReviewIncomingOperation(id); model.CancelIncomingOperationReview(cancelled.Id);
        await Assert.ThrowsAnyAsync<IOException>(() => model.ApproveIncomingOperation(cancelled)); Assert.Equal(before, File.ReadAllBytes(journal));
        var disconnected = await model.ReviewIncomingOperation(id); await model.DisconnectAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => model.ApproveIncomingOperation(disconnected)); Assert.Equal(before, File.ReadAllBytes(journal));
        var review = await model.ReviewIncomingOperation(id); await model.ApproveIncomingOperation(review);
        var receipt = Assert.Single(model.IncomingUploads); Assert.Equal(id, receipt.Id); Assert.Equal(StorageRules.MaxReadRange, receipt.Offset);
        Assert.Equal(review.ProposedExpiresAt, receipt.OperationExpiresAt); Assert.Equal(UploadPhase.UploadReceiving, receipt.Phase);
        Assert.False(model.IncomingWindowPreparationBusy); Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        Assert.Equal(part, Assert.Single(Directory.GetFiles(destination))); Assert.Equal(bytes[..StorageRules.MaxReadRange], File.ReadAllBytes(part));
    }
}
