using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.OtherMedia
{
    public sealed class ScraperAudiovisualSourceProvider : IAudiovisualSourceProvider
    {
        private readonly AudiovisualScraperEngine _engine;
        private readonly DomainHotSwapper _config;

        public ScraperAudiovisualSourceProvider(AudiovisualScraperEngine engine, DomainHotSwapper config)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private string GetMirrorUrl()
        {
            string url = _config.GetSetting("OtherMediaScraperUrl");
            return string.IsNullOrWhiteSpace(url) ? "https://vidsrc.to" : url.Trim();
        }

        public async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
            AudiovisualIdentity identity,
            AudiovisualUnit? unit = null,
            string? preferredLanguage = null,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            var request = new SourceSearchRequest { Identity = identity, Unit = unit, AudioLanguage = preferredLanguage };
            var sources = new List<AudiovisualSource>();
            await foreach (var source in FindSourceCandidatesAsync(request, token).ConfigureAwait(false))
                sources.Add(source);
            var identityRequest = request with { AudioLanguage = null, SubtitleLanguage = null };
            return sources.OrderBy(source => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia ? 0 : 1)
                .ThenByDescending(source => ExactAudiovisualMatcher.VerifyEvidence(identityRequest, source.Evidence).Status == SourceVerificationStatus.Verified)
                .ToArray();
        }

        public async IAsyncEnumerable<AudiovisualSource> FindSourceCandidatesAsync(
            SourceSearchRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Identity);
            var identity = request.Identity;
            var unit = request.Unit;
            var identityRequest = request with { AudioLanguage = null, SubtitleLanguage = null };

            if (string.IsNullOrWhiteSpace(identity.Title))
            {
                yield break;
            }

            string searchQuery = !string.IsNullOrWhiteSpace(identity.ImdbId)
                ? $"imdb:{identity.ImdbId}"
                : identity.TmdbId.HasValue && identity.TmdbId.Value > 0
                    ? $"tmdb:{identity.TmdbId.Value}"
                    : identity.Title;

            string kind = identity.Kind.ToString().ToLowerInvariant();
            int? year = identity.Year;
            int? season = unit?.SeasonNumber;
            int? episode = unit?.EpisodeNumber;
            string mirror = GetMirrorUrl();

            AppLogger.Log($"[ScraperAudiovisualSourceProvider] Searching scraper: query={searchQuery}, kind={kind}, season={season}, episode={episode}");
            var searchResults = await _engine.SearchAsync(searchQuery, kind, year, season, episode, mirror, token);
            if (searchResults.Length == 0)
            {
                yield break;
            }

            using var alternatives = CancellationTokenSource.CreateLinkedTokenSource(token);
            var queued = new Queue<AudiovisualScraperSearchResult>(searchResults
                .DistinctBy(result => result.Url, StringComparer.OrdinalIgnoreCase));
            var pending = new List<Task<(AudiovisualScraperSearchResult Result, AudiovisualScraperStreamResult? Stream)>>();
            async Task<(AudiovisualScraperSearchResult, AudiovisualScraperStreamResult?)> ResolveCandidateAsync(AudiovisualScraperSearchResult result) =>
                (result, await _engine.ResolveAsync(result.Url, alternatives.Token).ConfigureAwait(false));
            try
            {
                while (pending.Count > 0 || queued.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    // Each extraction owns a browser. Keep every candidate, but avoid
                    // starting four (or up to 24 title-search) browser trees together.
                    while (pending.Count < 2 && queued.TryDequeue(out var candidate))
                        pending.Add(ResolveCandidateAsync(candidate));
                    var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                    pending.Remove(completed);
                    // One failed alternative must not discard another native success.
                    (AudiovisualScraperSearchResult Result, AudiovisualScraperStreamResult? Stream) resolved;
                    try
                    {
                        resolved = await completed.ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    { AppLogger.Log($"[AudiovisualScraper] Alternative failed: {ex.GetType().Name}"); continue; }
                    var evidence = resolved.Stream?.Evidence ?? resolved.Result.Evidence;
                    if (ExactAudiovisualMatcher.VerifyEvidence(request, evidence).Status == SourceVerificationStatus.Rejected)
                        continue;
                    token.ThrowIfCancellationRequested();

                    var result = resolved.Result;
                    var stream = resolved.Stream;
                    if (stream == null || string.IsNullOrWhiteSpace(stream.Url))
                    {
                        continue;
                    }
                    if (!stream.RequiresWebView && !stream.MediaValidated) continue;

                    if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out Uri? streamUri) ||
                        streamUri.Scheme is not ("http" or "https") || streamUri.UserInfo.Length != 0)
                    {
                        continue;
                    }

                    bool isArabic = !string.IsNullOrWhiteSpace(request.AudioLanguage) && AudiovisualProviderDefinition.IsArabicLanguage(request.AudioLanguage);
                    bool arabicCartoonLane = identity.Kind == AudiovisualMediaKind.Cartoon && isArabic;

                    var languages = (stream.AudioLanguages ?? [])
                        .Where(language => !string.IsNullOrWhiteSpace(language) &&
                            !language.Equals("und", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                    var source = new AudiovisualSource
                    {
                        Evidence = stream.Evidence ?? result.Evidence,
                        Provenance = AudiovisualSourceProvenance.BuiltInScraper,
                        ProviderId = "scraper-" + result.Provider.ToLowerInvariant().Replace(" ", "-"),
                        ProviderName = result.Provider,
                        Location = streamUri,
                        AccessMode = stream.RequiresWebView ? AudiovisualSourceAccessMode.WebPage : AudiovisualSourceAccessMode.DirectMedia,
                        Authorization = ProviderAuthorization.UserAuthorized,
                        License = string.Empty,
                        Rights = "User authorized dynamic stream scraping",
                        ContentType = stream.MediaValidated && !stream.RequiresWebView &&
                            string.Equals(stream.ContentType, "application/dash+xml", StringComparison.OrdinalIgnoreCase)
                            ? "application/dash+xml" : GetContentType(streamUri),
                        ValidatedHlsVariant = stream.ValidatedHlsVariant ?? string.Empty,
                        MediaValidated = stream.MediaValidated,
                        Subtitles = UniversalMediaOS.Core.Services.MediaSubtitleTrack.Copy(stream.Subtitles),
                        UserAgent = stream.UserAgent?.Trim() ?? string.Empty,
                        Cookie = stream.Cookie?.Trim() ?? string.Empty,
                        Referer = stream.Referer?.Trim() ?? string.Empty,
                        RequestHeaders = CopyReplayHeaders(stream.Headers),
                        Languages = languages,
                        Identity = identity,
                        Unit = unit ?? AudiovisualUnit.Feature,
                        Lane = arabicCartoonLane ? "arabic-cartoon" : string.Empty
                    };
                    bool confirmedNative = !stream.RequiresWebView && stream.MediaValidated &&
                        ExactAudiovisualMatcher.VerifyEvidence(identityRequest, evidence).Status == SourceVerificationStatus.Verified;
                    // Playable bytes alone do not establish which film/episode they contain.
                    // Publish those candidates promptly, but keep looking for an independent match.
                    if (confirmedNative) alternatives.Cancel();
                    yield return source;
                    if (confirmedNative) break;
                }
            }
            finally
            {
                alternatives.Cancel();
                // Cancellation kills each owned CLI process tree, including on iterator disposal.
                var remaining = Task.WhenAll(pending);
                try { await remaining.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { _ = ObserveAlternativesAsync(remaining); }
                catch (Exception ex)
                { AppLogger.Log($"[AudiovisualScraper] Alternative ended: {ex.GetType().Name}"); }
            }
            token.ThrowIfCancellationRequested();
        }

        private static async Task ObserveAlternativesAsync(Task pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Log($"[AudiovisualScraper] Cancelled alternative ended: {ex.GetType().Name}"); }
        }

        private static string GetContentType(Uri location)
        {
            string extension = Path.GetExtension(location.AbsolutePath);
            return extension.ToLowerInvariant() switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl",
                ".mpd" => "application/dash+xml",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                _ => "video/mp4"
            };
        }

        private static IReadOnlyDictionary<string, string> CopyReplayHeaders(
            IReadOnlyDictionary<string, string>? headers)
        {
            if (headers == null || headers.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string value) in headers)
            {
                string normalizedName = name.Trim();
                string normalizedValue = value.Trim();
                if (normalizedName.Length == 0 ||
                    normalizedValue.Length == 0 ||
                    normalizedName.StartsWith(':') ||
                    IsConnectionSpecificHeader(normalizedName))
                {
                    continue;
                }

                copy[normalizedName] = normalizedValue;
            }

            return copy;
        }

        private static bool IsConnectionSpecificHeader(string name)
        {
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
        }
    }
}
