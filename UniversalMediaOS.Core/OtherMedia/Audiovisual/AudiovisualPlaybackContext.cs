namespace UniversalMediaOS.Core.OtherMedia;

/// <summary>Identity captured at playback selection, independent of transport URLs and credentials.</summary>
public sealed class AudiovisualPlaybackContext
{
    public string WorkKey { get; }
    public string? UnitKey { get; }
    public AudiovisualIdentity Identity { get; }
    public AudiovisualUnit Unit { get; }
    public string Title { get; }
    public string PosterUrl { get; }
    public string ProviderId { get; }
    public AudiovisualIdentity SourceIdentity { get; }
    public AudiovisualSourceEvidence? Evidence { get; }

    public AudiovisualPlaybackContext(string workKey, AudiovisualIdentity identity, AudiovisualUnit unit,
        string title, string posterUrl, AudiovisualSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workKey);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(source);
        WorkKey = workKey;
        Identity = CopyIdentity(identity);
        Unit = unit with { };
        Title = title;
        PosterUrl = posterUrl;
        ProviderId = source.ProviderId;
        SourceIdentity = CopyIdentity(source.Identity);
        Evidence = source.Evidence is { } evidence ? evidence with
        {
            Identity = evidence.Identity is { } observed ? CopyIdentity(observed) : null,
            Unit = evidence.Unit is { } observedUnit ? observedUnit with { } : null,
            Audio = CopyAudio(evidence.Audio),
            Subtitles = CopyAudio(evidence.Subtitles)
        } : null;
        // Request numbering cannot establish that an unnumbered special is S0E1.
        bool specialEstablished = Evidence is { Origin: SourceEvidenceOrigin.ProviderItem or SourceEvidenceOrigin.ObservedStream,
            SpecialUnitEstablished: true } && Evidence.Unit == Unit;
        try { UnitKey = AudiovisualIdentityKeys.CreateUnitKey(Identity.ContentForm, Unit, specialEstablished); }
        catch (ArgumentException) { UnitKey = null; }
    }

    private static AudiovisualIdentity CopyIdentity(AudiovisualIdentity identity) => identity with
    {
        ExternalIds = Array.AsReadOnly(identity.ExternalIds.ToArray()),
        AlternateTitles = Array.AsReadOnly(identity.AlternateTitles.ToArray())
    };

    private static AudioEvidence? CopyAudio(AudioEvidence? audio) => audio is null ? null : audio with
    { Languages = Array.AsReadOnly(audio.Languages.ToArray()) };
}
