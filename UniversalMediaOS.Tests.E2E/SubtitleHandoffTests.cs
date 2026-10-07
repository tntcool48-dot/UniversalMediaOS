using System.Reflection;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SubtitleHandoffTests
{
    [Fact]
    public async Task NativeLoadedCaptionsSurviveRetryWithoutRefetchAndReleaseWithPlayer()
    {
        const string body = "WEBVTT\n\n1\n00:01:03.125 --> 00:01:05.750\nEnglish caption\n";
        var track = JsonSerializer.Deserialize<MediaSubtitleTrack>(JsonSerializer.Serialize(new {
            url = "https://captions.example/rate-limited.srt", label = "English", language = "en", inline_vtt = body }))!;
        var message = new PlayMediaMessage("https://video.example/movie.mp4", "Fixture", subtitles: [track]);
        Assert.Equal(body, Assert.Single(message.Subtitles).InlineVtt);
        using var profile = new CaptionTestDataScope();
        using var proxy = new HlsLoopbackProxy();
        using var player = new PlaybackViewModel(new DatabaseContext(), null, null, null, proxy);
        player.SetTabActive(false);
        player.LoadMedia(message.Value, message.Title, subtitles: message.Subtitles);
        using var http = new HttpClient();
        string first = Assert.Single(CurrentMedia(player).Slaves).Uri;
        Assert.Contains("/subtitle/English.vtt", first);
        using var firstResponse = await http.GetAsync(first);
        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("text/vtt", firstResponse.Content.Headers.ContentType!.MediaType);
        Assert.Equal(body, await firstResponse.Content.ReadAsStringAsync());
        player.RetryPlaybackCommand.Execute(null);
        string retry = Assert.Single(CurrentMedia(player).Slaves).Uri;
        Assert.NotEqual(first, retry);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(first)).StatusCode);
        Assert.Equal(body, await http.GetStringAsync(retry));
        player.StopAndRelease();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(retry)).StatusCode);
    }

    [Theory]
    [InlineData("WEBVTT\n\n")]
    [InlineData("<html>00:00:01.000 --> 00:00:02.000</html>")]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<script>decoy</script>")]
    public void InvalidLoadedCaptionsCannotBypassRemoteValidation(string body)
    {
        var track = new MediaSubtitleTrack("https://captions.example/en.vtt") { InlineVtt = body };
        Assert.Empty(Assert.Single(MediaSubtitleTrack.Copy([track])).InlineVtt);
        using var proxy = new HlsLoopbackProxy();
        Assert.Throws<ArgumentException>(() => proxy.RegisterSession(new ProxySession(
            track.Url, null, null, null, null, DateTime.UtcNow, SubtitleContent: body)));
    }

    [Fact]
    public void LoadedCaptionLimitAppliesToUtf8BytesAndDoesNotPersistSourceCaptions()
    {
        string oversized = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n" + new string('界', 1024 * 1024);
        var track = new MediaSubtitleTrack("https://captions.example/en.vtt") { InlineVtt = oversized };
        Assert.Empty(Assert.Single(MediaSubtitleTrack.Copy([track])).InlineVtt);
        var source = new UniversalMediaOS.Core.OtherMedia.AudiovisualSource { Subtitles = [track] };
        Assert.DoesNotContain("inline_vtt", JsonSerializer.Serialize(source));
    }

    [Fact]
    public void MovieCheckedRenditionSurvivesNativeRetryWithoutReplacingMaster()
    {
        using var profile = new CaptionTestDataScope();
        using var proxy = new HlsLoopbackProxy();
        using var player = new PlaybackViewModel(new DatabaseContext(), null, null, null, proxy);
        player.SetTabActive(false);
        const string master = "https://video.example/master.m3u8";
        const string rendition = "https://video.example/1080/index.m3u8";
        var message = new PlayMediaMessage(master, "Movie", validatedHlsVariant: rendition);
        player.LoadMedia(message.Value, message.Title, referer: "https://player.example/movie", validatedHlsVariant: message.ValidatedHlsVariant);
        string first = CurrentMedia(player).Mrl;
        Assert.Contains("&variant=" + Uri.EscapeDataString(rendition), first);
        Assert.Equal(master, player.SourceInput);
        player.RetryPlaybackCommand.Execute(null);
        Assert.Contains("&variant=" + Uri.EscapeDataString(rendition), CurrentMedia(player).Mrl);
        player.LoadMedia("https://video.example/other.mp4", "Another movie", referer: "https://player.example/movie");
        Assert.DoesNotContain("&variant=", CurrentMedia(player).Mrl);
    }
    [Fact]
    public void ScraperCaptionsRetainLanguageAndOwnRequestContextThroughMessage()
    {
        const string json = """
            {"url":"https://cdn.example/master.m3u8","audio_languages":["jpn"],
             "subtitles":[{"url":"https://captions.example/en.vtt?token=fixture","label":"English",
                "language":"en","cookie":"session=fixture","referer":"https://player.example/episode/1",
                "headers":{"X-Caption-Token":"fixture"}}]}
            """;
        var stream = JsonSerializer.Deserialize<ScraperStreamResult>(json)!;
        var message = new PlayMediaMessage(stream.Url!, "Fixture", subtitles: stream.Subtitles);
        ((IDictionary<string, string>)stream.Subtitles![0].RequestHeaders!)["X-Caption-Token"] = "changed";
        var track = Assert.Single(message.Subtitles);
        Assert.True(track.IsEnglish);
        Assert.Equal("session=fixture", track.Cookie);
        Assert.Equal("https://player.example/episode/1", track.Referer);
        Assert.Equal("fixture", track.RequestHeaders!["X-Caption-Token"]);
        Assert.Equal("jpn", Assert.Single(stream.AudioLanguages!));
    }

    [Fact]
    public async Task NativeRetryRetainsCaptionAttachmentAndReleasesOldOwnedSession()
    {
        using var profile = new CaptionTestDataScope();
        using var proxy = new HlsLoopbackProxy();
        using var player = new PlaybackViewModel(new DatabaseContext(), null, null, null, proxy);
        player.LoadMedia("https://video.example/fixture.mp4", "Fixture", subtitles:
            [new MediaSubtitleTrack("https://captions.example/english.vtt", "English", "en",
                Cookie: "caption-session=fixture", Referer: "https://player.example/episode/1")],
            audioNotice: "Dub server selected · audio language unverified");
        string first = Assert.Single(CurrentMedia(player).Slaves).Uri;
        Assert.Contains("/subtitle/English.vtt", first);

        // The inactive tab stages retry media without starting a network decoder.
        player.SetTabActive(false);
        player.RetryPlaybackCommand.Execute(null);
        string second = Assert.Single(CurrentMedia(player).Slaves).Uri;
        Assert.NotEqual(first, second);
        Assert.Equal("Dub server selected · audio language unverified", player.AudioNotice);
        using var http = new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(first)).StatusCode);

        player.StopAndRelease();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(second)).StatusCode);
    }

    [Fact]
    public void CaptionsRejectFileUrisAndDuplicateTracksBeforeNativeAttachment()
    {
        var tracks = MediaSubtitleTrack.Copy([
            new("file:///C:/private.srt"),
            new("https://captions.example/en.vtt", "English", "en"),
            new("https://captions.example/en.vtt", "Duplicate", "en")]);
        Assert.Equal("English", Assert.Single(tracks).Label);
    }

    [Fact]
    public async Task NamedCaptionChoiceAttachesOnlyItsFileAndRetainsChoiceAcrossReorderedEpisodes()
    {
        using var profile = new CaptionTestDataScope();
        using var proxy = new HlsLoopbackProxy();
        using var player = new PlaybackViewModel(new DatabaseContext(), null, null, null, proxy);
        player.SetTabActive(false);
        player.LoadMedia("https://video.example/1.mp4", "Fixture", subtitles: [
            new("https://captions.example/1/en.vtt", "English", "eng"),
            new("https://captions.example/1/ar.vtt", "Arabic", "ar")]);
        string english = Assert.Single(CurrentMedia(player).Slaves).Uri;
        player.SelectedCaption = Assert.Single(player.CaptionOptions, option => option.Label == "Arabic");
        string arabic = Assert.Single(CurrentMedia(player).Slaves).Uri;
        Assert.Contains("/subtitle/Arabic.vtt", arabic);
        Assert.Equal("Arabic", player.SelectedCaption!.Label);
        using var http = new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(english)).StatusCode);
        player.LoadMedia("https://video.example/2.mp4", "Fixture", subtitles: [
            new("https://captions.example/2/ar.vtt", "Arabic", "ara"),
            new("https://captions.example/2/en.vtt", "English", "en")]);
        Assert.Contains("/subtitle/Arabic.vtt", Assert.Single(CurrentMedia(player).Slaves).Uri);
        Assert.Equal("Arabic", player.SelectedCaption!.Label);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(arabic)).StatusCode);
    }

    [Fact]
    public void CaptionOffAndMissingRememberedLanguageNeverLoadAnUnrequestedFile()
    {
        using var profile = new CaptionTestDataScope();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        player.LoadMedia("https://video.example/1.mp4", "Fixture", subtitles: [
            new("https://captions.example/en.vtt", "English", "en"),
            new("https://captions.example/ar.vtt", "Arabic", "ar")]);
        player.SelectedCaption = player.CaptionOptions.Single(option => option.Label == "Arabic");
        player.LoadMedia("https://video.example/2.mp4", "Fixture", subtitles:
            [new("https://captions.example/2/en.vtt", "English", "en")]);
        Assert.Empty(CurrentMedia(player).Slaves);
        Assert.Equal("off", player.SelectedCaption!.Key);
        player.SelectedCaption = player.CaptionOptions.Single(option => option.Label == "English");
        player.SelectedCaption = player.CaptionOptions.Single(option => option.Key == "off");
        player.RetryPlaybackCommand.Execute(null);
        Assert.Empty(CurrentMedia(player).Slaves);
        Assert.Equal("off", player.SelectedCaption!.Key);
    }

    [Fact]
    public void CaptionToggleRestoresNamedLanguageAndDoesNotSubstituteAMissingChoice()
    {
        using var profile = new CaptionTestDataScope();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        player.LoadMedia("https://video.example/1.mp4", "Fixture", subtitles: [
            new("https://captions.example/1/en.vtt", "English", "en"),
            new("https://captions.example/1/ar.vtt", "Arabic", "ar")]);
        player.SelectedCaption = player.CaptionOptions.Single(option => option.Label == "Arabic");
        player.ToggleCaptionsCommand.Execute(null);
        Assert.Equal("off", player.SelectedCaption!.Key);
        Assert.Empty(CurrentMedia(player).Slaves);
        player.ToggleCaptionsCommand.Execute(null);
        Assert.Equal("Arabic", player.SelectedCaption!.Label);
        Assert.Contains("ar.vtt", Assert.Single(CurrentMedia(player).Slaves).Uri);
        player.ToggleCaptionsCommand.Execute(null);
        player.LoadMedia("https://video.example/2.mp4", "Fixture", subtitles:
            [new("https://captions.example/2/en.vtt", "English", "en")]);
        player.ToggleCaptionsCommand.Execute(null);
        Assert.Equal("off", player.SelectedCaption!.Key);
        Assert.Empty(CurrentMedia(player).Slaves);
        player.LoadMedia("https://video.example/3.mp4", "Fixture", subtitles: [
            new("https://captions.example/3/ar.vtt", "Arabic", "ara"),
            new("https://captions.example/3/en.vtt", "English", "eng")]);
        player.ToggleCaptionsCommand.Execute(null);
        Assert.Equal("Arabic", player.SelectedCaption!.Label);
        Assert.Contains("ar.vtt", Assert.Single(CurrentMedia(player).Slaves).Uri);
    }

    [Fact]
    public void BoundDownloadedCaptionSelectorRetainsOffAndEnglishAcrossRefreshAndRetry()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var profile = new CaptionTestDataScope();
            string directory = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "CaptionSelector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string video = Path.Combine(directory, "episode.mp4");
            string caption = Path.Combine(directory, "episode.en.srt");
            File.WriteAllBytes(video, []);
            File.WriteAllText(caption, "1\n00:00:00,000 --> 00:00:10,000\nFirst line\nSecond line\n");
            try
            {
                using var player = new PlaybackViewModel(new DatabaseContext());
                player.SetTabActive(false);
                player.LoadMedia(video, "Episode", localCaptionPaths: [caption]);
                var selector = new System.Windows.Controls.ComboBox { DataContext = player, DisplayMemberPath = "Label" };
                selector.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(player.CaptionOptions)));
                selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(player.SelectedCaption)) { Mode = System.Windows.Data.BindingMode.TwoWay });
                Assert.Contains("English", ((PlaybackTrackOption)selector.SelectedItem).Label);
                selector.SelectedItem = player.CaptionOptions.Single(option => option.Key == "off");
                Assert.Equal("off", player.SelectedCaption!.Key);
                Assert.Empty(CurrentMedia(player).Slaves);
                Assert.Equal("off", ((PlaybackTrackOption)selector.SelectedItem).Key);
                player.RetryPlaybackCommand.Execute(null);
                Assert.Equal("off", ((PlaybackTrackOption)selector.SelectedItem).Key);
                selector.SelectedItem = player.CaptionOptions.Single(option => option.Label.Contains("English"));
                Assert.Contains("English", player.SelectedCaption!.Label);
                Assert.Single(CurrentMedia(player).Slaves);
                Assert.Contains("English", ((PlaybackTrackOption)selector.SelectedItem).Label);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public void AmbiguousAndUnlabelledCaptionsUseFileIdentityInsteadOfGuessingByOrder()
    {
        var first = PlaybackTrackOption.ExternalCaptions([
            new("https://captions.example/1/a.vtt", "English", "en"),
            new("https://captions.example/1/b.vtt", "English", "en"),
            new("https://captions.example/1/c.vtt")]);
        Assert.Equal(3, first.Select(option => option.Key).Distinct().Count());
        Assert.All(first, option => Assert.StartsWith("external-url:", option.Key));
        var next = PlaybackTrackOption.ExternalCaptions([new("https://captions.example/2/c.vtt")]);
        Assert.NotEqual(first[2].Key, next[0].Key);
    }

    [Fact]
    public void NativeAudioIdentityUsesDeclaredLanguageAndDescriptionInsteadOfDecoderNumber()
    {
        var english = PlaybackTrackOption.Native(2, "Track 1", "eng", "Commentary", "Audio");
        var reordered = PlaybackTrackOption.Native(7, "Track 2", "en", "Commentary", "Audio");
        Assert.Equal("English · Commentary", english.Label);
        Assert.Equal(english.Key, reordered.Key);
        var unknown = PlaybackTrackOption.Native(3, "Track 2", "", "", "Audio");
        Assert.StartsWith("unverified:", unknown.Key);
        Assert.DoesNotContain("English", unknown.Label);
    }

    [Fact]
    public void PauseIntentIsRetainedBeforeTheNativePlayerAcknowledgesIt()
    {
        using var profile = new CaptionTestDataScope();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsPlaying = true;
        player.TogglePlayPauseCommand.Execute(null);
        Assert.False(player.IsPlaying);
        Assert.True((bool)typeof(PlaybackViewModel).GetField("_pauseWhenStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)!);
        player.TogglePlayPauseCommand.Execute(null);
        Assert.False((bool)typeof(PlaybackViewModel).GetField("_pauseWhenStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)!);
    }

    [Theory]
    [InlineData(2.5, true)]
    [InlineData(38.5, false)]
    public void NativeReloadEstablishesCurrentPositionAndPauseBeforeStarting(double seconds, bool pause)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var profile = new CaptionTestDataScope();
            using var player = new PlaybackViewModel(new DatabaseContext());
            player.SetTabActive(false);
            player.LoadMedia("https://video.example/fixture.mp4", "Fixture",
                reloadPositionSeconds: seconds, pauseAfterReload: pause);
            ResumeDispatcherContentionTests.WaitForResumeLoad(player);
            Assert.Equal(seconds, typeof(PlaybackViewModel).GetField("_pendingResumePositionSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player));
            Assert.Equal(pause, typeof(PlaybackViewModel).GetField("_pauseWhenStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player));
            Assert.False(player.IsPlaying);
        });
    }

    [Fact]
    public void QualitySelectionPreservesAudioAndCaptionGroups()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",LANGUAGE="en",URI="en.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",LANGUAGE="en",URI="subs.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO="audio",SUBTITLES="subs"
            360.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2,AUDIO="audio",SUBTITLES="subs"
            1080.m3u8
            """;
        string selected = HlsLoopbackProxy.SelectVariantManifest(manifest,
            "https://cdn.example/master/", "https://cdn.example/master/1080.m3u8");
        Assert.Contains("TYPE=AUDIO", selected);
        Assert.Contains("TYPE=SUBTITLES", selected);
        Assert.Contains("1080.m3u8", selected);
        Assert.DoesNotContain("360.m3u8", selected);
        Assert.Throws<InvalidDataException>(() => HlsLoopbackProxy.SelectVariantManifest(manifest,
            "https://cdn.example/master/", "https://cdn.example/master/unknown.m3u8"));
    }

    [Theory]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nEnglish caption", true)]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nEnglish caption", true)]
    [InlineData("<html>WEBVTT 00:00:01.000 --> 00:00:02.000</html>", false)]
    [InlineData("{\"error\":\"unavailable\"}", false)]
    public void CaptionProxyRejectsDecoysAndAcceptsRealTimedText(string body, bool expected) =>
        Assert.Equal(expected, HlsLoopbackProxy.IsSubtitlePayload(System.Text.Encoding.UTF8.GetBytes(body)));

    private static Media CurrentMedia(PlaybackViewModel player) =>
        (Media)typeof(PlaybackViewModel).GetField("_currentMedia", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)!;

    private sealed class CaptionTestDataScope : IDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        public CaptionTestDataScope() => Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT",
            Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "Captions-" + Guid.NewGuid().ToString("N")));
        public void Dispose() => Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", previous);
    }
}
