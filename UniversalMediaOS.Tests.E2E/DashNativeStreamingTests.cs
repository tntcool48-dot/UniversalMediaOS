using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Buffers.Binary;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Streaming;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class DashNativeStreamingTests
{
    private const string Manifest = """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
        <BaseURL>media/</BaseURL><Period><AdaptationSet mimeType="video/mp4">
        <SegmentTemplate timescale="1" duration="2" startNumber="7" initialization="init-$RepresentationID$.mp4" media="$RepresentationID$/$Number%05d$-$Time$.m4s"/>
        <Representation id="low" bandwidth="1"/><Representation id="v" bandwidth="2" codecs="avc1.42c01e"/></AdaptationSet>
        <AdaptationSet mimeType="audio/mp4" lang="en"><Representation id="a" bandwidth="1">
        <BaseURL>https://other.example/audio/</BaseURL><SegmentTemplate timescale="1" duration="2" initialization="init.mp4" media="$Time$.m4s"/>
        </Representation></AdaptationSet></Period></MPD>
        """;

    [Fact]
    public async Task NativeDashRebuildsOnlyFiniteSelectedResourcesThroughTheirSession()
    {
        using var proxy = new HlsLoopbackProxy(); proxy.Start();
        string id = proxy.RegisterSession(new("https://cdn.example/root/video.mpd", "Agent", "session=private", null, null, DateTime.UtcNow));
        string rewritten = await proxy.RewriteDashManifestForSessionAsync(Encoding.UTF8.GetBytes(Manifest),
            new Uri("https://cdn.example/root/video.mpd"), id);
        var root = XElement.Parse(rewritten); XNamespace ns = root.Name.Namespace;
        Assert.DoesNotContain("SegmentTemplate", rewritten);
        Assert.DoesNotContain("BaseURL", rewritten);
        Assert.DoesNotContain("session=private", rewritten);
        Assert.All(root.Descendants(ns + "SegmentList"), list => Assert.Equal("2", (string?)list.Attribute("duration")));
        Assert.Single(root.Descendants(ns + "AdaptationSet"), set => (string?)set.Attribute("contentType") == "video");
        Assert.Contains(root.Descendants(ns + "AdaptationSet"), set => (string?)set.Attribute("lang") == "en");
        var references = root.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is "sourceURL" or "media").Select(attribute => attribute.Value).ToArray();
        Assert.Equal(6, references.Length);
        Assert.All(references, url => Assert.True(url.StartsWith(proxy.ListeningUri!.AbsoluteUri + "dashseg?id=" + id) ||
            url.StartsWith(proxy.ListeningUri!.AbsoluteUri + "dashinit?id=" + id)));
        Assert.Contains(references, url => Uri.UnescapeDataString(url).Contains("/root/media/v/00007-0.m4s"));
        Assert.Contains(references, url => Uri.UnescapeDataString(url).Contains("https://other.example/audio/2.m4s"));
        Assert.DoesNotContain(references, url => Uri.UnescapeDataString(url).Contains("/low/"));
        string stream = proxy.CreateDashUrl(id);
        Assert.True(proxy.OwnsStreamUrl(stream));
        proxy.UnregisterSession(id);
        Assert.False(proxy.OwnsStreamUrl(stream));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("live")]
    [InlineData("protected")]
    [InlineData("private")]
    public async Task UnqualifiedNativeDashFormatsFailBeforeRelayingTheirManifest(string failure)
    {
        string input = Manifest;
        if (failure == "range") input = input.Replace("<Period>", "<Period><SegmentList duration=\"2\"><Initialization sourceURL=\"whole.mp4\" range=\"0-99\"/><SegmentURL media=\"whole.mp4\" mediaRange=\"100-199\"/><SegmentURL media=\"whole.mp4\" mediaRange=\"200-999\"/></SegmentList>")
            .Replace("<SegmentTemplate", "<Unused").Replace("</SegmentTemplate>", "</Unused>");
        if (failure == "live") input = input.Replace("type=\"static\"", "type=\"dynamic\"");
        if (failure == "protected") input = input.Replace("<Period>", "<Period><ContentProtection/>");
        if (failure == "private") input = input.Replace("media/", "http://127.0.0.1/private/");
        using var proxy = new HlsLoopbackProxy(); proxy.Start();
        await Assert.ThrowsAsync<InvalidDataException>(() => proxy.RewriteDashManifestForSessionAsync(Encoding.UTF8.GetBytes(input), new Uri("https://cdn.example/video.mpd"), "test"));
    }

    [Theory]
    [InlineData("ftyp")]
    [InlineData("styp")]
    [InlineData("moof")]
    [InlineData("moov")]
    public void NativeDashRequiresMp4BoxBytesRatherThanAContentTypeOrPayloadSize(string box)
    {
        byte[] payload = new byte[500]; payload[3] = 24; Encoding.ASCII.GetBytes(box).CopyTo(payload, 4);
        Assert.True(PlaylistMediaDownload.IsDashMp4Prefix(payload));
        Assert.False(PlaylistMediaDownload.IsDashMp4Prefix("<html>Blocked</html>"u8));
        Assert.False(PlaylistMediaDownload.IsDashMp4Prefix(payload.AsSpan(0, 7)));
        payload[3] = 1;
        Assert.False(PlaylistMediaDownload.IsDashMp4Prefix(payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebuiltFiniteDashActuallyDecodesWithTheAppsNativeEngine(bool unknownMovieDuration)
    {
        string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeDashDecodeTests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            var runner = new PreparationProcessRunner();
            string original = Path.Combine(directory, "video.mpd");
            var encoded = await runner.RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440", "-t", "4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac",
                "-f", "dash", "-use_timeline", "0", "-seg_duration", "2", "-adaptation_sets", "id=0,streams=v id=1,streams=a", original.Replace('\\', '/')], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            if (unknownMovieDuration)
            {
                foreach (string initialization in Directory.GetFiles(directory, "init-*.m4s"))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(initialization, deadline.Token);
                    int mvhd = FindBoxType(bytes, "mvhd"), moov = FindBoxType(bytes, "moov");
                    Assert.Equal(0, bytes[mvhd + 4]);
                    // Upgrade our generated movie header to version 1 and its
                    // standard unknown-duration sentinel, as seen in live QA.
                    byte[] changed = new byte[bytes.Length + 12];
                    bytes.AsSpan(0, mvhd + 8).CopyTo(changed);
                    changed[mvhd + 4] = 1;
                    bytes.AsSpan(mvhd + 8, 4).CopyTo(changed.AsSpan(mvhd + 12));
                    bytes.AsSpan(mvhd + 12, 4).CopyTo(changed.AsSpan(mvhd + 20));
                    bytes.AsSpan(mvhd + 16, 4).CopyTo(changed.AsSpan(mvhd + 24));
                    changed.AsSpan(mvhd + 28, 8).Fill(255);
                    bytes.AsSpan(mvhd + 24).CopyTo(changed.AsSpan(mvhd + 36));
                    BinaryPrimitives.WriteUInt32BigEndian(changed.AsSpan(mvhd - 4, 4), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mvhd - 4, 4)) + 12);
                    BinaryPrimitives.WriteUInt32BigEndian(changed.AsSpan(moov - 4, 4), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(moov - 4, 4)) + 12);
                    HlsLoopbackProxy.NormalizeFiniteDashInitialization(changed);
                    Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(changed.AsSpan(mvhd + 28, 8)));
                    await File.WriteAllBytesAsync(initialization, changed, deadline.Token);
                }
            }
            var rewritten = await PlaylistMediaDownload.RewriteDashAsync(await File.ReadAllBytesAsync(original, deadline.Token), new Uri("https://cdn.example/video.mpd"),
                uri => Task.FromResult(new Uri(Path.Combine(directory, Path.GetFileName(uri.AbsolutePath))).AbsoluteUri), deadline.Token);
            string playable = Path.Combine(directory, "native.mpd"); await File.WriteAllTextAsync(playable, rewritten.Manifest, deadline.Token);
            LibVLCSharp.Shared.Core.Initialize();
            using var engine = new LibVLC(enableDebugLogs: true, "--vout=dummy", "--aout=dummy");
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            engine.Log += (_, log) => { if (logs.Count < 100) logs.Enqueue(log.Message); };
            using var player = new MediaPlayer(engine);
            using var media = new Media(engine, playable, FromType.FromPath);
            var decoded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.TimeChanged += (_, args) => { if (args.Time >= 500 && player.VoutCount > 0) decoded.TrySetResult(args.Time); };
            Assert.True(player.Play(media));
            try { await decoded.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token); }
            catch (TimeoutException) { throw new InvalidOperationException("Native DASH did not decode: " + string.Join(" | ", logs)); }
            Assert.InRange(player.Length, 3900, 4100);
            player.Stop();
        }
        finally
        {
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    private static int FindBoxType(byte[] bytes, string type)
    {
        for (int i = 4; i <= bytes.Length - 4; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual(Encoding.ASCII.GetBytes(type))) return i;
        throw new InvalidDataException("The generated fixture has no " + type + " box.");
    }

    [Fact]
    public void FiniteDashHeaderRepairChangesOnlyTheUnknownMovieDuration()
    {
        byte[] bytes = new byte[56];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 8); Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 48); Encoding.ASCII.GetBytes("moov").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 40); Encoding.ASCII.GetBytes("mvhd").CopyTo(bytes, 20);
        bytes[24] = 1; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44, 4), 90000); bytes.AsSpan(48, 8).Fill(255);
        byte[] expected = bytes.ToArray(); expected.AsSpan(48, 8).Clear();
        HlsLoopbackProxy.NormalizeFiniteDashInitialization(bytes);
        Assert.Equal(expected, bytes);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(48, 8), 12000);
        byte[] finite = bytes.ToArray(); HlsLoopbackProxy.NormalizeFiniteDashInitialization(bytes);
        Assert.Equal(finite, bytes);
        bytes[55] = 255; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44, 4), 0);
        Assert.Throws<InvalidDataException>(() => HlsLoopbackProxy.NormalizeFiniteDashInitialization(bytes));
        Assert.Throws<InvalidDataException>(() => HlsLoopbackProxy.NormalizeFiniteDashInitialization(expected[..^1]));
        Assert.Throws<InvalidDataException>(() => HlsLoopbackProxy.NormalizeFiniteDashInitialization("<html>Not media</html>"u8.ToArray()));
    }

    [Fact]
    public void SourceReplacementAndPlayerCloseReleaseOnlyTheirOwnNativeSessions()
    {
        string? previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.DashSessionTests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Roaming", "UniversalMediaOS"));
        File.WriteAllText(Path.Combine(directory, "Roaming", "UniversalMediaOS", "config.json"),
            JsonSerializer.Serialize(new { DatabasePath = Path.Combine(directory, "media.db") }));
        Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", directory);
        try
        {
            using var proxy = new HlsLoopbackProxy();
            using var db = new DatabaseContext();
            using var first = new PlaybackViewModel(db, null, null, hlsProxy: proxy);
            using var second = new PlaybackViewModel(db, null, null, hlsProxy: proxy);
            string Source(PlaybackViewModel player) => ((Media)typeof(PlaybackViewModel)
                .GetField("_currentMedia", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)!).Mrl;
            first.LoadMedia("https://cdn.example/one.mpd", "One", contentType: "application/dash+xml");
            string original = Source(first);
            second.LoadMedia("https://cdn.example/two.mpd", "Two", contentType: "application/dash+xml");
            string other = Source(second);
            Assert.True(proxy.OwnsStreamUrl(original));
            Assert.True(proxy.OwnsStreamUrl(other));
            first.LoadMedia(original, "Same source", contentType: "application/dash+xml");
            Assert.True(proxy.OwnsStreamUrl(original));
            first.LoadMedia("https://cdn.example/replacement.mpd", "Replacement", contentType: "application/dash+xml");
            string replacement = Source(first);
            Assert.False(proxy.OwnsStreamUrl(original));
            Assert.True(proxy.OwnsStreamUrl(replacement));
            first.Dispose();
            Assert.False(proxy.OwnsStreamUrl(replacement));
            Assert.True(proxy.OwnsStreamUrl(other));
            second.Dispose();
            Assert.False(proxy.OwnsStreamUrl(other));
        }
        finally
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "media.db")};Cache=Shared;"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", previous);
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NativeDashDetectionIncludesExtensionlessCapturedTypesAndLeavesLocalVideoAlone()
    {
        Assert.True(PlaybackViewModel.LooksLikeDashSource("https://cdn.example/video.MPD?token=private", ""));
        Assert.True(PlaybackViewModel.LooksLikeDashSource("https://cdn.example/manifest", "application/dash+xml"));
        Assert.False(PlaybackViewModel.LooksLikeDashSource("C:\\library\\video.mkv", ""));
        Assert.False(PlaybackViewModel.LooksLikeDashSource("https://cdn.example/video.m3u8", "application/x-mpegURL"));
    }
}
