using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IConnect.AppModel;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IConnect.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class DesktopModelTests : IDisposable
{
    private readonly string directory;
    private readonly string state;
    public DesktopModelTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        directory = Path.Combine(repo!.FullName, ".local", "desktop-model-tests", ProtocolRules.NewToken());
        state = Path.Combine(directory, "owner"); Directory.CreateDirectory(directory);
    }
    private static ProofClient.TrustedPeer Peer(DeviceIdentity identity) => new(identity.Id, identity.Descriptor.Name, Convert.ToBase64String(identity.PublicKey));
    private string SeedOwnerTrust(DeviceIdentity peer)
    {
        var key = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        using var owner = DeviceIdentity.OpenWindows(key, "Desktop fixture");
        using var store = new TrustedPeerStore(state, owner.Id); store.Add(Peer(peer)); return owner.Id;
    }

    [Fact]
    public void Offline_trust_is_exclusively_owned_and_revocation_survives_restart()
    {
        using var device = DeviceIdentity.Ephemeral("Phone fixture");
        using (var store = new TrustedPeerStore(state))
        {
            store.Add(Peer(device));
            Assert.Throws<IOException>(() => new TrustedPeerStore(state));
        }
        using (var reopened = new TrustedPeerStore(state))
        {
            Assert.Single(reopened.All); reopened.Remove(device.Id);
        }
        using var final = new TrustedPeerStore(state);
        Assert.Empty(final.All);
    }
    [Fact]
    public void A_failed_trust_save_cannot_authorize_a_device_in_memory()
    {
        using var device = DeviceIdentity.Ephemeral("Phone fixture");
        using var store = new TrustedPeerStore(state);
        Directory.CreateDirectory(Path.Combine(state, "peers.json.new"));
        var error = Record.Exception(() => store.Add(Peer(device)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(store.All);
    }
    [Fact]
    public async Task Desktop_settings_keep_identity_stable_and_revoke_storage_while_offline()
    {
        using var phone = DeviceIdentity.Ephemeral("Phone fixture");
        SeedOwnerTrust(phone);
        string id;
        await using (var model = new DesktopController(state, action => action()))
        {
            id = model.DeviceId;
            model.SaveSettings(new DesktopSettings("Updated desktop", "https://coordinator.example", "stun:discovery.example:3478"));
            Assert.Equal(id, model.DeviceId);
            var shared = Path.Combine(directory, "shared"); Directory.CreateDirectory(shared);
            await model.AddGrantAsync(phone.Id, shared, "Documents", StoragePermission.ReadOnly, [], null, enabled: false);
            Assert.False(Assert.Single(model.Grants).Enabled);
            await model.RevokeAsync(phone.Id);
            Assert.Empty(model.Peers); Assert.Empty(model.Grants);
            Assert.False(model.Connected);
        }
        await using var restarted = new DesktopController(state, action => action());
        Assert.Equal(id, restarted.DeviceId);
        Assert.Equal("Updated desktop", restarted.Settings.DeviceName);
        Assert.Empty(restarted.Peers); Assert.Empty(restarted.Grants);
        Assert.Contains(restarted.Activity, x => x.Category == "Devices");
    }
    [Fact]
    public async Task Native_batch_review_can_page_stop_offline_and_remove_only_reviewed_private_history()
    {
        using var phone = DeviceIdentity.Ephemeral("Phone fixture"); SeedOwnerTrust(phone);
        var original = Path.Combine(directory, "Selected documents"); Directory.CreateDirectory(Path.Combine(original, "Empty"));
        for (var index = 0; index < 70; index++) File.WriteAllText(Path.Combine(original, $"note-{index:D3}.txt"), $"original {index}");
        await using (var model = new DesktopController(state, action => action()))
        {
            var sourceId = await model.PrepareOutgoingBatch(phone.Id, [original], UploadCollision.UploadSkip);
            var source = Assert.Single(model.PrivateOutgoingBatches); Assert.Equal("Selected documents", source.Label);
            var manifest = await model.InspectPrivateBatch(sourceId); Assert.Equal(72, manifest.Items.Count); Assert.Equal(UploadCollision.UploadSkip, manifest.Collision);
            Assert.Equal("Selected documents / Empty", DesktopController.BatchItemPath(manifest, 1));
            var id = model.QueueOutgoingBatch(sourceId); var row = Assert.Single(model.OutgoingBatchSends); Assert.Equal(source.Label, row.Label);
            Assert.Equal(50, (await model.BatchResultPage(id, 0)).Count); Assert.Equal(22, (await model.BatchResultPage(id, 50)).Count);
            Assert.All(await model.BatchResultPage(id, 0), x => { Assert.StartsWith("Selected documents", x.Name); Assert.Equal("Not processed", x.Phase); });
            Assert.False(model.BatchReceiverSupported); Assert.Empty(model.Grants);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await model.SendOutgoingBatch(id));
            Assert.Throws<IOException>(() => model.ReviewOutgoingBatchRemoval(sourceId));
            await model.StopOutgoingBatch(id); Assert.Equal(BatchSendingPhase.Stopped, Assert.Single(model.OutgoingBatchSends).Phase);
            Assert.True(model.ConfirmOutgoingBatchHistory(model.ReviewOutgoingBatchHistory(id, BatchHistoryAction.Archive)));
            model.RemoveOutgoingBatchReviewed(model.ReviewOutgoingBatchRemoval(sourceId));
            Assert.Empty(model.PrivateOutgoingBatches); Assert.Equal(22, (await model.BatchResultPage(id, 50)).Count);
            Assert.All(await model.BatchResultPage(id, 50), x => Assert.StartsWith("Item ", x.Name));
        }
        await using (var reopened = new DesktopController(state, action => action()))
        {
            var row = Assert.Single(reopened.OutgoingBatchSends); Assert.True(row.Archived); Assert.Equal("Selected documents", row.Label);
            Assert.True(reopened.ConfirmOutgoingBatchHistory(reopened.ReviewOutgoingBatchHistory(row.Id, BatchHistoryAction.Restore)));
            Assert.True(reopened.ConfirmOutgoingBatchHistory(reopened.ReviewOutgoingBatchHistory(row.Id, BatchHistoryAction.Archive)));
            var done = reopened.ConfirmOutgoingBatchHistory(reopened.ReviewOutgoingBatchHistory(row.Id, BatchHistoryAction.Purge));
            while (!done) done = reopened.ContinueOutgoingBatchPurge(row.Id);
            Assert.Empty(reopened.OutgoingBatchSends); Assert.Empty(reopened.Grants);
        }
        Assert.True(Directory.Exists(Path.Combine(original, "Empty")));
        Assert.Equal(70, Directory.GetFiles(original).Length);
        for (var index = 0; index < 70; index++) Assert.Equal($"original {index}", File.ReadAllText(Path.Combine(original, $"note-{index:D3}.txt")));
    }
    [Fact]
    public async Task Native_sender_recovery_controller_preserves_unreadable_intent_and_requires_deliberate_new_copy()
    {
        using var phone = DeviceIdentity.Ephemeral("Phone fixture"); SeedOwnerTrust(phone);
        var original = Path.Combine(directory, "Recovery original"); Directory.CreateDirectory(original); File.WriteAllText(Path.Combine(original, "note.txt"), "generated original");
        string source, id;
        await using (var before = new DesktopController(state, action => action()))
        { source = await before.PrepareOutgoingBatch(phone.Id, [original], UploadCollision.UploadKeepBoth); id = before.QueueOutgoingBatch(source); }
        var record = Path.Combine(state, "outgoing-batch-jobs", id + ".json"); File.WriteAllText(record, "generated unreadable intent");
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.Contains(model.TransferRecovery, x => x.Area == "Batch sending"); Assert.False(model.PeerAuthenticated);
            var review = await model.ReviewBatchSenderReset(); Assert.Equal("generated unreadable intent", File.ReadAllText(record));
            var archive = await model.ConfirmBatchSenderReset(review); var preserved = model.OutgoingBatchRecoveryLocation(archive);
            Assert.DoesNotContain(model.TransferRecovery, x => x.Area == "Batch sending"); Assert.Equal("generated unreadable intent", File.ReadAllText(Path.Combine(preserved, id + ".json")));
            Assert.Throws<IOException>(() => model.QueueOutgoingBatch(source)); var current = model.QueueOutgoingBatch(source, anotherCopy: true);
            Assert.True(await model.ConfirmBatchSenderArchiveRemoval(await model.ReviewBatchSenderArchiveRemoval(archive)));
            Assert.Equal(current, Assert.Single(model.OutgoingBatchSends).Id); Assert.Single(model.PrivateOutgoingBatches); Assert.Empty(model.OutgoingBatchRecoveryArchives); Assert.Empty(model.Grants);
        }
        Assert.Equal("generated original", File.ReadAllText(Path.Combine(original, "note.txt")));
    }
    [Fact]
    public async Task Native_source_recovery_controller_preserves_snapshots_and_sender_jobs_and_retains_copy_review_after_cleanup()
    {
        using var phone = DeviceIdentity.Ephemeral("Phone fixture"); SeedOwnerTrust(phone);
        var original = Path.Combine(directory, "Source recovery original"); Directory.CreateDirectory(original); File.WriteAllText(Path.Combine(original, "note.txt"), "generated original");
        string source, job;
        await using (var before = new DesktopController(state, action => action()))
        { source = await before.PrepareOutgoingBatch(phone.Id, [original], UploadCollision.UploadKeepBoth); job = before.QueueOutgoingBatch(source); }
        var record = Path.Combine(state, "outgoing-batch-copies", source + ".json"); File.WriteAllText(record, "generated unreadable source ownership");
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.Contains(model.TransferRecovery, x => x.Area == "Private batches"); Assert.False(model.PeerAuthenticated);
            var review = await model.ReviewPrivateBatchStoreReset(); Assert.Equal("generated unreadable source ownership", File.ReadAllText(record));
            var archive = await model.ConfirmPrivateBatchStoreReset(review); var preserved = model.PrivateBatchRecoveryLocation(archive);
            Assert.DoesNotContain(model.TransferRecovery, x => x.Area == "Private batches"); Assert.Empty(model.PrivateOutgoingBatches); Assert.Equal(job, Assert.Single(model.OutgoingBatchSends).Id);
            Assert.Equal("generated unreadable source ownership", File.ReadAllText(Path.Combine(preserved, source + ".json")));
            Assert.NotEmpty(await model.InspectPrivateBatchArchive(archive, 0)); Assert.True(await model.ConfirmPrivateBatchArchiveRemoval(await model.ReviewPrivateBatchArchiveRemoval(archive)));
            Assert.Empty(model.PrivateBatchRecoveryArchives); Assert.Empty(model.Grants); Assert.Equal(job, Assert.Single(model.OutgoingBatchSends).Id);
        }
        await using (var reopened = new DesktopController(state, action => action()))
        {
            var next = await reopened.PrepareOutgoingBatch(phone.Id, [original], UploadCollision.UploadKeepBoth);
            Assert.True(Assert.Single(reopened.PrivateOutgoingBatches).MayHaveReceiverCopies); Assert.Throws<IOException>(() => reopened.QueueOutgoingBatch(next));
            Assert.NotEmpty(reopened.QueueOutgoingBatch(next, anotherCopy: true)); Assert.Empty(reopened.Grants);
        }
        Assert.Equal("generated original", File.ReadAllText(Path.Combine(original, "note.txt")));
    }
    [Fact]
    public async Task Owner_can_inspect_cancel_reopen_and_remove_only_reviewed_destination_side_parts_without_pairing()
    {
        var destination = Path.Combine(directory, "Generated part recovery"); Directory.CreateDirectory(destination);
        var original = Path.Combine(destination, "owner.txt"); File.WriteAllText(original, "generated owner document");
        var candidate = Path.Combine(destination, ".iconnect-transfer-" + ProtocolRules.NewToken() + ".part"); File.WriteAllText(candidate, "generated candidate");
        IConnect.Storage.Windows.ReceivingPartRemovalReview old;
        await using (var model = new DesktopController(state, action => action()))
        {
            var folder = await model.SelectReceivingPartFolder(destination); Assert.Equal(1, folder.Files); Assert.Equal(Path.GetFileName(candidate), Assert.Single(await model.InspectReceivingParts(folder.Id, 0)).Name);
            old = await model.ReviewReceivingPartRemoval(folder.Id); Assert.True(File.Exists(candidate)); Assert.True(model.Availability.Preferences.Paused);
            Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        }
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.Null(model.ReceivingPartFolder); await Assert.ThrowsAsync<IOException>(() => model.ConfirmReceivingPartRemoval(old, () => true));
            var folder = await model.SelectReceivingPartFolder(destination); var review = await model.ReviewReceivingPartRemoval(folder.Id);
            Assert.True(await model.ConfirmReceivingPartRemoval(review, () => true)); Assert.False(File.Exists(candidate)); Assert.Null(model.ReceivingPartCleanup);
            Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        }
        Assert.Equal("generated owner document", File.ReadAllText(original));
    }
    [Theory]
    [InlineData("http://untrusted.example", "")]
    [InlineData("https://name:secret@example.com", "")]
    [InlineData("https://example.com/path", "")]
    [InlineData("https://example.com", "turn:relay.example:3478")]
    public void Settings_cannot_enable_cleartext_remote_origins_credentials_paths_or_relays(string server, string stun) =>
        Assert.Throws<InvalidDataException>(() => LocalState.Validate(new DesktopSettings("Desktop", server, stun)));

    [Fact]
    public async Task Concurrent_share_and_revoke_cannot_leave_an_approval_for_a_forgotten_device()
    {
        using var phone = DeviceIdentity.Ephemeral("Phone fixture");
        SeedOwnerTrust(phone);
        await using var model = new DesktopController(state, action => action());
        var shared = Path.Combine(directory, "documents"); Directory.CreateDirectory(shared);
        var approval = model.AddGrantAsync(phone.Id, shared, "Documents", StoragePermission.ReadOnly, [], null);
        var revocation = model.RevokeAsync(phone.Id);
        try { await approval; } catch (InvalidDataException) { }
        await revocation;
        Assert.Empty(model.Peers); Assert.Empty(model.Grants);
    }

    [Fact]
    public void Activity_retention_is_persisted_and_the_limit_survives_more_events()
    {
        Directory.CreateDirectory(state);
        var records = Enumerable.Range(0, 500).Select(index => new ActivityEntry(ProtocolRules.NewToken(),
            index == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Connections", "Connection opened.", null)).ToList();
        LocalState.Write(Path.Combine(state, "activity.json"), records);
        var store = new LocalActivityStore(state);
        Assert.Equal(499, store.All.Count);
        Assert.Equal(499, LocalState.Read<System.Text.Json.JsonElement>(Path.Combine(state, "activity.json"), 2 * 1024 * 1024).GetProperty("Entries").GetArrayLength());
        store.Add("Connections", "Another connection opened.");
        store.Add("Connections", "Another connection closed.");
        Assert.Equal(500, store.All.Count);
        Assert.DoesNotContain(store.All, entry => entry.Id == records[1].Id);
    }

    [Fact]
    public async Task Desktop_controller_pairs_authenticates_browses_real_files_and_pauses_access()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var origin = http.BaseAddress!;
        var stageBudget = TimeSpan.FromSeconds(40);
        using var timeout = new CancellationTokenSource(stageBudget);
        await using var model = new DesktopController(state, action => action());
        model.SaveSettings(new DesktopSettings("Desktop fixture", origin.AbsoluteUri));
        await model.ConnectAsync(); Assert.True(model.Connected);
        using var phoneIdentity = DeviceIdentity.Ephemeral("Phone protocol fixture");
        var phoneState = Path.Combine(directory, "phone");
        var phoneRoot = Path.Combine(directory, "phone-documents"); Directory.CreateDirectory(phoneRoot);
        File.WriteAllText(Path.Combine(phoneRoot, "simu.txt"), "Actual phone protocol fixture. Habari!\n");
        Directory.CreateDirectory(Path.Combine(phoneRoot, "Empty"));
        for (var i = 0; i < 110; i++) File.WriteAllText(Path.Combine(phoneRoot, $"page-{i:D3}.txt"), "page fixture");
        using var phoneTrust = new TrustedPeerStore(phoneState, phoneIdentity.Id);
        var phoneGrants = new StorageGrantStore(phoneState, ownerBinding: phoneTrust.OwnerBinding);
        var phoneGrant = phoneGrants.Add(model.DeviceId, phoneRoot, label: "Phone documents");
        await using var phoneDownloads = new DownloadSourceStore(phoneGrants, phoneState);
        ChangedReceiptService? phoneService = null;
        await using var phone = new ProofClient(phoneIdentity, await SignalConnection.ConnectAsync(origin, phoneIdentity, timeout.Token),
            origin, phoneState, _ => { }, fileServiceFactory: peer =>
            {
                var inner = new WindowsFileService(phoneGrants, phoneDownloads); inner.BindOwnerPolicy(phoneTrust.OwnerBinding!, peer, phoneTrust.EnsureUploadScope(peer));
                return phoneService = new(inner);
            }, trustStore: phoneTrust);
        var invitation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerPair = new TaskCompletionSource<Pairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestPair = new TaskCompletionSource<Pairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.InvitationCreated += uri => invitation.TrySetResult(uri);
        model.PairingRequested += pairing => ownerPair.TrySetResult(pairing);
        phone.PairingRequested += pairing => guestPair.TrySetResult(pairing);
        phone.Paired += _ => guestPaired.TrySetResult();
        model.Changed += _ => { if (model.Peers.Count != 0) ownerPaired.TrySetResult(); if (model.PeerAuthenticated) direct.TrySetResult(); };
        var phoneRun = phone.RunAsync(timeout.Token);
        try
        {
            await model.CreateInvitationAsync();
            await phone.JoinInvitationAsync(await invitation.Task.WaitAsync(timeout.Token), timeout.Token);
            var pairings = await Task.WhenAll(ownerPair.Task, guestPair.Task).WaitAsync(timeout.Token);
            Assert.Equal(ProtocolRules.VerificationCode(pairings[0]), ProtocolRules.VerificationCode(pairings[1]));
            await model.ApproveAsync(ProtocolRules.VerificationCode(pairings[0]));
            await phone.ApproveAsync(ProtocolRules.VerificationCode(pairings[1]), timeout.Token);
            await Task.WhenAll(ownerPaired.Task, guestPaired.Task).WaitAsync(timeout.Token);
            await model.ConnectDeviceAsync(phoneIdentity.Id);
            await direct.Task.WaitAsync(timeout.Token);
            timeout.CancelAfter(stageBudget);
            var computerRoot = Path.Combine(directory, "NEVER-LOG-COMPUTER-FOLDER"); Directory.CreateDirectory(computerRoot);
            await model.AddGrantAsync(phoneIdentity.Id, computerRoot, "NEVER-LOG-GRANT-LABEL", StoragePermission.ReadOnly, [], null, true);
            var computerFolder = Assert.Single((await phone.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, timeout.Token)).Entries);
            var refused = await phone.RequestFilesAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = computerFolder.Id, Name = "NEVER-LOG-INCOMING-NAME" }, timeout.Token);
            Assert.Equal(FileStatus.AccessDenied, refused.Status); await model.FlushActivity();
            Assert.Contains(model.Activity, x => x.Category == "Incoming requests" && x.Description.Contains("refused"));
            Assert.DoesNotContain("NEVER-LOG", File.ReadAllText(Path.Combine(state, "activity.json")));
            await model.BrowseAsync(); var root = Assert.Single(model.Entries);
            Assert.Equal("Phone documents", root.Name);
            await model.BrowseAsync(root); Assert.Equal(112, model.FileCount); Assert.Equal(3, model.FilePageCount);
            model.SelectFile(model.Entries.First(x => x.Name == "page-000.txt").Id, true);
            model.SelectFile(model.Entries.Single(x => x.Name == "Empty").Id, true);
            model.SetFilePage(2); var document = Assert.Single(model.Entries, x => x.Name == "simu.txt");
            model.SelectFile(document.Id, true); Assert.Equal(3, model.SelectedFiles.Count);
            model.SetFileSort(ExplorerSort.Size, true); Assert.Equal(3, model.SelectedFiles.Count);
            model.SetFileLayout(ExplorerLayout.Grid); Assert.Equal(ExplorerLayout.Grid, model.FileLayout);
            await model.PreviewTextAsync(document);
            Assert.Equal("Actual phone protocol fixture. Habari!\n", model.Preview);
            await Assert.ThrowsAsync<IOException>(() => model.CreateFolderAsync("not-authorized"));
            var plan = await model.PlanBatchAsync(model.SelectedFiles.Select(x => x.Entry).ToArray());
            var destination = Path.Combine(directory, "received-batch"); Directory.CreateDirectory(destination);
            var batchId = model.StartBatch(plan, destination, DownloadCollision.KeepBoth);
            while (model.Batches.Single(x => x.Id == batchId).Phase == BatchPhase.Running) await Task.Delay(20, timeout.Token);
            var batch = model.Batches.Single(x => x.Id == batchId);
            Assert.True(batch.Phase == BatchPhase.Completed, batch.Error);
            await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Description.StartsWith("Folder download · completed"));
            Assert.Equal("Actual phone protocol fixture. Habari!\n", File.ReadAllText(Path.Combine(destination, "simu.txt")));
            Assert.True(Directory.Exists(Path.Combine(destination, "Empty"))); Assert.Empty(model.Downloads);
            timeout.Token.ThrowIfCancellationRequested();
            timeout.CancelAfter(stageBudget);
            await model.SearchFilesAsync("SIMU"); Assert.Single(model.Entries); Assert.Equal(3, model.SelectedFiles.Count);
            Assert.Equal("simu.txt", (await model.FileDetailsAsync(document)).Name);
            phoneGrants.Update(phoneGrant.Id, StoragePermission.ReadOnly | StoragePermission.Create | StoragePermission.Move, [], null, true);
            Assert.NotNull(phoneService); phoneService.RefuseChangeHistory = true;
            await model.BrowseAsync(root); await model.CreateFolderAsync("NEVER-LOG-REMOTE-TARGET");
            Assert.True(phoneService.HistoryRefusals > 0); phoneService.RefuseChangeHistory = false;
            await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Category == "Remote changes" && x.Description == "Create receiving folder · completed");
            foreach (var selected in model.SelectedFiles.ToArray()) model.SelectFile(selected.Entry.Id, false);
            await model.SearchFilesAsync("simu"); model.SelectFile(Assert.Single(model.Entries).Id, true); model.BeginMove();
            await model.SearchFilesAsync("NEVER-LOG-REMOTE-TARGET"); var moveTarget = Assert.Single(model.Entries); await model.BrowseAsync(moveTarget);
            Assert.NotNull(phoneService); phoneService.OmitMoveReceipt = true;
            await Assert.ThrowsAsync<IOException>(model.MoveHereAsync); Assert.NotNull(model.PendingMove);
            Assert.False(File.Exists(Path.Combine(phoneRoot, "simu.txt"))); Assert.Equal("Actual phone protocol fixture. Habari!\n", File.ReadAllText(Path.Combine(phoneRoot, "NEVER-LOG-REMOTE-TARGET", "simu.txt")));
            await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Category == "Remote changes" && x.Description == "Move item · needs review");
            Assert.DoesNotContain(model.Activity, x => x.Description == "Move item · completed");
            Assert.DoesNotContain("NEVER-LOG", File.ReadAllText(Path.Combine(state, "activity.json"))); model.CancelMove();
            await model.DisconnectAsync();
            await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Description == "Direct device connection · completed"); Assert.Contains(model.Activity, x => x.Description == "Direct device connection · canceled");
            Assert.Contains(model.Activity, x => x.Description == "Coordinator connection · completed"); Assert.Contains(model.Activity, x => x.Description == "Coordinator connection · canceled");
            Assert.Throws<IOException>(() => model.StartBatch(plan, destination, DownloadCollision.KeepBoth));
            Assert.False(model.Connected); Assert.False(model.PeerAuthenticated); Assert.Empty(model.Entries);
            Assert.All(model.Activity, entry => Assert.DoesNotContain("iconnect://", entry.Description));
            Assert.Contains(model.Activity, entry => entry.Category == "Connections");
        }
        finally
        {
            timeout.Cancel();
            try { await phoneRun; } catch (OperationCanceledException) { }
        }
    }
    [Fact]
    public async Task Availability_recovers_after_coordinator_restart_and_durable_pause_stops_retry()
    {
        await using var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        first.UseKestrel(0); using var firstHttp = first.CreateClient(); var origin = firstHttp.BaseAddress!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var model = new DesktopController(state, action => action());
        model.SaveSettings(new DesktopSettings("Availability fixture", origin.AbsoluteUri));
        model.SetAvailabilityOptions(true, false, true);
        await model.ResumeAvailabilityAsync(); Assert.True(model.Connected); var identity = model.DeviceId;
        var retry = new BackgroundReconnect(model.Availability,
            () => new(model.Connected, model.Connecting, model.PeerAuthenticated, model.PeerConnecting, true, model.Peers.Select(x => x.Id).ToArray()),
            model.ConnectAsync, model.ConnectDeviceAsync);
        await first.DisposeAsync();
        while (model.Connected || model.Connecting) await Task.Delay(20, deadline.Token);
        await retry.TickAsync(); Assert.False(model.Connected);
        await using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        restarted.UseKestrel(origin.Port); using var restartedHttp = restarted.CreateClient();
        retry.NetworkAvailable = false; await retry.TickAsync(); retry.NetworkAvailable = true;
        await retry.TickAsync(); Assert.True(model.Connected); Assert.Equal(identity, model.DeviceId);
        await model.PauseAvailabilityAsync(); Assert.False(model.Connected); Assert.True(model.Availability.Preferences.Paused);
        await retry.TickAsync(); Assert.False(model.Connected); Assert.False(new DesktopAvailability(state).AutomaticAllowed);
    }

    [Fact]
    public async Task Failed_revocation_cannot_be_bypassed_by_automatic_or_manual_resume()
    {
        using var peer = DeviceIdentity.Ephemeral("Revocation fixture");
        var ownerId = SeedOwnerTrust(peer);
        var shared = Path.Combine(directory, "approved"); Directory.CreateDirectory(shared);
        new StorageGrantStore(state, ownerBinding: new WindowsOwnerBinding(state, ownerId)).Add(peer.Id, shared);
        await using var model = new DesktopController(state, action => action());
        model.SetAvailabilityOptions(true, false, true); model.Availability.Resume();
        var blocker = Path.Combine(state, "storage-grants.json.new"); Directory.CreateDirectory(blocker);
        Assert.True(await Record.ExceptionAsync(() => model.RevokeAsync(peer.Id)) is IOException or UnauthorizedAccessException);
        Assert.False(model.Connected); Assert.False(model.Availability.AutomaticAllowed);
        Assert.True(new DesktopAvailability(state).Preferences.Paused);
        await Assert.ThrowsAsync<InvalidOperationException>(model.ResumeAvailabilityAsync);
        Directory.Delete(blocker); await model.RevokeAsync(peer.Id);
        Assert.Empty(model.Peers); Assert.Empty(model.Grants); Assert.True(model.Availability.Preferences.Paused);
    }

    [Fact]
    public async Task Offline_peer_failure_keeps_the_coordinator_ready_for_other_owner_actions()
    {
        using var offline = DeviceIdentity.Ephemeral("Offline fixture");
        SeedOwnerTrust(offline);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        factory.UseKestrel(0); using var http = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var model = new DesktopController(state, action => action());
        model.SaveSettings(new DesktopSettings("Offline-peer fixture", http.BaseAddress!.AbsoluteUri));
        await model.ResumeAvailabilityAsync();
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Changed += _ => { if (model.Status.Contains("paired device is unavailable", StringComparison.Ordinal)) unavailable.TrySetResult(); };
        await model.ConnectDeviceAsync(offline.Id); await unavailable.Task.WaitAsync(deadline.Token);
        Assert.True(model.Connected); Assert.False(model.PeerAuthenticated); Assert.False(model.PeerConnecting);
        await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Description == "Direct device connection · refused" && x.PeerId == offline.Id);
        Assert.DoesNotContain(model.Activity, x => x.Description == "Direct device connection · completed");
        var clientField = typeof(DesktopController).GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var idsField = typeof(ProofClient).GetField("sessionIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var oldSession = Assert.Single((IEnumerable<string>)idsField.GetValue(clientField.GetValue(model))!);
        await using var returning = await SignalConnection.ConnectAsync(http.BaseAddress!, offline, deadline.Token);
        var late = new Signal { SessionId = oldSession, FromDevice = offline.Id, ToDevice = model.DeviceId, Kind = "candidate", Mid = "0",
            Body = "candidate:fixture 1 UDP 123 192.0.2.1 9999 typ host", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) };
        late.Signature = Google.Protobuf.ByteString.CopyFrom(offline.Sign(ProtocolRules.SignalTranscript(late)));
        await returning.SendAsync("signal", late, deadline.Token); await returning.SendAsync("invite", new Notice(), deadline.Token);
        while ((await returning.ReceiveAsync(deadline.Token))?.Kind != "invitation") deadline.Token.ThrowIfCancellationRequested();
        var invitation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.InvitationCreated += value => invitation.TrySetResult(value);
        await model.CreateInvitationAsync(); Assert.StartsWith("iconnect://pair", await invitation.Task.WaitAsync(deadline.Token));
    }

    [Fact]
    public async Task Normalized_profile_launch_preserves_an_existing_trailing_separator_identity()
    {
        var legacyName = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((state + Path.DirectorySeparatorChar).ToUpperInvariant())));
        try
        {
            using var original = DeviceIdentity.OpenWindows(legacyName, "Legacy path fixture");
            await using var model = new DesktopController(state, action => action());
            Assert.Equal(original.Id, model.DeviceId);
        }
        finally { if (CngKey.Exists(legacyName)) { using var key = CngKey.Open(legacyName); key.Delete(); } }
    }

    public void Dispose()
    {
        var key = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        if (CngKey.Exists(key)) { using var handle = CngKey.Open(key); handle.Delete(); }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
