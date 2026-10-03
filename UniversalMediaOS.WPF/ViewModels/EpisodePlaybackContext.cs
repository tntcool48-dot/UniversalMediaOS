using System;
using System.Threading;
using System.Collections.Generic;
using UniversalMediaOS.Core.Services;
using System.Threading.Tasks;

namespace UniversalMediaOS.WPF.ViewModels
{
    /// <summary>
    /// Describes a resolved episode without coupling the player to the scraper or UI.
    /// The resolver is supplied by the details screen, which already owns the routing
    /// choices (provider, audio preference and native/WebView tier).
    /// </summary>
    public sealed record ResolvedEpisodePlayback(
        string Source,
        string Title,
        bool IsWebView,
        string Referer = "",
        IReadOnlyList<MediaSubtitleTrack>? Subtitles = null,
        string AudioNotice = "");

    public sealed class EpisodePlaybackContext
    {
        private readonly Func<int, CancellationToken, Task<ResolvedEpisodePlayback?>> _resolver;

        public EpisodePlaybackContext(
            int firstEpisode,
            int lastEpisode,
            Func<int, CancellationToken, Task<ResolvedEpisodePlayback?>> resolver,
            string audioPreference = "",
            AnimePlaybackIdentity? resumeIdentity = null)
        {
            if (firstEpisode < 1)
                throw new ArgumentOutOfRangeException(nameof(firstEpisode));
            if (lastEpisode < firstEpisode)
                throw new ArgumentOutOfRangeException(nameof(lastEpisode));

            FirstEpisode = firstEpisode;
            LastEpisode = lastEpisode;
            ResumeIdentity = resumeIdentity;
            AudioPreference = audioPreference.Equals("dub", StringComparison.OrdinalIgnoreCase) ? "dub"
                : audioPreference.Equals("sub", StringComparison.OrdinalIgnoreCase) ? "sub" : string.Empty;
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public int FirstEpisode { get; }
        public int LastEpisode { get; }
        public string AudioPreference { get; }
        public AnimePlaybackIdentity? ResumeIdentity { get; }

        public PlaybackProgressContext? CreateProgressContext(string episodeNumber)
        {
            if (ResumeIdentity?.WorkKey is not { } workKey ||
                !int.TryParse(episodeNumber, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int episode) ||
                episode < FirstEpisode || episode > LastEpisode) return null;
            string number = episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new(workKey, "episode:" + number, LegacyWorkKey: ResumeIdentity.LegacyWorkKey, LegacyUnitKey: number);
        }

        public Task<ResolvedEpisodePlayback?> ResolveAsync(int episode, CancellationToken token)
        {
            if (episode < FirstEpisode || episode > LastEpisode)
                return Task.FromResult<ResolvedEpisodePlayback?>(null);

            return _resolver(episode, token);
        }
    }
}
