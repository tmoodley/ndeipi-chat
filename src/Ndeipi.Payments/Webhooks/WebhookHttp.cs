using System.Net;
using System.Net.Sockets;

namespace Ndeipi.Payments.Webhooks;

/// <summary>
/// The HTTP client deliveries go out on. Integrators choose the URLs, so the client only connects
/// to public addresses: the host is resolved at connect time and loopback, private, link-local,
/// carrier-grade NAT and other internal ranges are refused. Otherwise an endpoint could aim the
/// server at its own network. Redirects are not followed, for the same reason.
/// </summary>
public static class WebhookHttp
{
    public const string ClientName = "webhooks";

    public static IServiceCollection AddWebhookHttp(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, http =>
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Ndeipi-Webhooks/1.0");
                // The dispatcher applies the delivery timeout per request.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = ConnectToPublicAddressAsync
            });
        return services;
    }

    static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether a delivery may connect to this address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // "this" network
                || b[0] == 10                                    // private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // carrier-grade NAT
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, including cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                  // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // benchmarking
                || b[0] >= 224);                                 // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                       // unique local, fc00::/7
        }

        return false;
    }
}
