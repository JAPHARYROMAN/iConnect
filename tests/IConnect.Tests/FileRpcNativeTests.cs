using System.Text;
using System.Security.Cryptography;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class FileRpcNativeTests
{
    [Fact]
    public async Task Bound_receiver_negotiates_and_publishes_private_folder_batch_over_native_session()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        var sandbox = Path.Combine(repository!.FullName, ".local", "rpc-tests", ProtocolRules.NewToken());
        var destination = Path.Combine(sandbox, "received");
        var original = Path.Combine(sandbox, "original");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(original, "empty"));
        var bytes = RandomNumberGenerator.GetBytes(StorageRules.MaxReadRange + 17);
        File.WriteAllBytes(Path.Combine(original, "data.bin"), bytes);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var owner = DeviceIdentity.Ephemeral("Batch receiving owner");
        using var remote = DeviceIdentity.Ephemeral("Batch sending owner");
        var profile = Path.Combine(sandbox, "receiver");
        var binding = new WindowsOwnerBinding(profile, owner.Id);
        var grants = new StorageGrantStore(profile, ownerBinding: binding);
        var grant = grants.Add(remote.Id, destination, StoragePermission.Write | StoragePermission.Create);
        using var files = new WindowsFileService(grants);
        files.BindOwnerPolicy(binding, remote.Id, ProtocolRules.NewToken());
        var session = ProtocolRules.NewToken();
        using var host = new PeerSession(owner, remote.PublicKey, session, true, Path.Combine(sandbox, "owner-inbox"), _ => { },
            loopbackOnly: true, fileService: files);
        using var guest = new PeerSession(remote, owner.PublicKey, session, false, Path.Combine(sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostLoop = host.RunAsync(ct.Token);
        var guestLoop = guest.RunAsync(ct.Token);
        async Task Bridge(PeerSession from, PeerSession to)
        {
            await foreach (var signal in from.Rtc.Signals.ReadAllAsync(ct.Token))
                if (signal.Kind == "candidate") to.Rtc.AddCandidate(signal.Body, signal.Mid);
                else to.Rtc.SetDescription(signal.Body, signal.Kind);
        }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer();
            await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(ct.Token);
            Assert.True(guest.AcceptsFolderBatches);
            Assert.False(host.AcceptsFolderBatches);
            await Assert.ThrowsAsync<NotSupportedException>(() => host.RequestFilesAsync(new() { Operation = FileOperation.UploadBatchContext }, ct.Token));
            var target = Assert.Single((await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadContext }, ct.Token)).Entries);
            Assert.Empty((await guest.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, ct.Token)).Entries);
            var authority = new UploadAuthority(remote.Id, ProtocolRules.NewToken(), owner.Id, ProtocolRules.NewToken());
            var senderProfile = Path.Combine(sandbox, "sender");
            await using var sources = new WindowsBatchSources(senderProfile, value => value == authority);
            await using var sender = new WindowsBatchSender(senderProfile, sources, value => value == authority);
            var source = await sources.PrepareAsync([original], authority, UploadCollision.UploadKeepBoth, ct.Token);
            var id = sender.Queue(source);
            await sender.SendAsync(id, target, guest.RequestFilesAsync, ct.Token);
            var completed = Assert.Single(sender.All);
            Assert.Equal(BatchSendingPhase.Completed, completed.Phase);
            Assert.All(completed.ReceiverOutcomes!, outcome => Assert.Equal(1, outcome));
            var manifest = sources.ReadManifest(source);
            string PublishedName(string name) => sender.ItemResult(id, manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == name))).Name;
            var folder = PublishedName("original");
            Assert.NotEqual("original", folder);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, folder, PublishedName("data.bin"))));
            Assert.True(Directory.Exists(Path.Combine(destination, folder, PublishedName("empty"))));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(original, "data.bin")));

            var header = UploadBatchHeader.Parser.ParseFrom(completed.Header);
            grants.Remove(grant.Id);
            var denied = await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadBatchStatus,
                TransferId = header.Id, MutationEpoch = header.Epoch }, ct.Token);
            Assert.Equal(FileStatus.AccessDenied, denied.Status);
        }
        finally
        {
            ct.Cancel(); host.Dispose(); guest.Dispose();
            foreach (var task in bridges.Append(hostLoop).Append(guestLoop))
                try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            await grants.BatchPlans.WhenFilesIdleAsync();
            var expectedParent = Path.GetFullPath(Path.Combine(repository!.FullName, ".local", "rpc-tests"));
            if (Path.GetDirectoryName(Path.GetFullPath(sandbox)) != expectedParent || !UploadBatchRules.CanonicalToken(Path.GetFileName(sandbox)))
                throw new IOException("Native fixture cleanup escaped its workspace.");
            Directory.Delete(sandbox, true);
        }
    }

    [Fact]
    public async Task Real_files_are_browsed_and_read_over_authenticated_native_channels_and_revoke_applies()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        var sandbox = Path.Combine(repository!.FullName, ".local", "rpc-tests", ProtocolRules.NewToken());
        var documents = Path.Combine(sandbox, "documents");
        Directory.CreateDirectory(documents);
        var expected = Encoding.UTF8.GetBytes("Habari, iConnect. This content came from a granted file on Windows.");
        File.WriteAllBytes(Path.Combine(documents, "ripoti.txt"), expected);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var owner = DeviceIdentity.Ephemeral("Windows owner");
        using var remote = DeviceIdentity.Ephemeral("Remote device");
        var grants = new StorageGrantStore(Path.Combine(sandbox, "state"));
        await using var downloads = new DownloadSourceStore(grants, Path.Combine(sandbox, "state"));
        var grant = grants.Add(remote.Id, documents);
        var id = ProtocolRules.NewToken();
        using var host = new PeerSession(owner, remote.PublicKey, id, true, Path.Combine(sandbox, "owner-inbox"), _ => { },
            loopbackOnly: true, fileService: new WindowsFileService(grants, downloads));
        using var guest = new PeerSession(remote, owner.PublicKey, id, false, Path.Combine(sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostLoop = host.RunAsync(ct.Token);
        var guestLoop = guest.RunAsync(ct.Token);
        async Task Bridge(PeerSession from, PeerSession to)
        {
            await foreach (var message in from.Rtc.Signals.ReadAllAsync(ct.Token))
                if (message.Kind == "candidate") to.Rtc.AddCandidate(message.Body, message.Mid);
                else to.Rtc.SetDescription(message.Body, message.Kind);
        }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer();
            await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(ct.Token);
            var roots = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, ct.Token);
            Assert.Equal(FileStatus.Ok, roots.Status);
            var root = Assert.Single(roots.Entries);
            var listing = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, ct.Token);
            var file = Assert.Single(listing.Entries);
            Assert.Equal("ripoti.txt", file.Name);
            var read = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.ReadRange, NodeId = file.Id,
                ExpectedVersion = file.Version, Length = 1024 }, ct.Token);
            Assert.Equal(FileStatus.Ok, read.Status);
            Assert.Equal(expected, read.Content.ToByteArray());
            var preparedSource = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = file.Id, ExpectedVersion = file.Version }, ct.Token);
            Assert.Equal(FileStatus.Ok, preparedSource.Status); Assert.Contains(downloads.All, x => x.Id == preparedSource.Download.Id && x.Peer == remote.Id);
            await downloads.StopOwnedAsync(preparedSource.Download.Id);
            var stopped = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.DownloadRead, TransferId = preparedSource.Download.Id, Length = 1024 }, ct.Token);
            Assert.Equal(FileStatus.AccessDenied, stopped.Status); Assert.Empty(stopped.Content); Assert.Empty(downloads.All);
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(documents, "ripoti.txt")));
            var destination = Path.Combine(sandbox, "real-downloads"); Directory.CreateDirectory(destination);
            await using (var receiver = new WindowsDownloadManager(Path.Combine(sandbox, "receiver")))
            {
                var transfer = receiver.Start(owner.Id, file, destination, DownloadCollision.KeepBoth, guest.RequestFilesAsync);
                await receiver.WaitAsync(transfer).WaitAsync(ct.Token);
                var completed = Assert.Single(receiver.Jobs);
                Assert.Equal(DownloadPhase.Completed, completed.Phase);
                Assert.Equal(expected, File.ReadAllBytes(Path.Combine(destination, completed.FinalName)));
            }
            var denied = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.CreateDirectory,
                DestinationId = root.Id, Name = "unauthorized" }, ct.Token);
            Assert.Equal(FileStatus.AccessDenied, denied.Status);
            grants.Update(grant.Id, StoragePermission.Contribute, [], null, true);
            var uploadContext = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.UploadContext }, ct.Token);
            Assert.Equal(FileStatus.Ok, uploadContext.Status);
            var uploadBytes = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
            var incoming = new UploadInfo { Id = MutationRules.NewId(), Name = "native-incoming.bin", Size = uploadBytes.Length,
                Sha256 = ByteString.CopyFrom(SHA256.HashData(uploadBytes)), Collision = UploadCollision.UploadKeepBoth };
            var prepared = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.PrepareUpload, DestinationId = root.Id,
                TransferId = incoming.Id, MutationEpoch = uploadContext.MutationEpoch, Upload = incoming }, ct.Token);
            Assert.Equal(FileStatus.Ok, prepared.Status);
            for (var offset = 0; offset < uploadBytes.Length; offset += StorageRules.MaxReadRange)
            {
                var count = Math.Min(StorageRules.MaxReadRange, uploadBytes.Length - offset);
                var ack = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.UploadWrite, TransferId = incoming.Id,
                    MutationEpoch = uploadContext.MutationEpoch, Offset = offset, Content = ByteString.CopyFrom(uploadBytes, offset, count) }, ct.Token);
                Assert.Equal(FileStatus.Ok, ack.Status); Assert.Equal(offset + count, ack.Upload.Offset);
            }
            var finishing = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.FinishUpload, TransferId = incoming.Id, MutationEpoch = uploadContext.MutationEpoch }, ct.Token);
            Assert.Equal(FileStatus.Ok, finishing.Status); await grants.Uploads.WhenIdleAsync().WaitAsync(ct.Token);
            var receipt = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.UploadStatus, TransferId = incoming.Id, MutationEpoch = uploadContext.MutationEpoch }, ct.Token);
            Assert.Equal(UploadPhase.UploadCompleted, receipt.Upload.Phase);
            Assert.Equal(uploadBytes, File.ReadAllBytes(Path.Combine(documents, receipt.Upload.FinalName)));
            var picked = Path.Combine(sandbox, "picked-for-send.bin"); File.WriteAllBytes(picked, uploadBytes);
            var authority = new UploadAuthority(remote.Id, ProtocolRules.NewToken(), owner.Id, ProtocolRules.NewToken());
            await using (var sender = new WindowsUploadSender(Path.Combine(sandbox, "private-sender"), value => value == authority))
            {
                var localCopy = await sender.PrepareAsync(picked, authority, ct.Token);
                var receivingRoot = Assert.Single((await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.UploadContext }, ct.Token)).Entries);
                var folderContext = await guest.RequestFilesAsync(new() { Operation = FileOperation.MutationContext }, ct.Token);
                var folderId = MutationRules.NewId();
                var created = await guest.RequestFilesAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = receivingRoot.Id,
                    Name = "native-created-folder", MutationId = folderId, MutationEpoch = folderContext.MutationEpoch }, ct.Token);
                Assert.Equal(FileStatus.Ok, created.Status);
                var folder = await CreatedDirectoryReferences.ResolveAsync(new(folderId, folderContext.MutationEpoch, receivingRoot.GrantId, "native-created-folder"), guest.RequestFilesAsync, ct.Token);
                await sender.SendAsync(localCopy, folder, guest.RequestFilesAsync, ct.Token);
                var sent = Assert.Single(sender.All); Assert.Equal(SendingPhase.Completed, sent.Phase);
                Assert.Equal(uploadBytes, File.ReadAllBytes(Path.Combine(documents, folder.Name, sent.FinalName)));
                Assert.Equal(uploadBytes, File.ReadAllBytes(picked));
            }
            grants.Remove(grant.Id);
            var afterRevoke = await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.GetMetadata, NodeId = file.Id }, ct.Token);
            Assert.Equal(FileStatus.AccessDenied, afterRevoke.Status);
            Assert.Empty((await guest.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, ct.Token)).Entries);
            Assert.Equal(FileStatus.AccessDenied, (await host.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, ct.Token)).Status);
            await Assert.ThrowsAsync<NotSupportedException>(() => host.RequestFilesAsync(new() { Operation = FileOperation.ResolveCreatedDirectory }, ct.Token));
            foreach (var operation in new[] { FileOperation.UploadBatchContext, FileOperation.PrepareBatchFile, FileOperation.WriteBatchFile, FileOperation.FinishBatchFile, FileOperation.CancelBatchFile, FileOperation.FinishUploadBatch, FileOperation.StopUploadBatch })
                await Assert.ThrowsAsync<NotSupportedException>(() => guest.RequestFilesAsync(new() { Operation = operation }, ct.Token));
        }
        finally
        {
            ct.Cancel(); host.Dispose(); guest.Dispose();
            foreach (var job in bridges.Append(hostLoop).Append(guestLoop))
                try { await job; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            await downloads.DisposeAsync();
            Directory.Delete(sandbox, true);
        }
    }
}
