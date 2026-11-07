using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using Xunit;

namespace IConnect.Tests;

public sealed class NativeUploadEngineTests
{
    private static string Sandbox()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        var sandbox = Path.Combine(root!.FullName, ".local", "native-upload-control", ProtocolRules.NewToken());
        Directory.CreateDirectory(sandbox); return sandbox;
    }
    private static Pairing Pair(DeviceIdentity host, DeviceIdentity guest)
    {
        var value = new Pairing { TransactionId = ProtocolRules.NewToken(), Token = ProtocolRules.NewToken(),
            Host = host.Descriptor, Guest = guest.Descriptor, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(110).ToUnixTimeMilliseconds() };
        var transcript = ProtocolRules.PairingTranscript(value);
        value.HostApproval = ByteString.CopyFrom(host.Sign(transcript)); value.GuestApproval = ByteString.CopyFrom(guest.Sign(transcript)); return value;
    }
    [Fact]
    public void Native_upload_fixture_rejects_missing_mutual_pairing_before_creating_authority()
    {
        using var host = DeviceIdentity.Ephemeral("Generated native host"); using var guest = DeviceIdentity.Ephemeral("Generated native guest");
        var pair = Pair(host, guest); pair.GuestApproval = ByteString.Empty; var sandbox = Sandbox();
        Assert.Throws<InvalidDataException>(() => new NativeUploadEndpoint(sandbox, host, pair));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox));
    }
    [Fact]
    public async Task Native_upload_endpoints_resume_original_receipts_preserve_collision_files_and_refuse_withdrawn_inboxes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        using var host = DeviceIdentity.Ephemeral("Generated native host"); using var guest = DeviceIdentity.Ephemeral("Generated native guest");
        var pair = Pair(host, guest); var sandbox = Sandbox();
        await using var hostFiles = new NativeUploadEndpoint(Path.Combine(sandbox, "host"), host, pair);
        await using var guestFiles = new NativeUploadEndpoint(Path.Combine(sandbox, "guest"), guest, pair);
        var session = ProtocolRules.NewToken();
        using var first = new PeerSession(host, guest.PublicKey, session, true, Path.Combine(sandbox, "host-inbox"), _ => { }, loopbackOnly: true, fileService: hostFiles.Service);
        using var second = new PeerSession(guest, host.PublicKey, session, false, Path.Combine(sandbox, "guest-inbox"), _ => { }, loopbackOnly: true, fileService: guestFiles.Service);
        var loops = new[] { first.RunAsync(timeout.Token), second.RunAsync(timeout.Token) };
        async Task Bridge(PeerSession from, PeerSession to)
        {
            await foreach (var signal in from.Rtc.Signals.ReadAllAsync(timeout.Token))
                if (!to.Rtc.TryApplyRemoteSignal(signal.Kind, signal.Body, signal.Mid)) throw new IOException("A native control signal was rejected.");
        }
        var bridges = new[] { Bridge(first, second), Bridge(second, first) };
        try
        {
            first.Rtc.Offer(); await Task.WhenAll(first.Authenticated, second.Authenticated).WaitAsync(timeout.Token);
            if (Environment.GetEnvironmentVariable("ICONNECT_EXPECTED_NATIVE_SHA256") is { Length: > 0 } expected)
            {
                var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "libDatachannel", "datachannel.dll"));
                var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(x => Path.GetFullPath(x.FileName).Equals(path, StringComparison.OrdinalIgnoreCase));
                using var input = File.OpenRead(module.FileName); Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(input)));
            }
            await Task.WhenAll(hostFiles.SendAsync(first, _ => { }, timeout.Token), guestFiles.SendAsync(second, _ => { }, timeout.Token));
            hostFiles.Revoke(); guestFiles.Revoke();
            var hostResult = JsonSerializer.SerializeToElement(await hostFiles.CheckAsync(first, timeout.Token));
            var guestResult = JsonSerializer.SerializeToElement(await guestFiles.CheckAsync(second, timeout.Token));
            Assert.Equal(hostResult.GetProperty("sentHash").GetString(), guestResult.GetProperty("receivedHash").GetString());
            Assert.Equal(guestResult.GetProperty("sentHash").GetString(), hostResult.GetProperty("receivedHash").GetString());
            foreach (var value in new[] { hostResult, guestResult })
            {
                Assert.Equal(NativeUploadEndpoint.PayloadBytes, value.GetProperty("bytes").GetInt32());
                Assert.Equal(1, value.GetProperty("preparations").GetInt32()); Assert.Equal(1, value.GetProperty("lostAcknowledgements").GetInt32());
                foreach (var field in new[] { "resumedOriginalReceipt", "collisionOriginalKept", "originalSourceKept", "publishedCopyChecked", "withdrawnInboxDenied" }) Assert.True(value.GetProperty(field).GetBoolean());
            }
        }
        finally
        {
            timeout.Cancel(); first.Dispose(); second.Dispose();
            foreach (var task in loops.Concat(bridges)) try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
}
