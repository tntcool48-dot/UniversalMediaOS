using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class ArabicCartoonLaneVerifier
{
    private static readonly TimeSpan PositiveCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProductionHostSpacing = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _hostSpacing;
    private readonly ConcurrentDictionary<string, VerificationCacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostProbeState> _hosts =
        new(StringComparer.OrdinalIgnoreCase);

    public ArabicCartoonLaneVerifier(HttpClient? httpClient = null)
        : this(httpClient ?? AudiovisualHttp.SharedClient, TimeProvider.System, ProductionHostSpacing)
    {
    }

    internal ArabicCartoonLaneVerifier(
        HttpClient httpClient,
        TimeProvider timeProvider,
        TimeSpan hostSpacing)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _hostSpacing = hostSpacing < TimeSpan.Zero ? TimeSpan.Zero : hostSpacing;
    }

    public async Task<AudiovisualSource?> VerifyAsync(
        AudiovisualSource source,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requestedIdentity);

        if (!IsEligible(source, requestedIdentity, requestedUnit))
        {
            return null;
        }

        string cacheKey = source.Location.AbsoluteUri;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out VerificationCacheEntry? cached) &&
            cached.ExpiresAtUtc > now)
        {
            return cached.Verified
                ? source with
                {
                    IsArabicCartoonVerified = true,
                    VerifiedAtUtc = cached.VerifiedAtUtc
                }
                : null;
        }

        bool verified = await ProbeAsync(source.Location, token).ConfigureAwait(false);
        now = _timeProvider.GetUtcNow();
        DateTimeOffset verifiedAt = verified ? now : default;
        _cache[cacheKey] = new VerificationCacheEntry(
            verified,
            verifiedAt,
            now.Add(verified ? PositiveCacheDuration : NegativeCacheDuration));

        return verified
            ? source with
            {
                IsArabicCartoonVerified = true,
                VerifiedAtUtc = verifiedAt
            }
            : null;
    }

    public async Task<IReadOnlyList<AudiovisualSource>> VerifyManyAsync(
        IEnumerable<AudiovisualSource> sources,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var verified = new List<AudiovisualSource>();
        foreach (AudiovisualSource source in sources)
        {
            token.ThrowIfCancellationRequested();
            AudiovisualSource? result = await VerifyAsync(
                source,
                requestedIdentity,
                requestedUnit,
                token).ConfigureAwait(false);
            if (result is not null)
            {
                verified.Add(result);
            }
        }

        return verified;
    }

    private static bool IsEligible(
        AudiovisualSource source,
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit)
    {
        return requestedIdentity.Kind == AudiovisualMediaKind.Cartoon &&
               source.Identity.Kind == AudiovisualMediaKind.Cartoon &&
               source.AccessMode == AudiovisualSourceAccessMode.DirectMedia &&
               source.Lane.Equals("arabic-cartoon", StringComparison.OrdinalIgnoreCase) &&
               source.Languages.Any(AudiovisualProviderDefinition.IsArabicLanguage) &&
               source.Location.Scheme is "http" or "https" &&
               ExactAudiovisualMatcher.IsExactMatch(
                   requestedIdentity,
                   requestedUnit,
                   source.Identity,
                   source.Unit);
    }

    private async Task<bool> ProbeAsync(Uri location, CancellationToken token)
    {
        HostProbeState host = _hosts.GetOrAdd(location.IdnHost, _ => new HostProbeState());
        await host.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset nextProbeAt = host.GetNextProbeAtUtc();
            if (nextProbeAt > now)
            {
                await Task.Delay(nextProbeAt - now, _timeProvider, token).ConfigureAwait(false);
            }

            host.SetNextProbeAtUtc(_timeProvider.GetUtcNow().Add(_hostSpacing));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, location);
            using HttpResponseMessage headResponse = await _httpClient.SendAsync(
                headRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);

            if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return await ProbeWithRangeGetAsync(location, timeoutCts.Token).ConfigureAwait(false);
            }

            return headResponse.IsSuccessStatusCode &&
                   LooksLikePlayableMedia(location, headResponse.Content.Headers);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
        finally
        {
            host.Gate.Release();
        }
    }

    private async Task<bool> ProbeWithRangeGetAsync(Uri location, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        return response.IsSuccessStatusCode &&
               LooksLikePlayableMedia(location, response.Content.Headers);
    }

    private static bool LooksLikePlayableMedia(
        Uri location,
        HttpContentHeaders headers)
    {
        string mediaType = headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase))
        {
            return headers.ContentLength is null or > 0;
        }

        if (mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(mediaType))
        {
            string extension = Path.GetExtension(location.AbsolutePath);
            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private sealed record VerificationCacheEntry(
        bool Verified,
        DateTimeOffset VerifiedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed class HostProbeState
    {
        private readonly object _sync = new();
        private DateTimeOffset _nextProbeAtUtc;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset GetNextProbeAtUtc()
        {
            lock (_sync)
            {
                return _nextProbeAtUtc;
            }
        }

        public void SetNextProbeAtUtc(DateTimeOffset value)
        {
            lock (_sync)
            {
                _nextProbeAtUtc = value;
            }
        }
    }
}
