using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SeasonValidationPreservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableReportedFileDoesNotDeletePermanentDataOrUseABasenameGuess(bool missingReportedPath)
    {
        await using var fixture = new CompletedTransfer(missingReportedPath);
        byte[] original = Encoding.UTF8.GetBytes("Existing permanent media must survive failed validation.");
        await File.WriteAllBytesAsync(fixture.PreservedFile, original);
        using var downloader = new SeasonDownloader(fixture.Configuration);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var messages = new List<string>();

        bool completed = await downloader.DownloadSeasonAsync("Preservation Show", messages.Add,
            token: cancellation.Token, audioPreference: "Sub");

        Assert.False(completed);
        Assert.Null(downloader.LastCompletedVideoPath);
        Assert.True(File.Exists(fixture.PreservedFile), "Failed validation deleted existing permanent media.");
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.PreservedFile));
        Assert.DoesNotContain(messages, message => message.Contains("Booting built-in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadableExactReportedFileStillCompletesAndRetainsItsBytes()
    {
        await using var fixture = new CompletedTransfer(false, ".avi");
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=10",
            "-t", "5", "-an", "-c:v", "rawvideo", "-pix_fmt", "bgr24", "-y", fixture.PreservedFile })
            start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start)!;
        var errors = encoder.StandardError.ReadToEndAsync();
        await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(encoder.ExitCode == 0, await errors);
        byte[] before = await File.ReadAllBytesAsync(fixture.PreservedFile);
        Assert.True(before.Length > 5 * 1024 * 1024);
        using var downloader = new SeasonDownloader(fixture.Configuration);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(await downloader.DownloadSeasonAsync("Preservation Show", _ => { },
            token: cancellation.Token, audioPreference: "Sub"));
        Assert.Equal(fixture.PreservedFile, downloader.LastCompletedVideoPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.PreservedFile));
    }

    [Fact]
    public async Task UnavailableProbeCannotPublishLargeUnreadableBytesAsCompletedMedia()
    {
        await using var fixture = new CompletedTransfer(false);
        using (var file = File.Create(fixture.PreservedFile)) file.SetLength(6 * 1024 * 1024);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ManagedProbePath)!);
        await File.WriteAllTextAsync(fixture.ManagedProbePath, "Owned fixture: not an executable.");
        using var downloader = new SeasonDownloader(fixture.Configuration);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var messages = new List<string>();

        Assert.False(await downloader.DownloadSeasonAsync("Preservation Show", messages.Add,
            token: cancellation.Token, audioPreference: "Sub"));
        Assert.Null(downloader.LastCompletedVideoPath);
        Assert.Equal(6 * 1024 * 1024, new FileInfo(fixture.PreservedFile).Length);
        Assert.Contains(messages, message => message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CompletedTransfer : IAsyncDisposable
    {
        private const string Hash = "0123456789abcdef0123456789abcdef01234567";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        private readonly string _root;
        private readonly string _reportedFile;
        private readonly string? _previousDataRoot;
        public string PreservedFile { get; }
        public string ManagedProbePath => Path.Combine(_root, "Local", "UniversalMediaOS", "Services", "ffprobe.exe");
        public DomainHotSwapper Configuration { get; }

        public CompletedTransfer(bool missingReportedPath, string extension = ".mkv")
        {
            _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS-season-validation-" + Guid.NewGuid().ToString("N"));
            string downloads = Path.Combine(_root, "downloads");
            string filename = "episode-1" + extension;
            _reportedFile = missingReportedPath ? "reported/" + filename : filename;
            PreservedFile = Path.Combine(downloads,
                missingReportedPath ? "unrelated/" + filename : filename);
            Directory.CreateDirectory(Path.GetDirectoryName(PreservedFile)!);
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            string origin = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(origin);
            _listener.Start();
            _server = ServeAsync();
            Configuration = new DomainHotSwapper(Path.Combine(_root, "config.json"));
            Configuration.SetSetting("DownloadDirectory", downloads);
            Configuration.SetSetting("NyaaUrl", origin + "rss?q=");
            Configuration.SetSetting("AnimeToshoUrl", origin + "rss?q=");
            Configuration.SetSetting("QBitHost", "127.0.0.1");
            Configuration.SetSetting("QBitPort", port.ToString());
            _previousDataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _root);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    string path = context.Request.Url!.AbsolutePath;
                    string body;
                    if (path == "/rss")
                    {
                        context.Response.ContentType = "application/xml";
                        body = $"<rss version=\"2.0\" xmlns:nyaa=\"https://nyaa.si/xmlns/nyaa\"><channel><item><title>Preservation Show Season 1 [Batch]</title><link>magnet:?xt=urn:btih:{Hash}</link><nyaa:seeders>1</nyaa:seeders><nyaa:infoHash>{Hash}</nyaa:infoHash></item></channel></rss>";
                    }
                    else if (path == "/api/v2/auth/login")
                    {
                        context.Response.Headers.Add("Set-Cookie", "SID=isolated-preservation-fixture; HttpOnly");
                        body = "Ok.";
                    }
                    else if (path == "/api/v2/torrents/info")
                    {
                        body = $"[{{\"hash\":\"{Hash}\",\"progress\":1,\"state\":\"stoppedUP\"}}]";
                    }
                    else if (path == "/api/v2/torrents/files")
                    {
                        body = $"[{{\"name\":\"{_reportedFile}\",\"size\":{new FileInfo(PreservedFile).Length}}}]";
                    }
                    else if (path is "/api/v2/torrents/start" or "/api/v2/torrents/resume") body = "";
                    else
                    {
                        context.Response.StatusCode = 404;
                        body = "Unexpected fixture request";
                    }
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
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousDataRoot);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string resolvedRoot = Path.GetFullPath(_root);
            if (!resolvedRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith("UniversalMediaOS-season-validation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned validation fixture.");
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
