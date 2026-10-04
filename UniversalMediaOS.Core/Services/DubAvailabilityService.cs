using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.Core.Services
{
    public enum DubAvailabilityCheckMode
    {
        Summary,
        Verified
    }

    public enum DubAvailabilityConfidence
    {
        Unknown,
        Summary,
        Verified
    }

    public sealed record DubAvailabilityResult(
        bool Checked,
        int SubEpisodes,
        int DubEpisodes,
        string Source,
        string Detail)
    {
        public DubAvailabilityConfidence Confidence { get; init; } =
            Checked ? DubAvailabilityConfidence.Summary : DubAvailabilityConfidence.Unknown;

        public bool Verified => Confidence == DubAvailabilityConfidence.Verified;

        /// <summary>
        /// Highest standard episode for which every episode from one through this number has a dub.
        /// Aggregate badge results deliberately leave this at zero because a count cannot prove continuity.
        /// </summary>
        public int HighestContiguousDubEpisode { get; init; }

        /// <summary>Exact distinct provider episode numbers, populated only by verified lookups.</summary>
        public IReadOnlyList<decimal> DubbedEpisodeNumbers { get; init; } = Array.Empty<decimal>();
        public IReadOnlyList<DubProviderUpdate> ProviderOutcomes { get; init; } = Array.Empty<DubProviderUpdate>();
    }

    public enum DubProviderOutcome { Pending, Completed, TimedOut, Unavailable, Backoff, IdentityRejected }

    public sealed record DubProviderUpdate(string Provider, DubProviderOutcome Outcome, DubAvailabilityResult? Result = null);

    public sealed record DubAvailabilityUpdate(DubAvailabilityResult Result, IReadOnlyList<DubProviderUpdate> Providers, bool IsComplete)
    {
        public long Sequence { get; init; }
        public int PendingProviders => Providers.Count(provider => provider.Outcome == DubProviderOutcome.Pending);
    }

    public sealed record DubAvailabilityProviderConfig
    {
        public string Name { get; init; } = string.Empty;
        public string SuggestUrlTemplate { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public int TimeoutSeconds { get; init; } = 8;
        public string ParserType { get; init; } = "Badge";

        /// <summary>
        /// Optional explicit provider adapter. "AniKoto" enables exact card discovery and episode-list verification.
        /// ParserType values AniKoto/AniKotoVerified/AniKotoSummary remain accepted for compatibility.
        /// </summary>
        public string AdapterType { get; init; } = string.Empty;

        [JsonIgnore]
        public bool IsBuiltIn { get; init; }
    }

    /// <summary>
    /// Looks for optional provider-side sub/dub episode badges. AniList and MAL do not expose this data.
    /// Dub availability is advisory only; playback still searches dub-capable sources when requested.
    /// </summary>
    public sealed class DubAvailabilityService
    {
        public const string ProviderConfigKey = "DubAvailabilityProviders";
        public const string UnknownDetail = "Dub availability unknown; playback can still search dub sources.";
        private const string MalMismatchDetail = "Provider episode metadata did not match the expected MAL title.";

        private const string DesktopUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

        private static readonly Regex NumericBadgeRegex = new(
            @"tick-(?:item\s+)?(?<kind>sub|dub)[^>]*>\s*(?<count>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClassBadgeRegex = new(
            @"class\s*=\s*[""'][^""']*\b(?<kind>sub|dub)\b[^""']*[""'][^>]*>\s*(?:<[^>]*>\s*)*(?<count>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string ResultCardClassPattern = @"(?:(?<![-\w])item(?![-\w])|(?<![-\w])flw-item(?![-\w]))";

        private static readonly Regex ResultItemRegex = new(
            $@"<div\s+class\s*=\s*[""'][^""']*{ResultCardClassPattern}[^""']*[""'][^>]*>.*?(?=<div\s+class\s*=\s*[""'][^""']*{ResultCardClassPattern}|</body>|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AnchorTextRegex = new(
            @"<a\b(?<attrs>[^>]*)>(?<title>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AttributeTitleRegex = new(
            @"\b(?:data-jp|title|alt)\s*=\s*[""'](?<title>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HtmlTagRegex = new(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private static readonly Regex SeasonMarkerRegex = new(
            @"\b(?:(?:season|s)\s*(?<number>\d+)|(?<ordinal>\d+)(?:st|nd|rd|th)\s+season)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PartMarkerRegex = new(
            @"\bpart\s*(?<number>\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataTipRegex = new(
            @"\bdata-tip\s*=\s*[""'](?<id>\d+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StartTagRegex = new(
            @"<a\b(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex HtmlAttributeRegex = new(
            @"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:[""'](?<quoted>.*?)[""']|(?<bare>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private const int MinimumCardTitleScore = 250;
        private const int MaxAutoProbeAttempts = 24;
        private const int MaxAutoProbeTitles = 2;
        private const int DefaultMaxResponseBytes = 2 * 1024 * 1024;
        private const int MaxCacheEntries = 1000;
        private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ProductionRequestSpacing = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan FinishedPositiveTtl = TimeSpan.FromHours(12);
        private static readonly TimeSpan FinishedZeroTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan FinishedUnknownTtl = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ReleasingPositiveTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ReleasingZeroTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ReleasingUnknownTtl = TimeSpan.FromSeconds(45);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly string[] CommonSearchPaths =
        [
            "/filter",
            "/ajax/search/suggest",
            "/search/suggest",
            "/search",
            "/anime",
            "/ajax/search",
            "/anime/search",
            "/api/search",
            "/api/anime/search",
            "/api/v1/search"
        ];

        private static readonly string[] CommonQueryNames =
        [
            "keyword",
            "query",
            "q",
            "search",
            "term",
            "s",
            "text",
            "name"
        ];

        private readonly HttpClient _httpClient;
        private readonly DomainHotSwapper? _config;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _minimumRequestSpacing;
        private readonly int _maxResponseBytes;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AniKotoDiscoveryEntry> _aniKotoDiscoveries = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, LookupFlight> _singleFlights = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ProviderHostState> _providerHosts = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<DubAvailabilityProviderConfig> _providers;

        public DubAvailabilityService(DomainHotSwapper? config = null)
            : this(config, CreateSafeHttpClient(), TimeProvider.System, ProductionRequestSpacing, DefaultMaxResponseBytes)
        {
        }

        internal DubAvailabilityService(DomainHotSwapper? config, HttpClient httpClient)
            : this(config, httpClient, TimeProvider.System, TimeSpan.Zero, DefaultMaxResponseBytes)
        {
        }

        internal DubAvailabilityService(
            DomainHotSwapper? config,
            HttpClient httpClient,
            TimeProvider timeProvider,
            TimeSpan minimumRequestSpacing,
            int maxResponseBytes = DefaultMaxResponseBytes)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _minimumRequestSpacing = minimumRequestSpacing < TimeSpan.Zero ? TimeSpan.Zero : minimumRequestSpacing;
            _maxResponseBytes = Math.Clamp(maxResponseBytes, 1024, 16 * 1024 * 1024);
            _config = config;
            _providers = LoadProviders(config);

            if (_config != null)
            {
                _config.SettingChanged += Config_SettingChanged;
            }
        }

        private void Config_SettingChanged(object? sender, SettingChangedEventArgs e)
        {
            if (!e.Key.Equals(ProviderConfigKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _providers = LoadProviders(_config);
            _cache.Clear();
            _aniKotoDiscoveries.Clear();
            foreach (LookupFlight flight in _singleFlights.Values)
            {
                flight.Cancel();
            }

            _singleFlights.Clear();
            _providerHosts.Clear();
            AppLogger.Log($"Dub availability providers reloaded: {_providers.Count} configured provider(s).");
        }

        public async Task<DubAvailabilityResult> CheckAsync(
            MediaResult media,
            CancellationToken token = default,
            bool bypassCache = false,
            DubAvailabilityCheckMode mode = DubAvailabilityCheckMode.Verified,
            IProgress<DubAvailabilityUpdate>? progress = null)
        {
            if (media == null || string.IsNullOrWhiteSpace(media.OfficialTitle))
            {
                return new DubAvailabilityResult(false, 0, 0, string.Empty, "No title to check");
            }

            token.ThrowIfCancellationRequested();

            string key = $"{BuildCacheKey(media)}:{mode}";
            if (!bypassCache && TryGetCached(key, out DubAvailabilityResult? cached))
            {
                if (progress != null) LookupFlight.Report(progress, new(cached!, cached!.ProviderOutcomes, true));
                return cached!;
            }

            var candidate = new LookupFlight((sharedToken, publish) => LookupAndCacheAsync(key, media, mode, sharedToken, publish));
            LookupFlight flight = _singleFlights.GetOrAdd(key, candidate);
            flight.AddWaiter();
            using IDisposable? subscription = flight.Subscribe(progress);
            if (!ReferenceEquals(candidate, flight))
            {
                candidate.Cancel();
            }
            else
            {
                _ = flight.Task.ContinueWith(
                    _ => RemoveFlight(key, flight),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                return await flight.Task.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                if (flight.ReleaseWaiter() == 0 && !flight.Task.IsCompleted)
                {
                    RemoveFlight(key, flight);
                    flight.Cancel();
                }
            }
        }

        private async Task<DubAvailabilityResult> LookupAndCacheAsync(
            string key,
            MediaResult media,
            DubAvailabilityCheckMode mode,
            CancellationToken token,
            Action<DubAvailabilityUpdate> publish)
        {
            DubAvailabilityResult result = await LookupCoreAsync(media, mode, token, publish).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TimeSpan ttl = GetCacheTtl(media, result);
            if (result.ProviderOutcomes.Any(provider => provider.Outcome is DubProviderOutcome.TimedOut or DubProviderOutcome.Unavailable or DubProviderOutcome.Backoff))
                ttl = GetCacheTtl(media, Unknown(media));
            _cache[key] = new CacheEntry(result, _timeProvider.GetUtcNow().Add(ttl));
            PruneCache();
            return result;
        }

        private async Task<DubAvailabilityResult> LookupCoreAsync(
            MediaResult media,
            DubAvailabilityCheckMode mode,
            CancellationToken token,
            Action<DubAvailabilityUpdate> publish)
        {
            var enabledProviders = _providers.Where(provider => provider.Enabled).ToArray();
            var queryTitles = BuildTitleCandidates(media).Take(8).ToArray();
            if (enabledProviders.Length == 0 || queryTitles.Length == 0)
            {
                publish(new(Unknown(media), [], true));
                return Unknown(media);
            }

            var outcomes = enabledProviders.Select(provider => new DubProviderUpdate(
                NormalizeProviderName(provider.Name), DubProviderOutcome.Pending)).ToArray();
            var sync = new object();
            using var concurrency = new SemaphoreSlim(2, 2);

            DubAvailabilityResult BestResult()
            {
                DubAvailabilityResult? best = outcomes.Select((update, index) => (update, index))
                    .Where(item => item.update.Result?.Checked == true)
                    .OrderByDescending(item => item.update.Result!.DubEpisodes > 0)
                    .ThenByDescending(item => item.update.Result!.Confidence)
                    .ThenBy(item => item.index).Select(item => item.update.Result).FirstOrDefault();
                if (best != null)
                {
                    // Other providers can still find a dub. An interim zero is not final absence.
                    return best.DubEpisodes == 0 && outcomes.Any(item => item.Outcome == DubProviderOutcome.Pending)
                        ? best with { Confidence = DubAvailabilityConfidence.Summary,
                            HighestContiguousDubEpisode = 0, DubbedEpisodeNumbers = Array.Empty<decimal>() }
                        : best;
                }
                DubProviderUpdate? rejected = outcomes.FirstOrDefault(item => item.Outcome == DubProviderOutcome.IdentityRejected);
                return Unknown(media, rejected?.Provider ?? outcomes.Last().Provider,
                    rejected != null ? MalMismatchDetail : UnknownDetail);
            }

            void Update(int index, DubProviderOutcome outcome, DubAvailabilityResult? result = null)
            {
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    outcomes[index] = new(outcomes[index].Provider, outcome, result);
                    publish(new(BestResult(), outcomes.ToArray(), outcomes.All(item => item.Outcome != DubProviderOutcome.Pending)));
                }
            }

            publish(new(Unknown(media), outcomes.ToArray(), false));
            await Task.WhenAll(enabledProviders.Select(async (provider, index) =>
            {
                await concurrency.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    providerCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 1, 30)));
                    DubAvailabilityResult result = IsAniKotoProvider(provider)
                        ? await CheckAniKotoProviderAsync(provider, media, mode, providerCts.Token,
                            summary => Update(index, DubProviderOutcome.Pending, summary)).ConfigureAwait(false)
                        : await CheckBadgeProviderAsync(provider, media, queryTitles, providerCts.Token).ConfigureAwait(false);
                    Update(index, result.Detail.Equals(MalMismatchDetail, StringComparison.Ordinal)
                        ? DubProviderOutcome.IdentityRejected : result.Checked
                            ? DubProviderOutcome.Completed : DubProviderOutcome.Unavailable, result);
                }
                catch (ProviderBackoffException)
                {
                    Update(index, DubProviderOutcome.Backoff);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    BackOffProviderHost(provider, TimeSpan.FromSeconds(45));
                    Update(index, DubProviderOutcome.TimedOut, outcomes[index].Result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    BackOffProviderHost(provider, TimeSpan.FromSeconds(45));
                    Update(index, DubProviderOutcome.Unavailable, outcomes[index].Result);
                    AppLogger.Log($"Dub availability lookup failed for '{NormalizeProviderName(provider.Name)}': {ex.Message}", "WARNING");
                }
                finally { concurrency.Release(); }
            })).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (sync) return BestResult() with { ProviderOutcomes = outcomes.ToArray() };
        }

        public static bool IsValidProviderTemplate(string? template)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return false;
            }

            string probeUrl = template.Contains("{query}", StringComparison.OrdinalIgnoreCase)
                ? template.Replace("{query}", "test", StringComparison.OrdinalIgnoreCase)
                : template.Trim();

            return Uri.TryCreate(probeUrl, UriKind.Absolute, out var uri) && IsSafeProviderUri(uri);
        }

        private static IReadOnlyList<DubAvailabilityProviderConfig> LoadProviders(DomainHotSwapper? config)
        {
            string raw = config?.GetSetting(ProviderConfigKey) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [CreateBuiltInProvider()];
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<DubAvailabilityProviderConfig>>(raw, JsonOptions)
                    ?? new List<DubAvailabilityProviderConfig>();

                var normalized = parsed
                    .Select(NormalizeProvider)
                    .Where(provider =>
                    {
                        if (!provider.Enabled)
                        {
                            return true;
                        }

                        bool valid = IsValidProviderTemplate(provider.SuggestUrlTemplate);
                        if (!valid)
                        {
                            AppLogger.Log($"Dub availability provider '{provider.Name}' has an invalid URL/template and will be ignored.", "WARNING");
                        }

                        return valid;
                    })
                    .ToArray();

                // A literal [] or an all-disabled list is an intentional user opt-out. New installs
                // receive an explicit AniKoto config default, while a genuinely missing setting can
                // still recover to the safe built-in adapter above.
                return normalized;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to parse {ProviderConfigKey}: {ex.Message}", "WARNING");
                return Array.Empty<DubAvailabilityProviderConfig>();
            }
        }

        private static DubAvailabilityProviderConfig CreateBuiltInProvider()
        {
            return new DubAvailabilityProviderConfig
            {
                Name = "AniKoto (built-in)",
                SuggestUrlTemplate = "https://anikoto.cz",
                Enabled = true,
                TimeoutSeconds = 10,
                ParserType = "AniKoto",
                AdapterType = "AniKoto",
                IsBuiltIn = true
            };
        }

        private static DubAvailabilityProviderConfig NormalizeProvider(DubAvailabilityProviderConfig provider)
        {
            string name = NormalizeProviderName(provider.Name);
            return provider with
            {
                Name = name,
                SuggestUrlTemplate = (provider.SuggestUrlTemplate ?? string.Empty).Trim(),
                ParserType = string.IsNullOrWhiteSpace(provider.ParserType) ? "Badge" : provider.ParserType.Trim(),
                AdapterType = (provider.AdapterType ?? string.Empty).Trim(),
                TimeoutSeconds = Math.Clamp(provider.TimeoutSeconds <= 0 ? 8 : provider.TimeoutSeconds, 1, 30)
            };
        }

        private static string NormalizeProviderName(string? name)
        {
            return string.IsNullOrWhiteSpace(name) ? "Configured provider" : name.Trim();
        }

        private static bool IsSupportedParser(string parserType)
        {
            return string.IsNullOrWhiteSpace(parserType) ||
                   parserType.Equals("Badge", StringComparison.OrdinalIgnoreCase) ||
                   parserType.Equals("NumericBadge", StringComparison.OrdinalIgnoreCase) ||
                   parserType.StartsWith("AniKoto", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAniKotoProvider(DubAvailabilityProviderConfig provider)
        {
            if (provider.AdapterType.Equals("AniKoto", StringComparison.OrdinalIgnoreCase) ||
                provider.ParserType.StartsWith("AniKoto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryGetProviderOrigin(provider, out Uri? origin))
            {
                return false;
            }

            Uri providerOrigin = origin!;

            // Existing installations only stored a name/root URL. Normalize the known AniKoto
            // origin to the adapter without requiring users to delete and recreate the provider.
            return provider.Name.Contains("AniKoto", StringComparison.OrdinalIgnoreCase) ||
                   providerOrigin.Host.Equals("anikoto.cz", StringComparison.OrdinalIgnoreCase) ||
                   providerOrigin.Host.EndsWith(".anikoto.cz", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DubAvailabilityResult> CheckAniKotoProviderAsync(
            DubAvailabilityProviderConfig provider,
            MediaResult media,
            DubAvailabilityCheckMode mode,
            CancellationToken token,
            Action<DubAvailabilityResult>? publishSummary = null)
        {
            string providerName = NormalizeProviderName(provider.Name);
            if (!TryGetProviderOrigin(provider, out Uri? origin))
            {
                return Unknown(media, providerName, "AniKoto provider URL was invalid.");
            }

            Uri providerOrigin = origin!;
            string discoveryKey = $"{GetHostKey(providerOrigin)}:{BuildCacheKey(media)}";
            if (mode == DubAvailabilityCheckMode.Verified &&
                TryGetAniKotoDiscovery(discoveryKey, out AniKotoDiscoveryEntry? cachedDiscovery))
            {
                if (cachedDiscovery!.Summary.Checked) publishSummary?.Invoke(cachedDiscovery.Summary);
                DubAvailabilityResult cachedVerification = await VerifyAniKotoDiscoveryAsync(
                    providerOrigin,
                    cachedDiscovery!,
                    media,
                    providerName,
                    token).ConfigureAwait(false);
                if (!cachedVerification.Detail.Equals(MalMismatchDetail, StringComparison.Ordinal))
                {
                    return cachedVerification;
                }

                _aniKotoDiscoveries.TryRemove(discoveryKey, out _);
            }

            string[] titles = BuildTitleCandidates(media)
                .Take(mode == DubAvailabilityCheckMode.Verified ? 3 : 1)
                .ToArray();
            bool sawMalMismatch = false;

            foreach (string title in titles)
            {
                var searchUri = new Uri(providerOrigin, $"/filter?keyword={Uri.EscapeDataString(title)}");
                ProviderPayloadResponse searchResponse = await SendProviderRequestAsync(
                    searchUri,
                    providerOrigin,
                    ajax: false,
                    token).ConfigureAwait(false);
                if (!searchResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                BadgeScope? exactCard = ResolveExactBadgeScope(ExtractHtmlPayload(searchResponse.Payload), media);
                if (exactCard == null)
                {
                    continue;
                }

                DubAvailabilityResult summary = ParseProviderBadges(exactCard.Html, media, providerName);
                if (summary.Checked)
                {
                    summary = summary with { Confidence = DubAvailabilityConfidence.Summary };
                    publishSummary?.Invoke(summary);
                }

                Match tipMatch = DataTipRegex.Match(exactCard.Html);
                long internalId = 0;
                bool hasInternalId = tipMatch.Success &&
                                     long.TryParse(tipMatch.Groups["id"].Value, out internalId) &&
                                     internalId > 0;
                if (hasInternalId)
                {
                    StoreAniKotoDiscovery(
                        discoveryKey,
                        new AniKotoDiscoveryEntry(
                            internalId,
                            searchUri,
                            summary,
                            _timeProvider.GetUtcNow().Add(media.DisplayStatus.Equals("Releasing", StringComparison.OrdinalIgnoreCase)
                                ? TimeSpan.FromMinutes(15)
                                : TimeSpan.FromHours(24))));
                }

                if (mode == DubAvailabilityCheckMode.Summary)
                {
                    return summary;
                }

                if (!hasInternalId)
                {
                    return SummaryFallback(
                        media,
                        providerName,
                        summary,
                        "Episode-level verification was unavailable because the result card had no internal identifier.");
                }

                var discovery = new AniKotoDiscoveryEntry(
                    internalId,
                    searchUri,
                    summary,
                    _timeProvider.GetUtcNow().Add(TimeSpan.FromMinutes(15)));
                DubAvailabilityResult verified = await VerifyAniKotoDiscoveryAsync(
                    providerOrigin,
                    discovery,
                    media,
                    providerName,
                    token).ConfigureAwait(false);
                if (verified.Verified || verified.Confidence == DubAvailabilityConfidence.Summary)
                {
                    return verified;
                }

                if (verified.Detail.Equals(MalMismatchDetail, StringComparison.Ordinal))
                {
                    sawMalMismatch = true;
                    continue;
                }

                return verified;
            }

            return Unknown(
                media,
                providerName,
                sawMalMismatch ? MalMismatchDetail : $"{providerName} did not return an exact title card.");
        }

        private async Task<DubAvailabilityResult> VerifyAniKotoDiscoveryAsync(
            Uri providerOrigin,
            AniKotoDiscoveryEntry discovery,
            MediaResult media,
            string providerName,
            CancellationToken token)
        {
            var episodeListUri = new Uri(
                providerOrigin,
                $"/ajax/episode/list/{discovery.InternalId.ToString(CultureInfo.InvariantCulture)}");
            ProviderPayloadResponse episodeResponse = await SendProviderRequestAsync(
                episodeListUri,
                discovery.Referrer,
                ajax: true,
                token).ConfigureAwait(false);
            if (!episodeResponse.IsSuccessStatusCode)
            {
                return SummaryFallback(
                    media,
                    providerName,
                    discovery.Summary,
                    $"Episode-level verification returned HTTP {(int)episodeResponse.StatusCode}.");
            }

            return ParseVerifiedEpisodeList(
                ExtractHtmlPayload(episodeResponse.Payload),
                media,
                providerName,
                discovery.Summary);
        }

        private bool TryGetAniKotoDiscovery(string key, out AniKotoDiscoveryEntry? discovery)
        {
            discovery = null;
            if (!_aniKotoDiscoveries.TryGetValue(key, out AniKotoDiscoveryEntry? found))
            {
                return false;
            }

            if (found.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _aniKotoDiscoveries.TryRemove(key, out _);
                return false;
            }

            discovery = found;
            return true;
        }

        private void StoreAniKotoDiscovery(string key, AniKotoDiscoveryEntry discovery)
        {
            _aniKotoDiscoveries[key] = discovery;
            if (_aniKotoDiscoveries.Count <= MaxCacheEntries)
            {
                return;
            }

            int excess = _aniKotoDiscoveries.Count - MaxCacheEntries;
            foreach (string staleKey in _aniKotoDiscoveries
                         .OrderBy(item => item.Value.ExpiresAtUtc)
                         .Take(excess)
                         .Select(item => item.Key))
            {
                _aniKotoDiscoveries.TryRemove(staleKey, out _);
            }
        }

        private async Task<DubAvailabilityResult> CheckBadgeProviderAsync(
            DubAvailabilityProviderConfig provider,
            MediaResult media,
            IReadOnlyList<string> queryTitles,
            CancellationToken token)
        {
            string providerName = NormalizeProviderName(provider.Name);
            if (!IsSupportedParser(provider.ParserType))
            {
                AppLogger.Log($"Dub availability provider '{providerName}' skipped: unsupported parser '{provider.ParserType}'.", "WARNING");
                return Unknown(media, providerName, "Unsupported dub availability parser.");
            }

            string[] candidateUrls = BuildProviderCandidateUrls(provider, queryTitles).ToArray();
            if (candidateUrls.Length == 0)
            {
                return Unknown(media, providerName, "Provider URL did not produce a safe search request.");
            }

            AppLogger.Log($"Dub availability provider '{providerName}' probing {candidateUrls.Length} bounded candidate URL(s).");
            DubAvailabilityResult? matchedZero = null;
            string lastDetail = string.Empty;

            foreach (string candidateUrl in candidateUrls)
            {
                token.ThrowIfCancellationRequested();
                if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out Uri? uri) || !IsSafeProviderUri(uri))
                {
                    continue;
                }

                ProviderPayloadResponse response = await SendProviderRequestAsync(
                    uri,
                    TryGetOrigin(uri, out Uri? origin) ? origin : null,
                    ajax: false,
                    token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastDetail = $"{providerName} returned HTTP {(int)response.StatusCode}.";
                    continue;
                }

                DubAvailabilityResult result = ParseProviderBadges(ExtractHtmlPayload(response.Payload), media, providerName);
                if (!result.Checked)
                {
                    lastDetail = result.Detail;
                    continue;
                }

                result = result with { Confidence = DubAvailabilityConfidence.Summary };
                if (result.DubEpisodes > 0)
                {
                    return result;
                }

                matchedZero ??= result;
            }

            return matchedZero ?? Unknown(media, providerName, lastDetail);
        }

        private static DubAvailabilityResult ParseVerifiedEpisodeList(
            string html,
            MediaResult media,
            string providerName,
            DubAvailabilityResult summary)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return SummaryFallback(media, providerName, summary, "Provider returned an empty episode list.");
            }

            var dubbed = new SortedSet<decimal>();
            var subbed = new SortedSet<decimal>();
            var providerMalIds = new HashSet<int>();
            int numberedEntries = 0;
            int malIdentifiedEntries = 0;
            int recognizedDubFlagEntries = 0;

            foreach (Match tag in StartTagRegex.Matches(html))
            {
                Dictionary<string, string> attributes = ParseHtmlAttributes(tag.Groups["attrs"].Value);
                if (!attributes.TryGetValue("data-num", out string? rawNumber) ||
                    !decimal.TryParse(rawNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal episodeNumber) ||
                    episodeNumber <= 0)
                {
                    continue;
                }

                numberedEntries++;
                if (attributes.TryGetValue("data-mal", out string? rawMal) &&
                    int.TryParse(rawMal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int providerMalId) &&
                    providerMalId > 0)
                {
                    malIdentifiedEntries++;
                    providerMalIds.Add(providerMalId);
                }

                if (attributes.TryGetValue("data-sub", out string? subValue) &&
                    TryParseAvailabilityFlag(subValue, out bool isSubbed) &&
                    isSubbed)
                {
                    subbed.Add(episodeNumber);
                }

                if (attributes.TryGetValue("data-dub", out string? dubValue) &&
                    TryParseAvailabilityFlag(dubValue, out bool isDubbed))
                {
                    recognizedDubFlagEntries++;
                    if (isDubbed)
                    {
                        dubbed.Add(episodeNumber);
                    }
                }
            }

            if (numberedEntries == 0)
            {
                return SummaryFallback(media, providerName, summary, "Provider episode list contained no numbered episodes.");
            }

            if (recognizedDubFlagEntries != numberedEntries)
            {
                return SummaryFallback(
                    media,
                    providerName,
                    summary,
                    "Episode-level audio flags were missing or used an unrecognized schema.");
            }

            if (media.IdMal <= 0)
            {
                return SummaryFallback(
                    media,
                    providerName,
                    summary,
                    "Episode-level identity could not be verified because the media has no MAL identifier.");
            }

            if (media.IdMal > 0 && providerMalIds.Any(id => id != media.IdMal))
            {
                return Unknown(media, providerName, MalMismatchDetail);
            }

            if (malIdentifiedEntries != numberedEntries)
            {
                return summary.Checked
                    ? summary with
                    {
                        Detail = $"{summary.Detail} Episode-level identity could not be verified because MAL metadata was missing.",
                        Confidence = DubAvailabilityConfidence.Summary
                    }
                    : Unknown(media, providerName, "Provider episode list omitted the MAL identity required for verification.");
            }

            int knownEpisodeCeiling = media.TotalEpisodes > 0
                ? media.TotalEpisodes
                : media.AvailableSubEpisodes > 1
                    ? media.AvailableSubEpisodes
                    : 0;
            int tolerance = media.DisplayStatus.Equals("Releasing", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (knownEpisodeCeiling > 0 && dubbed.Any(number =>
                    decimal.Truncate(number) == number && number > knownEpisodeCeiling + tolerance))
            {
                return Unknown(media, providerName, "Provider episode metadata exceeded the known episode range.");
            }

            decimal[] exactDubbedEpisodes = dubbed.ToArray();
            int contiguous = 0;
            while (dubbed.Contains(contiguous + 1))
            {
                contiguous++;
            }

            int subCount = Math.Max(
                Math.Max(media.AvailableSubEpisodes, summary.Checked ? summary.SubEpisodes : 0),
                subbed.Count);
            string detail = dubbed.Count > 0
                ? $"{providerName} verified {dubbed.Count} distinct dubbed episode(s) from the episode list."
                : $"{providerName} verified that the current episode list contains no dubbed episodes.";

            return new DubAvailabilityResult(true, subCount, dubbed.Count, providerName, detail)
            {
                Confidence = DubAvailabilityConfidence.Verified,
                HighestContiguousDubEpisode = contiguous,
                DubbedEpisodeNumbers = exactDubbedEpisodes
            };
        }

        private static DubAvailabilityResult SummaryFallback(
            MediaResult media,
            string providerName,
            DubAvailabilityResult summary,
            string reason)
        {
            return summary.Checked
                ? summary with
                {
                    Detail = $"{summary.Detail} {reason}",
                    Confidence = DubAvailabilityConfidence.Summary
                }
                : Unknown(media, providerName, reason);
        }

        private static Dictionary<string, string> ParseHtmlAttributes(string attributes)
        {
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in HtmlAttributeRegex.Matches(attributes))
            {
                string value = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["bare"].Value;
                parsed[match.Groups["name"].Value] = WebUtility.HtmlDecode(value);
            }

            return parsed;
        }

        private static bool TryParseAvailabilityFlag(string value, out bool available)
        {
            if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("available", StringComparison.OrdinalIgnoreCase))
            {
                available = true;
                return true;
            }

            if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
            {
                available = false;
                return true;
            }

            available = false;
            return false;
        }

        private static IEnumerable<string> BuildCandidateUrls(DubAvailabilityProviderConfig provider, string escapedQuery)
        {
            if (!IsValidProviderTemplate(provider.SuggestUrlTemplate))
            {
                yield break;
            }

            string configured = provider.SuggestUrlTemplate.Trim();
            if (CandidateWasExplicit(provider))
            {
                string url = configured.Replace("{query}", escapedQuery, StringComparison.OrdinalIgnoreCase);
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && IsSafeProviderUri(uri))
                {
                    yield return url;
                }

                yield break;
            }

            foreach (string candidate in BuildAutoProbeUrls(configured, escapedQuery))
            {
                yield return candidate;
            }
        }

        private static IEnumerable<string> BuildProviderCandidateUrls(
            DubAvailabilityProviderConfig provider,
            IReadOnlyList<string> queryTitles)
        {
            if (CandidateWasExplicit(provider))
            {
                return queryTitles
                    .SelectMany(title => BuildCandidateUrls(provider, Uri.EscapeDataString(title)))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
            }

            // Root/path-only providers are necessarily speculative. Interleave a small number of
            // title aliases so one title cannot consume the entire request budget before another
            // useful alias is tried, and bound the total number of network requests per provider.
            var candidatesByTitle = queryTitles
                .Take(MaxAutoProbeTitles)
                .Select(title => BuildCandidateUrls(provider, Uri.EscapeDataString(title)).ToArray())
                .ToArray();
            var bounded = new List<string>(MaxAutoProbeAttempts);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int candidateIndex = 0;
                 bounded.Count < MaxAutoProbeAttempts && candidatesByTitle.Any(candidates => candidateIndex < candidates.Length);
                 candidateIndex++)
            {
                foreach (string[] candidates in candidatesByTitle)
                {
                    if (candidateIndex >= candidates.Length)
                    {
                        continue;
                    }

                    string candidate = candidates[candidateIndex];
                    if (seen.Add(candidate))
                    {
                        bounded.Add(candidate);
                    }

                    if (bounded.Count >= MaxAutoProbeAttempts)
                    {
                        break;
                    }
                }
            }

            return bounded;
        }

        private static bool CandidateWasExplicit(DubAvailabilityProviderConfig provider)
        {
            return provider.SuggestUrlTemplate.Contains("{query}", StringComparison.OrdinalIgnoreCase);
        }

        private TimeSpan GetRateLimitBackoff(HttpResponseMessage response)
        {
            TimeSpan backoff = response.Headers.RetryAfter?.Delta ?? DefaultRateLimitBackoff;
            if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
            {
                backoff = retryAt - _timeProvider.GetUtcNow();
            }

            return TimeSpan.FromSeconds(Math.Clamp(backoff.TotalSeconds, 30, 15 * 60));
        }

        private static IEnumerable<string> BuildAutoProbeUrls(string configuredUrl, string escapedQuery)
        {
            if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = $"{uri.Scheme}://{uri.Authority}";
            string baseWithoutQuery = $"{root}{uri.AbsolutePath}".TrimEnd('/');
            if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
            {
                baseWithoutQuery = root;
            }

            bool configuredIsRoot = string.Equals(baseWithoutQuery, root, StringComparison.OrdinalIgnoreCase);
            if (!configuredIsRoot)
            {
                foreach (string candidate in BuildCandidatesForBase(configuredUrl, escapedQuery))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }

                foreach (string candidate in BuildCandidatesForBase(baseWithoutQuery, escapedQuery))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            foreach (string path in CommonSearchPaths)
            {
                string pathBase = root + path;
                foreach (string candidate in BuildCandidatesForBase(pathBase, escapedQuery))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            foreach (string candidate in BuildCandidatesForBase(root, escapedQuery))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }

            string unescapedQuery = Uri.UnescapeDataString(escapedQuery);
            foreach (string pathCandidate in new[]
                     {
                         $"{baseWithoutQuery}/{escapedQuery}",
                         $"{root}/search/{escapedQuery}",
                         $"{root}/anime/{escapedQuery}",
                         $"{root}/search/{Uri.EscapeDataString(unescapedQuery.Replace(' ', '-').ToLowerInvariant())}",
                         $"{root}/anime/{Uri.EscapeDataString(unescapedQuery.Replace(' ', '-').ToLowerInvariant())}"
                     })
            {
                if (seen.Add(pathCandidate))
                {
                    yield return pathCandidate;
                }
            }
        }

        private static IEnumerable<string> BuildCandidatesForBase(string baseUrl, string escapedQuery)
        {
            string trimmed = baseUrl.TrimEnd('/');
            if (trimmed.EndsWith("=", StringComparison.Ordinal) ||
                trimmed.EndsWith("%3D", StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmed + escapedQuery;
            }

            string separator = trimmed.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            foreach (string queryName in CommonQueryNames)
            {
                yield return $"{trimmed}{separator}{queryName}={escapedQuery}";
            }
        }

        private async Task<ProviderPayloadResponse> SendProviderRequestAsync(
            Uri requestUri,
            Uri? referrer,
            bool ajax,
            CancellationToken token)
        {
            Uri currentUri = requestUri;
            Uri originalOrigin = new($"{requestUri.Scheme}://{requestUri.Authority}/");

            for (int redirect = 0; redirect <= 2; redirect++)
            {
                if (!IsSafeProviderUri(currentUri))
                {
                    throw new SecurityException("Dub provider URL was blocked by network safety policy.");
                }

                string hostKey = GetHostKey(currentUri);
                ProviderHostState hostState = _providerHosts.GetOrAdd(hostKey, _ => new ProviderHostState());
                await hostState.Gate.WaitAsync(token).ConfigureAwait(false);
                HttpResponseMessage? response = null;
                try
                {
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    DateTimeOffset retryAt = hostState.GetBackoffUntil();
                    if (retryAt > now)
                    {
                        throw new ProviderBackoffException(retryAt);
                    }

                    DateTimeOffset nextRequestAt = hostState.GetNextRequestAt();
                    if (nextRequestAt > now)
                    {
                        await Task.Delay(nextRequestAt - now, _timeProvider, token).ConfigureAwait(false);
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    request.Headers.UserAgent.ParseAdd(DesktopUserAgent);
                    request.Headers.Accept.ParseAdd("application/json,text/html,*/*");
                    if (referrer != null && IsSameOrigin(currentUri, referrer))
                    {
                        request.Headers.Referrer = referrer;
                    }

                    if (ajax)
                    {
                        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    }

                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);
                    hostState.SetNextRequestAt(_timeProvider.GetUtcNow().Add(_minimumRequestSpacing));

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        TimeSpan backoff = GetRateLimitBackoff(response);
                        DateTimeOffset backoffUntil = _timeProvider.GetUtcNow().Add(backoff);
                        hostState.BackOffUntil(backoffUntil);
                        response.Dispose();
                        response = null;
                        AppLogger.Log(
                            $"Dub availability host '{currentUri.Host}' returned HTTP 429; respecting Retry-After for {backoff.TotalSeconds:0} seconds.",
                            "WARNING");
                        throw new ProviderBackoffException(backoffUntil);
                    }

                    if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
                    {
                        Uri redirectUri = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location
                            : new Uri(currentUri, response.Headers.Location);
                        response.Dispose();
                        response = null;
                        if (!IsSafeProviderUri(redirectUri) || !IsSameOrigin(originalOrigin, redirectUri))
                        {
                            throw new SecurityException("Cross-origin or unsafe dub provider redirect was blocked.");
                        }

                        currentUri = redirectUri;
                        continue;
                    }

                    string payload = await ReadBoundedStringAsync(response.Content, token).ConfigureAwait(false);
                    var result = new ProviderPayloadResponse(response.StatusCode, payload);
                    response.Dispose();
                    response = null;
                    return result;
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
                finally
                {
                    hostState.Gate.Release();
                }
            }

            throw new HttpRequestException("Dub provider exceeded the redirect limit.");
        }

        private async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken token)
        {
            if (content.Headers.ContentLength is long contentLength && contentLength > _maxResponseBytes)
            {
                throw new PayloadTooLargeException();
            }

            await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream(Math.Min(_maxResponseBytes, 64 * 1024));
            byte[] chunk = new byte[32 * 1024];
            int total = 0;

            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _maxResponseBytes)
                {
                    throw new PayloadTooLargeException();
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, total);
        }

        private void BackOffProviderHost(DubAvailabilityProviderConfig provider, TimeSpan duration)
        {
            if (!TryGetProviderOrigin(provider, out Uri? origin))
            {
                return;
            }

            ProviderHostState state = _providerHosts.GetOrAdd(GetHostKey(origin!), _ => new ProviderHostState());
            state.BackOffUntil(_timeProvider.GetUtcNow().Add(duration));
        }

        private bool TryGetCached(string key, out DubAvailabilityResult? result)
        {
            result = null;
            if (!_cache.TryGetValue(key, out CacheEntry? entry))
            {
                return false;
            }

            if (entry.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _cache.TryRemove(key, out _);
                return false;
            }

            result = entry.Result;
            return true;
        }

        private void PruneCache()
        {
            if (_cache.Count <= MaxCacheEntries)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (KeyValuePair<string, CacheEntry> item in _cache)
            {
                if (item.Value.ExpiresAtUtc <= now)
                {
                    _cache.TryRemove(item.Key, out _);
                }
            }

            int excess = _cache.Count - MaxCacheEntries;
            if (excess <= 0)
            {
                return;
            }

            foreach (string key in _cache
                         .OrderBy(item => item.Value.ExpiresAtUtc)
                         .Take(excess)
                         .Select(item => item.Key))
            {
                _cache.TryRemove(key, out _);
            }
        }

        private static TimeSpan GetCacheTtl(MediaResult media, DubAvailabilityResult result)
        {
            bool releasing = media.DisplayStatus.Equals("Releasing", StringComparison.OrdinalIgnoreCase);
            if (!result.Checked)
            {
                return releasing ? ReleasingUnknownTtl : FinishedUnknownTtl;
            }

            if (result.DubEpisodes == 0)
            {
                return releasing ? ReleasingZeroTtl : FinishedZeroTtl;
            }

            return releasing ? ReleasingPositiveTtl : FinishedPositiveTtl;
        }

        private void RemoveFlight(string key, LookupFlight flight)
        {
            if (_singleFlights.TryGetValue(key, out LookupFlight? current) && ReferenceEquals(current, flight))
            {
                _singleFlights.TryRemove(key, out _);
            }
        }

        private static bool TryGetProviderOrigin(DubAvailabilityProviderConfig provider, out Uri? origin)
        {
            string configured = provider.SuggestUrlTemplate.Contains("{query}", StringComparison.OrdinalIgnoreCase)
                ? provider.SuggestUrlTemplate.Replace("{query}", "test", StringComparison.OrdinalIgnoreCase)
                : provider.SuggestUrlTemplate;
            if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) || !IsSafeProviderUri(uri))
            {
                origin = null;
                return false;
            }

            origin = new Uri($"{uri.Scheme}://{uri.Authority}/");
            return true;
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.Moved or
                HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect;
        }

        private static string GetHostKey(Uri uri)
        {
            return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private static bool TryGetOrigin(string url, out Uri? origin)
        {
            origin = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            origin = new Uri($"{uri.Scheme}://{uri.Authority}/");
            return true;
        }

        private static bool TryGetOrigin(Uri uri, out Uri? origin)
        {
            origin = null;
            if (!IsSafeProviderUri(uri))
            {
                return false;
            }

            origin = new Uri($"{uri.Scheme}://{uri.Authority}/");
            return true;
        }

        private static DubAvailabilityResult ParseProviderBadges(string html, MediaResult media, string providerName)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return new DubAvailabilityResult(false, media.AvailableSubEpisodes, 0, string.Empty, "Empty provider response");
            }

            var badgeScope = ResolveBadgeScope(html, media);
            if (badgeScope == null)
            {
                return new DubAvailabilityResult(false, media.AvailableSubEpisodes, 0, string.Empty, "Provider response did not match title");
            }

            int sub = media.AvailableSubEpisodes;
            int dub = 0;
            bool sawNumericBadge = false;

            foreach (Match match in NumericBadgeRegex.Matches(badgeScope.Html).Cast<Match>().Concat(ClassBadgeRegex.Matches(badgeScope.Html).Cast<Match>()))
            {
                if (!int.TryParse(match.Groups["count"].Value, out int count))
                {
                    continue;
                }

                sawNumericBadge = true;
                string kind = match.Groups["kind"].Value;
                if (kind.Equals("sub", StringComparison.OrdinalIgnoreCase))
                {
                    sub = Math.Max(sub, count);
                }
                else if (kind.Equals("dub", StringComparison.OrdinalIgnoreCase))
                {
                    dub = Math.Max(dub, count);
                }
            }

            if (!sawNumericBadge)
            {
                return new DubAvailabilityResult(false, media.AvailableSubEpisodes, 0, string.Empty,
                    "Provider matched title but no numeric sub/dub badge was found.");
            }

            if (!IsDubCountPlausible(media, sub, dub))
            {
                return new DubAvailabilityResult(false, media.AvailableSubEpisodes, 0, string.Empty,
                    "Provider badge count exceeded the known episode count, so the match was ignored.");
            }

            return new DubAvailabilityResult(true, sub, dub, providerName, dub > 0
                ? $"{providerName} reported {dub} dubbed episode(s)."
                : $"{providerName} matched title but reported no dub badge.");
        }

        private sealed record BadgeScope(string Html, int Score, int Index, string MatchedTitle);

        private static BadgeScope? ResolveExactBadgeScope(string html, MediaResult media)
        {
            string[] exactTargets = BuildTitleCandidates(media)
                .Select(Normalize)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToArray();

            var scopes = ResultItemRegex.Matches(html)
                .Cast<Match>()
                .Select(match => match.Value)
                .Where(LooksLikeResultScope)
                .ToArray();

            for (int index = 0; index < scopes.Length; index++)
            {
                string? exactTitle = ExtractScopeTitles(scopes[index])
                    .FirstOrDefault(title => exactTargets.Contains(Normalize(title), StringComparer.OrdinalIgnoreCase));
                if (exactTitle != null)
                {
                    return new BadgeScope(scopes[index], 10_000 + Normalize(exactTitle).Length, index, exactTitle);
                }
            }

            // Never promote the whole page to a card: doing so could pair an exact title from one
            // result with badges or a data-tip identifier from another result.
            return null;
        }

        private static BadgeScope? ResolveBadgeScope(string html, MediaResult media)
        {
            var itemScopes = ResultItemRegex.Matches(html)
                .Cast<Match>()
                .Select(match => match.Value)
                .Where(LooksLikeResultScope)
                .ToList();

            if (itemScopes.Count > 0)
            {
                var best = itemScopes
                    .Select((scope, index) => new
                    {
                        Scope = scope,
                        Index = index,
                        Score = GetTitleMatchScore(scope, media, out string matchedTitle),
                        MatchedTitle = matchedTitle
                    })
                    .Where(candidate => candidate.Score >= MinimumCardTitleScore)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Index)
                    .FirstOrDefault();

                return best == null
                    ? null
                    : new BadgeScope(best.Scope, best.Score, best.Index, best.MatchedTitle);
            }

            int fallbackScore = GetTitleMatchScore(html, media, out string fallbackTitle);
            return fallbackScore >= MinimumCardTitleScore
                ? new BadgeScope(html, fallbackScore, 0, fallbackTitle)
                : null;
        }

        private static bool LooksLikeResultScope(string htmlScope)
        {
            return htmlScope.Contains("ep-status", StringComparison.OrdinalIgnoreCase) ||
                   htmlScope.Contains("tick-", StringComparison.OrdinalIgnoreCase) ||
                   htmlScope.Contains("d-title", StringComparison.OrdinalIgnoreCase) ||
                   htmlScope.Contains("film-detail", StringComparison.OrdinalIgnoreCase) ||
                   htmlScope.Contains("film-name", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetTitleMatchScore(string htmlScope, MediaResult media, out string matchedTitle)
        {
            matchedTitle = string.Empty;
            var providerTitles = ExtractScopeTitles(htmlScope).ToArray();
            var targetTitles = BuildTitleCandidates(media).ToArray();
            int bestScore = 0;

            foreach (string providerTitle in providerTitles)
            {
                foreach (string targetTitle in targetTitles)
                {
                    int score = ScoreTitlePair(providerTitle, targetTitle);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        matchedTitle = providerTitle;
                    }
                }
            }

            return bestScore;
        }

        private static IEnumerable<string> ExtractScopeTitles(string htmlScope)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in AttributeTitleRegex.Matches(htmlScope))
            {
                string title = CleanTitleText(match.Groups["title"].Value);
                string normalized = Normalize(title);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    yield return title;
                }
            }

            foreach (Match match in AnchorTextRegex.Matches(htmlScope))
            {
                string attrs = match.Groups["attrs"].Value;
                if (!attrs.Contains("d-title", StringComparison.OrdinalIgnoreCase) &&
                    !attrs.Contains("film-name", StringComparison.OrdinalIgnoreCase) &&
                    !attrs.Contains("name", StringComparison.OrdinalIgnoreCase) &&
                    !attrs.Contains("title=", StringComparison.OrdinalIgnoreCase) &&
                    !attrs.Contains("data-jp=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string title = CleanTitleText(match.Groups["title"].Value);
                string normalized = Normalize(title);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    yield return title;
                }
            }
        }

        private static int ScoreTitlePair(string providerTitle, string targetTitle)
        {
            string normalizedProvider = Normalize(providerTitle);
            string normalizedTarget = Normalize(targetTitle);
            if (string.IsNullOrWhiteSpace(normalizedProvider) || string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return 0;
            }

            if (normalizedProvider.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return 10_000 + normalizedTarget.Length;
            }

            if (HasConflictingSeasonOrPart(normalizedProvider, normalizedTarget) ||
                HasConflictingVariantMarker(normalizedProvider, normalizedTarget))
            {
                return 0;
            }

            int providerWordCount = CountWords(normalizedProvider);
            int targetWordCount = CountWords(normalizedTarget);

            if (normalizedProvider.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                int extraWords = Math.Max(0, providerWordCount - targetWordCount);
                return 4_500 + normalizedTarget.Length - (extraWords * 60);
            }

            if (normalizedTarget.Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase) &&
                providerWordCount >= 3)
            {
                int missingWords = Math.Max(0, targetWordCount - providerWordCount);
                return 2_500 + normalizedProvider.Length - (missingWords * 80);
            }

            var targetWords = SignificantWords(normalizedTarget).ToArray();
            if (targetWords.Length < 3)
            {
                return 0;
            }

            var providerWords = SignificantWords(normalizedProvider).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int matchedWords = targetWords.Count(providerWords.Contains);
            double coverage = matchedWords / (double)targetWords.Length;
            if (coverage < 0.85)
            {
                return 0;
            }

            int extraTokenPenalty = Math.Max(0, providerWords.Count - matchedWords) * 25;
            return 1_000 + (matchedWords * 75) - extraTokenPenalty;
        }

        private static bool HasConflictingSeasonOrPart(string normalizedProvider, string normalizedTarget)
        {
            int? providerSeason = ExtractNumberMarker(normalizedProvider, SeasonMarkerRegex);
            int? targetSeason = ExtractNumberMarker(normalizedTarget, SeasonMarkerRegex);
            if (providerSeason.HasValue != targetSeason.HasValue ||
                (providerSeason.HasValue && targetSeason.HasValue && providerSeason.Value != targetSeason.Value))
            {
                return true;
            }

            int? providerPart = ExtractNumberMarker(normalizedProvider, PartMarkerRegex);
            int? targetPart = ExtractNumberMarker(normalizedTarget, PartMarkerRegex);
            return providerPart.HasValue != targetPart.HasValue ||
                   (providerPart.HasValue && targetPart.HasValue && providerPart.Value != targetPart.Value);
        }

        private static int? ExtractNumberMarker(string normalizedTitle, Regex markerRegex)
        {
            var match = markerRegex.Match(normalizedTitle);
            if (!match.Success)
            {
                return null;
            }

            string number = match.Groups["number"].Success
                ? match.Groups["number"].Value
                : match.Groups["ordinal"].Value;
            return int.TryParse(number, out int parsed) ? parsed : null;
        }

        private static bool HasConflictingVariantMarker(string normalizedProvider, string normalizedTarget)
        {
            string[] variantWords = ["movie", "film", "ova", "ona", "special", "recap", "mini"];
            foreach (string marker in variantWords)
            {
                bool providerHas = HasWord(normalizedProvider, marker);
                bool targetHas = HasWord(normalizedTarget, marker);
                if (providerHas && !targetHas)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDubCountPlausible(MediaResult media, int providerSubEpisodes, int providerDubEpisodes)
        {
            if (providerDubEpisodes <= 0)
            {
                return true;
            }

            if (providerSubEpisodes > 0 && providerDubEpisodes > providerSubEpisodes)
            {
                return false;
            }

            int knownEpisodeCeiling = media.TotalEpisodes > 0
                ? media.TotalEpisodes
                : media.AvailableSubEpisodes > 1
                    ? media.AvailableSubEpisodes
                    : 0;
            if (knownEpisodeCeiling <= 0)
            {
                return true;
            }

            int tolerance = media.DisplayStatus.Equals("Releasing", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            return providerDubEpisodes <= knownEpisodeCeiling + tolerance;
        }

        private static int CountWords(string normalizedTitle)
        {
            return normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static IEnumerable<string> SignificantWords(string normalizedTitle)
        {
            foreach (string word in normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length <= 1 || word is "the" or "and" or "for")
                {
                    continue;
                }

                yield return word;
            }
        }

        private static bool HasWord(string normalizedTitle, string word)
        {
            return normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token.Equals(word, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildCacheKey(MediaResult media)
        {
            if (media.Id > 0)
            {
                return $"anilist:{media.Id}";
            }

            string joinedTitles = string.Join("|", BuildTitleCandidates(media).Select(Normalize));
            return string.IsNullOrWhiteSpace(joinedTitles)
                ? Normalize(media.OfficialTitle)
                : joinedTitles;
        }

        private static IEnumerable<string> BuildTitleCandidates(MediaResult media)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? title in new[]
                     {
                         media.OfficialTitle,
                         media.EnglishTitle,
                         media.RomajiTitle,
                         media.NativeTitle
                     }.Concat(media.Synonyms ?? Enumerable.Empty<string>()))
            {
                string cleaned = CleanTitleText(title ?? string.Empty);
                string normalized = Normalize(cleaned);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                yield return cleaned;
            }
        }

        private static string CleanTitleText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string withoutTags = HtmlTagRegex.Replace(value, " ");
            string decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static string ExtractHtmlPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("html", out var html) && html.ValueKind == JsonValueKind.String)
                {
                    return html.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    return result.GetString() ?? string.Empty;
                }
            }
            catch
            {
                // Some mirrors return plain HTML. Use the payload as-is.
            }

            return payload;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        }

        private static HttpClient CreateSafeHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = ConnectToPublicEndpointAsync
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        private static async ValueTask<Stream> ConnectToPublicEndpointAsync(
            SocketsHttpConnectionContext context,
            CancellationToken token)
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            Exception? lastError = null;

            foreach (IPAddress address in addresses.Where(IsPublicAddress))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    lastError = ex;
                    socket.Dispose();
                }
            }

            throw new SecurityException(
                $"Provider host '{context.DnsEndPoint.Host}' did not resolve to a permitted public address.",
                lastError);
        }

        private static bool IsSafeProviderUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri ||
                uri.Scheme is not ("http" or "https") ||
                string.IsNullOrWhiteSpace(uri.IdnHost) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            string host = uri.IdnHost.TrimEnd('.');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !IPAddress.TryParse(host, out IPAddress? address) || IsPublicAddress(address);
        }

        private static bool IsPublicAddress(IPAddress address)
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

        private static DubAvailabilityResult Unknown(MediaResult media)
        {
            return Unknown(media, string.Empty, UnknownDetail);
        }

        private static DubAvailabilityResult Unknown(
            MediaResult media,
            string source,
            string? detail)
        {
            return new DubAvailabilityResult(
                false,
                media.AvailableSubEpisodes,
                0,
                source,
                string.IsNullOrWhiteSpace(detail) ? UnknownDetail : detail)
            {
                Confidence = DubAvailabilityConfidence.Unknown
            };
        }

        private sealed record CacheEntry(DubAvailabilityResult Result, DateTimeOffset ExpiresAtUtc);

        private sealed record ProviderPayloadResponse(HttpStatusCode StatusCode, string Payload)
        {
            public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
        }

        private sealed record AniKotoDiscoveryEntry(
            long InternalId,
            Uri Referrer,
            DubAvailabilityResult Summary,
            DateTimeOffset ExpiresAtUtc);

        private sealed class LookupFlight
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Lazy<Task<DubAvailabilityResult>> _task;
            private int _waiters;
            private readonly object _sync = new();
            private readonly List<IProgress<DubAvailabilityUpdate>> _subscribers = new();
            private DubAvailabilityUpdate? _latest;
            private long _sequence;

            public LookupFlight(Func<CancellationToken, Action<DubAvailabilityUpdate>, Task<DubAvailabilityResult>> factory)
            {
                _task = new Lazy<Task<DubAvailabilityResult>>(
                    () => factory(_cts.Token, Publish),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            public IDisposable? Subscribe(IProgress<DubAvailabilityUpdate>? progress)
            {
                if (progress == null) return null;
                lock (_sync)
                {
                    _subscribers.Add(progress);
                    if (_latest != null) Report(progress, _latest);
                }
                return new Subscription(this, progress);
            }

            private void Publish(DubAvailabilityUpdate update)
            {
                lock (_sync)
                {
                    _latest = update with { Sequence = ++_sequence };
                    foreach (var subscriber in _subscribers.ToArray()) Report(subscriber, _latest);
                }
            }

            public static void Report(IProgress<DubAvailabilityUpdate> progress, DubAvailabilityUpdate update)
            {
                try { progress.Report(update); }
                catch (Exception ex) { AppLogger.Log($"Dub progress consumer failed: {ex.Message}", "WARNING"); }
            }

            private sealed class Subscription(LookupFlight flight, IProgress<DubAvailabilityUpdate> progress) : IDisposable
            {
                public void Dispose() { lock (flight._sync) flight._subscribers.Remove(progress); }
            }

            public Task<DubAvailabilityResult> Task => _task.Value;

            public void AddWaiter() => Interlocked.Increment(ref _waiters);

            public int ReleaseWaiter() => Interlocked.Decrement(ref _waiters);

            public void Cancel()
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private sealed class ProviderHostState
        {
            private readonly object _sync = new();
            private DateTimeOffset _nextRequestAt;
            private DateTimeOffset _backoffUntil;

            public SemaphoreSlim Gate { get; } = new(1, 1);

            public DateTimeOffset GetNextRequestAt()
            {
                lock (_sync)
                {
                    return _nextRequestAt;
                }
            }

            public DateTimeOffset GetBackoffUntil()
            {
                lock (_sync)
                {
                    return _backoffUntil;
                }
            }

            public void SetNextRequestAt(DateTimeOffset value)
            {
                lock (_sync)
                {
                    _nextRequestAt = value;
                }
            }

            public void BackOffUntil(DateTimeOffset value)
            {
                lock (_sync)
                {
                    if (value > _backoffUntil)
                    {
                        _backoffUntil = value;
                    }
                }
            }
        }

        private sealed class ProviderBackoffException : Exception
        {
            public ProviderBackoffException(DateTimeOffset retryAtUtc)
                : base("Provider request deferred by Retry-After/backoff policy.")
            {
                RetryAtUtc = retryAtUtc;
            }

            public DateTimeOffset RetryAtUtc { get; }
        }

        private sealed class PayloadTooLargeException : Exception
        {
        }
    }
}
