using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace IConnect.Tests;

public sealed class NativeHandshakeStressTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Concurrent_native_handshakes_keep_their_own_identity_readiness_and_file_authority_through_repeated_teardown()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, ".local", "native-handshake-stress", ProtocolRules.NewToken());
        Directory.CreateDirectory(root);
        for (var round = 0; round < 8; round++)
            await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Pair(round, index)));

        async Task Pair(int round, int index)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var host = DeviceIdentity.Ephemeral("Generated concurrent native host");
            using var guest = DeviceIdentity.Ephemeral("Generated concurrent native guest");
            var id = ProtocolRules.NewToken();
            var folder = Path.Combine(root, round + "-" + index);
            using var first = new PeerSession(host, guest.PublicKey, id, true, Path.Combine(folder, "host"), _ => { }, loopbackOnly: true);
            using var second = new PeerSession(guest, host.PublicKey, id, false, Path.Combine(folder, "guest"), _ => { }, loopbackOnly: true);
            var loops = new[] { first.RunAsync(deadline.Token), second.RunAsync(deadline.Token) };
            async Task Bridge(PeerSession from, PeerSession to)
            {
                await foreach (var signal in from.Rtc.Signals.ReadAllAsync(deadline.Token))
                    if (!to.Rtc.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid)) throw new IOException("A concurrent native signal was rejected.");
            }
            var bridges = new[] { Bridge(first, second), Bridge(second, first) };
            try
            {
                first.Rtc.Offer();
                await Task.WhenAll(first.Authenticated, second.Authenticated).WaitAsync(deadline.Token);
                Assert.Equal(guest.Id, first.RemoteId); Assert.Equal(host.Id, second.RemoteId);
                foreach (var peer in new[] { first, second })
                {
                    Assert.False(peer.AcceptsFolderBatches); Assert.False(peer.AcceptsPublishedUploadChecks);
                    for (var request = 0; request < 4; request++)
                    {
                        var reply = await peer.RequestFilesAsync(new() { Operation = FileOperation.ListRoots }, deadline.Token);
                        Assert.Equal(FileStatus.AccessDenied, reply.Status); Assert.Empty(reply.Entries); Assert.Empty(reply.Content);
                    }
                }
                Assert.False(Directory.Exists(folder));
            }
            catch (Exception failure)
            {
                output.WriteLine($"Primary concurrent native failure: round={round}; pair={index}; hostAuth={first.Authenticated.Status}; guestAuth={second.Authenticated.Status}\n{failure}");
                foreach (var key in new[] { "NativeConnectionState", "NativeChannelAttached", "NativeChannelOpened" })
                    if (failure.Data.Contains(key)) output.WriteLine($"Native failure context: {key}={failure.Data[key]}");
                foreach (var task in new[] { first.Authenticated, second.Authenticated }.Concat(loops).Concat(bridges))
                    if (task.IsFaulted) output.WriteLine("Faulted native task before cleanup: " + task.Exception);
                throw;
            }
            finally
            {
                deadline.Cancel(); first.Dispose(); second.Dispose();
                foreach (var task in loops.Concat(bridges))
                    try { await task; } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
            }
        }
    }
}
