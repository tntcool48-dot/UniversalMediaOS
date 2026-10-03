namespace UniversalMediaOS.Core.OtherMedia;

// Null Unit preserves provider episodes whose exact playback numbering is unknown.
// In particular, a special must not silently become a feature or a guessed S0E1.
public sealed record AudiovisualEpisodeMetadata(
    AudiovisualExternalId Id,
    string Title,
    int? SeasonNumber,
    AudiovisualUnit? Unit,
    bool IsSpecial);

public sealed record AudiovisualUnitsResult(
    IReadOnlyList<AudiovisualEpisodeMetadata> Items,
    IReadOnlyList<ProviderOutcome> Outcomes,
    bool IsPartial = false);

public interface IAudiovisualEpisodeMetadataClient
{
    Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default);
}
