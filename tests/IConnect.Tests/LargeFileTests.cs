using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace IConnect.Tests;

public sealed class LargeFileTests(ITestOutputHelper output)
{
    private const long SignedBoundary = 1L << 31;
    private const long UnsignedBoundary = 1L << 32;
    private const long Size = UnsignedBoundary + 8 * StorageRules.MaxReadRange + 17;
    private const string Name = "boundary-file.bin";

    private sealed class Accepted : IAttachmentChecks
    {
        public Task CheckAsync(string path, string identity, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string fixtureRoot;
        public string Root { get; }
        public string SourceDirectory => Path.Combine(Root, "source");
        public string SourcePath => Path.Combine(SourceDirectory, Name);
        public string Destination => Path.Combine(Root, "received");
        public string ReceiverProfile => Path.Combine(Root, "receiver");
        public string SenderProfile => Path.Combine(Root, "sender");
        public readonly UploadAuthority Authority = new(new string('a', 64), ProtocolRules.NewToken(), new string('b', 64), ProtocolRules.NewToken());
        public readonly StorageGrantStore Grants;
        public readonly DownloadSourceStore Sources;
        public readonly WindowsFileService Host;
        private readonly string sourceGrant;
        public string Hash { get; private set; } = "";

        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            fixtureRoot = Path.Combine(repo!.FullName, ".local", "large-file-tests");
            Root = Path.Combine(fixtureRoot, ProtocolRules.NewToken());
            Directory.CreateDirectory(SourceDirectory); Directory.CreateDirectory(Destination);
            var free = new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
            Assert.True(free > 3 * Size + 1024L * 1024 * 1024, "The large-file fixture requires room for its source, private snapshot and receiving file.");
            var hostProfile = Path.Combine(Root, "host");
            Grants = new(hostProfile) { AttachmentChecks = new Accepted() };
            sourceGrant = Grants.Add(Authority.Owner, SourceDirectory).Id;
            Grants.Add(Authority.Owner, Destination, StoragePermission.Write | StoragePermission.Create);
            Sources = new(Grants, hostProfile); Host = new(Grants, Sources);
        }

        public async Task WriteSourceAsync(CancellationToken ct)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = RandomNumberGenerator.GetBytes(1024 * 1024);
            await using var file = new FileStream(SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (long offset = 0; offset < Size; offset += buffer.Length)
            {
                // Distinct block positions make a wrapped offset change the expected whole-file hash.
                BinaryPrimitives.WriteInt64LittleEndian(buffer, offset);
                var length = (int)Math.Min(buffer.Length, Size - offset);
                await file.WriteAsync(buffer.AsMemory(0, length), ct);
                digest.AppendData(buffer, 0, length);
            }
            file.Flush(true);
            Hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            Assert.Equal(Size, file.Length);
        }

        public Task<FileResponse> Request(FileRequest value, CancellationToken ct)
        {
            var request = value.Clone(); request.RequestId = ProtocolRules.NewToken();
            return Host.ExecuteAsync(Authority.Owner, request, ct);
        }

        public async Task<FileEntry> DownloadEntry(CancellationToken ct)
        {
            var root = (await Request(new() { Operation = FileOperation.ListRoots }, ct)).Entries.Single(x => x.GrantId == sourceGrant);
            return (await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, ct)).Entries.Single();
        }

