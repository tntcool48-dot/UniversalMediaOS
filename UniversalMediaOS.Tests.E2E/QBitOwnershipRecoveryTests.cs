using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class QBitOwnershipRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JunctionCannotHideAReservedPhysicalParent(bool aliasInAncestor)
    {
        using var fixture = new JunctionFixture();
        using (var owner = new TorrentDownloadOwnership(fixture.Physical, "http://127.0.0.1:9001")) owner.Reserve();
        string alias = fixture.Link("alias", aliasInAncestor ? fixture.Physical : fixture.Child);
        string download = aliasInAncestor ? Path.Combine(alias, "child") : alias;
        var error = Assert.Throws<TorrentDownloadOwnershipException>(() =>
        {
            using var unexpected = new TorrentDownloadOwnership(download, "http://127.0.0.1:9002");
        });
        Assert.Contains("overlapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SamePhysicalRootAliasReusesTheExistingReservation()
    {
        using var fixture = new JunctionFixture();
        using (var owner = new TorrentDownloadOwnership(fixture.Physical, "http://127.0.0.1:9001")) owner.Reserve();
        string marker = Path.Combine(fixture.Physical, ".umos-torrent-owner.json");
        byte[] originalMarker = File.ReadAllBytes(marker);
        string alias = fixture.Link("alias", fixture.Physical);
        using (var owner = new TorrentDownloadOwnership(alias, "http://127.0.0.1:9001"))
        {
            Assert.True(owner.WasReserved);
            Assert.True(owner.IsReserved);
            Assert.True(TorrentDownloadOwnership.SamePath(alias, fixture.Physical));
        }
        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
    }

    [Fact]
    public void SamePhysicalRootAliasesCannotAcquireTwoWriterLeases()
    {
        using var fixture = new JunctionFixture();
        string alias = fixture.Link("alias", fixture.Physical);
        using var first = new TorrentDownloadOwnership(fixture.Physical, "http://127.0.0.1:9001");
        Assert.Throws<TorrentDownloadOwnershipException>(() =>
        {
            using var unexpected = new TorrentDownloadOwnership(alias, "http://127.0.0.1:9001");
        });
    }

    [Fact]
    public async Task RetargetingAJunctionAfterConstructionCannotMoveThePostedQBitSavePath()
    {
        using var folders = new JunctionFixture();
        await using var fixture = new TransferFixture();
        string alias = folders.Link("alias", folders.Physical);
        fixture.Configuration.SetSetting("DownloadDirectory", alias);
        using var downloader = fixture.CreateDownloader();
        folders.Retarget(alias, folders.Foreign);

        var attempt = await fixture.RunAsync(downloader: downloader);

        Assert.Equal(1, fixture.AddCalls);
        Assert.Equal(folders.Physical, fixture.PostedAddSavePath);
        Assert.Equal(folders.Physical, downloader.DownloadDirectory);
        AssertNoNativeFallback(attempt.Messages);
        Assert.NotNull(attempt.Error); // The fixture returns a malformed add reply; no payload starts.
        Assert.True(File.Exists(Path.Combine(folders.Physical, ".umos-torrent-owner.json")));
        Assert.False(File.Exists(Path.Combine(folders.Foreign, ".umos-torrent-owner.json")));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("server-error")]
    [InlineData("cancelled")]
    public async Task UnconfirmedAddRetainsItsEngineAcrossReconstructionAndAuthenticationFailure(string reply)
    {
        await using var fixture = new TransferFixture { AddReply = reply };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (reply == "cancelled") fixture.OnAdd = cancellation.Cancel;
        var first = await fixture.RunAsync(cancellation.Token);
        fixture.OnAdd = null;
        fixture.AuthenticationAvailable = false;
        // A different release hash still targets the same download folder.
        fixture.Hash = "abcdef0123456789abcdef0123456789abcdef0123";
        var restarted = await fixture.RunAsync();

        Assert.Equal(1, fixture.AddCalls);
        AssertNoNativeFallback(first.Messages);
        AssertNoNativeFallback(restarted.Messages);
        Assert.NotNull(restarted.Error);
        Assert.Contains("qBittorrent", restarted.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
        if (reply == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(first.Error);
    }

    [Fact]
    public async Task LateTorrentAppearsOnRetryAndUsesTheSameEngineWithoutAnotherAdd()
    {
        await using var fixture = new TransferFixture { AddReply = "malformed" };
        var first = await fixture.RunAsync();
        fixture.Present = true;
        var resumed = await fixture.RunAsync();

        AssertNoNativeFallback(first.Messages);
        AssertNoNativeFallback(resumed.Messages);
        Assert.Equal(1, fixture.AddCalls);
        Assert.Equal(1, fixture.ResumeCalls);
        Assert.Equal(1, fixture.FilesCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Fact]
    public async Task DefiniteFirstRejectionLeavesAnUntouchedFolderEligibleForNativeFallback()
    {
        await using var fixture = new TransferFixture { AddReply = "rejected" };
        var first = await fixture.RunAsync();
        fixture.AuthenticationAvailable = false;
        var restarted = await fixture.RunAsync();

        Assert.Contains(first.Messages, IsNativeFallback);
        Assert.Contains(restarted.Messages, IsNativeFallback);
        Assert.Equal(1, fixture.AddCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Fact]
    public async Task LaterRejectionOrAbsenceDoesNotReleaseAnOlderUnconfirmedWriter()
    {
        await using var fixture = new TransferFixture { AddReply = "malformed" };
        await fixture.RunAsync();
        fixture.AddReply = "rejected";
        var rejected = await fixture.RunAsync();
        fixture.AuthenticationAvailable = false;
        var offline = await fixture.RunAsync();

        AssertNoNativeFallback(rejected.Messages);
        AssertNoNativeFallback(offline.Messages);
        Assert.Equal(2, fixture.AddCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTorrentMustMatchBothHashAndDownloadFolderBeforeAnyMutation(bool wrongHash)
    {
        await using var fixture = new TransferFixture { Present = true };
        if (wrongHash) fixture.ReportedHash = "abcdef0123456789abcdef0123456789abcdef0123";
        else fixture.ReportedSavePath = Path.Combine(fixture.Root, "somebody-elses-downloads");
        var result = await fixture.RunAsync();

        AssertNoNativeFallback(result.Messages);
        Assert.Equal(0, fixture.AddCalls);
        Assert.Equal(0, fixture.ResumeCalls);
        Assert.Equal(0, fixture.FilesCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Fact]
    public async Task ChangingTheEndpointDoesNotTransferOwnershipToAnotherServer()
    {
        await using var fixture = new TransferFixture { AddReply = "malformed" };
        await fixture.RunAsync();
        fixture.Configuration.SetSetting("QBitPort", "1");
        var result = await fixture.RunAsync();

        AssertNoNativeFallback(result.Messages);
        Assert.NotNull(result.Error);
        Assert.Contains("connection", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.AddCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingToAnOverlappingFolderCannotBypassAnUnconfirmedWriter(bool parent)
    {
        await using var fixture = new TransferFixture { AddReply = "malformed" };
        await fixture.RunAsync();
        fixture.AuthenticationAvailable = false;
        fixture.Configuration.SetSetting("DownloadDirectory", parent ? fixture.Root : Path.Combine(fixture.Root, "downloads", "nested"));
        var result = await fixture.RunAsync();

        AssertNoNativeFallback(result.Messages);
        Assert.NotNull(result.Error);
        Assert.Contains("overlapping", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.AddCalls);
    }

    [Fact]
    public async Task UnconfirmedAddCannotAcknowledgePauseCancelOrAllowASecondFolderWriter()
    {
        await using var fixture = new TransferFixture();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnAdd = () => observed.TrySetResult();
        fixture.HoldAddReply = release.Task;
        using var firstDownloader = fixture.CreateDownloader();
        var first = fixture.RunAsync(downloader: firstDownloader);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(await firstDownloader.PauseActiveTransferAsync());
            Assert.False(await firstDownloader.CancelActiveTransferAsync());
            var second = await fixture.RunAsync();
            Assert.NotNull(second.Error);
            AssertNoNativeFallback(second.Messages);
            Assert.Equal(1, fixture.AddCalls);
            Assert.Equal(0, fixture.ResumeCalls);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task ReservationWriteFailurePreventsTheMutatingRequest()
    {
        await using var fixture = new TransferFixture();
        fixture.OnLookup = () => Directory.CreateDirectory(Path.Combine(fixture.Root, "downloads", ".umos-torrent-owner.json"));
        var result = await fixture.RunAsync();
        Assert.NotNull(result.Error);
        Assert.Contains("ownership could not be saved", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoNativeFallback(result.Messages);
        Assert.Equal(0, fixture.AddCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Fact]
    public async Task FailedLookupIsNotPermissionToAddOrUseAnotherEngine()
    {
        await using var fixture = new TransferFixture { LookupStatus = 503 };
        var result = await fixture.RunAsync();
        AssertNoNativeFallback(result.Messages);
        Assert.Equal(0, fixture.AddCalls);
        Assert.Equal(0, fixture.ResumeCalls);
        Assert.Equal(fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    private static bool IsNativeFallback(string message) => message.Contains("Booting built-in", StringComparison.Ordinal);

    [Fact]
    public async Task MonitorStopsImmediatelyIfTheTransferMovesToAnotherFolder()
    {
        await using var fixture = new TransferFixture { Present = true };
        var gate = new QBitLogicGate($"http://127.0.0.1:{fixture.Configuration.GetSetting("QBitPort")}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.True(await gate.AuthenticateAsync(username: "fixture", password: "fixture", token: deadline.Token));
        fixture.ReportedSavePath = Path.Combine(fixture.Root, "foreign-folder");
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.MonitorDownloadAsync(fixture.Hash, _ => { }, token: deadline.Token,
            expectedSavePath: Path.Combine(fixture.Root, "downloads")));
        Assert.Equal(0, fixture.FilesCalls);
    }
    private static void AssertNoNativeFallback(IEnumerable<string> messages) => Assert.DoesNotContain(messages, IsNativeFallback);
    private sealed record Attempt(List<string> Messages, Exception? Error);

    private sealed class JunctionFixture : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS-qbit-junction-" + Guid.NewGuid().ToString("N")));
        private readonly List<string> _links = [];
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("Preserve physical download bytes through alias checks.");
        private readonly byte[] _foreignBytes = Encoding.UTF8.GetBytes("Preserve foreign bytes after junction retargeting.");
        public string Physical => Path.Combine(_root, "physical");
        public string Child => Path.Combine(Physical, "child");
        public string Foreign => Path.Combine(_root, "foreign");
        private string Sentinel => Path.Combine(Child, "retained.bin");
        private string ForeignSentinel => Path.Combine(Foreign, "retained.bin");

        public JunctionFixture()
        {
            Directory.CreateDirectory(Child);
            File.WriteAllBytes(Sentinel, _bytes);
            Directory.CreateDirectory(Foreign);
            File.WriteAllBytes(ForeignSentinel, _foreignBytes);
        }

        public string Link(string name, string target)
        {
            string link = Path.GetFullPath(Path.Combine(_root, name));
            AssertOwned(link);
            AssertOwned(target);
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(5000), "Owned junction creation must finish.");
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
            _links.Add(link);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            return link;
        }

        public void Retarget(string link, string target)
        {
            AssertOwned(link);
            AssertOwned(target);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(link, recursive: false);
            Assert.True(_links.Remove(link));
            Assert.Equal(link, Link(Path.GetFileName(link), target));
        }

        private void AssertOwned(string path) => Assert.StartsWith(_root + Path.DirectorySeparatorChar,
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            Assert.Equal(_bytes, File.ReadAllBytes(Sentinel));
            Assert.Equal(_foreignBytes, File.ReadAllBytes(ForeignSentinel));
            foreach (string link in _links.AsEnumerable().Reverse())
            {
                AssertOwned(link);
                Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(link, recursive: false);
                Assert.True(Directory.Exists(Child));
            }
            AssertOwned(Physical);
            Assert.False((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0);
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TransferFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        private readonly string? _previousDataRoot;
        private readonly HttpClient _rssClient;
        private readonly DualTrackerRssParser _rss;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UniversalMediaOS-qbit-ownership-" + Guid.NewGuid().ToString("N"));
        public DomainHotSwapper Configuration { get; }
        public string Hash { get; set; } = "0123456789abcdef0123456789abcdef01234567";
        public string? ReportedHash { get; set; }
        public string? ReportedSavePath { get; set; }
        public bool AuthenticationAvailable { get; set; } = true;
        public bool Present { get; set; }
        public string AddReply { get; set; } = "malformed";
        public Action? OnAdd { get; set; }
        public Action? OnLookup { get; set; }
        public Task? HoldAddReply { get; set; }
        public int LookupStatus { get; set; } = 200;
        public int AddCalls, ResumeCalls, FilesCalls;
        public string? PostedAddSavePath { get; private set; }
        public byte[] OriginalBytes { get; } = Encoding.UTF8.GetBytes("Retained permanent bytes belonging to an earlier transfer.");
        public string PreservedFile => Path.Combine(Root, "downloads", "earlier.mkv");

        public TransferFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreservedFile)!);
            File.WriteAllBytes(PreservedFile, OriginalBytes);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Configuration = new DomainHotSwapper(Path.Combine(Root, "config.json"));
            Configuration.SetSetting("DownloadDirectory", Path.Combine(Root, "downloads"));
            Configuration.SetSetting("QBitHost", "127.0.0.1");
            Configuration.SetSetting("QBitPort", port.ToString());
            Configuration.SetSetting("QBitUsername", "isolated-fixture");
            Configuration.SetSetting("QBitPassword", "isolated-fixture");
            _previousDataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Root);
            _rssClient = new HttpClient(new FeedHandler(this));
            _rss = new DualTrackerRssParser(_rssClient, "http://fixture.invalid/rss?q=", "http://fixture.invalid/rss?q=");
            _server = ServeAsync();
        }

        public SeasonDownloader CreateDownloader() => new(Configuration, null, _rss);

        public async Task<Attempt> RunAsync(CancellationToken token = default, SeasonDownloader? downloader = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var messages = new List<string>();
            Exception? error = null;
            using var ownedDownloader = downloader == null ? CreateDownloader() : null;
            var activeDownloader = downloader ?? ownedDownloader!;
            try
            {
                await activeDownloader.DownloadSeasonAsync("Ownership Show", message =>
                {
                    messages.Add(message);
                    // Observe the actual production engine decision without starting
                    // a network engine in a regression that is expected to fail.
                    if (IsNativeFallback(message)) throw new InvalidOperationException("Fixture stopped native startup.");
                }, token: deadline.Token, audioPreference: "Sub");
            }
            catch (Exception exception) { error = exception; }
            return new Attempt(messages, error);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    string path = context.Request.Url!.AbsolutePath;
                    string body = "";
                    if (path == "/api/v2/auth/login")
                    {
                        context.Response.StatusCode = AuthenticationAvailable ? 204 : 403;
                        if (AuthenticationAvailable) context.Response.Headers.Add("Set-Cookie", "SID=isolated-ownership; HttpOnly");
                    }
                    else if (path == "/api/v2/torrents/info")
                    {
                        OnLookup?.Invoke();
                        context.Response.StatusCode = LookupStatus;
                        body = Present ? JsonSerializer.Serialize(new[] { new { hash = ReportedHash ?? Hash, save_path = ReportedSavePath ?? Path.Combine(Root, "downloads"), progress = 1, state = "stoppedUP" } }) : "[]";
                    }
                    else if (path == "/api/v2/torrents/add")
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        string requestBody = await reader.ReadToEndAsync(_lifetime.Token);
                        PostedAddSavePath = Regex.Match(requestBody, "name=\"?savepath\"?\\r?\\n\\r?\\n([^\\r\\n]+)").Groups[1].Value;
                        Interlocked.Increment(ref AddCalls);
                        OnAdd?.Invoke();
                        if (HoldAddReply != null) await HoldAddReply.WaitAsync(_lifetime.Token);
                        if (AddReply == "server-error") context.Response.StatusCode = 500;
                        if (AddReply == "truncated")
                        {
                            context.Response.ContentLength64 = 100;
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\""), _lifetime.Token);
                            context.Response.Abort();
                            continue;
                        }
                        body = AddReply == "rejected" ? "{\"success_count\":0,\"failure_count\":1,\"pending_count\":0}" : "<html>Unconfirmed add response</html>";
                    }
                    else if (path is "/api/v2/torrents/start" or "/api/v2/torrents/resume") Interlocked.Increment(ref ResumeCalls);
                    else if (path == "/api/v2/torrents/files") { Interlocked.Increment(ref FilesCalls); body = "[]"; }
                    else { context.Response.StatusCode = 404; }
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, _lifetime.Token);
                    }
                    catch (Exception) when (AddReply == "cancelled" || _lifetime.IsCancellationRequested) { }
                    finally { context.Response.Close(); }
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
            _rssClient.Dispose();
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousDataRoot);
            string resolved = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("UniversalMediaOS-qbit-ownership-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned fixture.");
            Directory.Delete(resolved, recursive: true);
        }

        private sealed class FeedHandler(TransferFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = $"<rss version=\"2.0\" xmlns:nyaa=\"https://nyaa.si/xmlns/nyaa\"><channel><item><title>Ownership Show Season 1 [Batch]</title><link>magnet:?xt=urn:btih:{fixture.Hash}</link><nyaa:seeders>1</nyaa:seeders><nyaa:infoHash>{fixture.Hash}</nyaa:infoHash></item></channel></rss>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });
            }
        }
    }
}
