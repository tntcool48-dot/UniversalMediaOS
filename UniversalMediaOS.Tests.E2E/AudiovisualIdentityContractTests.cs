using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AudiovisualIdentityContractTests
{
    [Fact]
    public void OldJsonRetainsDefaultsAndUnknownFormDoesNotInventTmdbNamespace()
    {
        var identity = JsonSerializer.Deserialize<AudiovisualIdentity>("""{"Kind":"Movie","Title":"Fixture","TmdbId":42}""")!;
        Assert.Null(identity.PrimaryId);
        Assert.Null(identity.IsAnimated);
        Assert.Empty(AudiovisualIdentityKeys.GetIds(identity));
        Assert.StartsWith("av:local:", AudiovisualIdentityKeys.CreateWorkKey(identity));
    }

    [Fact]
    public void NumericNamespacesRemainSeparateAndProviderItemCaseIsPreserved()
    {
        Assert.NotEqual(AudiovisualIdentityKeys.ExternalKey(new("tmdb", "movie", "42")), AudiovisualIdentityKeys.ExternalKey(new("tmdb", "tv", "42")));
        Assert.Equal("av:tmdb:movie:42", AudiovisualIdentityKeys.ExternalKey(new(" TMDB ", " Movie ", "00042")));
        Assert.NotEqual(AudiovisualIdentityKeys.ExternalKey(new("internetarchive", "item", "FilmA")), AudiovisualIdentityKeys.ExternalKey(new("internetarchive", "item", "filma")));
    }

    [Fact]
    public void KnownKeysIgnorePresentationAndStayFrozenAfterEnrichment()
    {
        var identity = new AudiovisualIdentity { ContentForm = AudiovisualContentForm.Feature, TmdbId = 42 };
        string key = AudiovisualIdentityKeys.CreateWorkKey(identity);
        Assert.Equal(key, AudiovisualIdentityKeys.CreateWorkKey(identity with { Kind = AudiovisualMediaKind.Cartoon, Title = "Translated", Year = 2026 }));
        string local = AudiovisualIdentityKeys.CreateWorkKey(new());
        Assert.Equal(local, AudiovisualIdentityKeys.CreateWorkKey(identity, local));
        Assert.NotEqual(local, AudiovisualIdentityKeys.CreateWorkKey(new()));
    }

    [Fact]
    public void ConflictsInAnySharedNamespacePreventCanonicalization()
    {
        var first = new AudiovisualIdentity { PrimaryId = new("imdb", "title", "tt42") };
        var second = first with { PrimaryId = new("imdb", "title", "tt43") };
        Assert.True(AudiovisualIdentityKeys.HasConflictingIds(first, second));
        Assert.Throws<ArgumentException>(() => AudiovisualIdentityKeys.CreateWorkKey(first with { ExternalIds = [second.PrimaryId!] }));
        Assert.Throws<ArgumentException>(() => AudiovisualIdentityKeys.CreateWorkKey(new() { ContentForm = AudiovisualContentForm.Feature, PrimaryId = new("tmdb", "tv", "42") }));
    }

    [Fact]
    public void NewIdentitySurvivesJsonRoundTrip()
    {
        var identity = new AudiovisualIdentity { PrimaryId = new("internetarchive", "item", "MixedCase"), IsAnimated = true };
        var copy = JsonSerializer.Deserialize<AudiovisualIdentity>(JsonSerializer.Serialize(identity))!;
        Assert.Equal(identity.PrimaryId, copy.PrimaryId);
        Assert.Equal(true, copy.IsAnimated);
        Assert.Equal(AudiovisualIdentityKeys.CreateWorkKey(identity), AudiovisualIdentityKeys.CreateWorkKey(copy));
    }

    [Fact]
    public void MissingEpisodesAreNotEpisodeOneAndSpecialsRequireEvidence()
    {
        Assert.Equal("feature", AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Feature, null));
        Assert.Throws<ArgumentException>(() => AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Series, null));
        var special = new AudiovisualUnit { SeasonNumber = 0, EpisodeNumber = 2 };
        Assert.Throws<ArgumentException>(() => AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Series, special));
        Assert.Equal("season:0:episode:2", AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Series, special, verifiedSpecial: true));
        Assert.Equal("season:2:episode:3", AudiovisualIdentityKeys.CreateUnitKey(AudiovisualContentForm.Series, new() { SeasonNumber = 2, EpisodeNumber = 3 }));
    }
}
