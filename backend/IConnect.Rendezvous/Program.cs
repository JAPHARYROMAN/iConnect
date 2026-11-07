using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Rendezvous;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(CoordinatorHosting.Configure);
if (!builder.Environment.IsDevelopment()) builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<PairingBroker>();
builder.Services.AddHostedService<PairingReaper>();
builder.Services.AddSingleton(_ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = CoordinatorHosting.MaxSignallingSockets, QueueLimit = 0
}));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("connections", context =>
    {
        var ip = context.Connection.RemoteIpAddress;
        var key = (ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip)?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});
var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
var development = app.Environment.IsDevelopment();
app.MapGet("/health", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!development && !CoordinatorHosting.HasDirectTls(context)) return Results.BadRequest();
    return app.Lifetime.ApplicationStopping.IsCancellationRequested
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { service = "iConnect rendezvous", protocol = 1, fileRelay = false });
});
app.Map("/signal", async (HttpContext context, PairingBroker broker, ConcurrencyLimiter sockets, TimeProvider clock) =>
{
    var loopback = context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
        context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);
    if (!CoordinatorHosting.HasDirectTls(context) && !(development && (context.Request.IsHttps || loopback)))
    {
        context.Response.StatusCode = 400;
        return;
    }
    // Native clients do not send an Origin; a browser cannot use this as a public signalling proxy.
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin") || context.Request.QueryString.HasValue)
    {
        context.Response.StatusCode = 400;
        return;
    }
    using var admission = sockets.AttemptAcquire();
    if (!admission.IsAcquired || app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await using var connection = new SignalConnection(socket);
    string? deviceId = null;
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    lifetime.CancelAfter(TimeSpan.FromHours(1));
    try
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var nonce = RandomNumberGenerator.GetBytes(32);
        await connection.SendAsync("challenge", new Challenge { Nonce = ByteString.CopyFrom(nonce) }, handshake.Token);
        var frame = await connection.ReceiveAsync(handshake.Token);
        if (frame?.Kind != "register") throw new InvalidDataException("Registration required.");
        var registration = Registration.Parser.ParseFrom(frame.Payload);
        if (registration.Identity is null || registration.Identity.Name.Length is < 1 or > 64 ||
            registration.Identity.Name.Any(char.IsControl) || !ProtocolRules.Verify(registration.Identity.PublicKey.ToByteArray(),
                ProtocolRules.RegistrationTranscript(nonce), registration.Signature.ToByteArray()))
            throw new InvalidDataException("Registration rejected.");
        deviceId = ProtocolRules.DeviceId(registration.Identity.PublicKey.ToByteArray());
        broker.Register(deviceId, registration.Identity, connection, registration.SignalStatus);
        var registered = new Notice { Message = deviceId }; registered.Capabilities.Add(PresenceTracker.Capability);
        await connection.SendAsync("registered", registered, lifetime.Token);
        var windowStart = clock.GetTimestamp();
        var count = 0;
        while ((frame = await connection.ReceiveAsync(lifetime.Token)) is not null)
        {
            if (clock.GetElapsedTime(windowStart) >= TimeSpan.FromMinutes(1))
            { windowStart = clock.GetTimestamp(); count = 0; }
            if (++count > 240) throw new InvalidDataException("Message rate exceeded.");
            await broker.HandleAsync(deviceId, connection, frame, lifetime.Token);
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidProtocolBufferException or
        CryptographicException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
    {
        // Deliberately omit payloads, identities, invitation tokens, and remote addresses.
        app.Logger.LogDebug("A signalling connection ended: {Category}", ex.GetType().Name);
    }
    finally { if (deviceId is not null) broker.Unregister(deviceId, connection); }
}).RequireRateLimiting("connections");
app.Run();

public partial class Program;
