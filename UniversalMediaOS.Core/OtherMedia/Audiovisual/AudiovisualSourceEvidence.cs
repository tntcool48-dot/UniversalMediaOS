using System.Text.Json.Serialization;

namespace UniversalMediaOS.Core.OtherMedia;

[JsonConverter(typeof(JsonStringEnumConverter<SourceEvidenceOrigin>))]
public enum SourceEvidenceOrigin { Unknown, RequestEcho, ProviderItem, ObservedStream }

[JsonConverter(typeof(JsonStringEnumConverter<SourceVerificationStatus>))]
public enum SourceVerificationStatus { Unverified, Verified, Rejected }

public sealed record SourceVerification(SourceVerificationStatus Status, string ReasonCode);

public sealed record AudioEvidence
{
    public SourceEvidenceOrigin Origin { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();
}

public sealed record AudiovisualSourceEvidence
{
    public SourceEvidenceOrigin Origin { get; init; }
    public AudiovisualIdentity? Identity { get; init; }
    public AudiovisualUnit? Unit { get; init; }
    public bool SpecialUnitEstablished { get; init; }
    public AudioEvidence? Audio { get; init; }
    public AudioEvidence? Subtitles { get; init; }
}

public sealed record SourceSearchRequest
{
    public required AudiovisualIdentity Identity { get; init; }
    public AudiovisualUnit? Unit { get; init; }
    public string? AudioLanguage { get; init; }
    public string? SubtitleLanguage { get; init; }
    public string? ProviderId { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public bool RequireArabicCartoonVerification { get; init; }
}

public enum AudiovisualSourceUpdateKind { CandidateDiscovered, VerificationChanged, SourceReady, ProviderFailed, Completed }

public sealed record SourceCompletion(int Candidates, int Ready, int Unverified, int Rejected, int FailedProviders);

public sealed record AudiovisualSourceUpdate(
    Guid OperationId,
    string CandidateId,
    AudiovisualSourceUpdateKind Kind,
    AudiovisualSource? Source = null,
    SourceVerification? Verification = null,
    ProviderOutcome? Outcome = null,
    SourceCompletion? Completion = null);

public interface IAudiovisualSourceUpdates
{
    IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request, CancellationToken token = default);
}
