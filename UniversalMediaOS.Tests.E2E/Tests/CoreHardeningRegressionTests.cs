using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Streaming;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class CoreHardeningRegressionTests
{
    [Fact]
    public void HlsChildRequests_KeepCredentialsOnTheirHostAndPublicContextAcrossCdns()
    {
        var session = new ProxySession(
            "https://media.example/master.m3u8",
            "Test Agent",
            "session=secret",
            null,
            "https://media.example/watch?token=secret#private",
            DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer secret",
                ["X-Playback-Token"] = "captured-secret",
                ["Accept-Language"] = "en-US"
            });

        MethodInfo? buildRequest = typeof(HlsLoopbackProxy).GetMethod(
            "BuildRequest",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildRequest);

        Uri sameOriginChild = new("https://media.example/next.m3u8");
        Uri crossOriginManifest = new("https://cdn.example/variant.m3u8");
        Uri crossOriginSegment = new("https://segments.example/episode-1.ts");
        Uri crossOriginKey = new("https://keys.example/aes.key");

        Assert.True(HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, sameOriginChild));
        Assert.False(HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, crossOriginManifest));
        Assert.False(HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, crossOriginSegment));
        Assert.False(HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, crossOriginKey));

        using var sameOrigin = (HttpRequestMessage)buildRequest!.Invoke(
            null,
            new object[]
            {
                HttpMethod.Get,
                sameOriginChild.AbsoluteUri,
                session,
                HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, sameOriginChild)
            })!;
        using var crossOrigin = (HttpRequestMessage)buildRequest.Invoke(
            null,
            new object[]
            {
                HttpMethod.Get,
                crossOriginSegment.AbsoluteUri,
                session,
                HlsLoopbackProxy.ShouldReplayCapturedHeaders(session, crossOriginSegment)
            })!;

        Assert.True(sameOrigin.Headers.Contains("Authorization"));
        Assert.True(sameOrigin.Headers.Contains("Cookie"));
        Assert.True(sameOrigin.Headers.Contains("Referer"));
        Assert.True(sameOrigin.Headers.Contains("Origin"));
        Assert.True(sameOrigin.Headers.Contains("X-Playback-Token"));

        Assert.False(crossOrigin.Headers.Contains("Authorization"));
        Assert.False(crossOrigin.Headers.Contains("Cookie"));
        Assert.Equal("https://media.example/", crossOrigin.Headers.Referrer!.AbsoluteUri);
        Assert.Equal("https://media.example", Assert.Single(crossOrigin.Headers.GetValues("Origin")));
        Assert.False(crossOrigin.Headers.Contains("X-Playback-Token"));
        Assert.Equal("Test Agent", crossOrigin.Headers.UserAgent.ToString());

        Assert.True(HlsLoopbackProxy.IsSameOrigin(
            new Uri("https://media.example/a"),
            new Uri("https://MEDIA.example/b")));
        Assert.False(HlsLoopbackProxy.IsSameOrigin(
            new Uri("https://media.example/a"),
            new Uri("https://other.example/b")));
        Assert.False(HlsLoopbackProxy.IsSameOrigin(
            new Uri("https://media.example/a"),
            new Uri("http://media.example/b")));
    }

    [Fact]
    public async Task HlsClient_EnforcesPublicAddressesAtConnectTimeAndDoesNotUseHttp3()
    {
        using HttpClient client = HlsLoopbackProxy.CreatePublicNetworkClient();
        Assert.Equal(HttpVersion.Version20, client.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.DefaultVersionPolicy);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://127.0.0.1:1/private-segment.ts"));
        Assert.Contains(
            "blocked because it is local, private, or reserved",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDatabaseMigration_PublishesAtomicallyAndCanRetryAfterFailure()
    {
        using var sandbox = new TemporaryDirectory("UniversalMediaOS-db-migration-tests");
        string legacyPath = Path.Combine(sandbox.Path, "legacy.db");
        string destinationPath = Path.Combine(sandbox.Path, "media_os.db");

        using (var legacy = new SqliteConnection($"Data Source={legacyPath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = "CREATE TABLE Sample (Value TEXT NOT NULL); INSERT INTO Sample VALUES ('preserved');";
            command.ExecuteNonQuery();
        }

        // Occupying the final path with a directory forces publication to fail
        // after the temporary backup has been created.
        Directory.CreateDirectory(destinationPath);
        Assert.False(DatabaseContext.TryMigrateLegacyDatabase(legacyPath, destinationPath));
        Assert.False(File.Exists(destinationPath));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Path, "*.migration-*.tmp"));

        Directory.Delete(destinationPath);
        Assert.True(DatabaseContext.TryMigrateLegacyDatabase(legacyPath, destinationPath));

        using var migrated = new SqliteConnection($"Data Source={destinationPath};Mode=ReadOnly");
        migrated.Open();
        using var verify = migrated.CreateCommand();
        verify.CommandText = "SELECT Value FROM Sample LIMIT 1";
        Assert.Equal("preserved", verify.ExecuteScalar());
    }

    [Theory]
    [InlineData("[Group] One Piece - 1100 [1080p]", "One Piece", true)]
    [InlineData("[Group] One Punch Man - 01-12 Batch", "One Piece", false)]
    [InlineData("[Group] Attack on Titan Complete", "Attack on Titan", true)]
    [InlineData("[Group] Attack on Train Complete", "Attack on Titan", false)]
    [InlineData("[Group] The Misfit of Demon King Academy Complete", "The Misfit of Demon King Academy", true)]
    [InlineData("[Group] Demon King Daimao Complete", "The Misfit of Demon King Academy", false)]
    [InlineData("[Group] That Time I Got Reincarnated as a Sword", "That Time I Got Reincarnated as a Slime", false)]
    [InlineData("[Group] 86 Eighty-Six Complete", "86", true)]
    public void TorrentTitleMatching_RequiresEveryMeaningfulIdentityToken(
        string torrentTitle,
        string targetTitle,
        bool expected)
    {
        Assert.Equal(expected, SeasonDownloader.TitleLooksLikeMatch(torrentTitle, targetTitle));
    }

    [Fact]
    public async Task QueuePersistence_SnapshotsStateAfterAcquiringWriteOrder()
    {
        using var sandbox = new TemporaryDirectory("UniversalMediaOS-queue-order-tests");
        string queuePath = Path.Combine(sandbox.Path, "download-queue.json");
        var seeded = new DownloadQueueJob
        {
            Title = "State ordering",
            Status = DownloadJobStatus.Paused,
            StatusMessage = "older state"
        };
        await File.WriteAllTextAsync(queuePath, JsonSerializer.Serialize(new[] { seeded }));

        using var queue = new DownloadQueueService(queuePath, new IdleExecutor());
        DownloadQueueJob job = Assert.Single(queue.GetJobsSnapshot());
        var persistenceLock = (SemaphoreSlim?)typeof(DownloadQueueService)
            .GetField("_persistenceLock", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(queue);
        MethodInfo? persistQueue = typeof(DownloadQueueService).GetMethod(
            "PersistQueue",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(persistenceLock);
        Assert.NotNull(persistQueue);

        await persistenceLock!.WaitAsync();
        using var writerStarted = new ManualResetEventSlim();
        Task writer = Task.Run(() =>
        {
            writerStarted.Set();
            persistQueue!.Invoke(queue, null);
        });

        try
        {
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(2)));
            await Task.Delay(100);
            job.Status = DownloadJobStatus.Cancelled;
            job.StatusMessage = "newer state";
        }
        finally
        {
            persistenceLock.Release();
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = JsonSerializer.Deserialize<List<DownloadQueueJob>>(
            await File.ReadAllTextAsync(queuePath));
        DownloadQueueJob persistedJob = Assert.Single(persisted!);
        Assert.Equal(DownloadJobStatus.Cancelled, persistedJob.Status);
        Assert.Equal("newer state", persistedJob.StatusMessage);
    }

    [Fact]
    public void ProtectedSettings_MigrateLegacyPlaintextToVersionedCiphertext()
    {
        using var sandbox = new TemporaryDirectory("UniversalMediaOS-config-migration-tests");
        string configPath = Path.Combine(sandbox.Path, "config.json");
        const string plaintextKey = "legacy-plaintext-tmdb-key";
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["TmdbApiKey"] = plaintextKey }));

        var config = new DomainHotSwapper(configPath);
        Assert.Equal(plaintextKey, config.GetSetting("TmdbApiKey"));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        string storedValue = document.RootElement.GetProperty("TmdbApiKey").GetString()!;
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("dpapi:v1:", storedValue, StringComparison.Ordinal);
            Assert.DoesNotContain(plaintextKey, storedValue, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(plaintextKey, storedValue);
        }
    }

    [Fact]
    public void ProtectedSettings_MigrateLegacyUnmarkedDpapiCiphertext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TemporaryDirectory("UniversalMediaOS-config-dpapi-tests");
        string configPath = Path.Combine(sandbox.Path, "config.json");
        const string plaintext = "legacy-dpapi-secret";
        string legacyCiphertext = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            null,
            DataProtectionScope.CurrentUser));
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["TmdbApiKey"] = legacyCiphertext }));

        var config = new DomainHotSwapper(configPath);
        Assert.Equal(plaintext, config.GetSetting("TmdbApiKey"));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        Assert.StartsWith(
            "dpapi:v1:",
            document.RootElement.GetProperty("TmdbApiKey").GetString(),
            StringComparison.Ordinal);
    }

    private sealed class IdleExecutor : IDownloadJobExecutor
    {
        public Task<DownloadExecutionResult> ExecuteAsync(
            DownloadQueueJob job,
            Action<string> log,
            Action<double> progress,
            CancellationToken token) =>
            Task.FromResult(new DownloadExecutionResult(false));

        public Task<bool> PauseActiveAsync(CancellationToken token) => Task.FromResult(true);

        public Task<bool> CancelActiveAsync(CancellationToken token) => Task.FromResult(true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string category)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                category,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
