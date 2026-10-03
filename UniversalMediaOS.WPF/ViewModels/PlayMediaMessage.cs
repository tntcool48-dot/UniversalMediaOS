using UniversalMediaOS.Core.OtherMedia;
using System;
using System.Collections.Generic;
using UniversalMediaOS.Core.Services;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class PlayMediaMessage : ValueChangedMessage<string>
    {
        public AudiovisualPlaybackContext? AudiovisualContext { get; }
        public string Title { get; }
        public bool IsWebView { get; }
        public string Referer { get; }
        public int MalId { get; }
        public string EpisodeNumber { get; }
        public int TotalEpisodes { get; }
        public EpisodePlaybackContext? EpisodeContext { get; }
        public string ContentType { get; }
        public string UserAgent { get; }
        public string Cookie { get; }
        public IReadOnlyDictionary<string, string> RequestHeaders { get; }
        public IReadOnlyList<MediaSubtitleTrack> Subtitles { get; }
        public string AudioNotice { get; }
        public IDisposable? TemporaryWatchLease { get; }
        public IReadOnlyList<string> LocalCaptionPaths { get; }
        public string ValidatedHlsVariant { get; }

        public PlayMediaMessage(
            string path,
            string title,
            bool isWebView = false,
            string referer = "",
            int malId = 0,
            string episodeNumber = "",
            int totalEpisodes = 0,
            EpisodePlaybackContext? episodeContext = null,
            string contentType = "",
            string userAgent = "",
            string cookie = "",
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            AudiovisualPlaybackContext? audiovisualContext = null,
            IEnumerable<MediaSubtitleTrack>? subtitles = null,
            string audioNotice = "",
            IDisposable? temporaryWatchLease = null,
            IEnumerable<string>? localCaptionPaths = null,
            string validatedHlsVariant = "") : base(path)
        {
            AudiovisualContext = audiovisualContext;
            Title = title;
            IsWebView = isWebView;
            Referer = referer;
            MalId = malId;
            EpisodeNumber = episodeNumber;
            TotalEpisodes = totalEpisodes;
            EpisodeContext = episodeContext;
            ContentType = contentType ?? string.Empty;
            UserAgent = userAgent ?? string.Empty;
            Cookie = cookie ?? string.Empty;
            RequestHeaders = CopyHeaders(requestHeaders);
            Subtitles = MediaSubtitleTrack.Copy(subtitles);
            AudioNotice = audioNotice ?? string.Empty;
            ValidatedHlsVariant = validatedHlsVariant ?? string.Empty;
            TemporaryWatchLease = temporaryWatchLease;
            LocalCaptionPaths = (localCaptionPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Take(12).ToArray();
        }

        private static IReadOnlyDictionary<string, string> CopyHeaders(
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requestHeaders == null)
            {
                return copy;
            }

            foreach (var header in requestHeaders)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && header.Value != null)
                {
                    copy[header.Key] = header.Value;
                }
            }

            return copy;
        }
    }
}
