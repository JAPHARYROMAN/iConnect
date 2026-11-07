using System.Security.Cryptography;
using System.Diagnostics;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class AttachmentChecksTests
{
    private sealed class Checks(Func<string, string, CancellationToken, Task> action) : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken cancellation) => action(path, identity, cancellation); }
    private static readonly IAttachmentChecks Accepted = new Checks((_, _, _) => Task.CompletedTask);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = Path.Combine(repo!.FullName, ".local", "attachment-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(Root);
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }
    private sealed class Source : IAsyncDisposable
    {
        private readonly Fixture fixture = new();
        public string Root => fixture.Root;
        public string Destination { get; }
        public string Profile => Path.Combine(Root, "receiving-profile");
        public const string Peer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public byte[] Bytes { get; } = RandomNumberGenerator.GetBytes(65_551);
        private readonly DownloadSourceStore sources;
        private readonly WindowsFileService files;
        public Source()
        {
            Destination = Path.Combine(Root, "received"); Directory.CreateDirectory(Destination);
            var documents = Path.Combine(Root, "source"); Directory.CreateDirectory(documents); File.WriteAllBytes(Path.Combine(documents, "fixture.bin"), Bytes);
            var grants = new StorageGrantStore(Path.Combine(Root, "source-profile")); grants.Add(Peer, documents);
            sources = new(grants, Path.Combine(Root, "source-profile")); files = new(grants, sources);
        }
        public Task<FileResponse> Request(FileRequest value, CancellationToken cancellation)
        { var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); return files.ExecuteAsync(Peer, request, cancellation); }
        public async Task<FileEntry> Entry()
        {
            var root = (await Request(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
            return (await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
        }
        public async ValueTask DisposeAsync() { files.Dispose(); await sources.DisposeAsync(); fixture.Dispose(); }
    }
    [Fact]
    public async Task Native_attachment_services_keep_generated_bytes_and_restricted_origin_on_actual_final_names()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var root = SecureWindowsDirectory.OpenRoot(f.Root);
        foreach (var name in new[] { "fixture.txt", "fixture.pdf", "fixture.bin" })
        {
            var bytes = "rem Generated iConnect attachment fixture. No command is executed.\r\n"u8.ToArray(); string identity;
            using (var file = root.CreateFile(name)) { file.Write(0, bytes); file.Flush(); file.MarkUntrusted(); identity = file.Info.Identity; }
            var path = Path.Combine(f.Root, name); var before = File.ReadAllText(path + ":Zone.Identifier");
            using (var file = root.OpenChild(name)) file.MarkUntrusted();
            Assert.Equal(before, File.ReadAllText(path + ":Zone.Identifier"));
            await ReceivedFileSecurity.VerifyPublishedAsync(root, name, identity, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), new WindowsAttachmentChecks(), () => { }, default);
            Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Contains("ZoneId=4", File.ReadAllText(path + ":Zone.Identifier"));
        }
    }
    [Fact]
    public async Task Native_high_risk_types_follow_Windows_policy_and_keep_origin_restrictions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var root = SecureWindowsDirectory.OpenRoot(f.Root);
        foreach (var name in new[] { "fixture.cmd", "fixture.exe" })
        {
            var bytes = "rem Generated, unexecuted iConnect fixture.\r\n"u8.ToArray(); string identity;
            using (var file = root.CreateFile(name)) { file.Write(0, bytes); file.Flush(); file.MarkUntrusted(); identity = file.Info.Identity; }
            try { await ReceivedFileSecurity.VerifyPublishedAsync(root, name, identity, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), new WindowsAttachmentChecks(), () => { }, default); }
            catch (AttachmentCheckException e) { Assert.True(e.Result < 0); }
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Root, name)));
            Assert.Contains("ZoneId=4", File.ReadAllText(Path.Combine(f.Root, name) + ":Zone.Identifier"));
        }
    }
    [Fact]
    public void Origin_marking_preserves_other_evidence_is_idempotent_and_keeps_malformed_metadata()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var path = Path.Combine(f.Root, "evidence.txt"); File.WriteAllText(path, "keep source bytes");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=1\r\nHostUrl=https://example.invalid/\r\n[Other]\r\nEvidence=keep\r\n");
        using var root = SecureWindowsDirectory.OpenRoot(f.Root);
        using (var file = root.OpenChild("evidence.txt")) file.MarkUntrusted();
        var marked = File.ReadAllText(path + ":Zone.Identifier"); Assert.Contains("ZoneId=4", marked); Assert.Contains("HostUrl=https://example.invalid/", marked); Assert.Contains("Evidence=keep", marked);
        using (var file = root.OpenChild("evidence.txt")) file.MarkUntrusted(); Assert.Equal(marked, File.ReadAllText(path + ":Zone.Identifier"));
        foreach (var invalid in new[] { new byte[] { 0xff, 0xfe, 0 }, new byte[17 * 1024] })
        {
            File.WriteAllBytes(path + ":Zone.Identifier", invalid);
            using (var file = root.OpenChild("evidence.txt")) Assert.Throws<IOException>(() => file.MarkUntrusted());
            Assert.Equal(invalid, File.ReadAllBytes(path + ":Zone.Identifier")); Assert.Equal("keep source bytes", File.ReadAllText(path));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_Unicode_origin_metadata_retains_its_encoding_and_evidence(bool bigEndian)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var path = Path.Combine(f.Root, "unicode.txt"); File.WriteAllText(path, "original");
        var encoding = new System.Text.UnicodeEncoding(bigEndian, true, true);
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\nEvidence=文書\r\n", encoding);
        using var root = SecureWindowsDirectory.OpenRoot(f.Root); using (var file = root.OpenChild("unicode.txt")) file.MarkUntrusted();
        var metadata = File.ReadAllBytes(path + ":Zone.Identifier"); Assert.True(metadata.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Contains("ZoneId=4", File.ReadAllText(path + ":Zone.Identifier")); Assert.Contains("Evidence=文書", File.ReadAllText(path + ":Zone.Identifier"));
    }
    [Fact]
    public async Task Revoking_an_upload_during_checks_prevents_completion_and_retry_reconciles_the_same_copy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var target = Path.Combine(f.Root, "incoming"); Directory.CreateDirectory(target);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var checks = new Checks(async (_, _, _) => { if (++calls == 1) { entered.TrySetResult(); await release.Task; } });
        var grants = new StorageGrantStore(Path.Combine(f.Root, "profile")) { AttachmentChecks = checks };
        var grant = grants.Add(Source.Peer, target, StoragePermission.Contribute); using var service = new WindowsFileService(grants);
        async Task<FileResponse> Request(FileRequest request) { request.RequestId = ProtocolRules.NewToken(); return await service.ExecuteAsync(Source.Peer, request, default); }
        var context = await Request(new() { Operation = FileOperation.UploadContext }); var bytes = "generated incoming copy"u8.ToArray(); var descriptor = new UploadInfo
        { Id = MutationRules.NewId(), Name = "fixture.txt", Size = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)), Collision = UploadCollision.UploadKeepBoth };
        Assert.Equal(FileStatus.Ok, (await Request(new() { Operation = FileOperation.PrepareUpload, DestinationId = context.Entries.Single().Id, TransferId = descriptor.Id, MutationEpoch = context.MutationEpoch, Upload = descriptor })).Status);
        var part = Directory.GetFiles(target, ".iconnect-*.part").Single(); using (var stream = new FileStream(part + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var text = new StreamReader(stream)) Assert.Contains("ZoneId=4", text.ReadToEnd());
        await Request(new() { Operation = FileOperation.UploadWrite, TransferId = descriptor.Id, MutationEpoch = context.MutationEpoch, Content = ByteString.CopyFrom(bytes) });
        await Request(new() { Operation = FileOperation.FinishUpload, TransferId = descriptor.Id, MutationEpoch = context.MutationEpoch });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); grants.Update(grant.Id, StoragePermission.ReadOnly, [], null, true); release.SetResult(); await grants.Uploads.WhenIdleAsync();
        var review = Assert.Single(grants.Uploads.All); Assert.Equal(UploadPhase.UploadReview, review.Phase); Assert.Equal(0, review.SecurityPolicy); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, review.FinalName)));
        Assert.Equal(FileStatus.AccessDenied, (await Request(new() { Operation = FileOperation.FinishUpload, TransferId = descriptor.Id, MutationEpoch = context.MutationEpoch })).Status);
        await grants.Uploads.CheckOwnedPublishedAsync(descriptor.Id);
        var complete = Assert.Single(grants.Uploads.All); Assert.Equal(UploadPhase.UploadCompleted, complete.Phase); Assert.Equal(1, complete.SecurityPolicy); Assert.Equal(review.FinalName, complete.FinalName); Assert.Single(Directory.GetFiles(target));
        Assert.Equal(StoragePermission.ReadOnly, grants.Get(grant.Id, Source.Peer).Permissions);
        Assert.Equal(FileStatus.AccessDenied, (await Request(new() { Operation = FileOperation.FinishUpload, TransferId = descriptor.Id, MutationEpoch = context.MutationEpoch })).Status);
    }
    [Fact]
    public async Task Identity_substitution_is_refused_before_marking_and_handler_content_changes_never_pass_integrity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var path = Path.Combine(f.Root, "fixture.txt"); var bytes = "generated bytes"u8.ToArray(); File.WriteAllBytes(path, bytes);
        using var root = SecureWindowsDirectory.OpenRoot(f.Root); var calls = 0; string identity;
        using (var file = root.OpenChild("fixture.txt")) identity = file.Info.Identity;
        var change = new Checks((actual, _, _) => { calls++; Assert.Throws<IOException>(() => File.Move(path, path + ".moved")); Assert.Throws<IOException>(() => Directory.Move(f.Root, f.Root + "-moved")); File.WriteAllBytes(actual, new byte[bytes.Length]); return Task.CompletedTask; });
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await Assert.ThrowsAsync<IOException>(() => ReceivedFileSecurity.VerifyPublishedAsync(root, "fixture.txt", "00000000:0000000000000000", bytes.Length, hash, change, () => { }, default));
        Assert.Equal(0, calls); Assert.False(File.Exists(path + ":Zone.Identifier"));
        var rereading = false; var offsets = new List<long>();
        await Assert.ThrowsAsync<InvalidDataException>(() => ReceivedFileSecurity.VerifyPublishedAsync(root, "fixture.txt", identity, bytes.Length, hash, change,
            () => { }, default, () => { Assert.Equal(1, calls); rereading = true; }, offset => { Assert.True(rereading); offsets.Add(offset); }));
        Assert.Equal(bytes.Length, Assert.Single(offsets));
        Assert.Equal(1, calls); Assert.Contains("ZoneId=4", File.ReadAllText(path + ":Zone.Identifier"));
    }
    [Fact]
    public async Task Blocked_download_cannot_be_completed_by_cancel_and_cold_retry_checks_the_same_published_copy()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Source(); var entry = await f.Entry(); string id; string name;
        var rejected = new Checks((_, _, _) => throw new AttachmentCheckException(unchecked((int)0x800700e1)));
        await using (var manager = new WindowsDownloadManager(f.Profile, rejected))
        {
            id = manager.Start(Source.Peer, entry, f.Destination, DownloadCollision.KeepBoth, f.Request); await manager.WaitAsync(id);
            var row = Assert.Single(manager.Jobs); Assert.Equal(DownloadPhase.Interrupted, row.Phase); Assert.Equal(0, row.SecurityPolicy); name = row.FinalName;
            Assert.NotEmpty(name); Assert.Contains("0x800700E1", row.Error); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, name)));
            await manager.Cancel(id); row = Assert.Single(manager.Jobs); Assert.Equal(DownloadPhase.Interrupted, row.Phase); Assert.Equal(name, row.FinalName);
        }
        await using var restored = new WindowsDownloadManager(f.Profile, Accepted); await restored.Resume(id, null);
        var complete = Assert.Single(restored.Jobs); Assert.Equal(DownloadPhase.Completed, complete.Phase); Assert.Equal(1, complete.SecurityPolicy); Assert.Equal(name, complete.FinalName);
        Assert.Single(Directory.GetFiles(f.Destination)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, name)));
    }
    [Fact]
    public async Task Pausing_during_checks_retains_the_finalization_name_and_private_origin_before_first_content_write()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Source(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var marked = false;
        var delayed = new Checks(async (_, _, cancellation) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); });
        string id; string name;
        await using (var manager = new WindowsDownloadManager(f.Profile, delayed))
        {
            async Task<FileResponse> Request(FileRequest request, CancellationToken cancellation)
            {
                if (!marked && request.Operation == FileOperation.DownloadRead)
                {
                    var partial = Assert.Single(Directory.GetFiles(f.Destination, ".iconnect-*.part"));
                    Assert.Equal(0, new FileInfo(partial).Length);
                    using (var stream = new FileStream(partial + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var text = new StreamReader(stream)) Assert.Contains("ZoneId=4", text.ReadToEnd()); marked = true;
                }
                return await f.Request(request, cancellation);
            }
            id = manager.Start(Source.Peer, await f.Entry(), f.Destination, DownloadCollision.KeepBoth, Request);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var progress = manager.ReadFinalization(id);
            Assert.Equal(IncomingFinalizationStage.AttachmentCheck, progress?.Stage);
            Assert.Null(progress!.Estimate);
            manager.Pause(id); await manager.WaitAsync(id);
            Assert.Null(manager.ReadFinalization(id));
            var row = Assert.Single(manager.Jobs); Assert.Equal(DownloadPhase.Paused, row.Phase); name = row.FinalName; Assert.NotEmpty(name); Assert.True(marked);
        }
        await using var restored = new WindowsDownloadManager(f.Profile, Accepted); await restored.Resume(id, null);
        Assert.Equal(DownloadPhase.Completed, restored.Jobs.Single().Phase); Assert.Equal(name, restored.Jobs.Single().FinalName); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Theory]
    [InlineData("fixture-negative.txt")]
    [InlineData("fixture-overflow.txt")]
    [InlineData("fixture-crash.txt")]
    [InlineData("fixture-hang.txt")]
    public async Task Broker_failures_and_deadlines_stop_the_owned_process(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = 0; var worker = Path.Combine(AppContext.BaseDirectory, "AttachmentWorker", "IConnect.Attachment.Fixture.exe");
        var checks = new WindowsAttachmentChecks(worker, TimeSpan.FromSeconds(2), id => pid = id);
        await Assert.ThrowsAnyAsync<IOException>(() => checks.CheckAsync(Path.Combine(Path.GetTempPath(), name), "00000000:0000000000000000", default));
        Assert.True(pid > 0); Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }
    [Fact]
    public async Task Broker_job_prevents_child_processes_and_missing_installation_never_reports_success()
    {
        if (!OperatingSystem.IsWindows()) return;
        var worker = Path.Combine(AppContext.BaseDirectory, "AttachmentWorker", "IConnect.Attachment.Fixture.exe");
        await new WindowsAttachmentChecks(worker).CheckAsync(Path.Combine(Path.GetTempPath(), "fixture-spawn.txt"), "00000000:0000000000000000", default);
        await Assert.ThrowsAsync<IOException>(() => new WindowsAttachmentChecks(Path.Combine(AppContext.BaseDirectory, "AttachmentWorker", "missing.exe")).CheckAsync("unused", "unused", default));
    }
    [Fact]
    public async Task Broker_does_not_inherit_an_explicitly_inheritable_owner_file_handle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var owner = new FileStream(Path.Combine(f.Root, "owner-sentinel"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var handle = owner.SafeFileHandle.DangerousGetHandle(); Assert.True(SetHandleInformation(handle, 1, 1));
        try
        {
            var identity = SecureWindowsDirectory.Inspect(owner.SafeFileHandle).Identity;
            var worker = Path.Combine(AppContext.BaseDirectory, "AttachmentWorker", "IConnect.Attachment.Fixture.exe");
            await new WindowsAttachmentChecks(worker).CheckAsync(Path.Combine(f.Root, $"fixture-handle-{handle}.txt"), identity, default);
        }
        finally { SetHandleInformation(handle, 1, 0); }
    }
    [Fact]
    public async Task Owner_recheck_uses_only_the_recorded_published_file_and_never_republishes_a_partial()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new Source(); var calls = 0; var checks = new Checks((_, _, _) => { calls++; return Task.CompletedTask; });
        await using var manager = new WindowsDownloadManager(f.Profile, checks);
        var id = manager.Start(Source.Peer, await f.Entry(), f.Destination, DownloadCollision.KeepBoth, f.Request); await manager.WaitAsync(id);
        var complete = Assert.Single(manager.Jobs); await manager.CheckPublishedAsync(id); Assert.Equal(2, calls); Assert.Equal(1, manager.Jobs.Single().SecurityPolicy);
        var final = Path.Combine(f.Destination, complete.FinalName); var partial = Path.Combine(f.Destination, ".iconnect-transfer-" + id + ".part"); File.Move(final, partial);
        await manager.CheckPublishedAsync(id); Assert.Equal(2, calls); Assert.NotEqual(DownloadPhase.Completed, manager.Jobs.Single().Phase);
        Assert.False(File.Exists(final)); Assert.True(File.Exists(partial)); Assert.Equal(f.Bytes, File.ReadAllBytes(partial));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
