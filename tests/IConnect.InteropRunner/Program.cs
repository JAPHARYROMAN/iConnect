using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

// Test-only fixture. Approvals still pass through the production client's signed
// approval method; stdin supplies the code reported by the Android test APK.
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var identity = DeviceIdentity.Ephemeral("Windows interoperability fixture");
var origin = new Uri("http://localhost:5078");
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
var directory = Path.Combine(repository!.FullName, ".local", "interop-fixtures", ProtocolRules.NewToken());
var documents = Path.Combine(directory, "documents");
Directory.CreateDirectory(documents);
PreviewFixtures.Write(Path.Combine(documents,"Preview fixture"));
Directory.CreateDirectory(Path.Combine(documents, "Changes fixture"));
File.WriteAllText(Path.Combine(documents, "Changes fixture", "original.txt"), "Manage this approved fixture.\n");
File.WriteAllText(Path.Combine(documents, "ripoti.txt"), "iConnect approved-file preview.\nHabari!\n");
File.WriteAllBytes(Path.Combine(documents, "jaribio.bin"), Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(x => (byte)(x * 31 % 251)).ToArray());
File.WriteAllBytes(Path.Combine(documents, "background.bin"), Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(x => (byte)(x * 31 % 251)).ToArray());
Directory.CreateDirectory(Path.Combine(documents, "Folder fixture", "Empty"));
Directory.CreateDirectory(Path.Combine(documents, "Folder fixture", "Reports"));
File.WriteAllText(Path.Combine(documents, "Folder fixture", "Reports", "notes.txt"), "iConnect nested batch document.\nSalama!\n");
File.WriteAllBytes(Path.Combine(documents, "Folder fixture", "background.bin"), Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(x => (byte)(x * 31 % 251)).ToArray());
Directory.CreateDirectory(Path.Combine(documents, "Explorer fixture", "Nested"));
File.WriteAllText(Path.Combine(documents, "Explorer fixture", "Nested", "Habari-文書.txt"), "Scoped explorer fixture.\n");
for (var i = 0; i < 110; i++) File.WriteAllText(Path.Combine(documents, "Explorer fixture", $"item-{i:D3}.txt"), new string('x', i + 1));
var backgroundGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var state = Path.Combine(directory, "owner");
var binding = new WindowsOwnerBinding(state, identity.Id);
var grants = new StorageGrantStore(state, ownerBinding: binding);
await using var sourceDownloads = new DownloadSourceStore(grants, state);
var connection = await SignalConnection.ConnectAsync(origin, identity, timeout.Token);
await using var client = new ProofClient(identity, connection, origin, state, Console.WriteLine,
    fileServiceFactory: _ => new GatedFixtureFiles(new WindowsFileService(grants, sourceDownloads), backgroundGate.Task));
