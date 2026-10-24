using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class DownloadReleasePinningTests
{
    [Fact]
    public async Task DownloadQueuePinsTheFirstReleaseAcrossRestartAndChangedFeeds()
    {
        await using var fixture = new PinningFixture();
        Guid jobId;
        using (var queue = fixture.Queue())
        {
            var job = queue.Enqueue("Pinning Show", "Sub", 123, 2);
            jobId = job.Id;
            await WaitForFailure(queue, jobId);
            Assert.Equal(new[] { PinningFixture.HashA }, fixture.AddedHashes.ToArray());
        }

        int feedRequests = fixture.Requests.Count(path => path == "/rss");
        fixture.FeedHash = PinningFixture.HashB;
        using (var restarted = fixture.Queue())
        {
            Assert.True(restarted.Retry(jobId));
            await WaitForFailure(restarted, jobId);
            Assert.Equal(new[] { PinningFixture.HashA }, fixture.AddedHashes.ToArray());
            Assert.Equal(new[] { PinningFixture.HashA }, fixture.ResumedHashes.ToArray());
            var job = Assert.Single(restarted.GetJobsSnapshot());
            Assert.Equal("Pinning Show", job.Title);
            Assert.Equal("Sub", job.AudioPreference);
        }

        fixture.Configuration.SetSetting("DownloadDirectory", fixture.ForeignDirectory);
        using (var movedConfiguration = fixture.Queue())
        {
            Assert.True(movedConfiguration.Retry(jobId));
            await WaitForFailure(movedConfiguration, jobId);
            Assert.Equal(new[] { PinningFixture.HashA }, fixture.AddedHashes.ToArray());
            Assert.Equal(new[] { PinningFixture.HashA, PinningFixture.HashA }, fixture.ResumedHashes.ToArray());
        }
        Assert.Equal(feedRequests, fixture.Requests.Count(path => path == "/rss"));

        using var persisted = JsonDocument.Parse(Assert.Single(fixture.QueueSnapshotsAtAdd));
        var pin = persisted.RootElement[0].GetProperty("ReleaseSelection");
        Assert.Equal(PinningFixture.HashA, pin.GetProperty("InfoHash").GetString());
        Assert.Equal("Pinning Show Season 1 [Batch]", pin.GetProperty("Title").GetString());
        Assert.Equal("Sub", pin.GetProperty("AudioPreference").GetString());
        Assert.Equal(fixture.DownloadDirectory, pin.GetProperty("DownloadDirectory").GetString());
        Assert.Contains(PinningFixture.HashA, pin.GetProperty("MagnetLink").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.ForeignDirectory, ".umos-torrent-owner.json")));
        Assert.DoesNotContain(fixture.Messages, message => message.Contains("Booting built-in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectionPersistenceFailurePreventsBackendMutation()
    {
        await using var fixture = new PinningFixture();
        fixture.OnFeed = () => Directory.CreateDirectory(fixture.QueuePath + ".tmp");
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", "Sub");
        await WaitForFailure(queue, job.Id);
        Assert.Contains("could not be saved", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.ReleaseSelection);
        Assert.Empty(fixture.AddedHashes);
        Assert.Empty(fixture.ResumedHashes);
        Assert.DoesNotContain(fixture.Requests, path => path.StartsWith("/api/", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.DownloadDirectory, ".umos-torrent-owner.json")));
    }

    [Theory]
    [InlineData(DownloadJobStatus.Pending)]
    [InlineData(DownloadJobStatus.Running)]
    [InlineData(DownloadJobStatus.Paused)]
    [InlineData(DownloadJobStatus.Failed)]
    [InlineData(DownloadJobStatus.Cancelled)]
    public async Task LegacyUnpinnedJobsRetainFilesAndRequireOriginalTransferInformation(DownloadJobStatus status)
    {
        await using var fixture = new PinningFixture();
        var saved = new DownloadQueueJob { Title = "Pinning Show", Status = status, Progress = 41 };
        File.WriteAllText(fixture.QueuePath, JsonSerializer.Serialize(new[] { saved }));
        using var queue = fixture.Queue();
        var job = Assert.Single(queue.GetJobsSnapshot());
        if (status is DownloadJobStatus.Running or DownloadJobStatus.Paused) Assert.True(queue.Resume(job.Id));
        else if (status != DownloadJobStatus.Pending) Assert.True(queue.Retry(job.Id));
        await WaitForFailure(queue, job.Id);
        Assert.Contains("original release selection is unknown", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Requests);
        Assert.Null(job.ReleaseSelection);
        Assert.False(File.Exists(Path.Combine(fixture.DownloadDirectory, ".umos-torrent-owner.json")));
    }

    [Fact]
    public async Task LegacyCompletedHistoryRemainsCompletedWithoutStartingATransfer()
    {
        await using var fixture = new PinningFixture();
        var saved = new DownloadQueueJob
        {
            Title = "Pinning Show", Status = DownloadJobStatus.Completed,
            CompletedFilePath = Path.Combine(fixture.DownloadDirectory, "retained.bin"), Progress = 100
        };
        File.WriteAllText(fixture.QueuePath, JsonSerializer.Serialize(new[] { saved }));
        using var queue = fixture.Queue();
        var job = Assert.Single(queue.GetJobsSnapshot());
        Assert.Equal(DownloadJobStatus.Completed, job.Status);
        Assert.Equal(saved.CompletedFilePath, job.CompletedFilePath);
        Assert.False(queue.Retry(job.Id));
        Assert.False(queue.Resume(job.Id));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("magnet")]
    [InlineData("catalog")]
    [InlineData("audio")]
    [InlineData("title")]
    [InlineData("missing-folder")]
    public async Task ConflictingOrIncompleteStoredSelectionsCannotStartAnotherTransfer(string conflict)
    {
        await using var fixture = new PinningFixture();
        var selection = new DownloadReleaseSelection(PinningFixture.HashA, "magnet:?xt=urn:btih:" + PinningFixture.HashA,
            "Pinning Show Season 1 [Batch]", "Pinning Show", "Sub", fixture.DownloadDirectory);
        selection = conflict switch
        {
            "hash" => selection with { InfoHash = PinningFixture.HashB },
            "magnet" => selection with { MagnetLink = "magnet:?xt=urn:btih:invalid" },
            "catalog" => selection with { CatalogTitle = "Different Show" },
            "audio" => selection with { AudioPreference = "Dub" },
            "title" => selection with { Title = "Different Show Season 1 [Batch]" },
            _ => selection with { DownloadDirectory = Path.Combine(fixture.DownloadDirectory, "missing") }
        };
        var saved = new DownloadQueueJob
        {
            Title = "Pinning Show", Status = DownloadJobStatus.Failed,
            ReleaseSelectionProtocol = 1, ReleaseSelection = selection
        };
        File.WriteAllText(fixture.QueuePath, JsonSerializer.Serialize(new[] { saved }));
        using var queue = fixture.Queue();
        Assert.True(queue.Retry(saved.Id));
        await WaitForFailure(queue, saved.Id);
        Assert.Contains("retained", Assert.Single(queue.GetJobsSnapshot()).StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.DownloadDirectory, "missing")));
    }

    /// <summary>One bad feed entry with more seeders than the usable release used to fail the season before any
    /// transfer, and every Retry searched again and failed the same way.</summary>
    [Theory]
    [InlineData("<link>magnet:?xt=urn:btih:ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ</link>")]
    [InlineData("<link>magnet:?xt=urn:btih:0123456789ABCDEF</link>")]
    [InlineData("<link>magnet:?dn=Pinning+Show</link>")]
    [InlineData("<nyaa:infoHash>not-a-hash</nyaa:infoHash>")]
    [InlineData("<link>magnet:?xt=urn:btih:" + PinningFixture.HashB + "</link><nyaa:infoHash>" + PinningFixture.HashA + "</nyaa:infoHash>")]
    public async Task MalformedMagnetInTheFeedDoesNotBlockAUsableRelease(string malformedItem)
    {
        await using var fixture = new PinningFixture { MalformedItem = malformedItem };
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", "Sub");
        await WaitForFailure(queue, job.Id);

        Assert.True(fixture.AddedHashes.SequenceEqual(new[] { PinningFixture.HashA }),
            $"Added [{string.Join(", ", fixture.AddedHashes)}]; job: {job.StatusMessage}");
        Assert.Equal(PinningFixture.HashA, job.ReleaseSelection?.InfoHash);
    }

    [Fact]
    public async Task OnlyMalformedMagnetsFailVisiblyWithoutATransferAndRetrySearchesAgain()
    {
        await using var fixture = new PinningFixture
        {
            MalformedItem = "<link>magnet:?xt=urn:btih:0123456789ABCDEF</link>", OfferValidRelease = false
        };
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", "Sub");
        await WaitForFailure(queue, job.Id);

        Assert.Contains("valid transfer hash", job.StatusMessage, StringComparison.Ordinal);
        Assert.Null(job.ReleaseSelection);
        Assert.Empty(fixture.AddedHashes);
        Assert.DoesNotContain(fixture.Requests, path => path.StartsWith("/api/v2/torrents/", StringComparison.Ordinal));

        fixture.OfferValidRelease = true;
        Assert.True(queue.Retry(job.Id));
        await WaitForFailure(queue, job.Id);
        Assert.Equal(new[] { PinningFixture.HashA }, fixture.AddedHashes.ToArray());
    }

    /// <summary>A permanent season must not substitute another audio track for the one requested: Dub needs a Dub or
    /// dual-audio label, and Sub excludes Dub-only releases, as the temporary watch already requires.</summary>
    [Theory]
    [InlineData("Dub", "Pinning Show Season 1 [Batch]")]
    [InlineData("Dub", "Pinning Show Season 1 [Batch] [Sub]")]
    [InlineData("Sub", "Pinning Show Season 1 [Batch] [English Dub]")]
    public async Task SeasonWithoutTheRequestedAudioLabelIsNotDownloaded(string audio, string onlyRelease)
    {
        await using var fixture = new PinningFixture { FeedTitle = onlyRelease };
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", audio);
        await WaitForFailure(queue, job.Id);

        Assert.True(fixture.AddedHashes.IsEmpty, $"Downloaded \"{job.ReleaseSelection?.Title}\" for {audio}; job: {job.StatusMessage}");
        Assert.Null(job.ReleaseSelection);
        Assert.Contains(audio == "Dub" ? "Dub" : "Sub", job.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dub", "Pinning Show Season 1 [Batch] [Dual Audio]")]
    [InlineData("Sub", "Pinning Show Season 1 [Batch] [Dual Audio]")]
    [InlineData("Sub", "Pinning Show Season 1 [Batch]")]
    public async Task SeasonWithTheRequestedAudioLabelIsStillDownloaded(string audio, string release)
    {
        await using var fixture = new PinningFixture { FeedTitle = release };
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", audio);
        await WaitForFailure(queue, job.Id);
        Assert.Equal(new[] { PinningFixture.HashA }, fixture.AddedHashes.ToArray());
        Assert.Equal(release, job.ReleaseSelection?.Title);
    }

    /// <summary>An older or manually added qBittorrent transfer of the same release in another folder is left
    /// untouched; the row must say so instead of a generic failure that Retry repeats.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task QBitTransferInAnotherFolderShowsWhyTheJobStopped(int lookupsBeforeMove)
    {
        await using var fixture = new PinningFixture { ForeignAfterLookups = lookupsBeforeMove };
        fixture.MarkPresent(PinningFixture.HashA);
        using var queue = fixture.Queue();
        var job = queue.Enqueue("Pinning Show", "Sub");
        await WaitForFailure(queue, job.Id);

        Assert.Contains("belongs to a different download folder", job.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.AddedHashes);
        Assert.DoesNotContain(fixture.Messages, message => message.Contains("Booting built-in", StringComparison.Ordinal));
    }

    private static async Task WaitForFailure(DownloadQueueService queue, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.GetJobsSnapshot().Single(job => job.Id == id).Status != DownloadJobStatus.Failed)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class PinningFixture : IAsyncDisposable
    {
        public const string HashA = "0123456789ABCDEF0123456789ABCDEF01234567";
        public const string HashB = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS-release-pin-" + Guid.NewGuid().ToString("N")));
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
        private readonly Task _server;
        private readonly byte[] _sentinel = Encoding.UTF8.GetBytes("Retain original and foreign transfer bytes.");
        public DomainHotSwapper Configuration { get; }
        public string DownloadDirectory => Path.Combine(_root, "downloads");
        public string ForeignDirectory => Path.Combine(_root, "foreign");
        public string QueuePath => Path.Combine(_root, "download-queue.json");
        public string FeedHash { get; set; } = HashA;
        /// <summary>The body of an extra, better-seeded feed result for the same season.</summary>
        public string? MalformedItem { get; set; }
        public bool OfferValidRelease { get; set; } = true;
        public string FeedTitle { get; set; } = "Pinning Show Season 1 [Batch]";
        /// <summary>After this many transfer lookups, qBittorrent reports present transfers in the foreign folder.</summary>
        public int ForeignAfterLookups { get; set; } = int.MaxValue;
        private int _lookups;
        public void MarkPresent(string hash) => _present.Add(hash);
        public ConcurrentQueue<string> AddedHashes { get; } = new();
        public ConcurrentQueue<string> ResumedHashes { get; } = new();
        public ConcurrentQueue<string> QueueSnapshotsAtAdd { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public Action? OnFeed { get; set; }

        public PinningFixture()
        {
            foreach (string directory in new[] { DownloadDirectory, ForeignDirectory })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "retained.bin"), _sentinel);
            }
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(endpoint + "/");
            _listener.Start();
            Configuration = new DomainHotSwapper(Path.Combine(_root, "config.json"));
            Configuration.SetSetting("DownloadDirectory", DownloadDirectory);
            Configuration.SetSetting("QBitHost", "127.0.0.1");
            Configuration.SetSetting("QBitPort", port.ToString());
            Configuration.SetSetting("QBitUsername", "isolated-pinning");
            Configuration.SetSetting("QBitPassword", "isolated-pinning");
            Configuration.SetSetting("NyaaUrl", endpoint + "/rss?q=");
            Configuration.SetSetting("AnimeToshoUrl", endpoint + "/rss?q=");
            _server = ServeAsync();
        }

        public DownloadQueueService Queue()
        {
            var queue = new DownloadQueueService(QueuePath, new SeasonDownloadJobExecutor(Configuration));
            queue.JobsChanged += (_, _) =>
            {
                foreach (var job in queue.GetJobsSnapshot()) Messages.Enqueue(job.StatusMessage);
            };
            return queue;
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    string path = context.Request.Url!.AbsolutePath;
                    Requests.Enqueue(path);
                    string body = "";
                    if (path == "/rss")
                    {
                        OnFeed?.Invoke();
                        string malformed = MalformedItem == null ? "" :
                            $"<item><title>Pinning Show Season 1 [Batch] (mirror)</title>{MalformedItem}<nyaa:seeders>50</nyaa:seeders></item>";
                        string valid = !OfferValidRelease ? "" :
                            $"<item><title>{FeedTitle}</title><link>magnet:?xt=urn:btih:{FeedHash}</link><nyaa:seeders>20</nyaa:seeders><nyaa:infoHash>{FeedHash}</nyaa:infoHash></item>";
                        body = $"<rss version=\"2.0\" xmlns:nyaa=\"https://nyaa.si/xmlns/nyaa\"><channel>{malformed}{valid}</channel></rss>";
                    }
                    else if (path == "/api/v2/auth/login")
                    {
                        context.Response.StatusCode = 204;
                        context.Response.Headers.Add("Set-Cookie", "SID=isolated-pinning; HttpOnly");
                    }
                    else if (path == "/api/v2/torrents/info")
                    {
                        string hash = context.Request.QueryString["hashes"] ?? "";
                        string savePath = Interlocked.Increment(ref _lookups) > ForeignAfterLookups ? ForeignDirectory : DownloadDirectory;
                        body = _present.Contains(hash) ? JsonSerializer.Serialize(new[] { new
                        {
                            hash, save_path = savePath, progress = 1, state = "stoppedUP"
                        } }) : "[]";
                    }
                    else if (path == "/api/v2/torrents/add")
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        string request = await reader.ReadToEndAsync(_lifetime.Token);
                        string hash = Regex.Match(request, @"btih:([A-Fa-f0-9]{40})").Groups[1].Value.ToUpperInvariant();
                        AddedHashes.Enqueue(hash);
                        QueueSnapshotsAtAdd.Enqueue(File.ReadAllText(QueuePath));
                        _present.Add(hash);
                        body = "<html>Unconfirmed after acceptance</html>";
                    }
                    else if (path is "/api/v2/torrents/start" or "/api/v2/torrents/resume")
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        string request = await reader.ReadToEndAsync(_lifetime.Token);
                        ResumedHashes.Enqueue(Uri.UnescapeDataString(request.Split('=', 2)[1]).ToUpperInvariant());
                    }
                    else if (path == "/api/v2/torrents/files") body = "[]";
                    else context.Response.StatusCode = 404;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _lifetime.Token);
                    context.Response.Close();
                }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested || !_listener.IsListening) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Close();
            await _server;
            _lifetime.Dispose();
            foreach (string directory in new[] { DownloadDirectory, ForeignDirectory })
                Assert.Equal(_sentinel, File.ReadAllBytes(Path.Combine(directory, "retained.bin")));
            string absolute = Path.GetFullPath(_root);
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("UniversalMediaOS-release-pin-", Path.GetFileName(absolute), StringComparison.Ordinal);
            Assert.False((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(absolute, recursive: true);
        }
    }
}
