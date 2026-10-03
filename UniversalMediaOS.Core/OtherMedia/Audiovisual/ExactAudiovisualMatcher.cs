using System.Globalization;
using System.Text;

namespace UniversalMediaOS.Core.OtherMedia;

public static class ExactAudiovisualMatcher
{
    /// <summary>Checks independent candidate evidence for the new source update API.</summary>
    public static SourceVerification VerifyEvidence(SourceSearchRequest request, AudiovisualSourceEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        static SourceVerification Unknown(string code) => new(SourceVerificationStatus.Unverified, code);
        static SourceVerification Reject(string code) => new(SourceVerificationStatus.Rejected, code);
        if (evidence?.Identity == null || evidence.Origin is not (SourceEvidenceOrigin.ProviderItem or SourceEvidenceOrigin.ObservedStream))
            return Unknown("independent_identity_missing");

        var wanted = request.Identity;
        var actual = evidence.Identity;
        if (wanted.Kind != actual.Kind) return Reject("media_kind_conflict");
        IReadOnlyList<AudiovisualExternalId> wantedIds;
        IReadOnlyList<AudiovisualExternalId> actualIds;
        try
        {
            if (AudiovisualIdentityKeys.HasConflictingIds(wanted, actual)) return Reject("identity_id_conflict");
            wantedIds = AudiovisualIdentityKeys.GetIds(wanted);
            actualIds = AudiovisualIdentityKeys.GetIds(actual);
        }
        catch (ArgumentException) { return Reject("invalid_identity"); }

        if (wanted.ContentForm == AudiovisualContentForm.Unknown || actual.ContentForm == AudiovisualContentForm.Unknown)
            return Unknown("content_form_missing");
        if (wanted.ContentForm != actual.ContentForm) return Reject("content_form_conflict");
        if (wanted.Year.HasValue && actual.Year.HasValue && wanted.Year != actual.Year) return Reject("year_conflict");
        bool sameId = wantedIds.Intersect(actualIds).Any();
        if (!sameId)
        {
            if (!wanted.Year.HasValue || !actual.Year.HasValue) return Unknown("title_year_evidence_missing");
            if (!GetNormalizedTitles(wanted).Overlaps(GetNormalizedTitles(actual))) return Reject("title_conflict");
        }
        if (wanted.Kind == AudiovisualMediaKind.Cartoon && actual.IsAnimated != true)
            return actual.IsAnimated == false ? Reject("animation_conflict") : Unknown("animation_evidence_missing");

        if (evidence.Unit == null) return Unknown("unit_evidence_missing");
        if (wanted.ContentForm == AudiovisualContentForm.Feature)
        {
            if (!(request.Unit ?? AudiovisualUnit.Feature).IsFeature || !evidence.Unit.IsFeature) return Reject("unit_conflict");
        }
        else
        {
            if (request.Unit?.SeasonNumber == null || request.Unit.EpisodeNumber == null ||
                evidence.Unit.SeasonNumber == null || evidence.Unit.EpisodeNumber == null)
                return Unknown("episode_number_missing");
            if (request.Unit.SeasonNumber != evidence.Unit.SeasonNumber || request.Unit.EpisodeNumber != evidence.Unit.EpisodeNumber)
                return Reject("episode_conflict");
            if (evidence.Unit.SeasonNumber < 0 || evidence.Unit.EpisodeNumber <= 0) return Reject("invalid_episode");
            if (evidence.Unit.SeasonNumber == 0 && !evidence.SpecialUnitEstablished) return Unknown("special_unit_unverified");
        }

        if (request.RequireArabicCartoonVerification && wanted.Kind != AudiovisualMediaKind.Cartoon)
            return Reject("invalid_arabic_lane");
        string? audio = request.RequireArabicCartoonVerification ? "ar" : request.AudioLanguage;
        if (request.RequireArabicCartoonVerification && !string.IsNullOrWhiteSpace(request.AudioLanguage) &&
            !AudiovisualProviderDefinition.IsArabicLanguage(request.AudioLanguage)) return Reject("language_request_conflict");
        foreach (var pair in new[] { (Preference: audio, Evidence: evidence.Audio, Name: "audio"), (Preference: request.SubtitleLanguage, Evidence: evidence.Subtitles, Name: "subtitle") })
        {
            if (string.IsNullOrWhiteSpace(pair.Preference)) continue;
            if (pair.Evidence?.Origin is not (SourceEvidenceOrigin.ProviderItem or SourceEvidenceOrigin.ObservedStream) || pair.Evidence.Languages == null || pair.Evidence.Languages.Count == 0)
                return Unknown(pair.Name + "_evidence_missing");
            if (!pair.Evidence.Languages.Any(language => AudiovisualProviderDefinition.LanguageMatches(language, pair.Preference)))
                return Reject(pair.Name + "_language_conflict");
        }
        return new(SourceVerificationStatus.Verified, "identity_unit_preferences_verified");
    }

