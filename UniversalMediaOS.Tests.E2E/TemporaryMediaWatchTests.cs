using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TemporaryMediaWatchTests
{
    [Fact]
    public async Task CapacityRejectionBeforeTheBodyKeepsPermanentFilesAndDoesNotProbeOrPublish()
    {
        using var fixture = new Fixture(capacity: _ => 0);
        Directory.CreateDirectory(fixture.Library);
        string permanent = Path.Combine(fixture.Library, "retained.mkv");
        File.WriteAllText(permanent, "retained");
        await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            fixture.Service.DownloadTemporaryAsync(Source(), Context(Source())));
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.Equal("retained", File.ReadAllText(permanent));
    }

    [Fact]
    public async Task CapacityLossDuringAnUnknownLengthBodyRemovesOnlyItsPartial()
    {
        int checks = 0;
        using var fixture = new Fixture(capacity: _ => ++checks == 1 ? long.MaxValue : 0);
        fixture.Handler.Body = new RepeatedBody(65L * 1024 * 1024);
        await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            fixture.Service.DownloadTemporaryAsync(Source(), Context(Source())));
        Assert.Equal(2, checks);
        Assert.Equal(0, fixture.Probes);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CompletedFileIsSharedUntilLastLeaseAndLibraryCopySurvives()
    {
        using var fixture = new Fixture();
        var source = Source() with { Referer = "https://provider.example/watch", Cookie = "session=test",
            RequestHeaders = new Dictionary<string, string> { ["X-Source"] = "test" } };
        var context = Context(source);
        var first = await fixture.Service.DownloadTemporaryAsync(source, context);
        var second = await fixture.Service.DownloadTemporaryAsync(source, context);
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Equal(1, fixture.Probes);
        Assert.Equal("session=test", fixture.Handler.Cookie);
        Assert.Equal(source.Referer, fixture.Handler.Referer);
        Assert.Equal("test", fixture.Handler.SourceHeader);
        Assert.Equal("Audio language unverified", second.AudioNotice);
        string saved = await fixture.Service.DownloadAsync(source, AudiovisualMediaKind.Movie, "Permanent copy");
        Assert.StartsWith(fixture.Library, saved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(fixture.Temporary, first.FilePath, StringComparison.OrdinalIgnoreCase);
        first.Lease.Dispose();
        first.Lease.Dispose();
        Assert.True(File.Exists(second.FilePath));
        second.Lease.Dispose();
        Assert.False(File.Exists(second.FilePath));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.True(File.Exists(saved));
    }

    [Fact]
    public async Task DifferentRemakesEpisodesAndSourceVersionsNeverShareAFile()
    {
        using var fixture = new Fixture();
        var source = Source();
        var original = Context(source);
        var remake = new AudiovisualPlaybackContext("film:remake", original.Identity with { Year = 2024 },
            original.Unit, original.Title, "", source);
        var series = original.Identity with { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series };
        var one = new AudiovisualPlaybackContext("show:test", series, new() { SeasonNumber = 1, EpisodeNumber = 1 }, "Series", "", source);
        var two = new AudiovisualPlaybackContext("show:test", series, new() { SeasonNumber = 2, EpisodeNumber = 1 }, "Series", "", source);
        var results = new[]
        {
            await fixture.Service.DownloadTemporaryAsync(source, original),
            await fixture.Service.DownloadTemporaryAsync(source, remake),
            await fixture.Service.DownloadTemporaryAsync(source, one),
            await fixture.Service.DownloadTemporaryAsync(source, two),
            await fixture.Service.DownloadTemporaryAsync(source with { Location = new("https://93.184.216.34/other-cut.mp4") }, original)
        };
        Assert.Equal(5, results.Select(result => result.FilePath).Distinct().Count());
        Assert.Contains("S02E01", results[3].FilePath);
        foreach (var result in results) result.Lease.Dispose();
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("incomplete")]
    [InlineData("empty")]
    [InlineData("page")]
    public async Task BadFileCannotReachPlaybackAndTemporaryDataIsRemoved(string failure)
    {
        using var fixture = new Fixture((_, _) => failure == "probe"
            ? Task.FromException<string>(new InvalidDataException("No video/audio")) : Task.FromResult(""));
        if (failure == "incomplete") fixture.Handler.DeclaredLength = 500;
        if (failure == "empty") fixture.Handler.Payload = [];
        if (failure == "page") fixture.Handler.ContentType = "text/html";
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadTemporaryAsync(Source(), Context(Source())));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.False(Directory.Exists(fixture.Library));
    }

    [Fact]
    public async Task WebsiteSourcesAreRejectedWithoutATransfer()
    {
        using var fixture = new Fixture();
        var source = Source() with { AccessMode = AudiovisualSourceAccessMode.WebPage };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DownloadTemporaryAsync(source, Context(source)));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CancellationAfterBytesArriveRemovesThePartialAndJob()
    {
        using var fixture = new Fixture();
        var stream = new StalledStream();
        fixture.Handler.Body = stream;
        using var cancellation = new CancellationTokenSource();
        Task transfer = fixture.Service.DownloadTemporaryAsync(Source(), Context(Source()), token: cancellation.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.GetFiles(fixture.Temporary, "*.partial", SearchOption.AllDirectories));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task CancellationDuringAnUncooperativeProbeStillPreventsHandoff()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, _) => { started.SetResult(); return release.Task; });
        using var cancellation = new CancellationTokenSource();
        Task transfer = fixture.Service.DownloadTemporaryAsync(Source(), Context(Source()), token: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.SetResult("Audio unverified");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Fact]
    public async Task RestartDeletesOrphansAndPreservesActiveLeasesAndUnrelatedDirectories()
    {
        using var fixture = new Fixture();
        var active = await fixture.Service.DownloadTemporaryAsync(Source(), Context(Source()));
        string orphan = Path.Combine(fixture.Temporary, Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(fixture.Temporary, "user-folder");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(orphan, "video.partial"), "unfinished");
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        _ = new AuthorizedMediaDownloadService(fixture.Http, fixture.Config, fixture.Temporary, (_, _) => Task.FromResult(""));
        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(active.FilePath));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        active.Lease.Dispose();
        Assert.False(File.Exists(active.FilePath));
    }

    private static AudiovisualSource Source() => new()
    {
        ProviderId = "test", AccessMode = AudiovisualSourceAccessMode.DirectMedia,
        Location = new("https://93.184.216.34/video.mp4")
    };
    private static AudiovisualPlaybackContext Context(AudiovisualSource source) => new("film:test",
        new() { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature, Title = "Movie", Year = 1984 },
        AudiovisualUnit.Feature, "Movie", "", source);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.TempMediaTests", Guid.NewGuid().ToString("N"));
        public string Temporary => Path.Combine(_root, "temporary");
        public string Library => Path.Combine(_root, "library");
        public ResponseHandler Handler { get; } = new();
        public HttpClient Http { get; }
        public DomainHotSwapper Config { get; }
        public AuthorizedMediaDownloadService Service { get; }
        public int Probes { get; private set; }
        public Fixture(Func<string, CancellationToken, Task<string>>? probe = null, Func<string, long>? capacity = null)
        {
            Directory.CreateDirectory(_root);
            Http = new(Handler);
            Config = new(Path.Combine(_root, "config.json"));
            Config.SetSetting("DownloadDirectory", Library);
            Service = new(Http, Config, Temporary, async (path, token) =>
            {
                Probes++;
                Assert.True(File.Exists(path));
                return probe == null ? "Audio language unverified" : await probe(path, token);
            }, availableSpace: capacity);
        }
        public void Dispose()
        {
            Http.Dispose();
            string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.TempMediaTests") + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public byte[] Payload { get; set; } = Encoding.UTF8.GetBytes("test video data");
        public long? DeclaredLength { get; set; }
        public string ContentType { get; set; } = "video/mp4";
        public Stream? Body { get; set; }
        public int Requests { get; private set; }
        public string? Cookie { get; private set; }
        public string? Referer { get; private set; }
        public string? SourceHeader { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
            Referer = request.Headers.Referrer?.AbsoluteUri;
            SourceHeader = request.Headers.TryGetValues("X-Source", out var values) ? values.Single() : null;
            HttpContent content = Body == null ? new ByteArrayContent(Payload) : new StreamContent(Body);
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            if (DeclaredLength.HasValue) content.Headers.ContentLength = DeclaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasBytes;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_hasBytes) { _hasBytes = true; buffer.Span[0] = 1; return 1; }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatedBody(long remaining) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Fill(1);
            remaining -= count;
            return ValueTask.FromResult(count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
