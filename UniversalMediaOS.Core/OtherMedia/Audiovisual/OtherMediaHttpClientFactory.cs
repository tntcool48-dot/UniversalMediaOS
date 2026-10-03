using System.Net;
using System.Net.Sockets;

namespace UniversalMediaOS.Core.OtherMedia;

/// <summary>
/// Creates the network client used by the independent non-Anime provider stack.
/// Every connection, including redirect hops, is established only to DNS answers
/// that are public at connect time. This prevents a validated public URL from
/// being redirected or rebound to a private service while preserving legitimate
/// bounded CDN/canonical redirects.
/// </summary>
public static class OtherMediaHttpClientFactory
{
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var handler = CreatePublicNetworkHandler(allowAutoRedirect: true);

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalMediaOS/1.0 (+https://github.com/tntcool48-dot/UniversalMediaOS)");
        return client;
    }

    /// <summary>
    /// Creates a handler whose socket is connected only to an address that is
    /// still public at connection time. Callers which implement their own
    /// redirect policy can disable automatic redirects without losing the
    /// DNS-rebinding boundary.
    /// </summary>
    internal static SocketsHttpHandler CreatePublicNetworkHandler(bool allowAutoRedirect)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression =
                DecompressionMethods.Brotli |
                DecompressionMethods.Deflate |
                DecompressionMethods.GZip,
            ConnectCallback = ConnectPublicAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = false
        };
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(
                    host,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new HttpRequestException($"Could not safely resolve '{host}'.", ex);
            }
        }

        IPAddress[] publicAddresses = addresses
            .Where(AudiovisualNetworkBoundary.IsPublicAddress)
            .Distinct()
            .ToArray();
        if (publicAddresses.Length == 0)
        {
            throw new HttpRequestException(
                $"Connection to '{host}' was blocked because it is local, private, or reserved.");
        }

        Exception? lastError = null;
        foreach (IPAddress address in publicAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"Could not connect to a validated public address for '{host}'.",
            lastError);
    }
}
