using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;

// Explicit transport fixture: stdin/stdout carry setup and native signaling only.
// File payloads, readiness, signatures and acknowledgements use production peers.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
if (args.Length is not (8 or 10)) throw new ArgumentException("Supply --run, --role, --native-sha, --directory and optional --suite.");
for (var i = 0; i < args.Length; i += 2)
    if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate fixture option.");
var run = options["--run"];
var role = options["--role"];
var expectedNative = options["--native-sha"];
var suite = options.GetValueOrDefault("--suite", "frames");
var batchSuite = suite is "batches" or "blind-batches";
if (!Regex.IsMatch(run, "\\A[A-Za-z0-9_-]{43}\\z") || role is not ("windows" or "android") ||
    !Regex.IsMatch(expectedNative, "\\A[a-f0-9]{64}\\z") || suite is not ("frames" or "uploads" or "batches" or "blind-batches")) throw new ArgumentException("Invalid fixture options.");
var directory = Path.GetFullPath(options["--directory"]);
if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("The fixture directory already exists.");
Directory.CreateDirectory(directory);
var inbox = Path.Combine(directory, "received");
using var identity = DeviceIdentity.Ephemeral("Native interoperability fixture");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(suite != "frames" ? 5 : 3));
var outputGate = new object();
void Emit(object value)
{
    lock (outputGate) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
}
string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
async Task<JsonDocument> Read()
{
    var line = await Console.In.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException("The fixture controller ended.");
    if (line.Length is < 2 or > 131072) throw new InvalidDataException("Oversized fixture command.");
    return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
}
string? sentHash = null;
void Report(string value)
{
    const string prefix = "Sent 256 KiB; destination verified SHA-256: ";
    if (value.StartsWith(prefix, StringComparison.Ordinal))
    {
        var hash = value[prefix.Length..];
        if (!Regex.IsMatch(hash, "\\A[a-f0-9]{64}\\z")) throw new InvalidDataException("Invalid sender result.");
        sentHash = hash;
    }
    Emit(new { @event = "report", message = value });
}
Emit(new { @event = "identity", run, role, key = Encode(identity.PublicKey), device = identity.Id });
using var first = await Read();
if (first.RootElement.GetProperty("op").GetString() != "peer") throw new InvalidDataException("Bind the explicit peer first.");
var peerKey = Decode(first.RootElement.GetProperty("key").GetString() ?? "");
if (peerKey.Length is < 64 or > 256) throw new InvalidDataException("Invalid fixture peer key.");
Pairing? pairing = null;
if (suite != "frames")
{
    var encodedPairing = first.RootElement.GetProperty("pairing").GetString() ?? "";
    if (encodedPairing.Length is < 1 or > 8192) throw new InvalidDataException("Pairing setup exceeds its fixture bound.");
    pairing = Pairing.Parser.ParseFrom(Decode(encodedPairing));
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    if (!pairing.Host.PublicKey.Span.SequenceEqual(identity.PublicKey) || !pairing.Guest.PublicKey.Span.SequenceEqual(peerKey) ||
        !ProtocolRules.TokenPattern().IsMatch(pairing.TransactionId) || !ProtocolRules.TokenPattern().IsMatch(pairing.Token) ||
        pairing.ExpiresAt <= now || pairing.ExpiresAt > now + 120_000 || pairing.HostApproval.Length != 0 ||
        !ProtocolRules.Verify(peerKey, ProtocolRules.PairingTranscript(pairing), pairing.GuestApproval.ToByteArray()))
        throw new InvalidDataException("The guest's explicit mutual-pairing setup was rejected.");
    pairing.HostApproval = ByteString.CopyFrom(identity.Sign(ProtocolRules.PairingTranscript(pairing)));
}
await using var uploads = pairing is null ? null : new NativeUploadEndpoint(directory, identity, pairing, listForBatches: suite == "batches");
if (batchSuite) uploads!.PrepareBatchDataset();
using var peer = new PeerSession(identity, peerKey, run, role == "windows", inbox, Report, fileService: uploads?.Service);
var nativePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "libDatachannel", "datachannel.dll"));
var loaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(module =>
    string.Equals(Path.GetFullPath(module.FileName), nativePath, StringComparison.OrdinalIgnoreCase));
using (var native = File.OpenRead(loaded.FileName))
    if (Convert.ToHexStringLower(SHA256.HashData(native)) != expectedNative)
        throw new InvalidDataException("The loaded native module differs from the reviewed candidate.");
