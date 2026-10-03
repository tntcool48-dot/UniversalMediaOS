using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AudiovisualPlaybackContextTests
{
    [Fact]
    public void CapturedIdentityAndEvidenceCannotBeChangedThroughProducerLists()
    {
        var ids = new List<AudiovisualExternalId> { new("tvmaze", "show", "526") };
        var titles = new List<string> { "Original title" };
        var languages = new List<string> { "en" };
        var identity = new AudiovisualIdentity { ContentForm = AudiovisualContentForm.Series,
            ExternalIds = ids, AlternateTitles = titles };
        var unit = new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 1 };
        var source = new AudiovisualSource { Identity = identity, Unit = unit,
            Evidence = new() { Identity = identity, Unit = unit, Origin = SourceEvidenceOrigin.ProviderItem,
                Audio = new() { Languages = languages, Origin = SourceEvidenceOrigin.ObservedStream } } };
        var context = new AudiovisualPlaybackContext("av:local:frozen", identity, unit, "Title", "", source);
        ids.Clear(); titles[0] = "Changed"; languages[0] = "ar";
        Assert.Single(context.Identity.ExternalIds);
        Assert.Equal("Original title", Assert.Single(context.SourceIdentity.AlternateTitles));
        Assert.Equal("en", Assert.Single(context.Evidence!.Audio!.Languages));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)context.Evidence.Audio.Languages)[0] = "fr");
        Assert.Equal("season:2:episode:1", context.UnitKey);
        Assert.Equal("av:local:frozen", context.WorkKey);
    }

    [Theory]
    [InlineData(SourceEvidenceOrigin.RequestEcho, true, false)]
    [InlineData(SourceEvidenceOrigin.ProviderItem, false, false)]
    [InlineData(SourceEvidenceOrigin.ProviderItem, true, true)]
    public void SpecialNumberingRequiresIndependentEvidence(SourceEvidenceOrigin origin, bool established, bool exact)
    {
        var identity = new AudiovisualIdentity { ContentForm = AudiovisualContentForm.Series };
        var unit = new AudiovisualUnit { SeasonNumber = 0, EpisodeNumber = 1 };
        var context = new AudiovisualPlaybackContext("av:local:1", identity, unit, "Special", "", new()
        { Evidence = new() { Origin = origin, SpecialUnitEstablished = established, Unit = unit } });
        Assert.Equal(exact ? "season:0:episode:1" : null, context.UnitKey);
    }

    [Fact]
    public void UnknownFormAndUnnumberedSeriesNeverBecomeFeatures()
    {
        foreach (var form in new[] { AudiovisualContentForm.Unknown, AudiovisualContentForm.Series })
        {
            var context = new AudiovisualPlaybackContext("av:local:1", new() { ContentForm = form },
                AudiovisualUnit.Feature, "Title", "", new());
            Assert.Null(context.UnitKey);
        }
    }

    [Fact]
    public void TransportChangesDoNotChangeKeysOrLeakTransportSecretsIntoContext()
    {
        var identity = new AudiovisualIdentity { ContentForm = AudiovisualContentForm.Feature,
            PrimaryId = new("imdb", "title", "tt0133093") };
        var source = new AudiovisualSource { Location = new("https://example.invalid/movie?token=secret"),
            Cookie = "private-cookie", RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "secret" } };
        string key = AudiovisualIdentityKeys.CreateWorkKey(identity);
        var first = new AudiovisualPlaybackContext(key, identity, AudiovisualUnit.Feature, "Title", "", source);
        var second = new AudiovisualPlaybackContext(key, identity, AudiovisualUnit.Feature, "New display title", "", source with
            { Location = new("https://other.invalid/different") });
        Assert.Equal(first.WorkKey, second.WorkKey);
        Assert.Equal("feature", second.UnitKey);
        string json = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private-cookie", json);
        Assert.DoesNotContain("example.invalid", json);
        var message = new PlayMediaMessage(source.Location.AbsoluteUri, "Title", audiovisualContext: first);
        Assert.Same(first, message.AudiovisualContext);
        Assert.Null(new PlayMediaMessage("legacy", "Anime").AudiovisualContext);
    }
}
