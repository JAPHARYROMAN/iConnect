using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace IConnect.Rendezvous;

internal static class CoordinatorHosting
{
    public const int MaxSignallingSockets = 80;

    public static bool HasDirectTls(HttpContext context) =>
        context.Request.IsHttps && context.Features.Get<ITlsHandshakeFeature>() is not null;

    public static void Configure(WebHostBuilderContext hosting, KestrelServerOptions options)
    {
        options.AddServerHeader = false;
        options.Limits.MaxConcurrentConnections = 128;
        options.Limits.MaxConcurrentUpgradedConnections = MaxSignallingSockets;
        options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        options.Limits.MaxRequestBodySize = 0;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);

        if (hosting.HostingEnvironment.IsDevelopment()) return;
        var config = hosting.Configuration;
        if (config.GetValue<bool>("ForwardedHeaders_Enabled") || config.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED") ||
            config.GetValue<bool>("DOTNET_FORWARDEDHEADERS_ENABLED"))
            throw new InvalidOperationException("The coordinator requires direct Kestrel TLS. Disable forwarded headers.");

        var hosts = config["AllowedHosts"]?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts is not { Length: > 0 } || hosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new InvalidOperationException("Configure AllowedHosts with explicit coordinator hostnames or addresses.");

        var endpoints = config.GetSection("Kestrel:Endpoints").GetChildren().ToArray();
        if (endpoints.Length == 0 || endpoints.Any(endpoint =>
            !Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.Port == 0))
            throw new InvalidOperationException("Configure explicit HTTPS-only Kestrel endpoints for the coordinator.");
        if (endpoints.Any(endpoint => string.IsNullOrWhiteSpace(endpoint["Certificate:Path"]) &&
            string.IsNullOrWhiteSpace(config["Kestrel:Certificates:Default:Path"])))
            throw new InvalidOperationException("Configure a certificate file for every coordinator HTTPS endpoint.");
        options.Configure(config.GetSection("Kestrel"), reloadOnChange: false);
    }
}