    public static bool IsIdentityMatch(
        AudiovisualIdentity requested,
        AudiovisualIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(candidate);

        if (requested.Kind != candidate.Kind)
        {
            return false;
        }

        if (requested.ContentForm != AudiovisualContentForm.Unknown &&
            candidate.ContentForm != AudiovisualContentForm.Unknown &&
            requested.ContentForm != candidate.ContentForm)
        {
            return false;
        }

        if (requested.TmdbId is > 0 && candidate.TmdbId is > 0)
        {
            if (requested.TmdbId != candidate.TmdbId)
            {
                return false;
            }

            return !requested.Year.HasValue ||
                   !candidate.Year.HasValue ||
                   requested.Year.Value == candidate.Year.Value;
        }

        if (!requested.Year.HasValue ||
            !candidate.Year.HasValue ||
            requested.Year.Value != candidate.Year.Value)
        {
            return false;
        }

        HashSet<string> requestedTitles = GetNormalizedTitles(requested);
        HashSet<string> candidateTitles = GetNormalizedTitles(candidate);
        return requestedTitles.Count > 0 && requestedTitles.Overlaps(candidateTitles);
    }

    public static bool IsUnitMatch(
        AudiovisualMediaKind kind,
        AudiovisualUnit? requested,
        AudiovisualUnit? candidate)
    {
        AudiovisualUnit expected = requested ?? AudiovisualUnit.Feature;
        AudiovisualUnit actual = candidate ?? AudiovisualUnit.Feature;

        if (kind == AudiovisualMediaKind.Movie || expected.IsFeature)
        {
            return expected.IsFeature && actual.IsFeature;
        }

        if (!expected.SeasonNumber.HasValue || !expected.EpisodeNumber.HasValue)
        {
            return false;
        }

        return actual.SeasonNumber == expected.SeasonNumber &&
               actual.EpisodeNumber == expected.EpisodeNumber;
    }

    public static bool IsUnitMatch(
        AudiovisualIdentity identity,
        AudiovisualUnit? requested,
        AudiovisualUnit? candidate)
    {
        ArgumentNullException.ThrowIfNull(identity);
        AudiovisualUnit expected = requested ?? AudiovisualUnit.Feature;
        AudiovisualUnit actual = candidate ?? AudiovisualUnit.Feature;

        if (identity.ContentForm == AudiovisualContentForm.Feature ||
            identity.Kind == AudiovisualMediaKind.Movie ||
            expected.IsFeature)
        {
            return expected.IsFeature && actual.IsFeature;
        }

        if (!expected.SeasonNumber.HasValue || !expected.EpisodeNumber.HasValue)
        {
            return false;
        }

        return actual.SeasonNumber == expected.SeasonNumber &&
               actual.EpisodeNumber == expected.EpisodeNumber;
    }

    public static bool IsExactMatch(
        AudiovisualIdentity requestedIdentity,
        AudiovisualUnit? requestedUnit,
        AudiovisualIdentity candidateIdentity,
        AudiovisualUnit? candidateUnit)
    {
        return IsIdentityMatch(requestedIdentity, candidateIdentity) &&
               IsUnitMatch(requestedIdentity, requestedUnit, candidateUnit);
    }

    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool previousWasSeparator = false;

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static HashSet<string> GetNormalizedTitles(AudiovisualIdentity identity)
    {
        return new[]
            {
                identity.Title,
                identity.OriginalTitle
            }
            .Concat(identity.AlternateTitles ?? Array.Empty<string>())
            .Select(NormalizeTitle)
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

}