await using var sending = new PhoneSendingFixture(client, directory);
client.InvitationCreated += value => Console.WriteLine("INVITATION " + value);
client.PairingRequested += value => Console.WriteLine("CODE " + ProtocolRules.VerificationCode(value));
client.Paired += peer =>
{
    grants.Add(peer.Id, documents, StoragePermission.ReadOnly | StoragePermission.Create | StoragePermission.Rename | StoragePermission.Move | StoragePermission.Delete, label: "Interop documents");
    Console.WriteLine("PEER " + peer.Id);
};
var loop = client.RunAsync(timeout.Token);
await client.CreateInvitationAsync(timeout.Token);
try
{
    while (!timeout.IsCancellationRequested && !loop.IsCompleted)
    {
        var input = Console.In.ReadLineAsync(timeout.Token).AsTask();
        if (await Task.WhenAny(input, loop) == loop) break;
        var line = await input;
        if (line is null) break;
        var command = line.Split(' ', 2);
        switch (command[0])
        {
            case "approve": await client.ApproveAsync(command[1], timeout.Token); break;
            case "connect": await client.ConnectPeerAsync(client.Peers.Single().Id, timeout.Token); break;
            case "send": await client.SendTestFileAsync(timeout.Token); break;
            case "release-background": backgroundGate.TrySetResult(); break;
            case "start-phone-send": await sending.Start(timeout.Token); break;
            case "release-sending": sending.Release(); break;
            case "sending-denied": await sending.AssertOtherTransfersDenied(timeout.Token); break;
            case "resume-phone-send": await sending.Resume(timeout.Token); break;
            case "phone-denied":
            {
                var denied = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                if (denied.Status != FileStatus.AccessDenied || denied.Entries.Count != 0 || denied.Content.Length != 0)
                    throw new InvalidDataException("Android exposed unrelated storage while its UI was inactive.");
                Console.WriteLine("BACKGROUND_BROWSING_DENIED"); break;
            }
            case "phone":
            {
                var roots = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
                var listing = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, timeout.Token);
                var file = listing.Entries.Single(x => x.Name == "simu.txt");
                var read = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ReadRange, NodeId = file.Id,
                    ExpectedVersion = file.Version, Length = StorageRules.MaxReadRange }, timeout.Token);
                if (read.Status != FileStatus.Ok || read.Content.ToStringUtf8() != "iConnect approved phone document.\nSalama!\n")
                    throw new InvalidDataException("Phone document preview did not match the actual fixture.");
                var denied = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.CreateDirectory, DestinationId = root.Id,
                    Name = "not-authorized" }, timeout.Token);
                if (denied.Status != FileStatus.AccessDenied) throw new InvalidDataException("The read-only Android grant permitted a mutation.");
                Console.WriteLine("PHONE_BROWSING_VERIFIED");
                break;
            }
            case "create-phone-folder":
            {
                var roots = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
                var context = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.MutationContext }, timeout.Token);
                var change = new FileRequest { Operation = FileOperation.CreateDirectory, DestinationId = root.Id, Name = "From Windows",
                    MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch };
                var first = await client.RequestFilesAsync(change, timeout.Token);
                var replay = await client.RequestFilesAsync(change, timeout.Token);
                var history = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListMutations, MutationEpoch = context.MutationEpoch }, timeout.Token);
                var listing = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, timeout.Token);
                if (first.Status != FileStatus.Ok || replay.Status != FileStatus.Ok || replay.Entries.Count != 0 ||
                    replay.Mutation?.Id != change.MutationId || replay.Mutation.Phase != MutationPhase.ChangeSucceeded ||
                    !history.Mutations.Any(x => x.Id == change.MutationId) || listing.Entries.Count(x => x.Name == "From Windows") != 1)
                    throw new InvalidDataException("Android did not preserve one durable folder creation across a repeated request.");
                Console.WriteLine("PHONE_FOLDER_CHANGE_VERIFIED"); break;
            }
            case "download-phone":
            {
                var roots = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
                var listing = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, timeout.Token);
                var file = listing.Entries.Single(x => x.Name == "simu.txt");
                var destination = Path.Combine(directory, "received"); Directory.CreateDirectory(destination);
                var pinned = client.CaptureFileRequester(client.Peers.Single().Id);
                await using var receiver = new WindowsDownloadManager(Path.Combine(directory, "receiver"));
                var id = receiver.Start(client.Peers.Single().Id, file, destination, DownloadCollision.KeepBoth, (request, ct) => pinned(request, ct));
                await receiver.WaitAsync(id).WaitAsync(timeout.Token);
                var job = receiver.Jobs.Single();
                if (job.Phase != DownloadPhase.Completed || File.ReadAllText(Path.Combine(destination, job.FinalName)) != "iConnect approved phone document.\nSalama!\n")
                    throw new InvalidDataException("Actual Android file download failed: " + job.Error);
                Console.WriteLine("REAL_PHONE_DOWNLOAD_VERIFIED " + job.Hash);
                break;
            }
            case "download-phone-folder":
            {
                var pinned = client.CaptureFileRequester(client.Peers.Single().Id);
                async Task<FileResponse> Request(FileRequest value, CancellationToken ct) => await pinned(value, ct);
                var roots = await Request(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
                var manifest = await TransferManifests.BuildAsync([new(root, [])], Request, timeout.Token);
                var destination = Path.Combine(directory, "received-batch"); Directory.CreateDirectory(destination);
                await using var receiver = new WindowsDownloadManager(Path.Combine(directory, "batch-receiver"));
                await using var batches = new WindowsBatchDownloads(Path.Combine(directory, "batch-receiver"), receiver);
                var id = batches.Start(client.Peers.Single().Id, manifest, destination, DownloadCollision.KeepBoth, Request);
                await batches.WaitAsync(id).WaitAsync(timeout.Token);
                var job = batches.Jobs.Single();
                if (job.Phase != BatchPhase.Completed ||
                    File.ReadAllText(Path.Combine(destination, root.Name, "simu.txt")) != "iConnect approved phone document.\nSalama!\n" ||
                    File.ReadAllText(Path.Combine(destination, root.Name, "Reports", "notes.txt")) != "nested fixture")
                    throw new InvalidDataException("Actual Android folder copy failed: " + job.Error);
                if (!Directory.Exists(Path.Combine(destination, root.Name, "Empty")) || Directory.EnumerateFileSystemEntries(Path.Combine(destination, root.Name, "Empty")).Any() ||
                    !File.ReadAllBytes(Path.Combine(destination, root.Name, "jaribio.bin")).SequenceEqual(Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(x => (byte)(x * 31 % 251))))
                    throw new InvalidDataException("The phone batch did not preserve empty directories or the large file.");
                Console.WriteLine("REAL_PHONE_FOLDER_VERIFIED " + job.Completed + " items " + job.VerifiedBytes + " bytes");
                break;
            }
            case "explore-phone":
            {
                var pinned = client.CaptureFileRequester(client.Peers.Single().Id);
                Task<FileResponse> Request(FileRequest value, CancellationToken ct) => pinned(value, ct);
                var roots = await Request(new FileRequest { Operation = FileOperation.ListRoots }, timeout.Token);
                var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
                var result = await ExplorerCatalog.SearchAsync([root], "NOTES", Request, timeout.Token);
                if (!result.Complete || result.Rows.Length != 1 || result.Rows[0].RelativePath != "Reports / notes.txt") throw new InvalidDataException("Actual Android scoped search failed: " + result.Notice);
                Console.WriteLine("PHONE_EXPLORER_VERIFIED"); break;
            }
            case "quit": timeout.Cancel(); break;
        }
        if (timeout.IsCancellationRequested) break;
    }
}

