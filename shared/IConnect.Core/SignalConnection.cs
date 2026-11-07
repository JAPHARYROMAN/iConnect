using System.Net.WebSockets;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

public sealed class SignalConnection(WebSocket socket) : IAsyncDisposable
{
    private readonly SemaphoreSlim sendLock = new(1);
    public bool SupportsPresence { get; private set; }
    public void ConfirmRegistration(Notice notice, string expectedId)
    {
        if (notice.Message != expectedId || notice.Capabilities.Count > 16 || notice.Capabilities.Any(x => x.Length is < 1 or > 32))
            throw new InvalidDataException("Invalid coordinator registration response.");
        SupportsPresence = notice.Capabilities.Contains(PresenceTracker.Capability);
    }

    public async Task SendAsync(string kind, IMessage payload, CancellationToken ct = default)
    {
        var bytes = ProtocolRules.Pack(kind, payload).ToByteArray();
        if (bytes.Length > ProtocolRules.MaxFrameBytes) throw new InvalidDataException("Message exceeds limit.");
        await sendLock.WaitAsync(ct);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct); }
        finally { sendLock.Release(); }
    }

    public async Task<Frame?> ReceiveAsync(CancellationToken ct = default)
    {
        var bytes = new byte[ProtocolRules.MaxFrameBytes];
        var length = 0;
        while (true)
        {
            if (length == bytes.Length) throw new InvalidDataException("Message exceeds limit.");
            var result = await socket.ReceiveAsync(bytes.AsMemory(length), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("Only binary protocol frames are accepted.");
            length += result.Count;
            if (result.EndOfMessage) return ProtocolRules.Parse(bytes[..length]);
        }
    }

    public static async Task<SignalConnection> ConnectAsync(Uri server, DeviceIdentity identity,
        CancellationToken ct = default)
    {
        ValidateServer(server);
        var wsUri = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws", Path = "/signal", Query = "" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(wsUri, ct);
            var connection = new SignalConnection(socket);
            var frame = await connection.ReceiveAsync(ct) ?? throw new IOException("Server disconnected.");
            if (frame.Kind != "challenge") throw new InvalidDataException("Expected server challenge.");
            var challenge = Challenge.Parser.ParseFrom(frame.Payload);
            if (challenge.Nonce.Length != 32) throw new InvalidDataException("Invalid challenge.");
            await connection.SendAsync("register", new Registration
            {
                Identity = identity.Descriptor,
                SignalStatus = true,
                Signature = ByteString.CopyFrom(identity.Sign(ProtocolRules.RegistrationTranscript(challenge.Nonce.ToByteArray())))
            }, ct);
            var registered = await connection.ReceiveAsync(ct);
            if (registered?.Kind != "registered") throw new InvalidDataException("Device registration was rejected.");
            connection.ConfirmRegistration(Notice.Parser.ParseFrom(registered.Payload), identity.Id);
            return connection;
        }
        catch { socket.Dispose(); throw; }
    }

    public static void ValidateServer(Uri server)
    {
        if (!server.IsAbsoluteUri || !string.IsNullOrEmpty(server.UserInfo) ||
            !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment) ||
            server.AbsolutePath != "/" ||
            (server.Scheme != "https" && !(server.Scheme == "http" && server.IsLoopback)))
            throw new InvalidDataException("Use an HTTPS coordinator origin (HTTP is limited to loopback development).");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        socket.Dispose();
        sendLock.Dispose();
    }
}
