using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ProxyRequestLifetimeTests
{
    private const string Root = "https://1.1.1.1/media/index.m3u8";
    private static ProxySession Session(string url = Root) => new(url, "PlayerAgent", "session=private", null,
        "https://provider.example/watch?secret=private", DateTime.UtcNow,
        new Dictionary<string, string> { ["Authorization"] = "Bearer private" });

    private static ProxyRequestLimits ShortLimits() => new()
    {
        HeaderDeadline = TimeSpan.FromMilliseconds(180), ManifestDeadline = TimeSpan.FromMilliseconds(180),
        KeyDeadline = TimeSpan.FromMilliseconds(180), SubtitleDeadline = TimeSpan.FromMilliseconds(180),
        MediaIdleDeadline = TimeSpan.FromMilliseconds(180), MediaDeadline = TimeSpan.FromSeconds(3),
        ShutdownDeadline = TimeSpan.FromSeconds(1)
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Fixture : IDisposable
    {
        internal HlsLoopbackProxy Proxy { get; }
        internal HttpClient Local { get; } = new() { Timeout = TimeSpan.FromSeconds(6) };
        private readonly HttpClient _remote;
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, ProxyRequestLimits? limits = null)
        {
            _remote = new(new Handler(send)) { Timeout = Timeout.InfiniteTimeSpan };
            Proxy = new(_remote, limits ?? ShortLimits()); Proxy.Start();
            Assert.True(Proxy.IsRunning, Proxy.LastStartupError);
        }
        internal string Url(string route, string id, string? remote = null) =>
            Proxy.ListeningUri!.AbsoluteUri + route + "?id=" + id + (remote == null ? "" : "&url=" + Uri.EscapeDataString(remote));
        public void Dispose() { Proxy.Dispose(); Local.Dispose(); _remote.Dispose(); }
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, string type = "video/MP2T") =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(type) } }
        };

    private static byte[] TsBytes(int length = 1504)
    {
        byte[] bytes = new byte[length];
        for (int offset = 0; offset + 4 <= bytes.Length; offset += 188)
            new byte[] { 0x47, 0x01, 0x00, 0x10 }.CopyTo(bytes, offset);
        return bytes;
    }

    private static byte[] Mp4Prefix() { byte[] bytes = new byte[32]; bytes[3] = 32; "ftyp"u8.CopyTo(bytes.AsSpan(4)); return bytes; }

    private class StallStream(byte[]? prefix = null, bool honorCancellation = true) : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        private int _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (prefix != null && _sent < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _sent);
                prefix.AsMemory(_sent, count).CopyTo(buffer); _sent += count; return count;
            }
            Entered.TrySetResult();
            try { await Finish.Task.WaitAsync(honorCancellation ? token : CancellationToken.None); return 0; }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage StalledResponse(HttpRequestMessage request, StallStream stream, string type = "video/MP2T") =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(stream) { Headers = { ContentType = new(type) } } };

    private static async Task ConsumeAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        _ = await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task SettledAsync(Task request)
    {
        // A cancelled relay may terminate with a connection error or an empty
        // response. Neither is playable media; it must finish within this bound.
        try { await request.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { }
    }

    private static async Task IdleAsync(HlsLoopbackProxy proxy, int expected = 0)
    {
        var watch = Stopwatch.StartNew();
        while (proxy.ActiveRequestCount != expected && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(15);
        Assert.Equal(expected, proxy.ActiveRequestCount);
    }

    [Fact]
    public async Task HealthyManifestAesKeyAndSeekRangeRetainTheirExactBytesAndHeaders()
    {
        byte[] media = TsBytes(); byte[] key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        using var fixture = new Fixture((request, _) =>
        {
            Assert.Equal("PlayerAgent", request.Headers.UserAgent.ToString());
            Assert.Equal("session=private", request.Headers.GetValues("Cookie").Single());
            Assert.Equal("Bearer private", request.Headers.Authorization!.ToString());
            if (request.RequestUri!.AbsolutePath.EndsWith(".m3u8"))
                return Task.FromResult(Response(request, "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nvideo.ts\n#EXT-X-ENDLIST\n"u8.ToArray(), "application/x-mpegURL"));
            if (request.RequestUri.AbsolutePath.EndsWith(".key")) return Task.FromResult(Response(request, key, "application/octet-stream"));
            Assert.Equal("bytes=188-563", request.Headers.Range!.ToString());
            var response = Response(request, media[188..564]); response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(188, 563, media.Length);
            response.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(response);
        }, new ProxyRequestLimits());
        string id = fixture.Proxy.RegisterSession(Session());
        string manifest = await fixture.Local.GetStringAsync(fixture.Proxy.CreateStreamUrl(id));
        Assert.Contains(fixture.Proxy.ListeningUri!.AbsoluteUri + "seg?id=" + id, manifest);
        Assert.Equal(key, await fixture.Local.GetByteArrayAsync(fixture.Url("key", id, "https://1.1.1.1/media/encryption.key")));
        using var range = new HttpRequestMessage(HttpMethod.Get, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/media/video.ts"));
        range.Headers.Range = new RangeHeaderValue(188, 563);
        using var returned = await fixture.Local.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, returned.StatusCode);
        Assert.Equal("bytes 188-563/1504", returned.Content.Headers.ContentRange!.ToString());
        Assert.Equal("bytes", returned.Headers.AcceptRanges.Single());
        Assert.Equal(media[188..564], await returned.Content.ReadAsByteArrayAsync());
        await IdleAsync(fixture.Proxy);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("text/html")]
    public async Task MislabelledTsIsRelayedCompletelyWhileDecoysAreRejected(string type)
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(Response(request,
            request.RequestUri!.AbsolutePath.EndsWith("decoy") ? "<html>Blocked</html>"u8.ToArray() : TsBytes(), type)));
        string id = fixture.Proxy.RegisterSession(Session());
        Assert.Equal(TsBytes(), await fixture.Local.GetByteArrayAsync(fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video")));
        using var decoy = await fixture.Local.GetAsync(fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/decoy"));
        Assert.Equal(HttpStatusCode.BadGateway, decoy.StatusCode);
    }

    [Fact]
    public async Task RedirectsKeepRangeAndPublicContextButDoNotReplaySecretsAcrossOrigins()
    {
        int calls = 0;
        using var fixture = new Fixture((request, _) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://8.8.8.8/video") } });
            Assert.False(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("https://provider.example/", request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("https://provider.example", request.Headers.GetValues("Origin").Single());
            Assert.Equal("bytes=0-1503", request.Headers.Range!.ToString());
            return Task.FromResult(Response(request, TsBytes()));
        });
        string id = fixture.Proxy.RegisterSession(Session());
        using var range = new HttpRequestMessage(HttpMethod.Get, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video"));
        range.Headers.Range = new RangeHeaderValue(0, 1503);
        using var response = await fixture.Local.SendAsync(range);
        Assert.Equal(TsBytes(), await response.Content.ReadAsByteArrayAsync()); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RedirectToPrivateAddressAndMultipleRangesNeverReachTheRemoteHandler()
    {
        int calls = 0;
        using var fixture = new Fixture((_, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("http://127.0.0.1/private") } });
        });
        string id = fixture.Proxy.RegisterSession(Session());
        await SettledAsync(ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video")));
        Assert.Equal(1, calls);
        using var ranges = new HttpRequestMessage(HttpMethod.Get, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video"));
        ranges.Headers.TryAddWithoutValidation("Range", "bytes=0-2,6-8");
        using var response = await fixture.Local.SendAsync(ranges);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("key")]
    [InlineData("subtitle/Captions.vtt")]
    [InlineData("stream", true)]
    [InlineData("dashinit")]
    public async Task SmallResourceBodiesCannotWaitIndefinitelyAfterHeaders(string route, bool dash = false)
    {
        var stream = new StallStream();
        using var fixture = new Fixture((request, _) => Task.FromResult(StalledResponse(request, stream)));
        string id = fixture.Proxy.RegisterSession(Session());
        string url = fixture.Url(route, id, Root) + (dash ? "&dash=1" : "");
        Task request = ConsumeAsync(fixture.Local, url);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SettledAsync(request); await IdleAsync(fixture.Proxy); Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData("seg")]
    [InlineData("media")]
    [InlineData("dashseg")]
    public async Task StalledMediaReadsCancelAndDisposeTheirRemoteBody(string route)
    {
        var stream = new StallStream(route == "dashseg" ? Mp4Prefix() : TsBytes());
        using var fixture = new Fixture((request, _) => Task.FromResult(StalledResponse(request, stream)));
        string id = fixture.Proxy.RegisterSession(Session());
        Task request = ConsumeAsync(fixture.Local, fixture.Url(route, id, "https://1.1.1.1/media/video"));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SettledAsync(request); await IdleAsync(fixture.Proxy); Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task HeaderDeadlineCancelsRequestsThatNeverReturnHeaders()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        });
        string id = fixture.Proxy.RegisterSession(Session());
        Task request = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SettledAsync(request); await IdleAsync(fixture.Proxy);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("declared-large")]
    [InlineData("truncated")]
    [InlineData("chunked-large")]
    [InlineData("bad-key")]
    public async Task UnusableBodiesDoNotBecomeSuccessfulMediaOrKeys(string failure)
    {
        using var fixture = new Fixture((request, _) =>
        {
            if (failure == "chunked-large")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(TsBytes())) });
            var response = Response(request, failure == "empty" ? [] : failure == "bad-key" ? new byte[15] : TsBytes(200));
            if (failure == "declared-large") response.Content.Headers.ContentLength = 1001;
            if (failure == "truncated") response.Content.Headers.ContentLength = 201;
            return Task.FromResult(response);
        }, ShortLimits() with { MaximumSegmentBytes = 1000 });
        string id = fixture.Proxy.RegisterSession(Session());
        string url = fixture.Url(failure == "bad-key" ? "key" : "seg", id, "https://1.1.1.1/video");
        if (failure is "truncated" or "chunked-large")
        {
            Exception? error = await Record.ExceptionAsync(() => fixture.Local.GetByteArrayAsync(url));
            Assert.NotNull(error);
        }
        else
        {
            using var response = await fixture.Local.GetAsync(url); Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        await IdleAsync(fixture.Proxy);
    }

    [Fact]
    public async Task FullMediaFilesUseTheirOwnBoundsRatherThanAPlaylistSegmentLimit()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(Response(request, TsBytes(2048))),
            ShortLimits() with { MaximumSegmentBytes = 512, MaximumDirectMediaBytes = 4096 });
        string id = fixture.Proxy.RegisterSession(Session());
        Assert.Equal(TsBytes(2048), await fixture.Local.GetByteArrayAsync(fixture.Proxy.CreateMediaUrl(id, "https://1.1.1.1/film.mp4")));
        using var segment = await fixture.Local.GetAsync(fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/chunk.ts"));
        Assert.Equal(HttpStatusCode.BadGateway, segment.StatusCode);
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("line-length")]
    [InlineData("resources")]
    [InlineData("rewrite-amplification")]
    public async Task ManifestLineResourceAndRewriteGrowthStayBounded(string failure)
    {
        string text = "#EXTM3U\n";
        string root = Root;
        if (failure == "lines") text += new string('\n', 100_001);
        if (failure == "line-length") text += "#" + new string('a', 65_537);
        if (failure == "resources") text += string.Concat(Enumerable.Repeat("s.ts\n", 20_001));
        if (failure == "rewrite-amplification")
        {
            root = "https://1.1.1.1/" + new string('x', 4500) + "/index.m3u8";
            text += "#EXT-X-KEY:" + string.Join(',', Enumerable.Repeat("URI=\"s\"", 4000));
        }
        using var fixture = new Fixture((request, _) => Task.FromResult(Response(request, Encoding.UTF8.GetBytes(text), "application/x-mpegURL")), new ProxyRequestLimits());
        string id = fixture.Proxy.RegisterSession(Session(root));
        using var rejected = await fixture.Local.GetAsync(fixture.Proxy.CreateStreamUrl(id));
        Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode); await IdleAsync(fixture.Proxy);
    }

    [Fact]
    public async Task SessionLimitPreservesExistingOwnersAndRestartDoesNotReviveClosedSessions()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(Response(request, TsBytes())), new ProxyRequestLimits { MaximumSessions = 2 });
        string a = fixture.Proxy.RegisterSession(Session()), b = fixture.Proxy.RegisterSession(Session());
        Assert.Throws<InvalidOperationException>(() => fixture.Proxy.RegisterSession(Session()));
        Assert.True(fixture.Proxy.OwnsStreamUrl(fixture.Proxy.CreateStreamUrl(a)));
        Assert.True(fixture.Proxy.OwnsStreamUrl(fixture.Proxy.CreateStreamUrl(b)));
        Assert.Equal(2, fixture.Proxy.SessionCount);
        await fixture.Proxy.StopAsync(); Assert.Equal(0, fixture.Proxy.SessionCount); Assert.False(fixture.Proxy.IsRunning);
        fixture.Proxy.Start(); Assert.True(fixture.Proxy.IsRunning);
        using var old = await fixture.Local.GetAsync(fixture.Proxy.CreateStreamUrl(a)); Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        fixture.Proxy.Dispose(); Assert.Throws<ObjectDisposedException>(() => fixture.Proxy.RegisterSession(Session()));
    }

    [Fact]
    public async Task ActiveTransfersDoNotExpireAndClosingOnePlayerLeavesTheOtherRequestAlive()
    {
        var a = new StallStream(TsBytes()); var b = new StallStream(TsBytes());
        using var fixture = new Fixture((request, _) => Task.FromResult(StalledResponse(request,
            request.RequestUri!.AbsolutePath.EndsWith("a") ? a : b)), new ProxyRequestLimits { SessionIdleLifetime = TimeSpan.FromMilliseconds(80) });
        string ownerA = fixture.Proxy.RegisterSession(Session()), ownerB = fixture.Proxy.RegisterSession(Session());
        Task requestA = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(ownerA, "https://1.1.1.1/a"));
        Task requestB = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(ownerB, "https://1.1.1.1/b"));
        await Task.WhenAll(a.Entered.Task, b.Entered.Task).WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(160);
        Assert.True(fixture.Proxy.OwnsStreamUrl(fixture.Proxy.CreateStreamUrl(ownerA)));
        Assert.True(fixture.Proxy.OwnsStreamUrl(fixture.Proxy.CreateStreamUrl(ownerB)));
        Assert.True(fixture.Proxy.UnregisterSession(ownerA)); await a.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SettledAsync(requestA); Assert.False(b.Cancelled.Task.IsCompleted);
        b.Finish.TrySetResult(); await requestB; await IdleAsync(fixture.Proxy); Assert.Equal(1, fixture.Proxy.SessionCount);
    }

    [Fact]
    public async Task PerPlayerAndGlobalRequestLimitsRejectExcessWorkAndFreeCapacityOnClose()
    {
        var streams = new ConcurrentDictionary<string, StallStream>();
        using var fixture = new Fixture((request, _) => Task.FromResult(StalledResponse(request,
            streams.GetOrAdd(request.RequestUri!.AbsolutePath, _ => new StallStream(TsBytes())))),
            new ProxyRequestLimits { MaximumRequests = 2, MaximumSessionRequests = 1 });
        string a = fixture.Proxy.RegisterSession(Session()), b = fixture.Proxy.RegisterSession(Session()), c = fixture.Proxy.RegisterSession(Session());
        Task first = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(a, "https://1.1.1.1/a"));
        await WaitForStreamAsync("/a");
        using var perPlayer = await fixture.Local.GetAsync(fixture.Proxy.CreateSegmentUrl(a, "https://1.1.1.1/extra"));
        Assert.Equal((HttpStatusCode)429, perPlayer.StatusCode); Assert.False(streams.ContainsKey("/extra"));
        Task second = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(b, "https://1.1.1.1/b")); await WaitForStreamAsync("/b");
        using var global = await fixture.Local.GetAsync(fixture.Proxy.CreateSegmentUrl(c, "https://1.1.1.1/c"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, global.StatusCode); Assert.False(streams.ContainsKey("/c"));
        fixture.Proxy.UnregisterSession(a); await SettledAsync(first); await IdleAsync(fixture.Proxy, 1);
        Task third = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(c, "https://1.1.1.1/c")); await WaitForStreamAsync("/c");
        Assert.Equal(2, fixture.Proxy.ActiveRequestCount);
        await fixture.Proxy.StopAsync(); await SettledAsync(second); await SettledAsync(third);
        Assert.Equal(0, fixture.Proxy.ActiveRequestCount); Assert.Equal(0, fixture.Proxy.SessionCount);

        async Task WaitForStreamAsync(string path)
        {
            var clock = Stopwatch.StartNew();
            while (!streams.ContainsKey(path) && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            await streams[path].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ShutdownReturnsWithinItsBoundEvenIfATransportIgnoresCancellation()
    {
        var stream = new StallStream(TsBytes(), honorCancellation: false);
        using var fixture = new Fixture((request, _) => Task.FromResult(StalledResponse(request, stream)),
            new ProxyRequestLimits { ShutdownDeadline = TimeSpan.FromMilliseconds(120) });
        string id = fixture.Proxy.RegisterSession(Session());
        Task request = ConsumeAsync(fixture.Local, fixture.Proxy.CreateSegmentUrl(id, "https://1.1.1.1/video"));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var watch = Stopwatch.StartNew(); await fixture.Proxy.StopAsync();
            Assert.InRange(watch.ElapsedMilliseconds, 80, 1500); Assert.False(fixture.Proxy.IsRunning); Assert.Equal(0, fixture.Proxy.SessionCount);
        }
        finally { stream.Finish.TrySetResult(); }
        await SettledAsync(request); await IdleAsync(fixture.Proxy); Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task MediaInactivityResetsOnProgressButTheOverallDeadlineStillApplies()
    {
        var stream = new ProgressStream(); using var output = new MemoryStream();
        using var overall = new CancellationTokenSource(TimeSpan.FromMilliseconds(240));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HlsLoopbackProxy.CopyMediaBodyAsync(stream, output,
            ReadOnlyMemory<byte>.Empty, 100_000, null, TimeSpan.FromMilliseconds(100), overall.Token));
        Assert.True(output.Length >= 64 * 3, "The stream should make healthy progress before its overall deadline.");
    }

    private sealed class ProgressStream : StallStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(30, token); buffer.Span[..64].Fill(7); return 64; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoNativePlayersDecodeThroughTheBoundedProxyAndSurviveAnotherOwnerClosing(bool dash)
    {
        string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ProxyLifetimeTests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            string filename = dash ? "video.mpd" : "video.m3u8";
            string manifest = Path.Combine(directory, filename);
            var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440", "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac" };
            args.AddRange(dash ? ["-f", "dash", "-use_timeline", "0", "-seg_duration", "2", "-adaptation_sets", "id=0,streams=v id=1,streams=a"]
                : ["-f", "hls", "-hls_time", "2", "-hls_list_size", "0"]);
            args.Add(manifest.Replace('\\', '/'));
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", args, deadline.Token); Assert.Equal(0, encoded.ExitCode);
            using var fixture = new Fixture(async (request, token) =>
            {
                string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                string type = name.EndsWith(".mpd") ? "application/dash+xml" : name.EndsWith(".m3u8") ? "application/x-mpegURL" : name.EndsWith(".m4s") ? "video/mp4" : "video/MP2T";
                return Response(request, await File.ReadAllBytesAsync(Path.Combine(directory, name), token), type);
            }, new ProxyRequestLimits());
            string a = fixture.Proxy.RegisterSession(Session("https://1.1.1.1/" + filename)), b = fixture.Proxy.RegisterSession(Session("https://1.1.1.1/" + filename));
            LibVLCSharp.Shared.Core.Initialize();
            using var engine = new LibVLC("--vout=dummy", "--aout=dummy");
            using var playerA = new MediaPlayer(engine); using var playerB = new MediaPlayer(engine);
            using var mediaA = new Media(engine, dash ? fixture.Proxy.CreateDashUrl(a) : fixture.Proxy.CreateStreamUrl(a), FromType.FromLocation);
            using var mediaB = new Media(engine, dash ? fixture.Proxy.CreateDashUrl(b) : fixture.Proxy.CreateStreamUrl(b), FromType.FromLocation);
            var framesA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var framesB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playerA.TimeChanged += (_, time) => { if (time.Time >= 500 && playerA.VoutCount > 0) framesA.TrySetResult(); };
            playerB.TimeChanged += (_, time) => { if (time.Time >= 500 && playerB.VoutCount > 0) framesB.TrySetResult(); };
            Assert.True(playerA.Play(mediaA)); Assert.True(playerB.Play(mediaB));
            await Task.WhenAll(framesA.Task, framesB.Task).WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            playerB.SetPause(true); playerA.Stop(); fixture.Proxy.UnregisterSession(a);
            Assert.True(fixture.Proxy.OwnsStreamUrl(dash ? fixture.Proxy.CreateDashUrl(b) : fixture.Proxy.CreateStreamUrl(b)));
            playerB.Time = 2500;
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playerB.TimeChanged += (_, time) => { if (time.Time > 2700 && playerB.VoutCount > 0) resumed.TrySetResult(); };
            playerB.SetPause(false); await resumed.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
            Assert.InRange(playerB.Length, 5500, 6500); playerB.Stop(); fixture.Proxy.UnregisterSession(b);
            await fixture.Proxy.StopAsync(); Assert.Equal(0, fixture.Proxy.SessionCount); Assert.Equal(0, fixture.Proxy.ActiveRequestCount);
        }
        finally
        {
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }
}