finally
{
    timeout.Cancel();
    try { await loop; } catch (OperationCanceledException) { }
}

// Only disposable test data uses this gate, so lifecycle behavior is deterministic.
sealed class GatedFixtureFiles(IFileService inner, Task backgroundGate) : IFileService
{
    private readonly HashSet<string> held = [];
    public async Task<FileResponse> ExecuteAsync(string peerId, FileRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == FileOperation.DownloadRead && held.Contains(request.TransferId))
            await backgroundGate.WaitAsync(cancellationToken);
        var result = await inner.ExecuteAsync(peerId, request, cancellationToken);
        if (request.Operation == FileOperation.PrepareDownload && result.Download?.Name == "background.bin") held.Add(result.Download.Id);
        return result;
    }
    public void Dispose() => inner.Dispose();
}

sealed class PhoneSendingFixture : IAsyncDisposable
{
    private readonly ProofClient client;
    private readonly string destination;
    private readonly WindowsDownloadManager manager;
    private TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task monitor = Task.CompletedTask;
    private string current = "";
    private string unselected = "";
    private FileEntry? otherFile;
    public PhoneSendingFixture(ProofClient client, string directory)
    {
        this.client = client; destination = Path.Combine(directory, "background-received"); Directory.CreateDirectory(destination);
        manager = new WindowsDownloadManager(Path.Combine(directory, "background-receiver"));
    }
    public async Task Start(CancellationToken ct)
    {
        if (!monitor.IsCompleted) throw new InvalidOperationException("Finish the previous fixture transfer first.");
        var peer = client.Peers.Single().Id;
        var rpc = client.CaptureFileRequester(peer);
        var roots = await rpc(new FileRequest { Operation = FileOperation.ListRoots }, ct);
        var root = roots.Entries.Single(x => x.Name == "Interop phone documents");
        var listing = await rpc(new FileRequest { Operation = FileOperation.ListDirectory, NodeId = root.Id }, ct);
        var file = listing.Entries.Single(x => x.Name == "kutuma.bin");
        otherFile = listing.Entries.Single(x => x.Name == "simu.txt");
        unselected = (await rpc(new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = otherFile.Id, ExpectedVersion = otherFile.Version }, ct)).Download.Id;
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Held(FileRequest request, CancellationToken token)
        {
            if (request.Operation == FileOperation.DownloadRead) await gate.Task.WaitAsync(token);
            return await rpc(request, token);
        }
        current = manager.Start(peer, file, destination, DownloadCollision.KeepBoth, Held);
        monitor = Observe(current, ct);
        Console.WriteLine("PHONE_SEND_STARTED");
    }
    public void Release() => gate.TrySetResult();
    public async Task AssertOtherTransfersDenied(CancellationToken ct)
    {
        foreach (var request in new[] {
            new FileRequest { Operation = FileOperation.DownloadStatus, TransferId = unselected },
            new FileRequest { Operation = FileOperation.DownloadRead, TransferId = unselected, Length = 1 },
            new FileRequest { Operation = FileOperation.ReadRange, NodeId = otherFile!.Id, ExpectedVersion = otherFile.Version, Length = 1 },
            new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = otherFile.Id, ExpectedVersion = otherFile.Version } })
        {
            var response = await client.RequestFilesAsync(request, ct);
            if (response.Status != FileStatus.AccessDenied || response.Content.Length != 0 || response.Entries.Count != 0 || response.Download is not null)
                throw new InvalidDataException("An unrelated background source request escaped its ticket boundary.");
        }
        Console.WriteLine("UNSELECTED_SENDING_DENIED");
    }
    public async Task Resume(CancellationToken ct)
    {
        if (!monitor.IsCompleted) throw new InvalidOperationException("Release the old fixture request before resuming.");
        var rpc = client.CaptureFileRequester(client.Peers.Single().Id);
        _ = manager.Resume(current, (request, token) => rpc(request, token));
        monitor = Observe(current, ct);
        await monitor;
    }
    private async Task Observe(string id, CancellationToken ct)
    {
        await manager.WaitAsync(id).WaitAsync(ct);
        var job = manager.Jobs.Single(x => x.Id == id);
        if (job.Phase == DownloadPhase.Completed)
        {
            var expected = Enumerable.Range(0, 2 * 1024 * 1024 + 17).Select(x => (byte)(x * 31 % 251)).ToArray();
            if (!File.ReadAllBytes(Path.Combine(destination, job.FinalName)).SequenceEqual(expected)) throw new InvalidDataException("Phone source bytes did not match.");
            Console.WriteLine("REAL_BACKGROUND_PHONE_SEND_VERIFIED " + job.Hash);
        }
        else Console.WriteLine("PHONE_SEND_INTERRUPTED " + job.Phase);
    }
    public async ValueTask DisposeAsync() { gate.TrySetResult(); await manager.DisposeAsync(); }
}