        public void VerifyPublished(string name)
        {
            Assert.Single(Directory.EnumerateFiles(Destination));
            foreach (var path in new[] { SourcePath, Path.Combine(Destination, name) })
            {
                Assert.Equal(Size, new FileInfo(path).Length);
                using var file = File.OpenRead(path);
                Assert.Equal(Hash, Convert.ToHexStringLower(SHA256.HashData(file)));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Host.Dispose(); await Sources.DisposeAsync(); await Grants.Uploads.WhenIdleAsync();
            var expected = Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
                !UploadBatchRules.CanonicalToken(Path.GetFileName(Root))) throw new IOException("Large-file cleanup escaped its owned fixture directory.");
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task Download_over_4_GiB_reopens_its_64_bit_checkpoint_and_verifies_the_published_file()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        await using var f = new Fixture(); await f.WriteSourceAsync(ct);
        output.WriteLine($"Generated {Size} bytes with SHA-256 {f.Hash}.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Withhold(FileRequest request, CancellationToken token)
        {
            ct.ThrowIfCancellationRequested();
            var response = await f.Request(request, token);
            if (request.Operation == FileOperation.DownloadRead && request.Offset >= UnsignedBoundary)
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return response;
        }

        string id, ticket;
        await using (var first = new WindowsDownloadManager(f.ReceiverProfile, new Accepted()))
        {
            id = first.Start(f.Authority.Peer, await f.DownloadEntry(ct), f.Destination, DownloadCollision.KeepBoth, Withhold);
            await Task.WhenAny(entered.Task, first.WaitAsync(id)).WaitAsync(ct);
            Assert.True(entered.Task.IsCompletedSuccessfully, Assert.Single(first.Jobs).Error);
            first.Pause(id); await first.WaitAsync(id).WaitAsync(ct);
            var paused = Assert.Single(first.Jobs);
            Assert.Equal(DownloadPhase.Paused, paused.Phase); Assert.Equal(UnsignedBoundary, paused.Offset);
            ticket = paused.Ticket;
            output.WriteLine($"Paused with durable checkpoint {paused.Offset}.");
        }

        await using var reopened = new WindowsDownloadManager(f.ReceiverProfile, new Accepted());
        Assert.Empty(reopened.RecoveryIssues); Assert.Equal(UnsignedBoundary, Assert.Single(reopened.Jobs).Offset);
        await reopened.Resume(id, f.Request).WaitAsync(ct);
        var completed = Assert.Single(reopened.Jobs);
        Assert.True(completed.Phase == DownloadPhase.Completed, completed.Phase + ": " + completed.Error);
        Assert.Equal(Size, completed.Offset); Assert.Equal(f.Hash, completed.Hash); Assert.Equal(ticket, completed.Ticket);
        f.VerifyPublished(completed.FinalName);
        output.WriteLine("The original and the single published download match the generated SHA-256 after reopen and resume.");
    }

    [Fact]
    public async Task Upload_over_4_GiB_reconciles_a_lost_range_reply_after_restart_without_an_extra_copy()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        await using var f = new Fixture(); await f.WriteSourceAsync(ct);
        output.WriteLine($"Generated {Size} bytes with SHA-256 {f.Hash}.");
        var target = (await f.Request(new() { Operation = FileOperation.UploadContext }, ct)).Entries.Single();
        string id, remote;
        await using (var first = new WindowsUploadSender(f.SenderProfile, authority => authority == f.Authority))
        {
            id = await first.PrepareAsync(f.SourcePath, f.Authority, ct);
            Assert.Equal(Size, Assert.Single(first.All).Size); Assert.Equal(f.Hash, Assert.Single(first.All).Hash);
            var lost = false;
            async Task<FileResponse> LoseAcceptedReply(FileRequest request, CancellationToken token)
            {
                var response = await f.Request(request, token);
                if (request.Operation == FileOperation.UploadWrite && request.Offset == UnsignedBoundary)
                { lost = true; throw new IOException("Lost acknowledged range beyond 4 GiB."); }
                return response;
            }
            await first.SendAsync(id, target, LoseAcceptedReply, ct).WaitAsync(ct);
            Assert.True(lost, Assert.Single(first.All).Message);
            var paused = Assert.Single(first.All);
            Assert.Equal(SendingPhase.Review, paused.Phase); Assert.Equal(UnsignedBoundary, paused.Offset);
            Assert.Equal(UnsignedBoundary + StorageRules.MaxReadRange, Assert.Single(f.Grants.Uploads.All).Offset);
            remote = paused.RemoteId;
            output.WriteLine($"Sender checkpoint {paused.Offset}; receiver checkpoint {UnsignedBoundary + StorageRules.MaxReadRange}.");
        }

        await using var reopened = new WindowsUploadSender(f.SenderProfile, authority => authority == f.Authority);
        Assert.Empty(reopened.RecoveryIssues); Assert.Equal(UnsignedBoundary, Assert.Single(reopened.All).Offset);
        await reopened.SendAsync(id, null, f.Request, ct).WaitAsync(ct);
        var completed = Assert.Single(reopened.All);
        Assert.True(completed.Phase == SendingPhase.Completed, completed.Phase + ": " + completed.Message);
        Assert.Equal(Size, completed.Offset); Assert.Equal(remote, completed.RemoteId);
        Assert.Equal(UploadPhase.UploadCompleted, Assert.Single(f.Grants.Uploads.All).Phase);
        f.VerifyPublished(completed.FinalName);
        output.WriteLine("The original and the single published upload match the generated SHA-256 after receipt reconciliation.");
    }

    [Fact]
    public async Task Offline_copy_over_4_GiB_resumes_after_the_signed_32_bit_boundary_and_verifies_the_original()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        await using var f = new Fixture(); await f.WriteSourceAsync(ct);
        output.WriteLine($"Generated {Size} bytes with SHA-256 {f.Hash}.");
        LocalCopySelection selection;
        using (var locations = new LocalFileLocations(f.ReceiverProfile))
        {
            locations.Add(f.SourceDirectory);
            var root = (await locations.RequestAsync(new() { Operation = FileOperation.ListRoots }, ct)).Entries.Single();
            var file = (await locations.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, ct)).Entries.Single();
            selection = await locations.CaptureCopyAsync(file, ct);
        }
        string id;
        long checkpoint;
        await using (var first = new WindowsLocalCopies(f.ReceiverProfile, new Accepted()))
        {
            id = first.Prepare(selection);
            first.PayloadChanged += row =>
            {
                if (row.Phase == DownloadPhase.Downloading && row.Offset >= SignedBoundary) first.Pause(id);
            };
            first.ChooseDestination(id, f.Destination, DownloadCollision.KeepBoth);
            await first.WaitAsync(id).WaitAsync(ct);
            var paused = Assert.Single(first.Jobs).Transfer!;
            Assert.Equal(DownloadPhase.Paused, paused.Phase); Assert.InRange(paused.Offset, SignedBoundary, Size);
            // Throttled progress notifications can place Pause beyond the requested boundary.
            checkpoint = paused.Offset;
            output.WriteLine($"Paused offline copy with checkpoint {paused.Offset}.");
        }

        await using var reopened = new WindowsLocalCopies(f.ReceiverProfile, new Accepted());
        Assert.Equal(checkpoint, Assert.Single(reopened.Jobs).Transfer!.Offset);
        await reopened.Resume(id).WaitAsync(ct);
        var completed = Assert.Single(reopened.Jobs).Transfer!;
        Assert.True(completed.Phase == DownloadPhase.Completed, completed.Phase + ": " + completed.Error);
        Assert.Equal(Size, completed.Offset); Assert.Equal(f.Hash, completed.Hash); Assert.Empty(completed.PeerId);
        f.VerifyPublished(completed.FinalName);
        output.WriteLine("The original and the single published offline copy match the generated SHA-256 after reopen and resume.");
    }
}
