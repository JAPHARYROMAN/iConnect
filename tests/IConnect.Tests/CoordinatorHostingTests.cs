using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading.RateLimiting;
using IConnect.Core;
using IConnect.Protocol;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IConnect.Tests;

public sealed class CoordinatorHostingTests
{
    private static readonly Uri Signal = new("wss://coordinator.test/signal");

    private sealed class FixtureHandshake : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => SslProtocols.Tls13;
        public TlsCipherSuite? NegotiatedCipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;
        public string HostName => "coordinator.test";
#pragma warning disable SYSLIB0058 // ITlsHandshakeFeature still requires these properties.
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes128;
        public int CipherStrength => 128;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
        public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;
        public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
    }

    private static Dictionary<string, string?> ProductionSettings() => new()
    {
        ["AllowedHosts"] = "coordinator.test",
        ["Kestrel:Endpoints:Public:Url"] = "https://0.0.0.0:8443",
        ["Kestrel:Endpoints:Public:Certificate:Path"] = "fixture.pfx"
    };

    private static WebApplicationFactory<Program> Production(Dictionary<string, string?> settings) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    private static void SetTls(HttpRequest request) => request.HttpContext.Features.Set<ITlsHandshakeFeature>(new FixtureHandshake());

    [Theory]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Kestrel:Endpoints:Public:Url", "http://0.0.0.0:8443", "HTTPS-only")]
    [InlineData("Kestrel:Endpoints:Public:Url", "", "HTTPS-only")]
    [InlineData("Kestrel:Endpoints:Public:Certificate:Path", "", "certificate file")]
    [InlineData("ForwardedHeaders_Enabled", "true", "Disable forwarded headers")]
    public async Task Production_listener_options_refuse_insecure_configuration(string key, string value, string expected)
    {
        var settings = ProductionSettings(); settings[key] = value;
        await using var factory = Production(settings);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Production_listener_options_set_bounded_transport_limits()
    {
        await using var factory = Production(ProductionSettings());
        var options = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.False(options.AddServerHeader);
        Assert.Equal(128, options.Limits.MaxConcurrentConnections);
        Assert.Equal(80, options.Limits.MaxConcurrentUpgradedConnections);
        Assert.Equal(0, options.Limits.MaxRequestBodySize);
        Assert.Equal(16 * 1024, options.Limits.MaxRequestHeadersTotalSize);
    }

    [Fact]
    public async Task Production_requires_tls_transport_and_rejects_an_unapproved_host_origin_and_query()
    {
        await using var factory = Production(ProductionSettings());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var forwarded = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "http"; context.Request.Method = "GET"; context.Request.Host = new("coordinator.test");
            context.Request.Path = "/health"; context.Request.Headers["X-Forwarded-Proto"] = "https";
        }, deadline.Token);
        Assert.Equal(400, forwarded.Response.StatusCode);
        Assert.Equal("no-store", forwarded.Response.Headers.CacheControl.ToString());
        await Refused(factory.Server.CreateWebSocketClient(), Signal, 400, deadline.Token);

        var otherHost = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https"; context.Request.Method = "GET"; context.Request.Host = new("other.test");
            context.Request.Path = "/health"; SetTls(context.Request);
        }, deadline.Token);
        Assert.Equal(400, otherHost.Response.StatusCode);
        var health = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https"; context.Request.Method = "GET"; context.Request.Host = new("coordinator.test");
            context.Request.Path = "/health"; SetTls(context.Request);
        }, deadline.Token);
        Assert.Equal(200, health.Response.StatusCode);
        Assert.Equal("no-store", health.Response.Headers.CacheControl.ToString());

        var browser = factory.Server.CreateWebSocketClient();
        browser.ConfigureRequest = request => { SetTls(request); request.Headers.Origin = "https://coordinator.test"; };
        await Refused(browser, Signal, 400, deadline.Token);
        var native = factory.Server.CreateWebSocketClient(); native.ConfigureRequest = SetTls;
        await Refused(native, new Uri(Signal + "?generated=ignored"), 400, deadline.Token);
        using var socket = await native.ConnectAsync(Signal, deadline.Token);
        await using var connection = new SignalConnection(socket);
        Assert.Equal("challenge", (await connection.ReceiveAsync(deadline.Token))!.Kind);
    }

    [Fact]
    public async Task Admission_limits_unregistered_sockets_and_releases_slots_after_a_refusal()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development")
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<ConcurrencyLimiter>();
                services.AddSingleton(_ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));
            }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(Signal, deadline.Token);
        await using var pending = new SignalConnection(socket);
        Assert.Equal("challenge", (await pending.ReceiveAsync(deadline.Token))!.Kind);
        await Refused(client, Signal, 503, deadline.Token);
        await pending.SendAsync("invite", new Notice(), deadline.Token);
        Assert.Null(await pending.ReceiveAsync(deadline.Token));
        var limiter = factory.Services.GetRequiredService<ConcurrencyLimiter>();
        while (limiter.GetStatistics()!.CurrentAvailablePermits == 0) await Task.Delay(10, deadline.Token);
        using var retry = await client.ConnectAsync(Signal, deadline.Token);
        await using var admitted = new SignalConnection(retry);
        Assert.Equal("challenge", (await admitted.ReceiveAsync(deadline.Token))!.Kind);
    }

    [Fact]
    public async Task Stopping_closes_a_pending_signalling_socket()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health", deadline.Token)).StatusCode);
        var client = factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(Signal, deadline.Token);
        await using var connection = new SignalConnection(socket);
        Assert.Equal("challenge", (await connection.ReceiveAsync(deadline.Token))!.Kind);
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.Null(await connection.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task IPv4_and_mapped_IPv6_share_one_connection_rate_limit()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ip = IPAddress.Parse("192.0.2.10");
        for (var i = 0; i <= 20; i++)
        {
            var address = i % 2 == 0 ? ip : ip.MapToIPv6();
            var response = await factory.Server.SendAsync(context =>
            {
                context.Request.Scheme = "https"; context.Request.Method = "GET"; context.Request.Host = new("coordinator.test");
                context.Request.Path = "/signal"; context.Connection.RemoteIpAddress = address;
            }, deadline.Token);
            Assert.Equal(i < 20 ? 400 : 429, response.Response.StatusCode);
        }
    }

    private static async Task Refused(WebSocketClient client, Uri uri, int status, CancellationToken ct)
    {
        var error = await Record.ExceptionAsync(async () => { using var socket = await client.ConnectAsync(uri, ct); });
        Assert.NotNull(error);
        Assert.Contains(status.ToString(), error.Message);
    }
}
