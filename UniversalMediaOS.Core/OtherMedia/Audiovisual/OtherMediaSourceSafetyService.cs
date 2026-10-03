using System.Net;

namespace UniversalMediaOS.Core.OtherMedia;

/// <summary>
/// Resolves a user-facing media/page URL through the SSRF-hardened OtherMedia
/// client before it is handed to LibVLC or the system browser.
/// </summary>
public sealed class OtherMediaSourceSafetyService
{
    private readonly HttpClient _httpClient;

    public OtherMediaSourceSafetyService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<Uri> ResolvePublicLocationAsync(
        Uri location,
        CancellationToken cancellationToken = default)
    {
        return await ResolvePublicLocationCoreAsync(
            location,
            source: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri> ResolvePublicLocationAsync(
        AudiovisualSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await ResolvePublicLocationCoreAsync(
            source.Location,
            source,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> ResolvePublicLocationCoreAsync(
        Uri location,
        AudiovisualSource? source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        await AudiovisualNetworkBoundary.EnsurePublicHostAsync(
            location,
            ResolveHostAsync,
            cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Head, location);
        AudiovisualRequestHeaderReplay.Apply(request, source);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        Uri finalLocation = response.RequestMessage?.RequestUri ?? location;
        await AudiovisualNetworkBoundary.EnsurePublicHostAsync(
            finalLocation,
            ResolveHostAsync,
            timeout.Token).ConfigureAwait(false);
        return finalLocation;
    }

    private static Task<IPAddress[]> ResolveHostAsync(
        string host,
        CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}
