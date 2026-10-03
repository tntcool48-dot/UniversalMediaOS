namespace UniversalMediaOS.Core.OtherMedia;

internal interface IAudiovisualMetadataClient : IAudiovisualEpisodeMetadataClient
{
    Task<AudiovisualUnitsResult> IAudiovisualEpisodeMetadataClient.GetUnitsAsync(
        AudiovisualIdentity identity, int? season, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        if (season < 0) throw new ArgumentOutOfRangeException(nameof(season));
        return Task.FromResult(new AudiovisualUnitsResult([], [new("metadata", ProviderOutcomeStatus.Unsupported,
            TimeSpan.Zero, DiagnosticCode: "episode_metadata_unsupported")]));
    }
    AudiovisualCatalogCapabilities Capabilities { get; }
    Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default);
    Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(AudiovisualMediaKind kind, string query, CancellationToken token = default);
    Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(AudiovisualMediaKind kind, CancellationToken token = default);
}
