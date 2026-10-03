using System.Net;
using System.Net.Sockets;

namespace UniversalMediaOS.Core.OtherMedia;

internal static class AudiovisualNetworkBoundary
{
    public static async Task EnsurePublicHostAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(resolver);

        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.IdnHost) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            IsLocalHostName(uri.IdnHost))
        {
            throw new InvalidDataException("Remote provider manifests must use a public HTTP(S) host.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver(uri.IdnHost, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Remote host '{uri.IdnHost}' could not be resolved safely.",
                    ex);
            }
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new InvalidDataException(
                $"Remote host '{uri.IdnHost}' resolves to a local, private, or reserved address.");
        }
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0 &&
                   bytes[0] != 10 &&
                   bytes[0] != 127 &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                   bytes[0] < 224;
        }

        return !address.IsIPv6LinkLocal &&
               !address.IsIPv6Multicast &&
               !address.IsIPv6SiteLocal &&
               (bytes[0] & 0xFE) != 0xFC;
    }

    private static bool IsLocalHostName(string host)
    {
        string normalized = host.TrimEnd('.');
        return normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
}
