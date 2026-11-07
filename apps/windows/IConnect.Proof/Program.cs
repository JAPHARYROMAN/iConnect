using System.Text.RegularExpressions;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Storage.Windows;
using IConnect.Protocol;
using QRCoder;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("The Windows proof host requires Windows x64."); return 1; }
string Option(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
if (args.Contains("--help"))
{
    Console.WriteLine("iConnect Phase 0: --server <https-origin> --name <label> --profile <local-profile> --stun <stun:host:port> [--reset-owner-policy]");
    return 0;
}
var profile = Option("--profile", "default");
var name = Option("--name", Environment.MachineName);
if (!Regex.IsMatch(profile, "^[A-Za-z0-9_-]{1,32}$") || name.Length is < 1 or > 64 || name.Any(char.IsControl))
{ Console.Error.WriteLine("Invalid profile or device name."); return 1; }
using var identity = DeviceIdentity.OpenWindows("iConnect.Proof.v1." + profile, name);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var server = new Uri(Option("--server", "http://localhost:5078"));
var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iConnect", "proof", profile);
try
{
    using var trust = new TrustedPeerStore(state, identity.Id);
    if (args.Contains("--reset-owner-policy")) trust.ResetOwnerPolicy();
    if (!trust.OwnerReady) { Console.Error.WriteLine(trust.OwnerBinding!.Issue + " Review the profile, then use --reset-owner-policy to archive trust/approvals and require fresh pairing. Files are kept."); return 1; }
    var connection = await SignalConnection.ConnectAsync(server, identity, shutdown.Token);
    var grants = new StorageGrantStore(state, ownerBinding: trust.OwnerBinding);
    await using var downloadSources = new DownloadSourceStore(grants, state);
    await using var client = new ProofClient(identity, connection, server, state, Console.WriteLine,
        args.Contains("--stun") ? Option("--stun", "") : null,
        fileServiceFactory: _ => new WindowsFileService(grants, downloadSources), trustStore: trust);
    client.InvitationCreated += uri =>
    {
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        using var svg = new SvgQRCode(data);
        var path = Path.Combine(state, "pairing.svg");
        File.WriteAllText(path, svg.GetGraphic(6));
        Console.WriteLine($"\nPairing invitation (expires in two minutes):\n{uri}\nQR image: {path}");
    };
    client.PairingRequested += pairing =>
    {
        Console.WriteLine($"\nCompare this code on both devices: {ProtocolRules.VerificationCode(pairing)}");
        Console.WriteLine("Pairing trusts the device; storage sharing is approved separately. Type: approve <code from the other device>, or reject.");
    };
    Console.WriteLine($"iConnect connectivity proof | {name}\nDevice: {identity.Id}\nState: {state}");
    Console.WriteLine("Commands: pair | join <invitation> | approve <code> | reject | devices | connect <id> | send | share <path> | shares | unshare <grant-id> | roots | ls <folder-id> | cat <file-id> <version> | revoke <id> | quit");
    var receive = client.RunAsync(shutdown.Token);
    while (!shutdown.IsCancellationRequested && !receive.IsCompleted)
    {
        var read = Console.In.ReadLineAsync(shutdown.Token).AsTask();
        if (await Task.WhenAny(read, receive) == receive) break;
        var line = await read;
        if (line is null) break;
        var command = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (command.Length == 0) continue;
        try
        {
            string Argument() => command.Length == 2 ? command[1] : throw new InvalidDataException("This command needs an argument.");
            switch (command[0])
            {
                case "pair": await client.CreateInvitationAsync(shutdown.Token); break;
                case "join": await client.JoinInvitationAsync(Argument(), shutdown.Token); break;
                case "approve": await client.ApproveAsync(Argument(), shutdown.Token); break;
                case "reject": await client.RejectAsync(shutdown.Token); break;
                case "devices": foreach (var peer in client.Peers) Console.WriteLine($"{peer.Name}  {peer.Id}"); break;
                case "connect": await client.ConnectPeerAsync(Argument(), shutdown.Token); break;
                case "send": await client.SendTestFileAsync(shutdown.Token); break;
                case "roots":
                case "ls":
                {
                    var request = new FileRequest { Operation = command[0] == "roots" ? FileOperation.ListRoots : FileOperation.ListDirectory,
                        NodeId = command[0] == "roots" ? "" : Argument() };
                    var response = await client.RequestFilesAsync(request, shutdown.Token);
                    Console.WriteLine(response.Status == FileStatus.Ok ? "Approved phone items:" : response.Message);
                    foreach (var entry in response.Entries) Console.WriteLine($"{entry.Id}  {(entry.IsDirectory ? "Folder" : "File")}  {entry.Name}  {entry.Size} bytes\n  Version: {entry.Version}");
                    if (response.Continuation.Length > 0) Console.WriteLine($"More items: next {request.NodeId} {response.Continuation}");
                    break;
                }
                case "next":
                {
                    var values = Argument().Split(' ', 2);
                    if (values.Length != 2) throw new InvalidDataException("Use next <folder-id> <page-token>.");
                    var response = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ListDirectory,
                        NodeId = values[0], Continuation = values[1] }, shutdown.Token);
                    Console.WriteLine(response.Message);
                    foreach (var entry in response.Entries) Console.WriteLine($"{entry.Id}  {entry.Name}  {entry.Size} bytes\n  Version: {entry.Version}");
                    if (response.Continuation.Length > 0) Console.WriteLine($"More items: next {values[0]} {response.Continuation}");
                    break;
                }
                case "cat":
                {
                    var values = Argument().Split(' ', 2);
                    if (values.Length != 2) throw new InvalidDataException("Use cat <file-id> <version> from the listing.");
                    var response = await client.RequestFilesAsync(new FileRequest { Operation = FileOperation.ReadRange, NodeId = values[0],
                        ExpectedVersion = values[1], Length = StorageRules.MaxReadRange }, shutdown.Token);
                    if (response.Status != FileStatus.Ok) Console.WriteLine(response.Message);
                    else
                    {
                        var text = System.Text.Encoding.UTF8.GetString(response.Content.Span);
                        // Remote bytes must not issue terminal escape/control sequences.
                        Console.WriteLine(new string(text.Select(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t') ? '\uFFFD' : c).ToArray()));
                    }
                    break;
                }
                case "share":
                {
                    var peer = client.Peers.Count == 1 ? client.Peers.Single() : throw new InvalidDataException("This console sharing command requires exactly one paired device.");
                    var grant = grants.Add(peer.Id, Argument().Trim('"'));
                    Console.WriteLine($"Read-only location approved for {peer.Name}: {grant.DisplayPath}\nGrant: {grant.Id}");
                    break;
                }
                case "shares":
                    foreach (var grant in grants.All) Console.WriteLine($"{grant.Id}  {grant.Label}  {grant.Permissions}  {grant.DisplayPath}");
                    break;
                case "unshare": grants.Remove(Argument()); Console.WriteLine("Storage access removed."); break;
                case "revoke": grants.RevokePeer(Argument()); client.Revoke(Argument()); break;
                case "quit": shutdown.Cancel(); break;
                default: Console.WriteLine("Unknown command. Use pair, devices, connect, send, or quit."); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine(ex.Message); }
    }
    shutdown.Cancel();
    try { await receive; } catch (OperationCanceledException) { }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception ex) { Console.Error.WriteLine($"iConnect stopped: {ex.Message}"); return 1; }
