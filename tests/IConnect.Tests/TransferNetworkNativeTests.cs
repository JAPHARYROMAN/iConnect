using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed class TransferNetworkNativeTests
{
    [Fact]
    public async Task Authenticated_native_file_flow_paces_reads_and_refuses_new_writes_while_preserving_status_and_cleanup()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        var sandbox = Path.Combine(repo!.FullName, ".local", "network-native-tests", ProtocolRules.NewToken());
        var documents = Path.Combine(sandbox, "documents"); var destination = Path.Combine(sandbox, "received"); var profile = Path.Combine(sandbox, "owner");
        Directory.CreateDirectory(documents); Directory.CreateDirectory(destination); var bytes = RandomNumberGenerator.GetBytes(128 * 1024 + 17); File.WriteAllBytes(Path.Combine(documents, "generated.bin"), bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65)); using var ownerId = DeviceIdentity.Ephemeral("Generated owner"); using var guestId = DeviceIdentity.Ephemeral("Generated guest");
        var grants = new StorageGrantStore(profile); var approval = grants.Add(guestId.Id, documents, StoragePermission.Contribute);
        await using var sources = new DownloadSourceStore(grants, profile); var policy = new FileTrafficPolicy(); policy.Configure(new(65536));
        var id = ProtocolRules.NewToken();
        using var host = new PeerSession(ownerId, guestId.PublicKey, id, true, Path.Combine(sandbox, "host-inbox"), _ => { }, loopbackOnly: true, fileService: new WindowsFileService(grants, sources), traffic: policy);
        using var guest = new PeerSession(guestId, ownerId.PublicKey, id, false, Path.Combine(sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostRun = host.RunAsync(timeout.Token); var guestRun = guest.RunAsync(timeout.Token);
        async Task Bridge(PeerSession from, PeerSession to)
        { await foreach (var signal in from.Rtc.Signals.ReadAllAsync(timeout.Token)) if (signal.Kind == "candidate") to.Rtc.AddCandidate(signal.Body, signal.Mid); else to.Rtc.SetDescription(signal.Body, signal.Kind); }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer(); await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(timeout.Token);
            var root = Assert.Single((await guest.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, timeout.Token)).Entries);
            var file = Assert.Single((await guest.RequestFilesAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, timeout.Token)).Entries);
            var watch = Stopwatch.StartNew();
            await using (var receiving = new WindowsDownloadManager(Path.Combine(sandbox, "receiver")))
            {
                var job = receiving.Start(ownerId.Id, file, destination, DownloadCollision.KeepBoth, guest.RequestFilesAsync);
                await receiving.WaitAsync(job).WaitAsync(timeout.Token); var result = Assert.Single(receiving.Jobs); Assert.Equal(DownloadPhase.Completed, result.Phase);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, result.FinalName))); Assert.InRange(watch.Elapsed.TotalSeconds, 1.95, 30);
            }
            var context = await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadContext }, timeout.Token);
            var upload = new UploadInfo { Id = MutationRules.NewId(), Name = "incoming.bin", Size = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)), Collision = UploadCollision.UploadFail };
            policy.Configure(new(65536, true)); policy.ObserveNetwork(TransferNetworkCost.Metered);
            var denied = await guest.RequestFilesAsync(new() { Operation = FileOperation.ReadRange, NodeId = file.Id, ExpectedVersion = file.Version, Length = 16 }, timeout.Token);
            Assert.Equal(FileStatus.AccessDenied, denied.Status); Assert.Empty(denied.Content);
            Assert.Equal(FileStatus.Ok, (await guest.RequestFilesAsync(new() { Operation = FileOperation.GetMetadata, NodeId = file.Id }, timeout.Token)).Status);
            var refused = await guest.RequestFilesAsync(new() { Operation = FileOperation.PrepareUpload, DestinationId = root.Id, TransferId = upload.Id, MutationEpoch = context.MutationEpoch, Upload = upload }, timeout.Token);
            Assert.Equal(FileStatus.AccessDenied, refused.Status); Assert.Empty(grants.Uploads.All);
            await Assert.ThrowsAsync<StorageException>(() => host.SendTestFileAsync(timeout.Token));
            policy.ObserveNetwork(TransferNetworkCost.Unrestricted);
            Assert.Equal(FileStatus.Ok, (await guest.RequestFilesAsync(new() { Operation = FileOperation.PrepareUpload, DestinationId = root.Id, TransferId = upload.Id, MutationEpoch = context.MutationEpoch, Upload = upload }, timeout.Token)).Status);
            var first = await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadWrite, TransferId = upload.Id, MutationEpoch = context.MutationEpoch, Offset = 0, Content = ByteString.CopyFrom(bytes, 0, 32768) }, timeout.Token);
            Assert.Equal(32768, first.Upload.Offset); policy.ObserveNetwork(TransferNetworkCost.Metered);
            var next = await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadWrite, TransferId = upload.Id, MutationEpoch = context.MutationEpoch, Offset = 32768, Content = ByteString.CopyFrom(bytes, 32768, 32768) }, timeout.Token);
            Assert.Equal(FileStatus.AccessDenied, next.Status); Assert.Equal(32768, Assert.Single(grants.Uploads.All).Offset); Assert.False(File.Exists(Path.Combine(documents, upload.Name)));
            var status = await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadStatus, TransferId = upload.Id, MutationEpoch = context.MutationEpoch }, timeout.Token); Assert.Equal(32768, status.Upload.Offset);
            Assert.Equal(FileStatus.Ok, (await guest.RequestFilesAsync(new() { Operation = FileOperation.CancelUpload, TransferId = upload.Id, MutationEpoch = context.MutationEpoch }, timeout.Token)).Status);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(documents, "generated.bin"))); Assert.Single(grants.All); Assert.Equal(approval.Id, grants.All.Single().Id);
        }
        finally
        {
            timeout.Cancel(); host.Dispose(); guest.Dispose();
            foreach (var task in bridges.Append(hostRun).Append(guestRun)) try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            await sources.DisposeAsync();
            var full = Path.GetFullPath(sandbox); if (!full.Contains("\\.local\\network-native-tests\\", StringComparison.Ordinal)) throw new IOException("Unexpected cleanup target."); Directory.Delete(full, true);
        }
    }
}
