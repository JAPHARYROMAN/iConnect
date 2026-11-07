using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadSenderTests
{
    [Theory]
    [InlineData("match")]
    [InlineData("old-receipt")]
    [InlineData("challenge")]
    [InlineData("epoch")]
    [InlineData("name")]
    [InlineData("hash")]
    public async Task Published_check_requires_fresh_matching_receiver_evidence(string kind)
    {
        await using var f = new Fixture();
        await f.Initialize();
        File.WriteAllBytes(f.SourcePath, []);
        var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        await f.Sender.SendAsync(id, f.Target, f.Request);
        var saved = Assert.Single(f.Sender.All);
        Assert.Equal(SendingPhase.Completed, saved.Phase);
        var seen = new List<string>();
        async Task<FileResponse> Verify(FileRequest request, CancellationToken token)
        {
            Assert.Equal(FileOperation.VerifyPublishedUpload, request.Operation);
            Assert.Equal(saved.RemoteId, request.TransferId);
            Assert.Equal(saved.RemoteEpoch, request.MutationEpoch);
            Assert.Matches(ProtocolRules.TokenPattern(), request.MutationId);
            seen.Add(request.MutationId);
            var response = await f.Request(new()
            { Operation = FileOperation.UploadStatus, TransferId = request.TransferId, MutationEpoch = request.MutationEpoch }, token);
            if (kind == "old-receipt") return response;
            response.PublishedUploadCheck = new()
            { Challenge = kind == "challenge" ? ProtocolRules.NewToken() : request.MutationId, Verified = seen.Count > 1 };
            if (kind == "epoch") response.MutationEpoch = ProtocolRules.NewToken();
            if (kind == "name") response.Upload.FinalName = "different.bin";
            if (kind == "hash") response.Upload.Sha256 = ByteString.CopyFrom(new byte[32]);
            return response;
        }
        if (kind == "match")
        {
            await f.Sender.VerifyPublishedAsync(id, Verify);
            Assert.Equal(2, seen.Count);
            Assert.Single(seen.Distinct());
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => f.Sender.VerifyPublishedAsync(id, Verify));
        Assert.Equal(saved, Assert.Single(f.Sender.All));
        Assert.True(File.Exists(f.SourcePath));
        Assert.True(File.Exists(Path.Combine(f.Destination, saved.FinalName)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static string Allocate()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
            return Path.Combine(root!.FullName, ".local", "sender-tests", ProtocolRules.NewToken());
        }
        public readonly string Root = Allocate();
        public string SourcePath => Path.Combine(Root, "original.bin");
        public string SenderPath => Path.Combine(Root, "sender");
        public string Destination => Path.Combine(Root, "received");
        public readonly UploadAuthority Authority = new(new string('a', 64), ProtocolRules.NewToken(), new string('b', 64), ProtocolRules.NewToken());
        public bool Current = true;
        public WindowsUploadSender Sender;
        public readonly StorageGrantStore Grants;
        public readonly WindowsFileService Host;
        public FileEntry Target = null!;
        public readonly byte[] Bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        public Fixture()
        {
            Directory.CreateDirectory(Destination); File.WriteAllBytes(SourcePath, Bytes);
            Sender = new(SenderPath, authority => Current && authority == Authority);
            Grants = new(Path.Combine(Root, "receiver")); Grants.Add(Authority.Owner, Destination, StoragePermission.Write | StoragePermission.Create); Host = new(Grants);
        }
        public async Task Initialize() => Target = Assert.Single((await Request(new() { Operation = FileOperation.UploadContext }, default)).Entries);
        public async Task<FileResponse> Request(FileRequest value, CancellationToken ct)
        { var copy = value.Clone(); copy.RequestId = ProtocolRules.NewToken(); return await Host.ExecuteAsync(Authority.Owner, copy, ct); }
        public async ValueTask DisposeAsync() { await Sender.DisposeAsync(); await Grants.Uploads.WhenIdleAsync(); Host.Dispose(); Directory.Delete(Root, true); }
    }
    [Fact]
    public async Task Private_snapshot_resumes_lost_range_reply_after_restart_and_keeps_original_source()
    {
        await using var f = new Fixture(); await f.Initialize();
        var progress = new List<OutgoingCopy>();
        var refreshes = new List<OutgoingCopy>();
        void Observe() => progress.Add(Assert.Single(f.Sender.All));
        void Refresh() => refreshes.Add(Assert.Single(f.Sender.All));
        f.Sender.Changed += Observe;
        f.Sender.MetadataChanged += Refresh;
        var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        Assert.Contains(progress, x => x.Phase == SendingPhase.Preparing && x.Offset == 1024 * 1024);
        Assert.DoesNotContain(refreshes, x => x.Phase == SendingPhase.Preparing && x.Offset > 0);
        Assert.Contains(refreshes, x => x.Phase == SendingPhase.Ready);
        var lost = true;
        await f.Sender.SendAsync(id, f.Target, async (value, token) =>
        { var reply = await f.Request(value, token); if (value.Operation == FileOperation.UploadWrite && lost) { lost = false; throw new IOException("Lost accepted-range acknowledgement fixture."); } return reply; });
        Assert.Equal(SendingPhase.Review, f.Sender.All.Single().Phase); Assert.Equal(0, f.Sender.All.Single().Offset);
        Assert.Equal(StorageRules.MaxReadRange, f.Grants.Uploads.All.Single().Offset);
        var remote = f.Sender.All.Single().RemoteId;
        await f.Sender.DisposeAsync(); f.Sender = new(f.SenderPath, authority => f.Current && authority == f.Authority);
        progress.Clear(); refreshes.Clear();
        f.Sender.Changed += Observe;
        f.Sender.MetadataChanged += Refresh;
        await f.Sender.SendAsync(id, null, f.Request);
        const int range = StorageRules.MaxReadRange;
        Assert.Contains(progress, x => x.Phase == SendingPhase.Sending && x.Offset == 2 * range);
        Assert.DoesNotContain(refreshes, x => x.Phase == SendingPhase.Sending && x.Offset == 2 * range);
        Assert.Contains(refreshes, x => x.Phase == SendingPhase.Sending && x.Offset == range);
        Assert.Contains(refreshes, x => x.Phase == SendingPhase.Sending && x.Offset == x.Size);
        Assert.Contains(refreshes, x => x.Phase == SendingPhase.Completed);
        var completed = f.Sender.All.Single(); Assert.Equal(SendingPhase.Completed, completed.Phase); Assert.Equal(remote, completed.RemoteId);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, completed.FinalName))); Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath));
        f.Sender.Changed -= Observe;
        f.Sender.MetadataChanged -= Refresh;
        await f.Sender.RemoveAsync(id); Assert.Empty(f.Sender.All); Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath)); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Revoked_pairing_or_a_changed_private_snapshot_cannot_start_an_outgoing_request()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        f.Current = false; await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendAsync(id, f.Target, f.Request)); f.Current = true;
        using (var file = new FileStream(Path.Combine(f.SenderPath, "outgoing-uploads", id + ".bin"), FileMode.Open, FileAccess.Write)) file.WriteByte((byte)(f.Bytes[0] ^ 1));
        var calls = 0;
        await f.Sender.SendAsync(id, f.Target, (request, ct) => { calls++; return f.Request(request, ct); });
        Assert.Equal(0, calls); Assert.Equal(SendingPhase.Review, f.Sender.All.Single().Phase); Assert.Empty(Directory.GetFiles(f.Destination));
        await f.Sender.RemoveAsync(id); Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath));
    }
    [Fact]
    public async Task Receiver_descriptor_substitution_stops_before_any_content_is_sent()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority); var writes = 0;
        await f.Sender.SendAsync(id, f.Target, async (value, token) =>
        { var reply = await f.Request(value, token); if (value.Operation == FileOperation.PrepareUpload) reply.Upload.Name = "substituted.bin"; if (value.Operation == FileOperation.UploadWrite) writes++; return reply; });
        Assert.Equal(0, writes); Assert.Equal(SendingPhase.Review, f.Sender.All.Single().Phase); Assert.False(File.Exists(Path.Combine(f.Destination, "original.bin")));
        await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(SendingPhase.Completed, f.Sender.All.Single().Phase);
    }
    [Fact]
    public async Task Deliberate_another_copy_uses_new_receipt_and_orphan_cleanup_never_touches_originals()
    {
        await using var f = new Fixture(); await f.Initialize(); File.WriteAllBytes(f.SourcePath, []);
        var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority); f.Sender.Rename(id, "empty.txt");
        await f.Sender.SendAsync(id, f.Target, f.Request); var first = f.Sender.All.Single().RemoteId;
        f.Sender.AnotherCopy(id); await f.Sender.SendAsync(id, f.Target, f.Request);
        Assert.NotEqual(first, f.Sender.All.Single().RemoteId); Assert.Equal(2, Directory.GetFiles(f.Destination).Length);
        var orphan = Path.Combine(f.SenderPath, "outgoing-uploads", ProtocolRules.NewToken() + ".bin"); File.WriteAllText(orphan, "owned orphan fixture");
        Assert.Equal(1, f.Sender.Orphans); f.Sender.RemoveOrphans(); Assert.False(File.Exists(orphan)); Assert.True(File.Exists(f.SourcePath));
        Assert.True(File.Exists(Path.Combine(f.SenderPath, "outgoing-uploads", id + ".bin")));
    }
    [Fact]
    public void Legacy_trust_scope_migrates_without_new_grants_and_repair_gets_a_distinct_scope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iconnect-upload-trust", ProtocolRules.NewToken());
        using var owner = DeviceIdentity.Ephemeral("Owner"); using var remote = DeviceIdentity.Ephemeral("Peer");
        using var trust = new TrustedPeerStore(directory, owner.Id); var peer = new ProofClient.TrustedPeer(remote.Id, "Peer", Convert.ToBase64String(remote.PublicKey)); trust.Add(peer);
        trust.InitializeUploadScopes(); var first = trust.EnsureUploadScope(remote.Id); trust.InitializeUploadScopes(); Assert.Equal(first, trust.EnsureUploadScope(remote.Id));
        trust.Remove(remote.Id); trust.Add(peer); Assert.NotEqual(first, trust.EnsureUploadScope(remote.Id));
    }
}
