using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IConnect.Core;

public static partial class ConnectionRules
{
    // The direct-only clients support the UDP stun: form, with no credentials, URI parameters or relay scheme.
    public static string Stun(string value)
    {
        const string error="Use stun:host[:port] or stun:[IPv6][:port], or leave STUN empty.";
        if(value is null || value.Length>256) throw new InvalidDataException(error);
        if(value.Length==0) return value;
        var match=StunPattern().Match(value);
        if(!match.Success || match.Groups["port"].Success && (!int.TryParse(match.Groups["port"].Value,out var port) || port is <1 or >65535)) throw new InvalidDataException(error);
        var host=match.Groups["host"].Value;
        if(host.StartsWith('['))
        {
            if(!IPAddress.TryParse(host[1..^1],out var address) || address.AddressFamily!=AddressFamily.InterNetworkV6) throw new InvalidDataException(error);
        }
        else
        {
            var dns=host.EndsWith('.')?host[..^1]:host; var labels=dns.Split('.');
            if(host.Length>253 || labels.Any(label=>label.Length is <1 or >63 || label[0]=='-' || label[^1]=='-')) throw new InvalidDataException(error);
            if(host.All(c=>char.IsAsciiDigit(c) || c=='.') && (labels.Length!=4 || labels.Any(label=>!int.TryParse(label,out var octet) || octet>255 || label.Length>1 && label[0]=='0'))) throw new InvalidDataException(error);
        }
        return value;
    }
    [GeneratedRegex("\\Astun:(?<host>\\[[0-9A-Fa-f:.]+\\]|[A-Za-z0-9.-]+)(?::(?<port>[0-9]{1,5}))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StunPattern();
}
