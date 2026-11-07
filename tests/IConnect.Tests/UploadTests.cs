using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private static string TestRoot()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
            return Path.Combine(repository!.FullName, ".local", "upload-tests", ProtocolRules.NewToken());
        }
        public readonly string Root = TestRoot();
        public string Profile => Path.Combine(Root, "profile");
        public string Destination => Path.Combine(Root, "destination");
        public readonly string Peer = new('a', 64);
        public StorageGrantStore Grants { get; private set; }
        public WindowsFileService Service { get; private set; }
        public StorageGrant Grant { get; private set; }
        public WindowsOwnerBinding? Binding { get; }
        private readonly TimeProvider? clock;
        public string Parent = "", Epoch = "";
        public readonly CancellationTokenSource Lifetime = new(TimeSpan.FromSeconds(30));
        public Fixture(StoragePermission permissions = StoragePermission.Contribute, bool ownerBound = false, TimeProvider? clock = null)
        {
            this.clock = clock;
            Directory.CreateDirectory(Destination); Directory.CreateDirectory(Profile);
            Binding = ownerBound ? new WindowsOwnerBinding(Profile, new string('b', 64)) : null;
            Grants = new(Profile, timeProvider: clock, ownerBinding: Binding); Grant = Grants.Add(Peer, Destination, permissions);
            Service = new(Grants);
        }
        public async Task Initialize()
        {
            var roots = (await Call(new() { Operation = FileOperation.ListRoots })).Entries;
            var context = await Call(new() { Operation = FileOperation.UploadContext });
            Parent = Assert.Single(roots.Count == 0 ? context.Entries : roots).Id;
            Epoch = context.MutationEpoch;
        }
        public Task<FileResponse> Raw(FileRequest request, string? peer = null)
        { request.RequestId = ProtocolRules.NewToken(); return Service.ExecuteAsync(peer ?? Peer, request, Lifetime.Token); }
        public async Task<FileResponse> Call(FileRequest request)
        { var result = await Raw(request); Assert.True(result.Status == FileStatus.Ok, $"{result.Status}: {result.Message}"); StorageRules.ValidateResponse(result); return result; }
        public static UploadInfo Descriptor(string name, byte[] bytes, UploadCollision collision = UploadCollision.UploadKeepBoth)
            => new() { Id = MutationRules.NewId(), Name = name, Size = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)), Collision = collision };
        public async Task<UploadInfo> Begin(UploadInfo info) => (await Call(new() { Operation = FileOperation.PrepareUpload, DestinationId = Parent, TransferId = info.Id, MutationEpoch = Epoch, Upload = info })).Upload;
        public async Task<UploadInfo> Write(UploadInfo info, byte[] bytes, long offset = 0)
        {
            UploadInfo current = info;
            while (offset < bytes.Length)
            {
                var length = (int)Math.Min(StorageRules.MaxReadRange, bytes.Length - offset);
                current = (await Call(new() { Operation = FileOperation.UploadWrite, TransferId = info.Id, MutationEpoch = Epoch, Offset = offset, Content = ByteString.CopyFrom(bytes, (int)offset, length) })).Upload;
                offset += length; Assert.Equal(offset, current.Offset);
            }
            return current;
        }
        public async Task<UploadInfo> Finish(UploadInfo info)
        {
            await Call(new() { Operation = FileOperation.FinishUpload, TransferId = info.Id, MutationEpoch = Epoch });
            await Grants.Uploads.WhenIdleAsync();
            return (await Call(new() { Operation = FileOperation.UploadStatus, TransferId = info.Id, MutationEpoch = Epoch })).Upload;
        }
        public async Task Restart()
        {
            await Grants.Uploads.WhenIdleAsync(); Service.Dispose(); Grants = new(Profile, timeProvider: clock, ownerBinding: Binding); Service = new(Grants); await Initialize();
        }
        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel(); await Grants.Uploads.WhenIdleAsync(); Service.Dispose(); Lifetime.Dispose();
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task New_file_is_durable_resumable_idempotent_and_verified_before_no_replace_publication()
    {
        await using var f = new Fixture(); await f.Initialize(); var data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        var descriptor = Fixture.Descriptor("phone-資料.bin", data); var first = await f.Begin(descriptor);
        Assert.Equal(UploadPhase.UploadReceiving, first.Phase); Assert.False(File.Exists(Path.Combine(f.Destination, descriptor.Name)));
        var chunk = ByteString.CopyFrom(data, 0, StorageRules.MaxReadRange);
        var written = await f.Call(new() { Operation = FileOperation.UploadWrite, TransferId = first.Id, MutationEpoch = f.Epoch, Content = chunk });
        Assert.Equal(StorageRules.MaxReadRange, written.Upload.Offset);
        Assert.Equal(StorageRules.MaxReadRange, (await f.Call(new() { Operation = FileOperation.UploadWrite, TransferId = first.Id, MutationEpoch = f.Epoch, Content = chunk })).Upload.Offset);
        var changed = chunk.ToByteArray(); changed[0] ^= 1;
        Assert.Equal(FileStatus.Conflict, (await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = first.Id, MutationEpoch = f.Epoch, Content = ByteString.CopyFrom(changed) })).Status);
        await f.Restart(); var resumed = (await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = first.Id, MutationEpoch = f.Epoch })).Upload;
        await f.Write(resumed, data, resumed.Offset); var completed = await f.Finish(resumed);
        Assert.Equal(UploadPhase.UploadCompleted, completed.Phase); Assert.Equal(data, File.ReadAllBytes(Path.Combine(f.Destination, completed.FinalName)));
        Assert.Equal(completed, await f.Finish(completed));
        await f.Restart(); Assert.Equal(UploadPhase.UploadCompleted, (await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = first.Id, MutationEpoch = f.Epoch })).Upload.Phase);
        Assert.Empty(Directory.GetFiles(f.Destination, "*.part"));
    }

    [Theory]
    [InlineData(UploadCollision.UploadKeepBoth, UploadPhase.UploadCompleted)]
    [InlineData(UploadCollision.UploadSkip, UploadPhase.UploadSkipped)]
    [InlineData(UploadCollision.UploadFail, UploadPhase.UploadReview)]
    public async Task Collision_policy_never_changes_an_existing_file(UploadCollision collision, UploadPhase expected)
    {
        await using var f = new Fixture(); await f.Initialize(); var original = "existing bytes"u8.ToArray(); File.WriteAllBytes(Path.Combine(f.Destination, "same.txt"), original);
        var bytes = "incoming bytes"u8.ToArray(); var row = await f.Begin(Fixture.Descriptor("same.txt", bytes, collision)); await f.Write(row, bytes);
        var finished = await f.Finish(row); Assert.Equal(expected, finished.Phase); Assert.Equal(original, File.ReadAllBytes(Path.Combine(f.Destination, "same.txt")));
        if (expected == UploadPhase.UploadCompleted) { Assert.Equal("same (2).txt", finished.FinalName); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, finished.FinalName))); }
        if (expected == UploadPhase.UploadReview) { await f.Grants.Uploads.DiscardOwnedPartialAsync(row.Id); Assert.Empty(Directory.GetFiles(f.Destination, "*.part")); }
    }

    [Fact]
    public async Task Read_only_grants_wrong_peers_old_epochs_and_reused_descriptors_cannot_write()
    {
        await using var f = new Fixture(StoragePermission.ReadOnly); await f.Initialize(); var descriptor = Fixture.Descriptor("new.txt", "new"u8.ToArray());
        var request = new FileRequest { Operation = FileOperation.PrepareUpload, DestinationId = f.Parent, MutationEpoch = f.Epoch, TransferId = descriptor.Id, Upload = descriptor };
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request)).Status); Assert.Empty(Directory.GetFiles(f.Destination));
        f.Grants.Update(f.Grant.Id, StoragePermission.List | StoragePermission.Create, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request)).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.List | StoragePermission.Write, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request)).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true); var prepared = (await f.Call(request)).Upload;
        Assert.Equal(FileStatus.NotFound, (await f.Raw(new() { Operation = FileOperation.UploadStatus, TransferId = prepared.Id, MutationEpoch = f.Epoch }, new string('b',64))).Status);
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(new() { Operation = FileOperation.UploadStatus, TransferId = prepared.Id, MutationEpoch = ProtocolRules.NewToken() })).Status);
        var reused = request.Clone(); reused.Upload.Name = "different.txt"; Assert.Equal(FileStatus.Conflict, (await f.Raw(reused)).Status);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = prepared.Id, MutationEpoch = f.Epoch, Offset = 1, Content = ByteString.CopyFromUtf8("xx") })).Status);
    }

    [Fact]
    public async Task Revocation_stops_remote_write_but_the_local_owner_can_discard_only_the_owned_partial()
    {
        await using var f = new Fixture(); await f.Initialize(); var data = RandomNumberGenerator.GetBytes(40_000); var row = await f.Begin(Fixture.Descriptor("later.bin", data));
        await f.Call(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Content = ByteString.CopyFrom(data, 0, StorageRules.MaxReadRange) });
        File.WriteAllText(Path.Combine(f.Destination, "keep.txt"), "owner file"); f.Grants.RevokePeer(f.Peer);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Offset = StorageRules.MaxReadRange, Content = ByteString.CopyFrom(data, StorageRules.MaxReadRange, data.Length - StorageRules.MaxReadRange) })).Status);
        await f.Grants.Uploads.DiscardOwnedPartialAsync(row.Id); Assert.Empty(Directory.GetFiles(f.Destination, "*.part")); Assert.Equal("owner file", File.ReadAllText(Path.Combine(f.Destination, "keep.txt")));
    }

    [Fact]
    public async Task Changed_partial_prefix_fails_after_restart_and_never_becomes_a_published_file()
    {
        await using var f = new Fixture(); await f.Initialize(); var data = RandomNumberGenerator.GetBytes(50_000); var row = await f.Begin(Fixture.Descriptor("corrupt.bin", data));
        await f.Call(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Content = ByteString.CopyFrom(data, 0, StorageRules.MaxReadRange) });
        using (var file = new FileStream(Assert.Single(Directory.GetFiles(f.Destination, "*.part")), FileMode.Open, FileAccess.Write)) file.WriteByte((byte)(data[0] ^ 1));
        await f.Restart(); Assert.Equal(FileStatus.Conflict, (await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Offset = StorageRules.MaxReadRange, Content = ByteString.CopyFrom(data, StorageRules.MaxReadRange, data.Length - StorageRules.MaxReadRange) })).Status);
        Assert.False(File.Exists(Path.Combine(f.Destination, row.Name))); await f.Grants.Uploads.DiscardOwnedPartialAsync(row.Id);
    }

    [Fact]
    public async Task Empty_files_work_and_bad_whole_hash_is_retained_for_review()
    {
        await using var f = new Fixture(); await f.Initialize(); var empty = await f.Begin(Fixture.Descriptor("empty.txt", []));
        Assert.Equal(UploadPhase.UploadCompleted, (await f.Finish(empty)).Phase); Assert.Equal(0, new FileInfo(Path.Combine(f.Destination, "empty.txt")).Length);
        var expected = "expected"u8.ToArray(); var changed = "modified"u8.ToArray(); var bad = await f.Begin(Fixture.Descriptor("bad.txt", expected));
        await f.Write(bad, changed); Assert.Equal(UploadPhase.UploadReview, (await f.Finish(bad)).Phase); Assert.False(File.Exists(Path.Combine(f.Destination, "bad.txt")));
        await f.Grants.Uploads.DiscardOwnedPartialAsync(bad.Id);
    }

    [Fact]
    public async Task Unacknowledged_bytes_after_failed_checkpoint_are_discarded_on_explicit_resume()
    {
        await using var f = new Fixture(); await f.Initialize(); var bytes = RandomNumberGenerator.GetBytes(40_000); var row = await f.Begin(Fixture.Descriptor("checkpoint.bin", bytes));
        var blocker = Path.Combine(f.Profile, "incoming-uploads", "journal.json.new"); Directory.CreateDirectory(blocker);
        Assert.Contains((await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange) })).Status, new[] { FileStatus.IoError, FileStatus.AccessDenied });
        Assert.Equal(StorageRules.MaxReadRange, new FileInfo(Assert.Single(Directory.GetFiles(f.Destination, "*.part"))).Length);
        Directory.Delete(blocker); await f.Restart();
        Assert.Equal(0, (await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = f.Epoch })).Upload.Offset);
        await f.Write(row, bytes); var done = await f.Finish(row); Assert.Equal(UploadPhase.UploadCompleted, done.Phase);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, done.FinalName)));
    }

    [Fact]
    public async Task Lost_publication_receipt_reconciles_the_same_file_after_restart_without_another_copy()
    {
        await using var f = new Fixture(); await f.Initialize(); var bytes = "published exactly once"u8.ToArray(); var row = await f.Begin(Fixture.Descriptor("receipt.txt", bytes)); await f.Write(row, bytes);
        var blocker = Path.Combine(f.Profile, "incoming-uploads", "journal.json.new"); var injected = false;
        f.Grants.Uploads.Changed += () => { if (!injected && f.Grants.Uploads.All.Single(x => x.Id == row.Id).Phase == UploadPhase.UploadFinalizing) { injected = true; Directory.CreateDirectory(blocker); } };
        await f.Call(new() { Operation = FileOperation.FinishUpload, TransferId = row.Id, MutationEpoch = f.Epoch }); await f.Grants.Uploads.WhenIdleAsync();
        Assert.True(injected); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, "receipt.txt"))); Assert.Empty(Directory.GetFiles(f.Destination, "*.part"));
        Directory.Delete(blocker); await f.Restart(); Assert.Equal(UploadPhase.UploadCompleted, (await f.Finish(row)).Phase);
        Assert.Single(Directory.GetFiles(f.Destination));
    }

    [Fact]
    public async Task Quotas_and_replaced_partial_identity_prevent_new_effects()
    {
        await using var f = new Fixture(); await f.Initialize(); var descriptor = Fixture.Descriptor("too-large.bin", []); descriptor.Size = UploadRules.PeerQuota + 1;
        Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(new() { Operation = FileOperation.PrepareUpload, TransferId = descriptor.Id, DestinationId = f.Parent, MutationEpoch = f.Epoch, Upload = descriptor })).Status);
        Assert.Empty(Directory.GetFiles(f.Destination));
        var bytes = RandomNumberGenerator.GetBytes(40_000); var row = await f.Begin(Fixture.Descriptor("substitution.bin", bytes));
        await f.Call(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange) });
        var temporary = Assert.Single(Directory.GetFiles(f.Destination, "*.part")); File.Move(temporary, temporary + ".preserved"); File.WriteAllBytes(temporary, bytes[..StorageRules.MaxReadRange]);
        await f.Restart(); Assert.Equal(FileStatus.Conflict, (await f.Raw(new() { Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Offset = StorageRules.MaxReadRange, Content = ByteString.CopyFrom(bytes, StorageRules.MaxReadRange, bytes.Length - StorageRules.MaxReadRange) })).Status);
        await Assert.ThrowsAsync<StorageException>(() => f.Grants.Uploads.DiscardOwnedPartialAsync(row.Id)); Assert.Equal(2, Directory.GetFiles(f.Destination).Length);
    }

    [Fact]
    public async Task Owner_reset_preserves_copies_but_requires_review_and_a_fresh_upload_epoch()
    {
        await using var f = new Fixture(ownerBound: true); await f.Initialize(); var originalEpoch = f.Epoch;
        var row = await f.Begin(Fixture.Descriptor("earlier.bin", "earlier bytes"u8.ToArray()));
        f.Binding!.Reset(); f.Grants.ReloadOwnerScope();
        var issue = Assert.Single(f.Grants.Uploads.RecoveryIssues); Assert.True(issue.CanArchive);
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = originalEpoch })).Status);
        Assert.Single(Directory.GetFiles(f.Destination, "*.part")); f.Grants.Uploads.ArchiveRecovery(issue.Id);
        Assert.Empty(f.Grants.Uploads.All); Assert.Equal(1, f.Grants.Uploads.PreservedRecords);
        f.Grants.Add(f.Peer, f.Destination, StoragePermission.Contribute); await f.Restart(); Assert.NotEqual(originalEpoch, f.Epoch);
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(new() { Operation = FileOperation.FinishUpload, TransferId = row.Id, MutationEpoch = originalEpoch })).Status);
        Assert.Single(Directory.GetFiles(f.Destination, "*.part")); Assert.False(File.Exists(Path.Combine(f.Destination, row.Name)));
    }

    [Fact]
    public async Task Receive_only_inbox_exposes_a_destination_without_existing_names_contents_or_folder_activity()
    {
        await using var f = new Fixture(StoragePermission.Write | StoragePermission.Create); await f.Initialize();
        File.WriteAllText(Path.Combine(f.Destination, "private.txt"), "original private bytes");
        Assert.Empty((await f.Call(new() { Operation = FileOperation.ListRoots })).Entries);
        var before = Assert.Single((await f.Call(new() { Operation = FileOperation.UploadContext })).Entries); Assert.Equal(0, before.ModifiedAt);
        foreach (var operation in new[] { FileOperation.ListDirectory, FileOperation.GetMetadata, FileOperation.ReadRange, FileOperation.PrepareDownload })
            Assert.Equal(FileStatus.AccessDenied, (await f.Raw(new() { Operation = operation, NodeId = f.Parent, Length = 32 })).Status);
        var data = "received private bytes"u8.ToArray(); var row = await f.Begin(Fixture.Descriptor("private.txt", data)); await f.Write(row, data);
        var receipt = await f.Finish(row); Assert.Equal(UploadPhase.UploadCompleted, receipt.Phase); Assert.StartsWith("private (", receipt.FinalName); Assert.EndsWith(").txt", receipt.FinalName);
        Assert.NotEqual("private (2).txt", receipt.FinalName); Assert.Equal(data, File.ReadAllBytes(Path.Combine(f.Destination, receipt.FinalName)));
        Assert.Equal("original private bytes", File.ReadAllText(Path.Combine(f.Destination, "private.txt")));
        var after = Assert.Single((await f.Call(new() { Operation = FileOperation.UploadContext })).Entries);
        Assert.Equal(before.Version, after.Version); Assert.Equal(0, after.ModifiedAt); Assert.Equal(0, after.Size);
    }

    [Fact]
    public async Task Permission_change_after_publication_intent_still_prevents_the_filesystem_effect()
    {
        await using var f = new Fixture(); await f.Initialize(); var bytes = "verify before publish"u8.ToArray(); var row = await f.Begin(Fixture.Descriptor("policy.txt", bytes)); await f.Write(row, bytes);
        var changed = false;
        f.Grants.Uploads.Changed += () => { if (!changed && f.Grants.Uploads.All.Single(x => x.Id == row.Id).Phase == UploadPhase.UploadFinalizing) { changed = true; f.Grants.Update(f.Grant.Id, StoragePermission.ReadOnly, [], null, true); } };
        await f.Call(new() { Operation = FileOperation.FinishUpload, TransferId = row.Id, MutationEpoch = f.Epoch }); await f.Grants.Uploads.WhenIdleAsync();
        Assert.True(changed); Assert.False(File.Exists(Path.Combine(f.Destination, row.Name))); Assert.Equal(UploadPhase.UploadReview, f.Grants.Uploads.All.Single().Phase);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true);
        Assert.Equal(UploadPhase.UploadCompleted, (await f.Finish(row)).Phase); Assert.Single(Directory.GetFiles(f.Destination));
    }

    [Fact]
    public async Task Explicit_history_rotation_preserves_partial_files_and_old_requests_cannot_replay()
    {
        await using var f = new Fixture(); await f.Initialize(); var row = await f.Begin(Fixture.Descriptor("kept.bin", "kept source"u8.ToArray())); var oldEpoch = f.Epoch;
        f.Grants.Uploads.ArchiveHistory(); Assert.Empty(f.Grants.Uploads.All); Assert.Equal(1, f.Grants.Uploads.PreservedRecords);
        Assert.Single(Directory.GetFiles(f.Destination, "*.part"));
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(new() { Operation = FileOperation.PrepareUpload, TransferId = row.Id, MutationEpoch = oldEpoch, Upload = row, DestinationId = f.Parent })).Status);
        await f.Initialize(); Assert.NotEqual(oldEpoch, f.Epoch);
        var fresh = await f.Begin(Fixture.Descriptor("new.txt", [])); Assert.Equal(UploadPhase.UploadCompleted, (await f.Finish(fresh)).Phase);
        f.Grants.Uploads.ClearPreservedRecords(); Assert.Equal(0, f.Grants.Uploads.PreservedRecords);
        Assert.Single(Directory.GetFiles(f.Destination, "*.part")); Assert.True(File.Exists(Path.Combine(f.Destination, "new.txt")));
    }
}
