using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Routing
{
    public interface ITripleNetHandoff
    {
        Task<PlaybackSource> ResolveBestSourceAsync(string query, string episodeId, string providerDomain, System.Action<string> onStatusUpdate = null, SourceTier minimumTier = SourceTier.Tier1_LocalP2P);
        Task<System.Collections.Generic.List<TorrentResult>> GetTorrentsAsync(string query, string episodeId, System.Action<string> onStatusUpdate = null);
        Task<PlaybackSource> InjectTorrentAsync(TorrentResult torrent, System.Action<string> onStatusUpdate = null);
    }

    public class PlaybackSource
    {
        public SourceTier Tier { get; set; }
        public string UrlOrPath { get; set; } = string.Empty;
        public string EmbedOrigin { get; set; } = string.Empty;
    }

    public enum SourceTier
    {
        Tier1_LocalP2P,
        Tier2_ConsumetHttp,
        Tier3_WebViewEmbed
    }
}
