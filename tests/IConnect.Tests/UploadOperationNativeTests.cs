using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed class UploadOperationNativeTests
{
    [Fact]
    public async Task Authenticated_native_sender_resumes_the_original_receipt_only_after_receiving_owner_window_review()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        var sandbox = Path.Combine(repo!.FullName, ".local", "upload-operation-native-tests", ProtocolRules.NewToken());
        var destination = Path.Combine(sandbox, "received"); Directory.CreateDirectory(destination);
        var source = Path.Combine(sandbox, "generated.bin"); var bytes = RandomNumberGenerator.GetBytes(4 * StorageRules.MaxReadRange + 17); File.WriteAllBytes(source, bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var owner = DeviceIdentity.Ephemeral("Generated receiving owner"); using var remote = DeviceIdentity.Ephemeral("Generated sending owner");
        var clock = new UploadTests.OperationClock(); var grants = new StorageGrantStore(Path.Combine(sandbox, "receiver"), clock);
        grants.Add(remote.Id, destination, StoragePermission.Write | StoragePermission.Create);
        var session = ProtocolRules.NewToken();
        using var host = new PeerSession(owner, remote.PublicKey, session, true, Path.Combine(sandbox, "host-inbox"), _ => { }, loopbackOnly: true, fileService: new WindowsFileService(grants));
        using var guest = new PeerSession(remote, owner.PublicKey, session, false, Path.Combine(sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostLoop = host.RunAsync(timeout.Token); var guestLoop = guest.RunAsync(timeout.Token);
        async Task Bridge(PeerSession from, PeerSession to)
        {
            await foreach (var signal in from.Rtc.Signals.ReadAllAsync(timeout.Token))
                if (signal.Kind == "candidate") to.Rtc.AddCandidate(signal.Body, signal.Mid);
                else to.Rtc.SetDescription(signal.Body, signal.Kind);
        }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer(); await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(timeout.Token);
            var authority = new UploadAuthority(remote.Id, ProtocolRules.NewToken(), owner.Id, ProtocolRules.NewToken());
            await using var sender = new WindowsUploadSender(Path.Combine(sandbox, "sender"), value => value == authority);
            var target = Assert.Single((await guest.RequestFilesAsync(new() { Operation = FileOperation.UploadContext }, timeout.Token)).Entries);
            var id = await sender.PrepareAsync(source, authority, timeout.Token); var calls = 0; var writes = 0; var prepares = 0;
            async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
            {
                calls++; if (request.Operation == FileOperation.PrepareUpload) prepares++;
                if (request.Operation == FileOperation.UploadWrite && ++writes == 2) clock.Advance(TimeSpan.FromHours(25));
                return await guest.RequestFilesAsync(request, ct);
            }
            await sender.SendAsync(id, target, Request, timeout.Token);
            var stopped = Assert.Single(sender.All); var incoming = Assert.Single(grants.Uploads.All);
            Assert.Equal(SendingPhase.Review, stopped.Phase); Assert.Contains("receiving owner", stopped.Message);
            Assert.Equal(StorageRules.MaxReadRange, incoming.Offset); Assert.Equal(stopped.RemoteId, incoming.Id);
            Assert.Single(Directory.GetFiles(destination, "*.part")); Assert.False(File.Exists(Path.Combine(destination, "generated.bin")));
            var before = calls; var review = await grants.Uploads.ReviewOperationAsync(incoming.Id, () => true, timeout.Token);
            await grants.Uploads.ConfirmOperationAsync(review, timeout.Token); Assert.Equal(before, calls);
            Assert.Equal(incoming.Offset, Assert.Single(grants.Uploads.All).Offset); Assert.Equal(SendingPhase.Review, Assert.Single(sender.All).Phase);
            await sender.SendAsync(id, null, Request, timeout.Token);
            var completed = Assert.Single(sender.All); Assert.Equal(SendingPhase.Completed, completed.Phase); Assert.Equal(stopped.RemoteId, completed.RemoteId);
            Assert.Equal(1, prepares); Assert.Single(grants.Uploads.All); Assert.Single(Directory.GetFiles(destination));
            Assert.True(guest.AcceptsPublishedUploadChecks);
            await sender.VerifyPublishedAsync(id, guest.RequestFilesAsync, timeout.Token);
            await grants.Uploads.WhenIdleAsync();
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, completed.FinalName))); Assert.Equal(bytes, File.ReadAllBytes(source));
        }
        finally
        {
            timeout.Cancel(); host.Dispose(); guest.Dispose(); grants.Uploads.PauseVerification(); await grants.Uploads.WhenIdleAsync();
            foreach (var task in bridges.Append(hostLoop).Append(guestLoop))
                try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            Directory.Delete(sandbox, true);
        }
    }
}
