using IConnect.AppModel;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text;

// Separate, test-only native-UI fixture. It can create trust only within its
// own newly allocated repository .local directory; no app auto-approval mode.
if (!OperatingSystem.IsWindows()) return 1;
var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
var directory = Path.Combine(repo!.FullName, ".local", "desktop-fixtures", ProtocolRules.NewToken());
var state = Path.Combine(directory, "desktop");
var documents = Path.Combine(directory, "phone-documents"); Directory.CreateDirectory(documents);
File.WriteAllText(Path.Combine(documents, "simu.txt"), "iConnect native desktop preview.\nHabari kutoka kwenye simu!\n");
Directory.CreateDirectory(Path.Combine(documents, "Reports"));
File.WriteAllText(Path.Combine(documents, "Reports", "notes.txt"), "Nested fixture document.");
if (args.Contains("--media", StringComparer.Ordinal)) PreviewFixtures.Write(documents);
if (args.Contains("--explorer", StringComparer.Ordinal))
{
    for (var i = 0; i < 110; i++) File.WriteAllText(Path.Combine(documents, $"page-{i:D3}.txt"), new string('x', i + 1));
    File.WriteAllText(Path.Combine(documents, "Reports", "Habari-文書.txt"), "Scoped Unicode filename fixture.");
}
if (args.Contains("--local-only", StringComparer.Ordinal))
{
    if (args.Contains("--seed-batch-plan", StringComparer.Ordinal))
    {
        var localKey = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        using var owner = DeviceIdentity.OpenWindows(localKey, "Offline batch-plan UI fixture");
        using var sender = DeviceIdentity.Ephemeral("Offline batch sender fixture");
        using var trust = new TrustedPeerStore(state, owner.Id); trust.Add(new(sender.Id, sender.Descriptor.Name, Convert.ToBase64String(sender.PublicKey)));
        var pairing = trust.EnsureUploadScope(sender.Id); var approvals = new StorageGrantStore(state, ownerBinding: trust.OwnerBinding);
        var grant = approvals.Add(sender.Id, documents, StoragePermission.Contribute);
        using var files = new WindowsFileService(approvals); files.BindOwnerPolicy(trust.OwnerBinding!, sender.Id, pairing);
        async Task<IConnect.Protocol.FileResponse> BatchRequest(IConnect.Protocol.FileRequest request)
        { request.RequestId = ProtocolRules.NewToken(); var response = await files.ExecuteAsync(sender.Id, request, default); StorageRules.ValidateResponse(response); if (response.Status != IConnect.Protocol.FileStatus.Ok) throw new IOException(response.Message); return response; }
        var target = (await BatchRequest(new() { Operation = IConnect.Protocol.FileOperation.UploadContext })).Entries.Single();
        var context = await BatchRequest(new() { Operation = IConnect.Protocol.FileOperation.UploadBatchContext });
        var manifest = new IConnect.Protocol.UploadBatchManifest(); manifest.Items.Add(new IConnect.Protocol.UploadBatchItem { Parent = -1, Name = "Generated batch folder", IsDirectory = true });
        var header = UploadBatchRules.Header(MutationRules.NewId(), context.MutationEpoch, grant.Id, manifest);
        await BatchRequest(new() { Operation = IConnect.Protocol.FileOperation.BeginUploadBatch, MutationEpoch = header.Epoch, TransferId = header.Id, BatchHeader = header, DestinationId = target.Id });
        await BatchRequest(new() { Operation = IConnect.Protocol.FileOperation.WriteUploadManifest, MutationEpoch = header.Epoch, TransferId = header.Id, Content = Google.Protobuf.ByteString.CopyFrom(UploadBatchRules.Encode(manifest)) });
        await BatchRequest(new() { Operation = IConnect.Protocol.FileOperation.SealUploadBatch, MutationEpoch = header.Epoch, TransferId = header.Id });
    }
    if (args.Contains("--seed-upload", StringComparer.Ordinal))
    {
        var localKey = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        using var owner = DeviceIdentity.OpenWindows(localKey, "Offline sending UI fixture");
        using var recipient = DeviceIdentity.Ephemeral("Offline receiving fixture");
        using var trust = new TrustedPeerStore(state, owner.Id);
        trust.Add(new(recipient.Id, recipient.Descriptor.Name, Convert.ToBase64String(recipient.PublicKey)));
        var pairing = trust.EnsureUploadScope(recipient.Id);
        var authority = new UploadAuthority(owner.Id, trust.OwnerBinding!.Epoch, recipient.Id, pairing);
        await using var sender = new WindowsUploadSender(state, scope => scope == authority);
        await sender.PrepareAsync(Path.Combine(documents, "simu.txt"), authority);
        if (args.Contains("--seed-received", StringComparer.Ordinal))
        {
            var sourceProfile = Path.Combine(directory, "offline-source");
            var sourceGrants = new StorageGrantStore(sourceProfile); sourceGrants.Add(owner.Id, documents);
            await using var source = new DownloadSourceStore(sourceGrants, sourceProfile); using var files = new WindowsFileService(sourceGrants, source);
            Task<IConnect.Protocol.FileResponse> Request(IConnect.Protocol.FileRequest value, CancellationToken cancellation)
            { var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); return files.ExecuteAsync(owner.Id, request, cancellation); }
            var roots = await Request(new() { Operation = IConnect.Protocol.FileOperation.ListRoots }, default);
            var entries = await Request(new() { Operation = IConnect.Protocol.FileOperation.ListDirectory, NodeId = roots.Entries.Single().Id }, default);
            var received = Path.Combine(directory, "received"); Directory.CreateDirectory(received);
            await using var downloads = new WindowsDownloadManager(state);
            var id = downloads.Start(recipient.Id, entries.Entries.Single(x => x.Name == "simu.txt"), received, DownloadCollision.KeepBoth, Request); await downloads.WaitAsync(id);
            if (downloads.Jobs.Single().Phase != DownloadPhase.Completed || downloads.Jobs.Single().SecurityPolicy != 1) throw new IOException("The generated receiving security fixture did not complete.");
        }
    }
    LocalState.Write(Path.Combine(state, "settings.json"), new DesktopSettings("Local UI fixture"));
    using var local = new LocalFileLocations(state); local.Add(documents);
    if (args.Contains("--recovery", StringComparer.Ordinal))
    {
        var queue = Path.Combine(state, "downloads"); Directory.CreateDirectory(queue);
        File.WriteAllText(Path.Combine(queue, ProtocolRules.NewToken() + ".json"), "{generated damaged progress fixture");
    }
    Console.WriteLine("DESKTOP_STATE " + state); return 0;
}
if (args.Contains("--owner-recovery", StringComparer.Ordinal))
{
    var oldKeyName = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
    using (var prior = DeviceIdentity.OpenWindows(oldKeyName, "Owner recovery fixture"))
    using (var approved = DeviceIdentity.Ephemeral("Earlier paired fixture"))
    using (var trust = new TrustedPeerStore(state, prior.Id))
    {
        trust.Add(new(approved.Id, approved.Descriptor.Name, Convert.ToBase64String(approved.PublicKey)));
        new StorageGrantStore(state, ownerBinding: trust.OwnerBinding).Add(approved.Id, documents);
        LocalState.Write(Path.Combine(state, "settings.json"), new DesktopSettings("Owner recovery fixture"));
        using var local = new LocalFileLocations(state); local.Add(documents);
    }
    using (var removed = CngKey.Open(oldKeyName)) removed.Delete(); // Only this freshly generated fixture identity.
    Console.WriteLine("DESKTOP_STATE " + state); return 0;
}
if (args.Contains("--deleted-local", StringComparer.Ordinal))
{
    var fixtureKey = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
    using var owner = DeviceIdentity.OpenWindows(fixtureKey, "Deleted-item UI fixture");
    using var requestingPeer = DeviceIdentity.Ephemeral("Revoked fixture peer");
    using var trust = new TrustedPeerStore(state, owner.Id);
    var approvals = new StorageGrantStore(state, ownerBinding: trust.OwnerBinding);
    approvals.Add(requestingPeer.Id, documents, StoragePermission.Manage);
    using var files = new WindowsFileService(approvals);
    async Task<IConnect.Protocol.FileResponse> Call(IConnect.Protocol.FileRequest request)
    { request.RequestId = ProtocolRules.NewToken(); return await files.ExecuteAsync(requestingPeer.Id, request, default); }
    var root = (await Call(new() { Operation = IConnect.Protocol.FileOperation.ListRoots })).Entries.Single();
    var entry = (await Call(new() { Operation = IConnect.Protocol.FileOperation.ListDirectory, NodeId = root.Id })).Entries.Single(x => x.Name == "simu.txt");
    var context = await Call(new() { Operation = IConnect.Protocol.FileOperation.RecoveryContext });
    var deleted = await Call(new() { Operation = IConnect.Protocol.FileOperation.Delete, NodeId = entry.Id, ExpectedVersion = entry.Version,
        MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch });
    if (deleted.Status != IConnect.Protocol.FileStatus.Ok) throw new IOException(deleted.Message);
    approvals.RevokePeer(requestingPeer.Id);
    LocalState.Write(Path.Combine(state, "settings.json"), new DesktopSettings("Deleted-item UI fixture"));
    Console.WriteLine("DESKTOP_STATE " + state); return 0;
}
using var phone = DeviceIdentity.Ephemeral("Phone UI fixture");
var keyName = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
using (var owner = DeviceIdentity.OpenWindows(keyName, "Desktop UI fixture"))
{
    LocalState.Write(Path.Combine(state, "settings.json"), new DesktopSettings("Desktop UI fixture", "http://localhost:5078"));
    var desktopBinding = new WindowsOwnerBinding(state, owner.Id);
    using (var trust = new TrustedPeerStore(state, owner.Id)) trust.Add(new(phone.Id, phone.Descriptor.Name, Convert.ToBase64String(phone.PublicKey)));
    if (args.Contains("--availability", StringComparer.Ordinal))
    {
        var availability = new DesktopAvailability(state); availability.SetOptions(true, false, true); availability.RememberPeer(phone.Id); availability.Resume();
        new StorageGrantStore(state, ownerBinding: desktopBinding).Add(phone.Id, documents, label: "Background fixture documents");
    }
    var phoneState = Path.Combine(directory, "phone");
    var phoneBinding = new WindowsOwnerBinding(phoneState, phone.Id);
    using (var trust = new TrustedPeerStore(phoneState, phone.Id)) trust.Add(new(owner.Id, owner.Descriptor.Name, Convert.ToBase64String(owner.PublicKey)));
    var grants = new StorageGrantStore(phoneState, ownerBinding: phoneBinding);
    await using var sourceDownloads = new DownloadSourceStore(grants, phoneState);
    grants.Add(owner.Id, documents, label: "Phone fixture documents", permissions: StoragePermission.ReadOnly | StoragePermission.Create | StoragePermission.Rename | StoragePermission.Move);
    if (args.Contains("--seed-batch", StringComparer.Ordinal))
    {
        using var sourceFiles = new WindowsFileService(grants, sourceDownloads);
        Task<IConnect.Protocol.FileResponse> FixtureRpc(IConnect.Protocol.FileRequest request, CancellationToken ct)
        { var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); return sourceFiles.ExecuteAsync(owner.Id, copy, ct); }
        var roots = await FixtureRpc(new() { Operation = IConnect.Protocol.FileOperation.ListRoots }, default);
        var manifest = await TransferManifests.BuildAsync([new(roots.Entries.Single(), [])], FixtureRpc, default);
        var destination = Path.Combine(directory, "received-ui"); Directory.CreateDirectory(destination);
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(phone.Id, manifest, destination, DownloadCollision.KeepBoth, FixtureRpc); await batches.WaitAsync(id);
        if (batches.Jobs.Single().Phase != BatchPhase.Completed) throw new IOException("The disposable UI seed batch failed: " + batches.Jobs.Single().Error);
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var origin = new Uri("http://localhost:5078");
    await using var peer = new ProofClient(phone, await SignalConnection.ConnectAsync(origin, phone, timeout.Token), origin,
        phoneState, Console.WriteLine, fileServiceFactory: _ => new WindowsFileService(grants, sourceDownloads));
    Console.WriteLine("DESKTOP_STATE " + state);
    Console.WriteLine("Only disposable, pre-pinned fixture identities are used. This is not a pairing-UI test.");
    var run = peer.RunAsync(timeout.Token);
    try
    {
        while (!timeout.IsCancellationRequested && !run.IsCompleted)
        {
            var input = Console.In.ReadLineAsync(timeout.Token).AsTask();
            if (await Task.WhenAny(input, run) == run) break;
            var command = await input;
            if (command is null or "quit") break;
            if (command == "verify-background")
            {
                var roots = await peer.RequestFilesAsync(new() { Operation = IConnect.Protocol.FileOperation.ListRoots }, timeout.Token);
                var approved = roots.Entries.Single(x => x.Name == "Background fixture documents");
                var listing = await peer.RequestFilesAsync(new() { Operation = IConnect.Protocol.FileOperation.ListDirectory, NodeId = approved.Id }, timeout.Token);
                var document = listing.Entries.Single(x => x.Name == "simu.txt");
                var data = await peer.RequestFilesAsync(new() { Operation = IConnect.Protocol.FileOperation.ReadRange, NodeId = document.Id, ExpectedVersion = document.Version, Length = 32768 }, timeout.Token);
                if (data.Status != IConnect.Protocol.FileStatus.Ok || data.Content.ToStringUtf8() != "iConnect native desktop preview.\nHabari kutoka kwenye simu!\n")
                    throw new InvalidDataException("The background window did not serve its approved fixture.");
                Console.WriteLine("BACKGROUND_FILE_ACCESS_VERIFIED");
            }
        }
    }
    finally { timeout.Cancel(); try { await run; } catch (OperationCanceledException) { } }
}
return 0;
