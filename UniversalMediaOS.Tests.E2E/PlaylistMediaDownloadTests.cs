using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PlaylistMediaDownloadTests
{
    [Fact]
    public async Task FiniteDashLocalizesNumberAndTimeTemplatesAndRetainsBothAudioLanguages()
    {
        using var fixture = new Fixture();
        PrepareDash(fixture);
        var source = DashSource();
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.Contains("lang=\"en\"", fixture.Runner.Manifest!);
        Assert.Contains("lang=\"ja\"", fixture.Runner.Manifest!);
        Assert.DoesNotContain("https://", fixture.Runner.Manifest!);
        Assert.DoesNotContain("SegmentTemplate", fixture.Runner.Manifest!);
        Assert.DoesNotContain("extension_picky", fixture.Runner.Arguments!);
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/v/00007-0.m4s");
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/v/00008-2.m4s");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.Contains("low"));
        var second = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.Equal(result.FilePath, second.FilePath);
        result.Lease.Dispose();
        Assert.True(File.Exists(second.FilePath));
        second.Lease.Dispose();
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task DashSegmentListsKeepRangesOnOwnedFilesAndFiniteNegativeRepeatsExpandToTheEnd()
    {
        using var fixture = new Fixture();
        string list = "<SegmentList timescale=\"1\"><Initialization sourceURL=\"whole.mp4\" range=\"0-99\"/>" +
            "<SegmentTimeline><S t=\"0\" d=\"2\" r=\"-1\"/></SegmentTimeline>" +
            "<SegmentURL media=\"whole.mp4\" mediaRange=\"100-199\"/><SegmentURL media=\"whole.mp4\" mediaRange=\"200-999\"/></SegmentList>";
        fixture.Handler.Text("video.mpd", DashManifest(list));
        fixture.Handler.Bytes("whole.mp4", Mp4());
        var source = DashSource();
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/whole.mp4"));
        Assert.Contains("mediaRange=\"200-999\"", fixture.Runner.Manifest!);
        Assert.DoesNotContain("r=\"-1\"", fixture.Runner.Manifest!);
        result.Lease.Dispose();
    }

    [Theory]
    [InlineData("live")]
    [InlineData("periods")]
    [InlineData("protected")]
    [InlineData("external-xml")]
    [InlineData("private")]
    [InlineData("gap")]
    [InlineData("excessive")]
    [InlineData("decoy")]
    [InlineData("unsupported-token")]
    [InlineData("offset")]
    public async Task UnsafeIncompleteOrAmbiguousDashNeverReachesHandoff(string failure)
    {
        using var fixture = new Fixture();
        PrepareDash(fixture);
        string manifest = DashManifest();
        if (failure == "live") manifest = manifest.Replace("type=\"static\"", "type=\"dynamic\"");
        if (failure == "periods") manifest = manifest.Replace("</MPD>", "<Period duration=\"PT4S\"/></MPD>");
        if (failure == "protected") manifest = manifest.Replace("<Period>", "<Period><ContentProtection schemeIdUri=\"urn:test\"/>");
        if (failure == "external-xml") manifest = "<!DOCTYPE MPD [<!ENTITY external SYSTEM \"file:///private\">]>" + manifest;
        if (failure == "private") manifest = manifest.Replace("init-$RepresentationID$.mp4", "http://127.0.0.1/init.mp4");
        if (failure == "gap") manifest = manifest.Replace("media=\"$RepresentationID$/$Number%05d$-$Time$.m4s\"/>",
            "media=\"$RepresentationID$/$Number%05d$-$Time$.m4s\"><SegmentTimeline><S t=\"1\" d=\"2\"/></SegmentTimeline></SegmentTemplate>");
        if (failure == "excessive") manifest = manifest.Replace("PT4S", "PT999999S");
        if (failure == "decoy") fixture.Handler.Bytes("init-v.mp4", Encoding.UTF8.GetBytes("<html>not video</html>"));
        if (failure == "unsupported-token") manifest = manifest.Replace("$Number%05d$", "$Unknown$");
        if (failure == "offset") manifest = manifest.Replace("timescale=\"1\"", "presentationTimeOffset=\"1\" timescale=\"1\"");
        fixture.Handler.Text("video.mpd", manifest);
        var source = DashSource();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task DashCancellationDuringAssemblyRemovesTheOwnedJobAndStopsItsRunner()
    {
        using var fixture = new Fixture();
        PrepareDash(fixture); fixture.Runner.Stall = true;
        using var cancellation = new CancellationTokenSource();
        var source = DashSource();
        var transfer = fixture.Service.DownloadTemporaryAsync(source, Context(source), token: cancellation.Token);
        await fixture.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.True(fixture.Runner.Cancelled);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task RealFfmpegDashDownloadAssemblesDecodableVideoWithTwoAudioLanguagesAndFinalOwnerCleanup()
    {
        using var fixture = new Fixture(realMedia: true);
        string generated = Path.Combine(fixture.Root, "generated"); Directory.CreateDirectory(generated);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var runner = new PreparationProcessRunner();
        var encoded = await runner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-f", "lavfi", "-i", "sine=frequency=440",
            "-f", "lavfi", "-i", "sine=frequency=880", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=jpn", "-t", "4",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", "-f", "dash",
            "-seg_duration", "2", "-adaptation_sets", "id=0,streams=v id=1,streams=1 id=2,streams=2",
            Path.Combine(generated, "video.mpd").Replace('\\', '/')], deadline.Token);
        Assert.Equal(0, encoded.ExitCode);
        foreach (string path in Directory.GetFiles(generated)) fixture.Handler.Bytes(Path.GetFileName(path), File.ReadAllBytes(path));
        var source = DashSource();
        var first = await fixture.Service.DownloadTemporaryAsync(source, Context(source), token: deadline.Token);
        using var firstLease = first.Lease;
        var probe = await runner.RunAsync("ffprobe", ["-v", "error", "-show_entries",
            "format=duration:stream=codec_type:stream_tags=language", "-of", "json", first.FilePath], deadline.Token);
        Assert.Equal(0, probe.ExitCode);
        using var json = JsonDocument.Parse(probe.Output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
        Assert.Equal(2, audio.Length);
        Assert.Contains(audio, stream => stream.GetProperty("tags").GetProperty("language").GetString() == "eng");
        Assert.Contains(audio, stream => stream.GetProperty("tags").GetProperty("language").GetString() == "jpn");
        var decoded = await runner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error", "-xerror",
            "-i", first.FilePath, "-map", "0:v", "-map", "0:a", "-f", "null", "-"], deadline.Token);
        Assert.Equal(0, decoded.ExitCode);
        int requests = fixture.Handler.Requests.Count;
        var second = await fixture.Service.DownloadTemporaryAsync(source, Context(source), token: deadline.Token);
        using var secondLease = second.Lease;
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        var identity = Context(source).Identity with { PrimaryId = new("tvmaze", "show", "169"), Year = 2008 };
        var librarySource = source with { Evidence = new()
            { Origin = SourceEvidenceOrigin.ProviderItem, Identity = identity, Unit = Context(source).Unit } };
        var libraryContext = new AudiovisualPlaybackContext(Context(source).WorkKey, identity, Context(source).Unit, "Series", "", librarySource);
        var saved = await fixture.Service.DownloadLibraryAsync(librarySource, libraryContext, token: deadline.Token);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(saved.FilePath));
        first.Lease.Dispose(); Assert.True(File.Exists(second.FilePath));
        second.Lease.Dispose(); Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.True(File.Exists(saved.FilePath));
        Assert.True(File.Exists(saved.MetadataPath));
    }

    private static byte[] Mp4()
    {
        byte[] bytes = new byte[1000];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, 1000);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    private static string DashManifest(string? segments = null) =>
        "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT4S\"><Period>" +
        (segments ?? "<SegmentTemplate timescale=\"1\" duration=\"2\" startNumber=\"7\" initialization=\"init-$RepresentationID$.mp4\" media=\"$RepresentationID$/$Number%05d$-$Time$.m4s\"/>") +
        "<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><Representation id=\"low\" bandwidth=\"1\"/><Representation id=\"v\" bandwidth=\"2\"/></AdaptationSet>" +
        "<AdaptationSet contentType=\"audio\" mimeType=\"audio/mp4\" lang=\"en\"><Representation id=\"en\" bandwidth=\"1\"/></AdaptationSet>" +
        "<AdaptationSet contentType=\"audio\" mimeType=\"audio/mp4\" lang=\"ja\"><Representation id=\"ja\" bandwidth=\"1\"/></AdaptationSet></Period></MPD>";

    private static void PrepareDash(Fixture fixture)
    {
        fixture.Handler.Text("video.mpd", DashManifest());
        foreach (string id in new[] { "v", "en", "ja" })
            foreach (string name in new[] { $"init-{id}.mp4", $"{id}/00007-0.m4s", $"{id}/00008-2.m4s" })
                fixture.Handler.Bytes(name, Mp4());
    }

    private static AudiovisualSource DashSource() => Source() with
    { Location = new("https://93.184.216.34/video.mpd"), ContentType = "application/dash+xml" };

    [Fact]
    public async Task FiniteHlsSharesLocalVideoAndTimedSidecarUntilFinalOwnerCloses()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf());
        fixture.Handler.Bytes("one.ts", new byte[1000]);
        fixture.Handler.Bytes("two.ts", new byte[1000]);
        var source = Source() with { Subtitles = [new("https://93.184.216.34/caption", "English", "en")
            { InlineVtt = "WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nTwo lines\nStay together\n" }] };
        var first = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        var second = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.Equal(first.CaptionPaths, second.CaptionPaths);
        Assert.Contains("Two lines\nStay together", File.ReadAllText(Assert.Single(first.CaptionPaths)));
        Assert.EndsWith(".en.vtt", first.CaptionPaths[0]);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(first.FilePath)!).Count(path => !path.EndsWith(".active")));
        first.Lease.Dispose();
        Assert.True(File.Exists(second.FilePath));
        Assert.True(File.Exists(second.CaptionPaths[0]));
        second.Lease.Dispose();
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CheckedRenditionRetainsBothAudioLanguagesAndDoesNotFetchOtherVideo()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Japanese\",LANGUAGE=\"ja\",URI=\"ja.m3u8\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",URI=\"en.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=100,AUDIO=\"aud\"\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=500,AUDIO=\"aud\"\nhigh.m3u8\n");
        fixture.Handler.Text("low.m3u8", Leaf());
        fixture.Handler.Text("ja.m3u8", Leaf("ja"));
        fixture.Handler.Text("en.m3u8", Leaf("en"));
        foreach (string name in new[] { "one.ts", "two.ts", "jaone.ts", "jatwo.ts", "enone.ts", "entwo.ts" })
            fixture.Handler.Bytes(name, new byte[1000]);
        var source = Source() with { ValidatedHlsVariant = "https://93.184.216.34/low.m3u8" };
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/high.m3u8");
        Assert.Contains("LANGUAGE=\"ja\"", fixture.Runner.Manifest!);
        Assert.Contains("LANGUAGE=\"en\"", fixture.Runner.Manifest!);
        Assert.DoesNotContain("https://", fixture.Runner.Manifest!);
        Assert.Contains("file,crypto", fixture.Runner.Arguments!);
        result.Lease.Dispose();
    }

    [Fact]
    public async Task AesKeyAndInitMapStayLocalAndCapturedCredentialsDoNotFollowCdnRedirect()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\",IV=0x00000000000000000000000000000001\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2,\none.ts\n#EXTINF:2,\nhttps://8.8.8.8/redirect.ts\n#EXT-X-ENDLIST\n");
        fixture.Handler.Bytes("key", new byte[16]); fixture.Handler.Bytes("init.mp4", new byte[1000]);
        fixture.Handler.Bytes("one.ts", new byte[1000]); fixture.Handler.Bytes("cdn.ts", new byte[1000]);
        fixture.Handler.Redirects["/redirect.ts"] = new("https://1.1.1.1/cdn.ts");
        var source = Source() with { Cookie = "session=private", Referer = "https://provider.example/watch?token=private",
            RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer private" } };
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.All(fixture.Handler.Requests.Where(request => request.Uri.Host == "93.184.216.34"), request =>
        { Assert.Equal("session=private", request.Cookie); Assert.Equal("Bearer private", request.Authorization); });
        Assert.All(fixture.Handler.Requests.Where(request => request.Uri.Host != "93.184.216.34"), request =>
        { Assert.Null(request.Cookie); Assert.Null(request.Authorization); Assert.Equal("https://provider.example/", request.Referer); });
        Assert.Contains("URI=\"asset-00001.key\"", fixture.Runner.Manifest!);
        Assert.Contains("URI=\"asset-00002.mp4\"", fixture.Runner.Manifest!);
        result.Lease.Dispose();
    }

    [Theory]
    [InlineData("live")]
    [InlineData("page")]
    [InlineData("private")]
    [InlineData("key")]
    [InlineData("variant")]
    [InlineData("duration")]
    [InlineData("size")]
    public async Task IncompleteUnsafeOrWrongLengthVideoNeverReachesHandoff(string failure)
    {
        using var fixture = new Fixture();
        string manifest = Leaf();
        if (failure == "live") manifest = manifest.Replace("#EXT-X-ENDLIST", "");
        if (failure == "page") manifest = "<html>not a video</html>";
        if (failure == "private") manifest = manifest.Replace("one.ts", "http://127.0.0.1/private.ts");
        if (failure == "key") manifest = manifest.Replace("#EXTINF:2,", "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"key\"\n#EXTINF:2,");
        if (failure == "variant") manifest = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nother.m3u8\n";
        fixture.Handler.Text("video.m3u8", manifest);
        fixture.Handler.Bytes("one.ts", new byte[failure == "size" ? 1_048_577 : 1000]);
        fixture.Handler.Bytes("two.ts", new byte[1000]);
        if (failure == "size") fixture.Config.SetSetting("OtherMediaMaximumDownloadBytes", "1048576");
        if (failure == "duration") fixture.Runner.Duration = "1";
        var source = Source() with { ValidatedHlsVariant = failure == "variant" ? "https://93.184.216.34/missing.m3u8" : "" };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.Equal(0, fixture.Probes);
    }

    [Fact]
    public async Task TransientSegmentHeaderFailureRetriesTheSameResourceBeforeNativeHandoff()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf());
        fixture.Handler.Bytes("one.ts", new byte[1000]); fixture.Handler.Bytes("two.ts", new byte[1000]);
        fixture.Handler.Failures["/two.ts"] = new Queue<HttpStatusCode?>([null, HttpStatusCode.ServiceUnavailable]);
        var source = Source();
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        Assert.Equal(3, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/two.ts"));
        Assert.Equal(1, fixture.Probes);
        Assert.True(File.Exists(result.FilePath));
        result.Lease.Dispose();
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task EmptyThenTruncatedSegmentBodiesRetryWithoutKeepingPartialBytesInTheCompletedAsset()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf());
        fixture.Handler.Bytes("one.ts", new byte[1000]); fixture.Handler.Bytes("two.ts", new byte[1000]);
        fixture.Handler.Bodies["/two.ts"] = new([([], 0), (new byte[5], 1000)]);
        var source = Source();
        var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
        using var lease = result.Lease;
        Assert.Equal(3, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/two.ts"));
        Assert.Equal(1, fixture.Probes);
        Assert.True(File.Exists(result.FilePath));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(result.FilePath)!), path => Path.GetFileName(path).StartsWith("asset-"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistentlyMissingRenditionCanUseOnlyAnAdvertisedAlternativeWithTheSameAudioGroups(bool sameGroups)
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"main\",NAME=\"Audio\",URI=\"audio.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2,AUDIO=\"main\"\nbad.m3u8\n" +
            $"#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"{(sameGroups ? "main" : "different")}\"\ngood.m3u8\n");
        fixture.Handler.Text("audio.m3u8", Leaf("audio-"));
        fixture.Handler.Text("bad.m3u8", Leaf("bad-")); fixture.Handler.Text("good.m3u8", Leaf("good-"));
        foreach (string name in new[] { "audio-one.ts", "audio-two.ts", "good-one.ts", "good-two.ts" }) fixture.Handler.Bytes(name, new byte[1000]);
        fixture.Handler.Bodies["/bad-one.ts"] = new([([], 0), ([], 0), ([], 0)]);
        var source = Source() with { ValidatedHlsVariant = "https://93.184.216.34/bad.m3u8" };
        if (sameGroups)
        {
            var result = await fixture.Service.DownloadTemporaryAsync(source, Context(source));
            using var lease = result.Lease;
            Assert.Equal(1, fixture.Probes);
            Assert.Contains("AUDIO=\"main\"", fixture.Runner.Manifest!);
            Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/good-one.ts");
        }
        else
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
            Assert.Equal(0, fixture.Probes);
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/good.m3u8");
        }
        Assert.Equal(3, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/bad-one.ts"));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task PersistentlyEmptySegmentBodiesStopAfterThreeAttemptsAndDoNotPublish()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf()); fixture.Handler.Bytes("one.ts", new byte[1000]);
        fixture.Handler.Bodies["/one.ts"] = new([([], 0), ([], 0), ([], 0)]);
        var source = Source();
        await Assert.ThrowsAsync<EndOfStreamException>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Equal(3, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/one.ts"));
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task BodyRetryBytesRemainInsideTheTotalTransferBudget()
    {
        using var fixture = new Fixture();
        fixture.Config.SetSetting("OtherMediaMaximumDownloadBytes", "1048576");
        fixture.Handler.Text("video.m3u8", Leaf()); fixture.Handler.Bytes("one.ts", new byte[1000]);
        fixture.Handler.Bodies["/one.ts"] = new([(new byte[600000], 750000), (new byte[750000], 750000)]);
        var source = Source();
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/one.ts"));
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CancellationDuringBodyRetryBackoffStopsBeforeASecondRequest()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf()); fixture.Handler.Bytes("one.ts", new byte[1000]);
        fixture.Handler.Bodies["/one.ts"] = new([([], 0)]);
        using var cancellation = new CancellationTokenSource();
        var source = Source();
        var transfer = fixture.Service.DownloadTemporaryAsync(source, Context(source), token: cancellation.Token);
        await fixture.Handler.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/one.ts"));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task FailedSegmentRetriesStayBoundedAndNeverReachHandoff(HttpStatusCode status, int attempts)
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf());
        fixture.Handler.Bytes("one.ts", new byte[1000]);
        fixture.Handler.Failures["/one.ts"] = new Queue<HttpStatusCode?>([status, status, status]);
        var source = Source();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Equal(attempts, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/one.ts"));
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CancellationDuringAssemblyClosesItsOperationAndRemovesEveryOwnedAsset()
    {
        using var fixture = new Fixture();
        fixture.Handler.Text("video.m3u8", Leaf());
        fixture.Handler.Bytes("one.ts", new byte[1000]); fixture.Handler.Bytes("two.ts", new byte[1000]);
        fixture.Runner.Stall = true;
        using var cancellation = new CancellationTokenSource();
        var transfer = fixture.Service.DownloadTemporaryAsync(Source(), Context(Source()), token: cancellation.Token);
        await fixture.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.True(fixture.Runner.Cancelled);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    private static string Leaf(string prefix = "") => $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n{prefix}one.ts\n#EXTINF:2,\n{prefix}two.ts\n#EXT-X-ENDLIST\n";
    private static AudiovisualSource Source() => new()
    { ProviderId = "fixture", Location = new("https://93.184.216.34/video.m3u8"), AccessMode = AudiovisualSourceAccessMode.DirectMedia };
    private static AudiovisualPlaybackContext Context(AudiovisualSource source) => new("tvmaze:169",
        new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series, Title = "Series" },
        new() { SeasonNumber = 2, EpisodeNumber = 1 }, "Series", "", source);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PlaylistTests", Guid.NewGuid().ToString("N"));
        public string Root => _root;
        public string Temporary => Path.Combine(_root, "temporary");
        public Handler Handler { get; } = new();
        public Runner Runner { get; } = new();
        public DomainHotSwapper Config { get; }
        public AuthorizedMediaDownloadService Service { get; }
        private readonly HttpClient _http;
        public int Probes { get; private set; }
        public Fixture(bool realMedia = false)
        {
            Directory.CreateDirectory(_root); _http = new(Handler);
            Config = new(Path.Combine(_root, "config.json"));
            Config.SetSetting("DownloadDirectory", Path.Combine(_root, "permanent"));
            Service = new(_http, Config, Temporary, (path, token) =>
            { Probes++; return realMedia ? AuthorizedMediaDownloadService.ProbeDownloadedMediaAsync(path, token) : Task.FromResult("Audio unverified"); },
                _http, realMedia ? new PreparationProcessRunner() : Runner);
        }
        public void Dispose()
        {
            _http.Dispose();
            string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PlaylistTests") + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }

    private sealed class Runner : IPreparationProcessRunner
    {
        public string? Manifest { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string Duration { get; set; } = "4";
        public bool Stall { get; set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (arguments.Contains("format=duration")) return new(0, $"{{\"format\":{{\"duration\":\"{Duration}\"}}}}");
            Arguments = arguments;
            string path = arguments[arguments.ToList().IndexOf("-i") + 1];
            Manifest = File.ReadAllText(path);
            Started.TrySetResult();
            try { if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            await File.WriteAllTextAsync(arguments[^1], "assembled video", token);
            return new(0, "");
        }
    }

    private sealed record Request(Uri Uri, string? Cookie, string? Authorization, string? Referer);
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = [];
        public List<Request> Requests { get; } = [];
        public Dictionary<string, Uri> Redirects { get; } = [];
        public Dictionary<string, Queue<HttpStatusCode?>> Failures { get; } = [];
        public Dictionary<string, Queue<(byte[] Bytes, long Expected)>> Bodies { get; } = [];
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Text(string name, string text) => Bytes(name, Encoding.UTF8.GetBytes(text));
        public void Bytes(string name, byte[] bytes) => _responses["/" + name] = bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.RequestUri!, request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null,
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null,
                request.Headers.Referrer?.AbsoluteUri));
            if (Failures.TryGetValue(request.RequestUri!.AbsolutePath, out var failures) && failures.TryDequeue(out var status))
            {
                if (status == null) throw new OperationCanceledException("Transient header deadline.");
                return Task.FromResult(new HttpResponseMessage(status.Value) { RequestMessage = request });
            }
            if (Redirects.TryGetValue(request.RequestUri!.AbsolutePath, out var redirect))
            { var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request }; response.Headers.Location = redirect; return Task.FromResult(response); }
            if (Bodies.TryGetValue(request.RequestUri!.AbsolutePath, out var bodies) && bodies.TryDequeue(out var body))
            {
                BodyStarted.TrySetResult();
                var content = new ByteArrayContent(body.Bytes); content.Headers.ContentLength = body.Expected;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(_responses[request.RequestUri!.AbsolutePath]) });
        }
    }
}
