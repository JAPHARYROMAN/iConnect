using System.Collections.Concurrent;
using System.Text.Json;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class WindowsStorageTests
{
    private Task<FileResponse> ChangeRpc(FileRequest request, CancellationToken token)
    { var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); return files.ExecuteAsync(peer, copy, token); }

    [Fact]
    public async Task Desktop_change_confirms_real_receipts_without_mutating_the_callers_intent_or_logging_private_metadata()
    {
        var parent = await Root(StoragePermission.ReadOnly | StoragePermission.Create);
        var intent = new FileRequest { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = "NEVER-LOG-CONFIRMED-FOLDER" };
        var original = intent.Clone(); var events = new ConcurrentQueue<WindowsActivityEvent>();
        var reply = await DesktopFileChange.ExecuteAsync(intent, peer, ChangeRpc, events.Enqueue, default);
        Assert.Equal(MutationPhase.ChangeSucceeded, reply.Mutation.Phase); Assert.True(Directory.Exists(Path.Combine(root, intent.Name))); Assert.Equal(original, intent);
        Assert.Equal(new[] { WindowsActivityOutcome.Requested, WindowsActivityOutcome.Completed }, events.Select(x => x.Outcome));
        Assert.All(events, x => { Assert.Equal(peer, x.Peer); Assert.Equal(FileOperation.CreateDirectory, x.Operation); });
        var journal = JsonSerializer.Serialize(events); Assert.DoesNotContain("NEVER-LOG", journal); Assert.DoesNotContain(reply.Mutation.Id, journal); Assert.DoesNotContain(parent.Id, journal);
        intent.Name = "Observer failure fixture"; string? observedPeer = null;
        await DesktopFileChange.ExecuteAsync(intent, "iconnect://NEVER-LOG-INVALID-PEER", ChangeRpc, value => { observedPeer = value.Peer; throw new IOException("Generated observer failure"); }, default);
        Assert.Equal("", observedPeer);
        Assert.True(Directory.Exists(Path.Combine(root, intent.Name)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("operation")]
    [InlineData("status")]
    [InlineData("pending")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task Desktop_change_rejects_unbound_or_unconfirmed_receipts_after_a_real_effect_without_repeating_it(string substitution)
    {
        var parent = await Root(StoragePermission.ReadOnly | StoragePermission.Create); var calls = 0;
        var events = new ConcurrentQueue<WindowsActivityEvent>();
        await Assert.ThrowsAsync<IOException>(() => DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = "Receipt fixture" }, peer,
            async (request, token) =>
            {
                var reply = await ChangeRpc(request, token); if (request.Operation != FileOperation.CreateDirectory) return reply;
                calls++; Assert.Equal(MutationPhase.ChangeSucceeded, reply.Mutation.Phase);
                switch (substitution)
                {
                    case "id": reply.Mutation.Id = MutationRules.NewId(); reply.Mutation.CreatedAt = MutationRules.CreatedAt(reply.Mutation.Id); break;
                    case "name": reply.Mutation.RequestedName = "Different folder"; break;
                    case "operation": reply.Mutation.Operation = FileOperation.Rename; break;
                    case "status": reply.Status = FileStatus.AccessDenied; break;
                    case "pending": reply.Mutation.Phase = MutationPhase.ChangePending; break;
                    case "missing": reply.Mutation = null; break;
                    case "malformed": reply.Mutation.CreatedAt++; break;
                }
                return reply;
            }, events.Enqueue, default));
        Assert.Equal(1, calls); Assert.Equal("Receipt fixture", Path.GetFileName(Assert.Single(Directory.GetDirectories(root))));
        Assert.Equal(new[] { WindowsActivityOutcome.Requested, WindowsActivityOutcome.Review }, events.Select(x => x.Outcome));
    }

    [Fact]
    public async Task Desktop_change_lost_reply_and_cancellation_after_dispatch_need_review_while_pre_dispatch_cancel_has_no_effect()
    {
        var parent = await Root(StoragePermission.ReadOnly | StoragePermission.Create);
        foreach (var canceled in new[] { false, true })
        {
            var events = new ConcurrentQueue<WindowsActivityEvent>(); var calls = 0;
            var error = await Record.ExceptionAsync(() => DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = canceled ? "Canceled after effect" : "Lost reply" }, peer,
                async (request, token) => { var reply = await ChangeRpc(request, token); if (request.Operation != FileOperation.CreateDirectory) return reply; calls++; if (canceled) throw new OperationCanceledException(); throw new IOException("NEVER-LOG-LOST-REPLY"); }, events.Enqueue, default));
            Assert.True(error is IOException or OperationCanceledException); Assert.Equal(1, calls); Assert.Equal(WindowsActivityOutcome.Review, events.Last().Outcome);
        }
        using var stop = new CancellationTokenSource(); var before = new ConcurrentQueue<WindowsActivityEvent>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = "Must not exist" }, peer,
            async (request, token) => { var reply = await ChangeRpc(request, token); stop.Cancel(); return reply; }, before.Enqueue, stop.Token));
        Assert.Equal(WindowsActivityOutcome.Canceled, before.Last().Outcome); Assert.False(Directory.Exists(Path.Combine(root, "Must not exist"))); Assert.Equal(2, Directory.GetDirectories(root).Length);
    }

    [Fact]
    public async Task Desktop_change_permission_refusal_keeps_files_and_reports_no_completion()
    {
        var parent = await Root(); var events = new ConcurrentQueue<WindowsActivityEvent>();
        var response = await DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = "Denied fixture" }, peer, ChangeRpc, events.Enqueue, default);
        Assert.Equal(FileStatus.AccessDenied, response.Status); Assert.Equal(WindowsActivityOutcome.Refused, events.Last().Outcome); Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.DoesNotContain(events, x => x.Outcome == WindowsActivityOutcome.Completed);
    }

    [Fact]
    public async Task Desktop_change_real_rename_move_delete_and_restore_bind_independent_intents_and_preserve_bytes()
    {
        File.WriteAllText(Path.Combine(root, "original.txt"), "Generated management fixture bytes"); Directory.CreateDirectory(Path.Combine(root, "Target"));
        var parent = await Root(StoragePermission.ReadOnly | StoragePermission.Create | StoragePermission.Rename | StoragePermission.Move | StoragePermission.Delete);
        var entries = (await Request(FileOperation.ListDirectory, parent.Id)).Entries; var source = entries.Single(x => x.Name == "original.txt"); var target = entries.Single(x => x.Name == "Target");
        var events = new ConcurrentQueue<WindowsActivityEvent>();
        var renamed = await DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.Rename, NodeId = source.Id, ExpectedVersion = source.Version, Name = "renamed.txt" }, peer, ChangeRpc, events.Enqueue, default);
        Assert.Equal(MutationPhase.ChangeSucceeded, renamed.Mutation.Phase); Assert.False(File.Exists(Path.Combine(root, "original.txt")));
        source = (await Request(FileOperation.ListDirectory, parent.Id)).Entries.Single(x => x.Name == "renamed.txt");
        await DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.Move, NodeId = source.Id, ExpectedVersion = source.Version, DestinationId = target.Id, Name = source.Name }, peer, ChangeRpc, events.Enqueue, default);
        source = Assert.Single((await Request(FileOperation.ListDirectory, target.Id)).Entries);
        var deleted = await DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.Delete, NodeId = source.Id, ExpectedVersion = source.Version }, peer, ChangeRpc, events.Enqueue, default);
        Assert.False(File.Exists(Path.Combine(root, "Target", "renamed.txt")));
        await DesktopFileChange.ExecuteAsync(new() { Operation = FileOperation.Restore, DeletedId = deleted.Mutation.Id, Name = "restored.txt" }, peer, ChangeRpc, events.Enqueue, default);
        Assert.Equal("Generated management fixture bytes", File.ReadAllText(Path.Combine(root, "Target", "restored.txt")));
        Assert.Equal(new[] { FileOperation.Rename, FileOperation.Move, FileOperation.Delete, FileOperation.Restore }, events.Where(x => x.Outcome == WindowsActivityOutcome.Completed).Select(x => x.Operation!.Value));
        Assert.All(events, x => Assert.True(x.Outcome is WindowsActivityOutcome.Requested or WindowsActivityOutcome.Completed));
    }
}
