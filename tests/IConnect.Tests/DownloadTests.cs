using Google.Protobuf;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DownloadTests : IAsyncLifetime
{
    private string sandbox = "";
    private string documents = "";
    private string target = "";
    private readonly string peer = new('a', 64);
    private StorageGrantStore grants = null!;
    private DownloadSourceStore sources = null!;
    private WindowsFileService service = null!;
    private string grantId = "";
    private byte[] expected = [];
    public Task InitializeAsync()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "download-tests", ProtocolRules.NewToken());
        documents = Path.Combine(sandbox, "documents"); target = Path.Combine(sandbox, "received");
        Directory.CreateDirectory(documents); Directory.CreateDirectory(target);
        expected = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        File.WriteAllBytes(Path.Combine(documents, "ripoti-文書.bin"), expected);
        grants = new StorageGrantStore(Path.Combine(sandbox, "owner")); grantId = grants.Add(peer, documents).Id;
        sources = new DownloadSourceStore(grants, Path.Combine(sandbox, "owner")); service = new WindowsFileService(grants, sources);
        return Task.CompletedTask;
    }
    private async Task<FileResponse> Rpc(FileRequest request, CancellationToken ct)
    {
        var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken();
        return await service.ExecuteAsync(peer, copy, ct);
    }
    private async Task<FileEntry> FileEntry()
    {
        var root = (await Rpc(new FileRequest { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        return (await Rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
    }
    [Fact]
    public async Task Download_byte_progress_ticks_without_rebuilding_and_pause_requests_a_page_refresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var model = new DesktopController(Path.Combine(sandbox, "desktop"), action => action());
        var field = typeof(DesktopController).GetField("downloads", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var manager = Assert.IsType<WindowsDownloadManager>(field.GetValue(model));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRanges = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRanges = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedBytes = new TaskCompletionSource<DownloadJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick = new TaskCompletionSource<DesktopChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const long firstWindow = 4L * StorageRules.MaxReadRange;
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead)
            {
                if (request.Offset < firstWindow)
                {
                    entered.TrySetResult();
                    await releaseFirstRanges.Task.WaitAsync(ct);
                }
                else
                {
                    nextRanges.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }
            return await Rpc(request, ct);
        }
        var id = manager.Start(peer, await FileEntry(), target, DownloadCollision.KeepBoth, Request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            model.Changed += change =>
            {
                if ((change & (DesktopChange.Payload | DesktopChange.Transfers | DesktopChange.LocalFiles)) != 0)
                    tick.TrySetResult(change);
                if ((change & DesktopChange.Transfers) != 0) rebuild.TrySetResult();
            };
            manager.Changed += row =>
            {
                if (row.Phase == DownloadPhase.Downloading && row.Offset > 0) observedBytes.TrySetResult(row);
            };
            Assert.Equal(DesktopChange.Payload, await tick.Task.WaitAsync(TimeSpan.FromSeconds(8)));
            await Task.Delay(120);
            releaseFirstRanges.TrySetResult();
            await nextRanges.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(firstWindow, (await observedBytes.Task.WaitAsync(TimeSpan.FromSeconds(8))).Offset);
            Assert.Equal(firstWindow, model.DownloadSnapshot(id)?.Offset);
            Assert.False(rebuild.Task.IsCompleted);
            manager.Pause(id);
            await rebuild.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(DownloadPhase.Paused, model.DownloadSnapshot(id)?.Phase);
        }
        finally
        {
            manager.Pause(id);
            await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }
    [Fact]
    public async Task Releasing_one_download_does_not_revoke_another_lease_for_the_same_file()
    {
        var file = await FileEntry();
        var prepare = new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = file.Id, ExpectedVersion = file.Version };
        var first = (await Rpc(prepare, default)).Download.Id;
        var second = (await Rpc(prepare, default)).Download.Id;
        Assert.NotEqual(first, second);
        var statusRequest = new FileRequest { Operation = FileOperation.DownloadStatus, TransferId = second };
        for (var i = 0; i < 100; i++)
        {
            if ((await Rpc(statusRequest, default)).Download.State == DownloadState.DownloadReady) break;
            await Task.Delay(10);
        }
        await Rpc(new FileRequest { Operation = FileOperation.ReleaseDownload, TransferId = first }, default);
        Assert.Equal(DownloadState.DownloadReady, (await Rpc(statusRequest, default)).Download.State);
        var read = await Rpc(new FileRequest { Operation = FileOperation.DownloadRead, TransferId = second, Length = 32 }, default);
        Assert.Equal(FileStatus.Ok, read.Status); Assert.Equal(expected[..32], read.Content.ToByteArray());
    }
    [Fact]
    public async Task Real_file_is_verified_and_committed_without_replacing_an_existing_destination()
    {
        var file = await FileEntry();
        File.WriteAllText(Path.Combine(target, file.Name), "existing owner data");
        await using var manager = new WindowsDownloadManager(Path.Combine(sandbox, "receiver"));
        var id = manager.Start(peer, file, target, DownloadCollision.KeepBoth, Rpc);
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadPhase.Completed, job.Phase);
        Assert.Equal("existing owner data", File.ReadAllText(Path.Combine(target, file.Name)));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, job.FinalName)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), job.Hash);
        Assert.Empty(Directory.GetFiles(target, ".iconnect-*.part"));
    }
    [Fact]
    public async Task Paused_download_survives_receiver_and_source_service_restart()
    {
        var file = await FileEntry(); var state = Path.Combine(sandbox, "receiver"); string id;
        await using (var manager = new WindowsDownloadManager(state))
        {
            async Task<FileResponse> Interrupt(FileRequest request, CancellationToken ct)
            {
                if (request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024)
                    manager.Pause(manager.Jobs.Single().Id);
                return await Rpc(request, ct);
            }
            id = manager.Start(peer, file, target, DownloadCollision.Fail, Interrupt);
            await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
            var paused = Assert.Single(manager.Jobs); Assert.Equal(DownloadPhase.Paused, paused.Phase);
            Assert.InRange(paused.Offset, 1024 * 1024, expected.LongLength - 1);
            Assert.False(File.Exists(Path.Combine(target, file.Name)));
        }
        service.Dispose(); await sources.DisposeAsync();
        sources = new DownloadSourceStore(grants, Path.Combine(sandbox, "owner")); service = new WindowsFileService(grants, sources);
        await using var resumed = new WindowsDownloadManager(state);
        await resumed.Resume(id, Rpc).WaitAsync(TimeSpan.FromSeconds(15));
        var complete = Assert.Single(resumed.Jobs); Assert.True(complete.Phase == DownloadPhase.Completed, $"{complete.Phase}: {complete.Error}");
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, complete.FinalName)));
    }
    [Fact]
    public async Task Corrupt_network_bytes_never_receive_a_final_destination_name()
    {
        var file = await FileEntry();
        async Task<FileResponse> Corrupt(FileRequest request, CancellationToken ct)
        {
            var response = await Rpc(request, ct);
            if (request.Operation == FileOperation.DownloadRead && request.Offset == 0 && response.Content.Length != 0)
            { var bytes = response.Content.ToByteArray(); bytes[0] ^= 1; response.Content = ByteString.CopyFrom(bytes); }
            return response;
        }
        await using var manager = new WindowsDownloadManager(Path.Combine(sandbox, "receiver"));
        var id = manager.Start(peer, file, target, DownloadCollision.Fail, Corrupt);
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadPhase.Failed, Assert.Single(manager.Jobs).Phase);
        Assert.False(File.Exists(Path.Combine(target, file.Name)));
    }
    [Fact]
    public async Task Altered_partial_content_is_rejected_before_resume_and_cancel_only_removes_its_temporary_file()
    {
        var file = await FileEntry();
        await using var manager = new WindowsDownloadManager(Path.Combine(sandbox, "receiver"));
        async Task<FileResponse> Interrupt(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024) manager.Pause(manager.Jobs.Single().Id);
            return await Rpc(request, ct);
        }
        var id = manager.Start(peer, file, target, DownloadCollision.Fail, Interrupt);
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
        var temporary = Assert.Single(Directory.GetFiles(target, ".iconnect-*.part"));
        using (var changed = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite))
        { var original = changed.ReadByte(); changed.Position = 0; changed.WriteByte((byte)(original ^ 1)); }
        await manager.Resume(id, Rpc).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadPhase.Failed, Assert.Single(manager.Jobs).Phase);
        await manager.Cancel(id);
        Assert.Equal(DownloadPhase.Canceled, Assert.Single(manager.Jobs).Phase); Assert.Empty(Directory.GetFiles(target));
    }
    [Fact]
    public async Task Saved_source_ticket_cannot_bypass_peer_identity_revocation_or_source_changes()
    {
        var file = await FileEntry();
        var preparation = await Rpc(new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = file.Id, ExpectedVersion = file.Version }, default);
        var ticket = preparation.Download.Id;
        var request = new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.DownloadStatus, TransferId = ticket };
        Assert.Equal(FileStatus.AccessDenied, (await service.ExecuteAsync(new string('b', 64), request, default)).Status);
        for (var i = 0; i < 100; i++)
        {
            var status = await Rpc(request, default); if (status.Download?.State == DownloadState.DownloadReady) break;
            await Task.Delay(10);
        }
        File.WriteAllText(Path.Combine(documents, file.Name), "changed");
        Assert.Equal(FileStatus.Conflict, (await Rpc(request, default)).Status);
        grants.Remove(grantId);
        Assert.Equal(FileStatus.AccessDenied, (await Rpc(request, default)).Status);
    }
    [Fact]
    public async Task Interrupted_finalization_is_reconciled_by_identity_and_hash_without_source_access()
    {
        var state = Path.Combine(sandbox, "receiver"); var file = await FileEntry(); DownloadJob complete;
        await using (var manager = new WindowsDownloadManager(state))
        {
            var id = manager.Start(peer, file, target, DownloadCollision.Fail, Rpc);
            await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15)); complete = Assert.Single(manager.Jobs);
            Assert.Equal(DownloadPhase.Completed, complete.Phase);
        }
        // Recreate the durable intent left by a crash after rename but before
        // the completion receipt; the committed file itself is unchanged.
        File.WriteAllText(Path.Combine(state, "downloads", complete.Id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(complete with { Phase = DownloadPhase.Finalizing }));
        await using var recovered = new WindowsDownloadManager(state);
        await recovered.Resume(complete.Id, null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadPhase.Completed, Assert.Single(recovered.Jobs).Phase);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, complete.FinalName)));
    }
    [Fact]
    public async Task Cancel_with_a_moved_destination_never_deletes_outside_the_pinned_path()
    {
        var file = await FileEntry(); await using var manager = new WindowsDownloadManager(Path.Combine(sandbox, "receiver"));
        async Task<FileResponse> Interrupt(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024) manager.Pause(manager.Jobs.Single().Id);
            return await Rpc(request, ct);
        }
        var id = manager.Start(peer, file, target, DownloadCollision.Fail, Interrupt);
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
        var moved = target + "-moved"; Directory.Move(target, moved);
        await manager.Cancel(id);
        Assert.Equal(DownloadPhase.Canceled, Assert.Single(manager.Jobs).Phase);
        Assert.Single(Directory.GetFiles(moved, ".iconnect-*.part"));
    }
    public async Task DisposeAsync()
    {
        service.Dispose(); await sources.DisposeAsync();
        Directory.Delete(sandbox, true);
    }
}
