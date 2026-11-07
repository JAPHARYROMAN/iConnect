using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed class ReceivingPartRecoveryTests
{
    private sealed class AcceptedFiles : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root;
        public string Profile => Path.Combine(Root, "profile");
        public string Destination => Path.Combine(Root, "destination");
        public string Original => Path.Combine(Root, "original");
        public readonly string Peer = new('a', 64);
        public readonly WindowsOwnerBinding Owner;
        public StorageGrantStore Grants = null!;
        public WindowsFileService Service = null!;
        public DownloadSourceStore Tickets = null!;
        public WindowsDownloadManager Files = null!;
        public WindowsBatchDownloads Batches = null!;
        public WindowsReceivingPartRecovery Recovery = null!;
        public bool Allowed = true;
        public string? OwnerOverride;
        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = Path.Combine(repo!.FullName, ".local", "receiving-part-recovery-tests", ProtocolRules.NewToken());
            Directory.CreateDirectory(Destination); Directory.CreateDirectory(Original);
            File.WriteAllText(Path.Combine(Destination, "owner.txt"), "generated owner document");
            File.WriteAllText(Path.Combine(Original, "original.txt"), "generated original bytes");
            Owner = new(Profile, new string('b', 64)); Open(); Grants.Add(Peer, Destination, StoragePermission.Contribute);
        }
        private void Open()
        {
            Grants = new(Profile, ownerBinding: Owner) { AttachmentChecks = new AcceptedFiles() }; Tickets = new(Grants, Profile); Service = new(Grants, Tickets);
            Files = new(Profile, new AcceptedFiles()); Batches = new(Profile, Files); NewRecovery();
        }
        public void NewRecovery()
        {
            Recovery?.Dispose(); Recovery = new(Grants, Files, Batches,
                () => OwnerOverride ?? Owner.OwnerId + "." + Owner.Epoch, () => Allowed);
        }
        public async Task Reopen(Action change)
        {
            Recovery.Dispose(); await Batches.DisposeAsync(); await Files.DisposeAsync(); Service.Dispose(); await Tickets.DisposeAsync();
            change(); Open();
        }
        public string Candidate(byte[]? bytes = null, string? token = null, string? folder = null)
        {
            var path = Path.Combine(folder ?? Destination, ".iconnect-transfer-" + (token ?? ProtocolRules.NewToken()) + ".part");
            File.WriteAllBytes(path, bytes ?? [1, 2, 3]); return path;
        }
        public async Task<FileResponse> Call(FileRequest request)
        {
            request.RequestId = ProtocolRules.NewToken(); var reply = await Service.ExecuteAsync(Peer, request, default);
            Assert.True(reply.Status == FileStatus.Ok, reply.Status + ": " + reply.Message); return reply;
        }
        public async Task<string> StageIncoming()
        {
            var context = await Call(new() { Operation = FileOperation.UploadContext }); var target = context.Entries.Single();
            var bytes = "generated partial bytes"u8.ToArray(); var descriptor = new UploadInfo { Id = MutationRules.NewId(), Name = "incoming.txt", Size = bytes.Length,
                Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)), Collision = UploadCollision.UploadKeepBoth };
            await Call(new() { Operation = FileOperation.PrepareUpload, MutationEpoch = context.MutationEpoch, DestinationId = target.Id, TransferId = descriptor.Id, Upload = descriptor });
            await Call(new() { Operation = FileOperation.UploadWrite, MutationEpoch = context.MutationEpoch, TransferId = descriptor.Id, Content = ByteString.CopyFrom(bytes) });
            var cache = JsonNode.Parse(File.ReadAllText(Path.Combine(Profile, "incoming-uploads", "journal.json")))!;
            return Path.Combine(Destination, ".iconnect-transfer-" + cache["Rows"]![0]!["Temporary"]!.GetValue<string>() + ".part");
        }
        public async Task<FileEntry> Source()
        {
            var grant = Grants.Add(Peer, Original); var root = (await Call(new() { Operation = FileOperation.ListRoots })).Entries.Single(x => x.GrantId == grant.Id);
            return Assert.Single((await Call(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id })).Entries);
        }
        public void OriginalsIntact()
        {
            Assert.Equal("generated owner document", File.ReadAllText(Path.Combine(Destination, "owner.txt")));
            Assert.Equal("generated original bytes", File.ReadAllText(Path.Combine(Original, "original.txt")));
        }
        public async ValueTask DisposeAsync()
        {
            Recovery.Dispose(); await Batches.DisposeAsync(); await Files.DisposeAsync(); Service.Dispose(); await Tickets.DisposeAsync();
            Grants.Uploads.PauseVerification(); await Grants.Uploads.WhenIdleAsync(); Grants.BatchPlans.PauseFileVerification(); await Grants.BatchPlans.WhenFilesIdleAsync();
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task Current_upload_receipt_protects_actual_bytes_and_only_separately_reviewed_candidates_are_removed()
    {
        await using var f = new Fixture(); var current = await f.StageIncoming(); var original = File.ReadAllBytes(current); var candidate = f.Candidate();
        var folder = f.Recovery.Select(f.Destination); Assert.Equal(1, folder.Files); Assert.Equal(3, folder.Bytes);
        Assert.Equal(Path.GetFileName(candidate), Assert.Single(f.Recovery.Inspect(folder.Id, 0)).Name);
        var review = f.Recovery.ReviewRemoval(folder.Id); Assert.True(File.Exists(candidate));
        Assert.True(f.Recovery.ConfirmRemoval(review)); Assert.False(File.Exists(candidate)); Assert.Equal(original, File.ReadAllBytes(current));
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); f.OriginalsIntact();
    }
    [Fact]
    public async Task History_reset_invalidates_old_permission_and_requires_a_separate_review_for_actual_retained_parts()
    {
        await using var f = new Fixture(); var current = await f.StageIncoming(); var candidate = f.Candidate();
        var folder = f.Recovery.Select(f.Destination); var review = f.Recovery.ReviewRemoval(folder.Id); f.Grants.Uploads.ArchiveHistory();
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.True(File.Exists(current)); Assert.True(File.Exists(candidate));
        var fresh = f.Recovery.ReviewRemoval(folder.Id); Assert.Equal(2, fresh.Files); Assert.Equal(26, fresh.Bytes);
        Assert.True(f.Recovery.ConfirmRemoval(fresh)); Assert.False(File.Exists(current)); Assert.False(File.Exists(candidate));
        Assert.Equal(1, f.Grants.Uploads.PreservedRecords); f.OriginalsIntact();
    }
    [Fact]
    public async Task Forged_changed_and_replayed_reviews_cannot_remove_any_file()
    {
        await using var f = new Fixture(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination);
        var review = f.Recovery.ReviewRemoval(folder.Id); Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review with { Files = review.Files + 1 }));
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.True(File.Exists(candidate));
        review = f.Recovery.ReviewRemoval(folder.Id); File.AppendAllText(candidate, " changed");
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.EndsWith(" changed", File.ReadAllText(candidate));
        Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); f.OriginalsIntact();
    }
    [Fact]
    public async Task Removal_is_bounded_paged_and_does_not_include_new_files_during_continuation()
    {
        await using var f = new Fixture(); var paths = Enumerable.Range(0, 66).Select(_ => f.Candidate()).ToArray(); var folder = f.Recovery.Select(f.Destination);
        Assert.Equal(50, f.Recovery.Inspect(folder.Id, 0).Count); Assert.Equal(16, f.Recovery.Inspect(folder.Id, 50).Count);
        Assert.False(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); Assert.Equal(64, f.Recovery.Progress!.Removed); Assert.Equal(2, paths.Count(File.Exists));
        var added = f.Candidate([9, 8, 7, 6]); Assert.True(f.Recovery.ContinueRemoval()); Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(added)); Assert.Null(f.Recovery.Selected); Assert.Null(f.Recovery.Progress);
        Assert.Equal(1, f.Recovery.Select(f.Destination).Files); f.OriginalsIntact();
    }
    [Fact]
    public async Task Reopening_cleanup_requires_a_new_folder_selection_and_fresh_review_of_remaining_files()
    {
        await using var f = new Fixture(); var paths = Enumerable.Range(0, 66).Select(_ => f.Candidate()).ToArray(); var folder = f.Recovery.Select(f.Destination);
        var review = f.Recovery.ReviewRemoval(folder.Id); Assert.False(f.Recovery.ConfirmRemoval(review)); f.NewRecovery();
        Assert.Null(f.Recovery.Progress); Assert.Null(f.Recovery.Selected); Assert.Throws<IOException>(() => f.Recovery.ContinueRemoval());
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.Throws<IOException>(() => f.Recovery.Inspect(folder.Id, 0));
        var fresh = f.Recovery.Select(f.Destination); Assert.Equal(2, fresh.Files); Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(fresh.Id)));
        Assert.All(paths, path => Assert.False(File.Exists(path))); f.OriginalsIntact();
    }
    [Fact]
    public async Task A_changed_remaining_file_ends_old_permission_and_preserves_it_for_new_review()
    {
        await using var f = new Fixture(); var paths = Enumerable.Range(0, 66).Select(_ => f.Candidate()).ToArray(); var folder = f.Recovery.Select(f.Destination);
        Assert.False(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); var changed = paths.Where(File.Exists).Order(StringComparer.Ordinal).First(); File.AppendAllText(changed, " changed");
        Assert.Throws<IOException>(() => f.Recovery.ContinueRemoval()); Assert.EndsWith(" changed", File.ReadAllText(changed)); Assert.Null(f.Recovery.Progress);
        Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); f.OriginalsIntact();
    }
    [Fact]
    public async Task Screen_withdrawal_keeps_exact_current_process_progress_until_deliberate_continuation()
    {
        await using var f = new Fixture(); var paths = Enumerable.Range(0, 10).Select(_ => f.Candidate()).ToArray(); var folder = f.Recovery.Select(f.Destination); var checks = 0;
        Assert.Throws<OperationCanceledException>(() => f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id), () => ++checks <= 4));
        var removed = f.Recovery.Progress!.Removed; Assert.InRange(removed, 1, 9); Assert.Equal(10 - removed, paths.Count(File.Exists));
        Assert.True(f.Recovery.ContinueRemoval()); Assert.All(paths, path => Assert.False(File.Exists(path))); f.OriginalsIntact();
    }
    [Fact]
    public async Task Revoked_peer_does_not_prevent_local_cleanup_but_owner_generation_changes_end_the_review()
    {
        await using var f = new Fixture(); var current = await f.StageIncoming(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination);
        f.Grants.RevokePeer(f.Peer); Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); Assert.True(File.Exists(current)); Assert.False(File.Exists(candidate));
        candidate = f.Candidate(); folder = f.Recovery.Select(f.Destination); var review = f.Recovery.ReviewRemoval(folder.Id); f.Owner.Reset();
        Assert.ThrowsAny<Exception>(() => f.Recovery.ConfirmRemoval(review)); Assert.True(File.Exists(candidate)); Assert.True(File.Exists(current)); f.OriginalsIntact();
    }
    [Fact]
    public async Task Changed_owner_and_disconnected_boundary_cannot_use_earlier_reviews()
    {
        await using var f = new Fixture(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination); var review = f.Recovery.ReviewRemoval(folder.Id);
        f.Allowed = false; Assert.Throws<OperationCanceledException>(() => f.Recovery.ConfirmRemoval(review)); Assert.True(File.Exists(candidate)); f.Allowed = true;
        f.OwnerOverride = new string('c', 64) + "." + ProtocolRules.NewToken(); Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.True(File.Exists(candidate));
        Assert.Throws<IOException>(() => f.Recovery.ReviewRemoval(folder.Id)); f.OriginalsIntact();
    }
    [Theory]
    [InlineData("journal.json")]
    [InlineData("journal.json.new")]
    public async Task Changed_or_uncommitted_incoming_metadata_blocks_inspection_and_preserves_files(string name)
    {
        await using var f = new Fixture(); var current = await f.StageIncoming(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination);
        var review = f.Recovery.ReviewRemoval(folder.Id); var path = Path.Combine(f.Profile, "incoming-uploads", name); File.AppendAllText(path, "generated changed ownership");
        Assert.ThrowsAny<Exception>(() => f.Recovery.ConfirmRemoval(review)); Assert.ThrowsAny<Exception>(() => f.Recovery.Inspect(folder.Id, 0));
        Assert.True(File.Exists(candidate)); Assert.True(File.Exists(current)); Assert.EndsWith("generated changed ownership", File.ReadAllText(path)); f.OriginalsIntact();
    }
    [Fact]
    public async Task Replaced_destination_folder_and_files_do_not_inherit_earlier_removal_permission()
    {
        await using var f = new Fixture(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination); var review = f.Recovery.ReviewRemoval(folder.Id);
        var preserved = Path.Combine(f.Root, "preserved-destination"); Directory.Move(f.Destination, preserved); Directory.CreateDirectory(f.Destination);
        var replacement = Path.Combine(f.Destination, Path.GetFileName(candidate)); File.WriteAllText(replacement, "generated replacement owner file");
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.Equal("generated replacement owner file", File.ReadAllText(replacement));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(preserved, Path.GetFileName(candidate))));
    }
    [Fact]
    public async Task Hard_links_and_temporary_looking_directories_are_kept_without_following_or_descending()
    {
        await using var f = new Fixture(); var alias = Path.Combine(f.Destination, ".iconnect-transfer-" + ProtocolRules.NewToken() + ".part");
        Assert.True(CreateHardLink(alias, Path.Combine(f.Original, "original.txt"), IntPtr.Zero));
        Assert.ThrowsAny<Exception>(() => f.Recovery.Select(f.Destination)); f.OriginalsIntact(); File.Delete(alias);
        Directory.CreateDirectory(alias); var nested = Path.Combine(alias, "owner.txt"); File.WriteAllText(nested, "generated nested owner data");
        Assert.Throws<IOException>(() => f.Recovery.Select(f.Destination)); Assert.Equal("generated nested owner data", File.ReadAllText(nested)); f.OriginalsIntact();
    }
    [Fact]
    public async Task Locked_file_is_kept_and_requires_fresh_review_after_the_fault()
    {
        await using var f = new Fixture(); var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination); var review = f.Recovery.ReviewRemoval(folder.Id);
        using (var held = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.ThrowsAny<IOException>(() => f.Recovery.ConfirmRemoval(review));
        Assert.True(File.Exists(candidate)); Assert.Null(f.Recovery.Progress); Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); f.OriginalsIntact();
    }
    [Fact]
    public async Task Active_downloads_refuse_cleanup_and_paused_actual_download_parts_stay_protected()
    {
        await using var f = new Fixture(); var entry = await f.Source(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return await f.Call(request);
        }
        var id = f.Files.Start(f.Peer, entry, f.Destination, DownloadCollision.KeepBoth, Request);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Throws<IOException>(() => f.Recovery.Select(f.Destination)); }
        finally { f.Files.Pause(id); await f.Files.WaitAsync(id); }
        var part = Path.Combine(f.Destination, ".iconnect-transfer-" + id + ".part"); Assert.True(File.Exists(part)); var candidate = f.Candidate();
        var folder = f.Recovery.Select(f.Destination); Assert.Equal(1, folder.Files); Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id)));
        Assert.True(File.Exists(part)); Assert.False(File.Exists(candidate)); f.OriginalsIntact();
    }
    [Fact]
    public async Task A_current_folder_download_protects_its_actual_part_even_after_the_separate_file_record_is_lost()
    {
        await using var f = new Fixture(); var entry = await f.Source(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return await f.Call(request);
        }
        var manifest = await TransferManifests.BuildAsync([new(entry, [entry.Name])], Request, default);
        var batch = f.Batches.Start(f.Peer, manifest, f.Destination, DownloadCollision.KeepBoth, Request);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Throws<IOException>(() => f.Recovery.Select(f.Destination)); }
        finally { f.Batches.Pause(batch); await f.Batches.WaitAsync(batch); }
        var child = Assert.Single(Assert.Single(f.Batches.Jobs).Items).DownloadId; await f.Files.WaitAsync(child);
        var part = Path.Combine(f.Destination, ".iconnect-transfer-" + child + ".part"); Assert.True(File.Exists(part));
        await f.Reopen(() => File.Delete(Path.Combine(f.Profile, "downloads", child + ".json")));
        Assert.Empty(f.Files.Jobs); Assert.Equal(child, Assert.Single(Assert.Single(f.Batches.Jobs).Items).DownloadId);
        var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination); Assert.Equal(1, folder.Files);
        Assert.True(f.Recovery.ConfirmRemoval(f.Recovery.ReviewRemoval(folder.Id))); Assert.True(File.Exists(part)); Assert.False(File.Exists(candidate)); f.OriginalsIntact();
    }
    [Fact]
    public async Task A_cold_interrupted_download_keeps_its_part_and_uncommitted_folder_metadata_ends_the_review()
    {
        await using var f = new Fixture(); var entry = await f.Source(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadRead) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return await f.Call(request);
        }
        var id = f.Files.Start(f.Peer, entry, f.Destination, DownloadCollision.KeepBoth, Request);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); } finally { f.Files.Pause(id); await f.Files.WaitAsync(id); }
        await f.Reopen(() =>
        {
            var path = Path.Combine(f.Profile, "downloads", id + ".json"); var row = JsonNode.Parse(File.ReadAllText(path))!;
            row["Phase"] = (int)DownloadPhase.Downloading; row["Error"] = ""; File.WriteAllText(path, row.ToJsonString());
        });
        Assert.Equal(DownloadPhase.Interrupted, Assert.Single(f.Files.Jobs).Phase); var part = Path.Combine(f.Destination, ".iconnect-transfer-" + id + ".part");
        var candidate = f.Candidate(); var folder = f.Recovery.Select(f.Destination); Assert.Equal(1, folder.Files); var review = f.Recovery.ReviewRemoval(folder.Id);
        var staged = Path.Combine(f.Profile, "batches", ProtocolRules.NewToken() + ".json.new"); File.WriteAllText(staged, "generated uncommitted folder ownership");
        Assert.Throws<IOException>(() => f.Recovery.ConfirmRemoval(review)); Assert.Null(f.Recovery.Progress); Assert.True(File.Exists(part)); Assert.True(File.Exists(candidate));
        Assert.Equal("generated uncommitted folder ownership", File.ReadAllText(staged)); f.OriginalsIntact();
    }

    [Fact]
    public async Task Local_copy_receipts_protect_paused_parts_and_unreadable_local_ownership_blocks_cleanup()
    {
        await using var f = new Fixture(); var path = Path.Combine(f.Original, "large-local.bin");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 17));
        using var browser = new LocalFileLocations(f.Profile); browser.Add(f.Original);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var entry = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == "large-local.bin");
        var selected = await browser.CaptureCopyAsync(entry);
        WindowsLocalCopies? copies = new(f.Profile, new AcceptedFiles());
        try
        {
            var active = copies;
            active.Changed += () => { var row = active.Jobs.SingleOrDefault()?.Transfer; if (row is { Phase: DownloadPhase.Downloading, Offset: > 0 }) active.Pause(row.Id); };
            var id = copies.Prepare(selected); copies.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth);
            await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(DownloadPhase.Paused, copies.Jobs.Single().Transfer!.Phase);
            var part = Path.Combine(f.Destination, ".iconnect-transfer-" + id + ".part"); Assert.True(File.Exists(part));
            using var recovery = new WindowsReceivingPartRecovery(f.Grants, f.Files, f.Batches, () => f.Owner.OwnerId + "." + f.Owner.Epoch, () => true, () => copies);
            Assert.Equal(0, recovery.Select(f.Destination).Files);
            var orphan = f.Candidate(); Assert.Equal(1, recovery.Select(f.Destination).Files); Assert.True(File.Exists(part));
            Assert.Throws<IOException>(() => f.Recovery.Select(f.Destination)); // A consumer cannot silently omit an existing local queue.
            await copies.DisposeAsync(); copies = null;
            File.WriteAllText(Path.Combine(f.Profile, "local-copies", "receiving", "downloads", id + ".json"), "{damaged local receipt");
            copies = new(f.Profile, new AcceptedFiles()); Assert.Single(copies.RecoveryIssues);
            Assert.Throws<IOException>(() => recovery.Select(f.Destination)); Assert.True(File.Exists(part)); Assert.True(File.Exists(orphan));
        }
        finally { if (copies is not null) await copies.DisposeAsync(); }
    }
    [Fact]
    public async Task New_local_copy_ownership_invalidates_an_earlier_unidentified_part_removal_review()
    {
        await using var f = new Fixture(); var orphan = f.Candidate();
        using var browser = new LocalFileLocations(f.Profile); browser.Add(f.Original);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var file = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
        await using var copies = new WindowsLocalCopies(f.Profile, new AcceptedFiles());
        using var recovery = new WindowsReceivingPartRecovery(f.Grants, f.Files, f.Batches, () => f.Owner.OwnerId + "." + f.Owner.Epoch, () => true, () => copies);
        var folder = recovery.Select(f.Destination); Assert.Equal(1, folder.Files); var review = recovery.ReviewRemoval(folder.Id);
        var id = copies.Prepare(await browser.CaptureCopyAsync(file)); copies.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth);
        await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(DownloadPhase.Completed, copies.Jobs.Single().Transfer!.Phase);
        Assert.Throws<IOException>(() => recovery.ConfirmRemoval(review)); Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task Local_batch_receipts_protect_actual_paused_parts_and_unreadable_ownership_blocks_cleanup()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Original, "large-local.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        using var browser = new LocalFileLocations(f.Profile); browser.Add(f.Original);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var entry = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == "large-local.bin");
        WindowsLocalBatchCopies? copies = new(f.Profile, new AcceptedFiles());
        try
        {
            var active = copies; var id = await copies.PrepareAsync([await browser.CaptureBatchItemAsync(entry)]);
            active.Changed += () => { if (Directory.EnumerateFiles(f.Destination, ".iconnect-transfer-*.part").Any(x => new FileInfo(x).Length > 0)) active.Pause(id); };
            copies.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth); await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(BatchPhase.Paused, copies.Jobs.Single().Transfer!.Phase); var part = Assert.Single(Directory.GetFiles(f.Destination, ".iconnect-transfer-*.part")); Assert.True(new FileInfo(part).Length > 0);
            using var recovery = new WindowsReceivingPartRecovery(f.Grants, f.Files, f.Batches, () => f.Owner.OwnerId + "." + f.Owner.Epoch, () => true, localBatches: () => copies);
            Assert.Equal(0, recovery.Select(f.Destination).Files); var orphan = f.Candidate(); Assert.Equal(1, recovery.Select(f.Destination).Files);
            Assert.Throws<IOException>(() => f.Recovery.Select(f.Destination));
            var child = copies.Jobs.Single().Transfer!.Items.Single().DownloadId; await copies.DisposeAsync(); copies = null;
            File.WriteAllText(Path.Combine(f.Profile, "local-batch-copies", "receiving", "downloads", child + ".json"), "{damaged local batch child");
            copies = new(f.Profile, new AcceptedFiles()); Assert.Single(copies.RecoveryIssues); Assert.Throws<IOException>(() => recovery.Select(f.Destination)); Assert.True(File.Exists(part)); Assert.True(File.Exists(orphan));
        }
        finally { if (copies is not null) await copies.DisposeAsync(); }
    }
    [Fact]
    public async Task New_local_batch_ownership_invalidates_an_earlier_partial_removal_review()
    {
        await using var f = new Fixture(); var orphan = f.Candidate(); using var browser = new LocalFileLocations(f.Profile); browser.Add(f.Original);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var entry = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
        await using var copies = new WindowsLocalBatchCopies(f.Profile, new AcceptedFiles());
        using var recovery = new WindowsReceivingPartRecovery(f.Grants, f.Files, f.Batches, () => f.Owner.OwnerId + "." + f.Owner.Epoch, () => true, localBatches: () => copies);
        var folder = recovery.Select(f.Destination); var review = recovery.ReviewRemoval(folder.Id);
        var id = await copies.PrepareAsync([await browser.CaptureBatchItemAsync(entry)]); copies.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth); await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(BatchPhase.Completed, copies.Jobs.Single().Transfer!.Phase); Assert.Throws<IOException>(() => recovery.ConfirmRemoval(review)); Assert.True(File.Exists(orphan));
    }
    [Fact]
    public async Task Unattached_local_batch_parts_remain_protected_and_stopping_the_receipt_ends_old_reviews()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Original, "large-local.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        using var browser = new LocalFileLocations(f.Profile); browser.Add(f.Original); var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var entry = (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == "large-local.bin"); string id, child, part;
        await using (var first = new WindowsLocalBatchCopies(f.Profile, new AcceptedFiles()))
        {
            id = await first.PrepareAsync([await browser.CaptureBatchItemAsync(entry)]); var requested = false;
            first.Changed += () => { if (!requested && Directory.EnumerateFiles(f.Destination, ".iconnect-transfer-*.part").Any(x => new FileInfo(x).Length > 0)) { requested = true; first.Pause(id); } };
            first.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth); await first.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(BatchPhase.Paused, first.Jobs.Single().Transfer!.Phase);
            child = first.Jobs.Single().Transfer!.Items.Single().DownloadId; part = Assert.Single(Directory.GetFiles(f.Destination, ".iconnect-transfer-*.part"));
        }
        File.Delete(Path.Combine(f.Profile, "local-batch-copies", "receiving", "batches", id + ".json")); await using var copies = new WindowsLocalBatchCopies(f.Profile, new AcceptedFiles()); Assert.Single(copies.OrphanFiles);
        using var recovery = new WindowsReceivingPartRecovery(f.Grants, f.Files, f.Batches, () => f.Owner.OwnerId + "." + f.Owner.Epoch, () => true, localBatches: () => copies);
        Assert.Equal(0, recovery.Select(f.Destination).Files); var candidate = f.Candidate(); var folder = recovery.Select(f.Destination); Assert.Equal(1, folder.Files); var review = recovery.ReviewRemoval(folder.Id);
        await copies.StopOrphanAsync(child); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (copies.OrphanActive(child)) await Task.Delay(10, timeout.Token);
        Assert.False(File.Exists(part)); Assert.True(File.Exists(candidate)); Assert.Throws<IOException>(() => recovery.ConfirmRemoval(review)); copies.RemoveOrphan(child);
        Assert.Equal(1, recovery.Select(f.Destination).Files); Assert.True(copies.Jobs.Single().CopyMayExist);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
