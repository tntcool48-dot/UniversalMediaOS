using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class ProviderRequestCoordinator
{
    private const int MaxCacheEntries = 512;
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ProviderRuntimeState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderResponseCacheEntry> _cache =
        new(StringComparer.Ordinal);

    public ProviderRequestCoordinator(HttpClient? httpClient = null)
        : this(httpClient ?? AudiovisualHttp.SharedClient, TimeProvider.System)
    {
    }

    internal ProviderRequestCoordinator(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    // Compatibility adapter until catalog and source consumers use typed outcomes.
    public async Task<string?> GetStringAsync(
        AudiovisualProviderDefinition provider, Uri requestUri, CancellationToken token = default)
    {
        ProviderFetchResult result = await FetchAsync(provider, requestUri, token).ConfigureAwait(false);
        return result.Outcome.Status == ProviderOutcomeStatus.Success ? result.ResponseText : null;
    }

    public async Task<ProviderFetchResult> FetchAsync(
        AudiovisualProviderDefinition provider,
        Uri requestUri,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(requestUri);

        token.ThrowIfCancellationRequested();
        long started = _timeProvider.GetTimestamp();
        string safeId = System.Text.RegularExpressions.Regex.IsMatch(provider.Id ?? "", @"\A[a-zA-Z0-9_.-]{1,80}\z") ? provider.Id! : "provider";
        ProviderFetchResult Result(ProviderOutcomeStatus status, string code, string? text = null,
            bool cached = false, DateTimeOffset? retry = null, DateTimeOffset? expires = null) =>
            new(new ProviderOutcome(safeId, status, _timeProvider.GetElapsedTime(started), retry, code), text, cached, false, expires);

        if (!provider.Enabled)
            return Result(ProviderOutcomeStatus.Disabled, "provider_disabled");
        if (string.IsNullOrWhiteSpace(provider.Id))
            return Result(ProviderOutcomeStatus.NotConfigured, "provider_id_missing");
        if (!provider.IsUriAllowed(requestUri))
            return Result(ProviderOutcomeStatus.Unsupported, "uri_not_allowed");

        string cacheKey = BuildCacheKey(provider.Id, requestUri);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (TryGetCached(cacheKey, now, out string? cached))
        {
            return Result(ProviderOutcomeStatus.Success, "cache_hit", cached, cached: true,
                expires: _cache.TryGetValue(cacheKey, out var entry) ? entry.ExpiresAtUtc : null);
        }

        ProviderRuntimeState state = _states.GetOrAdd(provider.Id, _ => new ProviderRuntimeState());
        if (state.GetBackoffUntilUtc() is DateTimeOffset preflightBackoff && preflightBackoff > now)
        {
            return Result(state.LastFailureStatus, "provider_backoff", retry: preflightBackoff);
        }

        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (TryGetCached(cacheKey, now, out cached))
            {
                return Result(ProviderOutcomeStatus.Success, "cache_hit", cached, cached: true,
                expires: _cache.TryGetValue(cacheKey, out var entry) ? entry.ExpiresAtUtc : null);
            }

            if (state.GetBackoffUntilUtc() is DateTimeOffset backoffUntil && backoffUntil > now)
            {
                return Result(state.LastFailureStatus, "provider_backoff", retry: backoffUntil);
            }

            DateTimeOffset nextRequestAt = state.GetNextRequestAtUtc();
            if (nextRequestAt > now)
            {
                await Task.Delay(nextRequestAt - now, _timeProvider, token).ConfigureAwait(false);
                now = _timeProvider.GetUtcNow();
            }

            TimeSpan spacing = GetRequestSpacing(provider.RequestsPerMinute);
            state.SetNextRequestAtUtc(now.Add(spacing));

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, 60)));

            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    DateTimeOffset retryAt = GetRetryAt(response, now);
                    var status = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? ProviderOutcomeStatus.RateLimited : ProviderOutcomeStatus.Unavailable;
                    state.RegisterFailure(now, retryAt, status);
                    return Result(status, "http_" + (int)response.StatusCode, retry: retryAt);
                }

                // A missing resource is not a provider outage. Some catalog APIs use
                // 404 as end-of-index; callers decide whether it means completion.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    state.RegisterSuccess(now);
                    return Result(ProviderOutcomeStatus.NotFound, "http_404");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? ProviderOutcomeStatus.InvalidCredentials : ProviderOutcomeStatus.Unavailable;
                    state.RegisterFailure(now, GetFailureBackoff(state.ConsecutiveFailures + 1, now), status);
                    return Result(status, "http_" + (int)response.StatusCode, retry: state.GetBackoffUntilUtc());
                }

                string payload = await ReadBoundedStringAsync(
                    response.Content,
                    Math.Clamp(provider.MaxResponseBytes, 1024, 16 * 1024 * 1024),
                    timeoutCts.Token).ConfigureAwait(false);

                now = _timeProvider.GetUtcNow();
                state.RegisterSuccess(now);
                if (provider.CacheSeconds > 0)
                {
                    _cache[cacheKey] = new ProviderResponseCacheEntry(
                        provider.Id,
                        payload,
                        now.AddSeconds(Math.Clamp(provider.CacheSeconds, 0, 86_400)));
                    PruneCache(now);
                }

                return Result(ProviderOutcomeStatus.Success, "response_received", payload,
                    expires: provider.CacheSeconds > 0 ? now.AddSeconds(Math.Clamp(provider.CacheSeconds, 0, 86_400)) : null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                now = _timeProvider.GetUtcNow();
                state.RegisterFailure(now, GetFailureBackoff(state.ConsecutiveFailures + 1, now), ProviderOutcomeStatus.Timeout);
                return Result(ProviderOutcomeStatus.Timeout, "request_timeout", retry: state.GetBackoffUntilUtc());
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                now = _timeProvider.GetUtcNow();
                var status = ex is InvalidDataException ? ProviderOutcomeStatus.InvalidResponse : ProviderOutcomeStatus.Unavailable;
                state.RegisterFailure(now, GetFailureBackoff(state.ConsecutiveFailures + 1, now), status);
                return Result(status, ex is InvalidDataException ? "response_too_large" : "transport_failure", retry: state.GetBackoffUntilUtc());
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public IReadOnlyList<ProviderHealthSnapshot> GetHealthSnapshots()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return _states
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                ProviderRuntimeSnapshot runtime = pair.Value.Snapshot();
                ProviderHealthState health = runtime.BackoffUntilUtc is DateTimeOffset backoff && backoff > now
                    ? ProviderHealthState.BackingOff
                    : runtime.ConsecutiveFailures > 0
                        ? ProviderHealthState.Degraded
                        : runtime.LastSuccessUtc.HasValue
                            ? ProviderHealthState.Healthy
                            : ProviderHealthState.Unknown;

                return new ProviderHealthSnapshot
                {
                    ProviderId = pair.Key,
                    State = health,
                    ConsecutiveFailures = runtime.ConsecutiveFailures,
                    LastSuccessUtc = runtime.LastSuccessUtc,
                    LastFailureUtc = runtime.LastFailureUtc,
                    BackoffUntilUtc = runtime.BackoffUntilUtc,
                    CachedResponseCount = _cache.Values.Count(entry =>
                        entry.ProviderId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) &&
                        entry.ExpiresAtUtc > now)
                };
            })
            .ToArray();
    }

    public void Invalidate(string? providerId = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            _cache.Clear();
            _states.Clear();
            return;
        }

        foreach ((string key, ProviderResponseCacheEntry entry) in _cache)
        {
            if (entry.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }

        _states.TryRemove(providerId, out _);
    }

    private bool TryGetCached(
        string cacheKey,
        DateTimeOffset now,
        out string? payload)
    {
        if (_cache.TryGetValue(cacheKey, out ProviderResponseCacheEntry? entry))
        {
            if (entry.ExpiresAtUtc > now)
            {
                payload = entry.Payload;
                return true;
            }

            _cache.TryRemove(cacheKey, out _);
        }

        payload = null;
        return false;
    }

    private void PruneCache(DateTimeOffset now)
    {
        foreach ((string key, ProviderResponseCacheEntry entry) in _cache)
        {
            if (entry.ExpiresAtUtc <= now)
            {
                _cache.TryRemove(key, out _);
            }
        }

        int overflow = _cache.Count - MaxCacheEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (string key in _cache
                     .OrderBy(pair => pair.Value.ExpiresAtUtc)
                     .Take(overflow)
                     .Select(pair => pair.Key))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string BuildCacheKey(string providerId, Uri requestUri)
    {
        return $"{providerId.Trim().ToLowerInvariant()}\n{requestUri.AbsoluteUri}";
    }

    private static TimeSpan GetRequestSpacing(int requestsPerMinute)
    {
        return requestsPerMinute <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(1d / Math.Clamp(requestsPerMinute, 1, 600));
    }

    private static DateTimeOffset GetRetryAt(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate && retryDate > now)
        {
            return retryDate;
        }

        if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelay && retryDelay > TimeSpan.Zero)
        {
            return now.Add(retryDelay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : retryDelay);
        }

        return now.Add(DefaultBackoff);
    }

    private static DateTimeOffset? GetFailureBackoff(int failureCount, DateTimeOffset now)
    {
        if (failureCount < 3)
        {
            return null;
        }

        int exponent = Math.Min(failureCount - 3, 5);
        return now.Add(TimeSpan.FromSeconds(30 * Math.Pow(2, exponent)));
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken token)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("Provider response exceeds its configured size limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("Provider response exceeds its configured size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed record ProviderResponseCacheEntry(
        string ProviderId,
        string Payload,
        DateTimeOffset ExpiresAtUtc);

    private sealed class ProviderRuntimeState
    {
        private readonly object _sync = new();
        private DateTimeOffset _nextRequestAtUtc;
        private DateTimeOffset? _backoffUntilUtc;
        private DateTimeOffset? _lastSuccessUtc;
        private DateTimeOffset? _lastFailureUtc;
        private int _consecutiveFailures;
        private ProviderOutcomeStatus _lastFailureStatus = ProviderOutcomeStatus.Unavailable;
        public ProviderOutcomeStatus LastFailureStatus { get { lock (_sync) return _lastFailureStatus; } }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int ConsecutiveFailures
        {
            get
            {
                lock (_sync)
                {
                    return _consecutiveFailures;
                }
            }
        }

        public DateTimeOffset GetNextRequestAtUtc()
        {
            lock (_sync)
            {
                return _nextRequestAtUtc;
            }
        }

        public DateTimeOffset? GetBackoffUntilUtc()
        {
            lock (_sync)
            {
                return _backoffUntilUtc;
            }
        }

        public void SetNextRequestAtUtc(DateTimeOffset value)
        {
            lock (_sync)
            {
                _nextRequestAtUtc = value;
            }
        }

        public void RegisterSuccess(DateTimeOffset now)
        {
            lock (_sync)
            {
                _lastSuccessUtc = now;
                _consecutiveFailures = 0;
                _backoffUntilUtc = null;
            }
        }

        public void RegisterFailure(DateTimeOffset now, DateTimeOffset? backoffUntil, ProviderOutcomeStatus status)
        {
            lock (_sync)
            {
                _lastFailureUtc = now;
                _lastFailureStatus = status;
                _consecutiveFailures++;
                if (backoffUntil.HasValue &&
                    (!_backoffUntilUtc.HasValue || backoffUntil > _backoffUntilUtc))
                {
                    _backoffUntilUtc = backoffUntil;
                }
            }
        }

        public ProviderRuntimeSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new ProviderRuntimeSnapshot(
                    _consecutiveFailures,
                    _lastSuccessUtc,
                    _lastFailureUtc,
                    _backoffUntilUtc);
            }
        }
    }

    private sealed record ProviderRuntimeSnapshot(
        int ConsecutiveFailures,
        DateTimeOffset? LastSuccessUtc,
        DateTimeOffset? LastFailureUtc,
        DateTimeOffset? BackoffUntilUtc);
}