Emit(new { @event = "bound", nativeSha256 = expectedNative, peer = peer.RemoteId, suite, pairing = pairing is null ? "" : Encode(pairing.ToByteArray()) });
var running = peer.RunAsync(deadline.Token);
var signaling = Task.Run(async () =>
{
    var count = 0;
    await foreach (var signal in peer.Rtc.Signals.ReadAllAsync(deadline.Token))
    {
        if (++count > 128) throw new InvalidDataException("Native signaling exceeded the fixture bound.");
        Emit(new { @event = "signal", kind = signal.Kind, body = signal.Body, mid = signal.Mid });
    }
}, deadline.Token);
var readiness = Task.Run(async () =>
{
    await peer.Authenticated.WaitAsync(deadline.Token);
    Emit(new { @event = "authenticated" });
}, deadline.Token);
Task? sending = null;
try
{
    if (role == "windows" && suite == "frames") peer.Rtc.Offer();
    Task<JsonDocument>? next = null;
    var sendingStarted = false;
    while (true)
    {
        if (sending?.IsFaulted == true) await sending;
        next ??= Read();
        var completed = sending is { IsCompleted: false } ? await Task.WhenAny(next, running, sending) : await Task.WhenAny(next, running);
        if (completed == running) { await running; throw new IOException("Peer ended before fixture completion."); }
        if (completed == sending) { await sending!; sending = null; continue; }
        using var command = await next;
        next = null;
        var value = command.RootElement;
        switch (value.GetProperty("op").GetString())
        {
            case "start" when uploads is not null:
                if (role == "windows") peer.Rtc.Offer();
                break;
            case "signal":
                if (!peer.Rtc.TryApplyRemoteSignal(value.GetProperty("kind").GetString() ?? "", value.GetProperty("body").GetString() ?? "",
                        value.GetProperty("mid").GetString() ?? "")) throw new IOException("Native signal was rejected.");
                break;
            case "send":
                if (uploads is not null)
                {
                    if (sendingStarted) throw new InvalidDataException("The fixture allows one private upload.");
                    sendingStarted = true; sending = batchSuite ? uploads.SendBatchAsync(peer, Emit, deadline.Token) : uploads.SendAsync(peer, Emit, deadline.Token); break;
                }
                await peer.SendTestFileAsync(deadline.Token);
                if (sentHash is null) throw new InvalidDataException("The source hash was not observed.");
                Emit(new { @event = "sent", sha256 = sentHash, bytes = 256 * 1024 });
                break;
            case "check":
                if (uploads is not null)
                {
                    Emit(batchSuite ? await uploads.CheckBatchAsync(peer, value.GetProperty("names").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(), deadline.Token) :
                        await uploads.CheckAsync(peer, deadline.Token)); break;
                }
                var denied = await peer.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListRoots }, deadline.Token);
                if (denied.Status != FileStatus.AccessDenied || denied.Entries.Count != 0 || denied.Content.Length != 0)
                    throw new InvalidDataException("A peer without a storage service exposed file authority.");
                var file = Directory.GetFiles(inbox, "*.bin").Single();
                if (new FileInfo(file).Length != 256 * 1024 || Directory.GetFiles(inbox, "*.part").Length != 0 || sentHash is null)
                    throw new InvalidDataException("The received payload did not settle exactly once.");
                var receivedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, deadline.Token)));
                var pair = peer.Rtc.SelectedPair();
                string Kind(string candidate)
                {
                    var match = Regex.Match(candidate, "(?:^| )typ (host|srflx|prflx)(?: |$)");
                    return match.Success ? match.Groups[1].Value : throw new InvalidDataException("A non-direct candidate was selected.");
                }
                Emit(new { @event = "verified", sentHash, receivedHash, bytes = 256 * 1024, localType = Kind(pair.Local), remoteType = Kind(pair.Remote) });
                break;
            case "revoke" when uploads is not null:
                if (batchSuite) uploads.RevokeBatch(); else uploads.Revoke();
                Emit(new { @event = "revoked" }); break;
            case "finish": return;
            default: throw new InvalidDataException("Unknown fixture command.");
        }
    }
}
finally
{
    deadline.Cancel();
    peer.Dispose();
    if (sending is not null) try { await sending; } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    foreach (var task in new[] { running, signaling, readiness })
        try { await task; } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
}
