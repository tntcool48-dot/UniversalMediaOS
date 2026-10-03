namespace UniversalMediaOS.Core.OtherMedia;

public enum ProviderOutcomeStatus
{
    Success, Unavailable, Timeout, RateLimited, InvalidCredentials,
    NotConfigured, Unsupported, InvalidResponse, Disabled, Busy, NotFound
}

public sealed record ProviderOutcome(
    string ProviderId,
    ProviderOutcomeStatus Status,
    TimeSpan Elapsed,
    DateTimeOffset? RetryAtUtc = null,
    string DiagnosticCode = "");

public sealed record ProviderFetchResult(
    ProviderOutcome Outcome,
    string? ResponseText = null,
    bool IsFromCache = false,
    bool IsStale = false,
    DateTimeOffset? CacheExpiresAtUtc = null);
