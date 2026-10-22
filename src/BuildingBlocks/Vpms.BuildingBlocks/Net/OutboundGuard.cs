using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Vpms.BuildingBlocks.Net;

/// <summary>The destination was refused by policy (it is, or resolves to, an internal address). Retrying cannot help.</summary>
public sealed class OutboundBlockedException() : HttpRequestException("The destination is not allowed.");

/// <summary>Which addresses count as "inside": a server-side request to one of them could reach services the caller must not.</summary>
public static class AddressRules
{
    public static bool IsInternalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsInternal(ip);
    }

    /// <summary>True for anything that is not an ordinary public unicast address: private, loopback, link-local, carrier-grade NAT, multicast, documentation, reserved, and IPv6 forms that embed such an IPv4 address.</summary>
    public static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast) return true;
            var v6 = ip.GetAddressBytes();
            // NAT64 64:ff9b::/96 and 6to4 2002::/16 carry an IPv4 address; Teredo 2001::/32 and documentation 2001:db8::/32 are never a public destination.
            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b) return IsInternal(new IPAddress(v6[12..16]));
            if (v6[0] == 0x20 && v6[1] == 0x02) return IsInternal(new IPAddress(v6[2..6]));
            if (v6[0] == 0x20 && v6[1] == 0x01 && ((v6[2] == 0x00 && v6[3] == 0x00) || (v6[2] == 0x0d && v6[3] == 0xb8))) return true;
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127
               || (b[0] == 100 && b[1] is >= 64 and <= 127)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 198 && b[1] is 18 or 19)
               || (b[0] == 198 && b[1] == 51 && b[2] == 100)
               || (b[0] == 203 && b[1] == 0 && b[2] == 113)
               || b[0] >= 224;
    }
}

/// <summary>
/// Outbound connections to addresses an administrator typed in (webhooks, mail servers, the ERP, identity providers). The name is resolved once, every
/// resulting address is checked, and the connection goes to the checked address, so a name that later points inside (DNS rebinding) or that resolves
/// to a private address is refused. Redirects are never followed by clients built here.
/// </summary>
public static class OutboundHttp
{
    public static SocketsHttpHandler CreateHandler(bool allowInternal) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var socket = await ConnectSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowInternal, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    };

    /// <param name="resolve">Name resolution; replaceable so tests can simulate a name that points inside.</param>
    public static async Task<Socket> ConnectSocketAsync(string host, int port, bool allowInternal, CancellationToken ct, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        if (!allowInternal && AddressRules.IsInternalHost(host)) throw new OutboundBlockedException();
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await (resolve ?? ((h, c) => Dns.GetHostAddressesAsync(h, c)))(host, ct);
        if (addresses.Length == 0) throw new HttpRequestException($"The host '{host}' did not resolve.");
        if (!allowInternal && addresses.Any(AddressRules.IsInternal))
        {
            throw new OutboundBlockedException();
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                if (ex is OperationCanceledException) throw;
            }
        }

        if (last is SocketException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last).Throw();
        throw new HttpRequestException($"Could not connect to '{host}'.", last);
    }
}
