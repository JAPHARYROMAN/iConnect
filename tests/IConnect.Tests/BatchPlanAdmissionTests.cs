using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public TimeSpan PayloadElapsed;
        public Action? TimestampRead;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { TimestampRead?.Invoke(); return PayloadElapsed.Ticks; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public const string Peer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public string Root { get; }
        public string Profile => Path.Combine(Root, "profile");
        public string Destination => Path.Combine(Root, "destination");
        public Clock Time { get; } = new();
        public WindowsOwnerBinding? Owner { get; }
        public string Pairing { get; set; } = ProtocolRules.NewToken();
        public StorageGrantStore Grants { get; private set; }
        public WindowsFileService Service { get; private set; }
        public StorageGrant Grant { get; }
        private readonly IAttachmentChecks? checks;
        public Fixture(bool owner = false, IAttachmentChecks? checks = null)
        {
            this.checks = checks;
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = Path.Combine(repo!.FullName, ".local", "batch-plan-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(Destination);
            if (owner) Owner = new(Profile, new string('b', 64));
            Grants = new(Profile, Time, Owner) { AttachmentChecks = checks ?? new WindowsAttachmentChecks() }; Grant = Grants.Add(Peer, Destination, StoragePermission.Contribute); Service = MakeService();
        }
        private WindowsFileService MakeService() { var result = new WindowsFileService(Grants); if (Owner is not null) result.BindOwnerPolicy(Owner, Peer, Pairing); return result; }
        public Task<FileResponse> Raw(FileRequest request, string peer = Peer) { request = request.Clone(); request.RequestId = ProtocolRules.NewToken(); return Service.ExecuteAsync(peer, request, default); }
        public async Task<FileResponse> Call(FileRequest request, string peer = Peer)
        { var response = await Raw(request, peer); Assert.True(response.Status == FileStatus.Ok, response.Status + ": " + response.Message); StorageRules.ValidateResponse(response); return response; }
        public async Task<(UploadBatchHeader Header, string Target)> Intent(UploadBatchManifest manifest, string peer = Peer)
        {
            var target = (await Call(new() { Operation = FileOperation.UploadContext }, peer)).Entries.Single();
            var epoch = (await Call(new() { Operation = FileOperation.UploadBatchContext }, peer)).MutationEpoch;
            return (UploadBatchRules.Header(MutationRules.NewId(), epoch, target.GrantId, manifest), target.Id);
        }
        public FileRequest Request(UploadBatchHeader h, FileOperation operation) => new() { Operation = operation, TransferId = h.Id, MutationEpoch = h.Epoch };
        public Task<FileResponse> Begin(UploadBatchHeader h, string target, string peer = Peer) => Call(new() { Operation = FileOperation.BeginUploadBatch, TransferId = h.Id, MutationEpoch = h.Epoch, DestinationId = target, BatchHeader = h }, peer);
        public async Task Upload(UploadBatchHeader h, UploadBatchManifest manifest)
        {
            var bytes = UploadBatchRules.Encode(manifest);
            for (var offset = 0; offset < bytes.Length; offset += StorageRules.MaxReadRange)
            { var request = Request(h, FileOperation.WriteUploadManifest); request.Offset = offset; request.Content = ByteString.CopyFrom(bytes, offset, Math.Min(StorageRules.MaxReadRange, bytes.Length - offset)); await Call(request); }
        }
        public async Task Reopen() { Service.Dispose(); await Grants.Uploads.WhenIdleAsync(); await Grants.BatchPlans.WhenFilesIdleAsync(); Grants = new(Profile, Time, Owner) { AttachmentChecks = checks ?? new WindowsAttachmentChecks() }; Service = MakeService(); }
        public async ValueTask DisposeAsync() { Service.Dispose(); await Grants.Uploads.WhenIdleAsync(); await Grants.BatchPlans.WhenFilesIdleAsync(); Directory.Delete(Root, true); }
    }
    private static UploadBatchManifest Plan(int count = 2, long bytes = 6)
    {
        var result = new UploadBatchManifest { Collision = UploadCollision.UploadKeepBoth };
        result.Items.Add(new UploadBatchItem { Parent = -1, Name = "folder", IsDirectory = true });
        for (var i = 1; i < count; i++) result.Items.Add(new UploadBatchItem { Parent = 0, Name = "Habari-文書-" + i + ".txt", Size = bytes,
            Sha256 = ByteString.CopyFrom(bytes == 0 ? SHA256.HashData([]) : SHA256.HashData("salama"u8)) });
        return result;
    }
    [Fact]
    public async Task Full_4096_item_plan_is_durable_after_restart_without_destination_effects()
    {
        await using var f = new Fixture(); var manifest = Plan(4096, 0); var (header, target) = await f.Intent(manifest);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanReceiving, (await f.Begin(header, target)).Batch.Phase); await f.Upload(header, manifest);
        var accepted = await f.Call(f.Request(header, FileOperation.SealUploadBatch)); Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, accepted.Batch.Phase);
        Assert.Equal(header, accepted.Batch.Header); Assert.Equal(header.EncodedLength, accepted.Batch.ManifestOffset); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
        await f.Reopen(); var status = await f.Call(f.Request(header, FileOperation.UploadBatchStatus)); Assert.Equal(header, status.Batch.Header); Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, status.Batch.Phase);
        var currentTarget = (await f.Call(new() { Operation = FileOperation.UploadContext })).Entries.Single();
        Assert.NotEqual(target, currentTarget.Id); Assert.Equal(header, (await f.Begin(header, currentTarget.Id)).Batch.Header);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, (await f.Call(f.Request(header, FileOperation.SealUploadBatch))).Batch.Phase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination)); Assert.Single(f.Grants.BatchPlans.All); Assert.Empty(f.Grants.Uploads.All);
    }
    [Fact]
    public async Task Interrupted_assembly_restarts_at_zero_under_the_same_durable_intent()
    {
        await using var f = new Fixture(); var manifest = Plan(4096, 0); var (h, target) = await f.Intent(manifest); await f.Begin(h, target);
        var bytes = UploadBatchRules.Encode(manifest); var chunk = f.Request(h, FileOperation.WriteUploadManifest); chunk.Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange);
        Assert.Equal((uint)StorageRules.MaxReadRange, (await f.Call(chunk)).Batch.ManifestOffset); Assert.Equal((uint)StorageRules.MaxReadRange, (await f.Call(chunk)).Batch.ManifestOffset);
        await f.Reopen(); Assert.Equal(0u, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch.ManifestOffset);
        chunk.Offset = StorageRules.MaxReadRange; Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(chunk)).Status);
        await f.Upload(h, manifest); Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, (await f.Call(f.Request(h, FileOperation.SealUploadBatch))).Batch.Phase);
        Assert.Single(f.Grants.BatchPlans.All); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Batch_and_standalone_admission_share_the_same_incoming_byte_quota(bool batchFirst)
    {
        await using var f = new Fixture(); var manifest = Plan(bytes: batchFirst ? UploadRules.PeerQuota : 1); var (h, target) = await f.Intent(manifest);
        var context = await f.Call(new() { Operation = FileOperation.UploadContext });
        var upload = new UploadInfo { Id = MutationRules.NewId(), Name = "standalone.bin", Size = batchFirst ? 1 : UploadRules.PeerQuota, Sha256 = ByteString.CopyFrom(new byte[32]) };
        var ordinary = new FileRequest { Operation = FileOperation.PrepareUpload, DestinationId = target, MutationEpoch = context.MutationEpoch, TransferId = upload.Id, Upload = upload };
        var batch = new FileRequest { Operation = FileOperation.BeginUploadBatch, DestinationId = target, MutationEpoch = h.Epoch, TransferId = h.Id, BatchHeader = h };
        await f.Call(batchFirst ? batch : ordinary); Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(batchFirst ? ordinary : batch)).Status);
        if (batchFirst) await f.Call(f.Request(h, FileOperation.CancelUploadBatch));
        else await f.Call(new() { Operation = FileOperation.CancelUpload, TransferId = upload.Id, MutationEpoch = context.MutationEpoch });
        await f.Call(batchFirst ? ordinary : batch);
        Assert.Equal(batchFirst ? 1 : 0, f.Grants.Uploads.All.Count(x => x.Phase != UploadPhase.UploadCanceled));
    }
    [Fact]
    public async Task Slot_limits_are_per_peer_and_global_and_foreign_receipts_are_unavailable()
    {
        await using var f = new Fixture(); var manifest = Plan(bytes: 0); UploadBatchHeader? first = null;
        foreach (var peer in new[] { Fixture.Peer, new string('b', 64) })
        {
            if (peer != Fixture.Peer) { var folder = Path.Combine(f.Root, "second"); Directory.CreateDirectory(folder); f.Grants.Add(peer, folder, StoragePermission.Contribute); }
            for (var index = 0; index < 4; index++) { var (h, target) = await f.Intent(manifest, peer); await f.Begin(h, target, peer); first ??= h; }
            var (extra, extraTarget) = await f.Intent(manifest, peer);
            Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(new() { Operation = FileOperation.BeginUploadBatch, TransferId = extra.Id, MutationEpoch = extra.Epoch, DestinationId = extraTarget, BatchHeader = extra }, peer)).Status);
        }
        Assert.Equal(8, f.Grants.BatchPlans.All.Count);
        Assert.Equal(FileStatus.NotFound, (await f.Raw(f.Request(first!, FileOperation.UploadBatchStatus), new string('b', 64))).Status);
        var third = new string('c', 64); var thirdFolder = Path.Combine(f.Root, "third"); Directory.CreateDirectory(thirdFolder); f.Grants.Add(third, thirdFolder, StoragePermission.Contribute);
        var (last, lastTarget) = await f.Intent(manifest, third);
        Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(new() { Operation = FileOperation.BeginUploadBatch, TransferId = last.Id, MutationEpoch = last.Epoch, DestinationId = lastTarget, BatchHeader = last }, third)).Status);
    }
    [Theory]
    [InlineData("permission")][InlineData("revoke")][InlineData("replacement")][InlineData("pairing")]
    public async Task Current_authority_and_destination_identity_are_required_for_retained_receipts(string change)
    {
        await using var f = new Fixture(owner: change == "pairing"); var (h, target) = await f.Intent(Plan()); await f.Begin(h, target);
        if (change == "permission") f.Grants.Update(f.Grant.Id, StoragePermission.ReadOnly, [], null, true, f.Grant.Label);
        if (change == "revoke") f.Grants.Remove(f.Grant.Id);
        if (change == "replacement") { Directory.Move(f.Destination, f.Destination + "-original"); Directory.CreateDirectory(f.Destination); }
        if (change == "pairing") f.Pairing = ProtocolRules.NewToken();
        await f.Reopen(); Assert.NotEqual(FileStatus.Ok, (await f.Raw(f.Request(h, FileOperation.UploadBatchStatus))).Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Fact]
    public async Task An_existing_retained_intent_survives_clock_expiry_but_a_missing_one_cannot_restart()
    {
        await using var f = new Fixture(); var manifest = Plan(); var (h, target) = await f.Intent(manifest); await f.Begin(h, target);
        f.Time.Now += TimeSpan.FromDays(2); var current = (await f.Call(new() { Operation = FileOperation.UploadContext })).Entries.Single();
        Assert.Equal(h, (await f.Begin(h, current.Id)).Batch.Header); await f.Upload(h, manifest); await f.Call(f.Request(h, FileOperation.SealUploadBatch));
        var another = h.Clone(); another.Id = MutationRules.NewId();
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(new() { Operation = FileOperation.BeginUploadBatch, TransferId = another.Id, MutationEpoch = another.Epoch, DestinationId = current.Id, BatchHeader = another })).Status);
    }
    [Fact]
    public async Task Excluded_or_tampered_manifest_never_becomes_ready_and_can_be_canceled()
    {
        await using var f = new Fixture(); var manifest = Plan(); manifest.Items[1].Name = ".ssh"; var (h, target) = await f.Intent(manifest); await f.Begin(h, target); await f.Upload(h, manifest);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(f.Request(h, FileOperation.SealUploadBatch))).Status);
        Assert.Equal(0u, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch.ManifestOffset);
        await f.Call(f.Request(h, FileOperation.CancelUploadBatch)); Assert.Equal(UploadBatchPlanPhase.UploadPlanCanceled, (await f.Begin(h, target)).Batch.Phase);
        var broken = Plan(); var (bad, _) = await f.Intent(broken); bad.Sha256 = ByteString.CopyFrom(new byte[32]); await f.Begin(bad, target); await f.Upload(bad, broken);
        Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(f.Request(bad, FileOperation.SealUploadBatch))).Status);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanReceiving, (await f.Call(f.Request(bad, FileOperation.UploadBatchStatus))).Batch.Phase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Fact]
    public async Task Corrupt_manifest_blocks_shared_admission_until_owner_archives_and_rotates_history()
    {
        await using var f = new Fixture(); var manifest = Plan(); var (h, target) = await f.Intent(manifest); await f.Begin(h, target); await f.Upload(h, manifest); await f.Call(f.Request(h, FileOperation.SealUploadBatch));
        var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".json"); var row = JsonNode.Parse(File.ReadAllText(path))!; row["Manifest"] = Convert.ToBase64String([1, 2, 3]); File.WriteAllText(path, row.ToJsonString());
        await f.Reopen(); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues); Assert.NotEqual(FileStatus.Ok, (await f.Raw(f.Request(h, FileOperation.UploadBatchStatus))).Status);
        var ordinaryContext = await f.Call(new() { Operation = FileOperation.UploadContext }); var upload = new UploadInfo { Id = MutationRules.NewId(), Name = "kept.txt", Size = 1, Sha256 = ByteString.CopyFrom(new byte[32]) };
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(new() { Operation = FileOperation.PrepareUpload, DestinationId = ordinaryContext.Entries.Single().Id, MutationEpoch = ordinaryContext.MutationEpoch, TransferId = upload.Id, Upload = upload })).Status);
        File.WriteAllText(Path.Combine(f.Destination, "owner.txt"), "kept"); f.Grants.BatchPlans.ArchiveHistory();
        Assert.Empty(f.Grants.BatchPlans.All); Assert.Equal(1, f.Grants.BatchPlans.PreservedRecords);
        Assert.NotEqual(h.Epoch, (await f.Call(new() { Operation = FileOperation.UploadBatchContext })).MutationEpoch);
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(f.Request(h, FileOperation.UploadBatchStatus))).Status);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(f.Destination, "owner.txt")));
        await f.Call(new() { Operation = FileOperation.PrepareUpload, DestinationId = ordinaryContext.Entries.Single().Id, MutationEpoch = ordinaryContext.MutationEpoch, TransferId = upload.Id, Upload = upload });
        f.Grants.BatchPlans.ClearPreservedRecords(); Assert.Equal(0, f.Grants.BatchPlans.PreservedRecords);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(f.Destination, "owner.txt")));
    }
    [Theory]
    [InlineData("header")][InlineData("destination")][InlineData("epoch")][InlineData("child-id")]
    public async Task Rebinding_or_minting_an_expired_child_as_a_parent_has_no_effect(string change)
    {
        await using var f = new Fixture(); var (h, target) = await f.Intent(Plan()); await f.Begin(h, target);
        var changed = h.Clone(); var destination = target; var expected = FileStatus.Conflict;
        if (change == "header") changed.TotalBytes++;
        if (change == "destination")
        {
            Directory.CreateDirectory(Path.Combine(f.Destination, "other"));
            destination = (await f.Call(new() { Operation = FileOperation.ListDirectory, NodeId = target })).Entries.Single().Id;
        }
        if (change == "epoch") { changed.Epoch = ProtocolRules.NewToken(); expected = FileStatus.StaleReference; }
        if (change == "child-id") { changed.Id = UploadBatchRules.ChildId(Fixture.Peer, h, 1, false); expected = FileStatus.StaleReference; }
        Assert.Equal(expected, (await f.Raw(new() { Operation = FileOperation.BeginUploadBatch, TransferId = changed.Id, MutationEpoch = changed.Epoch, DestinationId = destination, BatchHeader = changed })).Status);
        Assert.Single(f.Grants.BatchPlans.All); Assert.False(Directory.Exists(Path.Combine(f.Destination, "folder")));
    }
    [Fact]
    public async Task Failed_receipt_commit_blocks_writes_and_preserves_existing_destination_data()
    {
        await using var f = new Fixture(); var (h, target) = await f.Intent(Plan());
        var blocker = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".json.new"); Directory.CreateDirectory(blocker);
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(new() { Operation = FileOperation.BeginUploadBatch, TransferId = h.Id, MutationEpoch = h.Epoch, DestinationId = target, BatchHeader = h })).Status);
        Assert.Empty(f.Grants.BatchPlans.All); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
        Directory.Delete(blocker); f.Grants.BatchPlans.ArchiveHistory(); Assert.NotEqual(h.Epoch, (await f.Call(new() { Operation = FileOperation.UploadBatchContext })).MutationEpoch);
    }
    [Fact]
    public async Task Failed_seal_commit_recovers_the_original_intent_and_reuses_only_private_staging()
    {
        await using var f = new Fixture(); var manifest = Plan(); var (h, target) = await f.Intent(manifest); await f.Begin(h, target); await f.Upload(h, manifest);
        var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".json");
        using (var reader = File.OpenRead(path)) Assert.NotEqual(FileStatus.Ok, (await f.Raw(f.Request(h, FileOperation.SealUploadBatch))).Status);
        Assert.True(File.Exists(path + ".new")); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
        await f.Reopen(); Assert.Equal(UploadBatchPlanPhase.UploadPlanReceiving, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch.Phase);
        await f.Upload(h, manifest); Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, (await f.Call(f.Request(h, FileOperation.SealUploadBatch))).Batch.Phase);
        Assert.False(File.Exists(path + ".new")); Assert.Single(f.Grants.BatchPlans.All); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
}
