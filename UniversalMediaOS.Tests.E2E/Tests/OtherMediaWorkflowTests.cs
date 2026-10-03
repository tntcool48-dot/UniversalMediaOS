using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class OtherMediaWorkflowTests
{
    [Fact]
    public async Task AudiovisualLibrary_IsIndependentAndPersistsFavoriteStatusAndProgress()
    {
        string root = CreateTempRoot("Library");
        string storagePath = Path.Combine(root, "audiovisual-library.json");
        try
        {
            var service = new AudiovisualLibraryService(storagePath);
            AudiovisualLibraryKey movieKey = AudiovisualLibraryKey.Create(
                AudiovisualMediaKind.Movie,
                42,
                "The Example",
                2025);
            AudiovisualLibraryKey tvKey = AudiovisualLibraryKey.Create(
                AudiovisualMediaKind.Television,
                42,
                "The Example",
                2025);

            await service.SetFavoriteAsync(movieKey, "The Example", "https://img.test/movie.jpg", true);
            await service.SetStatusAsync(
                movieKey,
                "The Example",
                "https://img.test/movie.jpg",
                AudiovisualLibraryStatus.Planned);
            await service.RecordProgressAsync(
                tvKey,
                "The Example",
                "https://img.test/tv.jpg",
                2,
                7,
                123,
                1500);

            var reloaded = new AudiovisualLibraryService(storagePath);
            AudiovisualLibraryEntry movie = Assert.IsType<AudiovisualLibraryEntry>(
                await reloaded.GetAsync(movieKey));
            AudiovisualLibraryEntry tv = Assert.IsType<AudiovisualLibraryEntry>(
                await reloaded.GetAsync(tvKey));

            Assert.True(movie.IsFavorite);
            Assert.Equal(AudiovisualLibraryStatus.Planned, movie.Status);
            Assert.False(tv.IsFavorite);
            Assert.Equal(AudiovisualLibraryStatus.Watching, tv.Status);
            Assert.Equal(2, tv.LastSeasonNumber);
            Assert.Equal(7, tv.LastEpisodeNumber);
            Assert.Equal(123, tv.PositionSeconds);
            Assert.NotEqual(movie.Key.StableId, tv.Key.StableId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DirectDownloader_WritesAtomicallyInsideMediaKindDirectory()
    {
        string root = CreateTempRoot("Download");
        string configPath = Path.Combine(root, "config.json");
        string downloadsPath = Path.Combine(root, "downloads");
        try
        {
            var config = new DomainHotSwapper(configPath);
            Assert.True(config.SetSettings(new Dictionary<string, string>
            {
                ["DownloadDirectory"] = downloadsPath,
                ["OtherMediaMaximumDownloadBytes"] = "1048576"
            }));
            byte[] payload = Encoding.UTF8.GetBytes("authorized test media");
            using var client = new HttpClient(new StaticResponseHandler(payload, "video/mp4"));
            var service = new AuthorizedMediaDownloadService(client, config);

            string result = await service.DownloadAsync(
                new Uri("https://93.184.216.34/files/example.mp4"),
                AudiovisualMediaKind.Cartoon,
                "Example: Cartoon");

            Assert.True(File.Exists(result));
            Assert.Equal(payload, await File.ReadAllBytesAsync(result));
            Assert.Contains(
                Path.Combine("downloads", "Cartoons"),
                result,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(downloadsPath, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1/video.mp4")]
    [InlineData("https://localhost/video.mp4")]
    [InlineData("http://192.168.1.20/video.mp4")]
    [InlineData("file:///C:/video.mp4")]
    public void DirectDownloader_RejectsLocalPrivateAndNonHttpSources(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            AuthorizedMediaDownloadService.ValidateRemoteUri(new Uri(value)));
    }

    [Fact]
    public async Task DirectDownloader_RejectsRedirectThatEndsOnPrivateNetwork()
    {
        string root = CreateTempRoot("Redirect");
        try
        {
            var config = new DomainHotSwapper(Path.Combine(root, "config.json"));
            using var client = new HttpClient(new StaticResponseHandler(
                Encoding.UTF8.GetBytes("not accepted"),
                "video/mp4",
                new Uri("http://127.0.0.1/private.mp4")));
            var service = new AuthorizedMediaDownloadService(client, config);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.DownloadAsync(
                    new Uri("https://93.184.216.34/public.mp4"),
                    AudiovisualMediaKind.Movie,
                    "Redirect test"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task HardenedOtherMediaClient_BlocksPrivateConnectionsAtConnectTime()
    {
        using HttpClient client = OtherMediaHttpClientFactory.Create(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://127.0.0.1:6553/private"));
    }

    [Fact]
    public async Task SourceSafety_RejectsAResolvedPrivateRedirectBeforePlayback()
    {
        using var client = new HttpClient(new StaticResponseHandler(
            Encoding.UTF8.GetBytes("redirected"),
            "video/mp4",
            new Uri("http://127.0.0.1/private.mp4")));
        var safety = new OtherMediaSourceSafetyService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            safety.ResolvePublicLocationAsync(
                new Uri("https://93.184.216.34/public.mp4")));
    }

    [Fact]
    public async Task DirectDownloader_RejectsHlsPlaylistInsteadOfSavingBrokenMp4()
    {
        string root = CreateTempRoot("Hls");
        try
        {
            var config = new DomainHotSwapper(Path.Combine(root, "config.json"));
            using var client = new HttpClient(new StaticResponseHandler(
                Encoding.UTF8.GetBytes("#EXTM3U"),
                "application/vnd.apple.mpegurl"));
            var service = new AuthorizedMediaDownloadService(client, config);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadAsync(
                    new Uri("https://93.184.216.34/master.m3u8"),
                    AudiovisualMediaKind.Television,
                    "HLS test"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void CartoonFeatureAndSeries_DoNotCollideAndConfiguredSourcesExposeValidActions()
    {
        var feature = new AudiovisualIdentity
        {
            Kind = AudiovisualMediaKind.Cartoon,
            ContentForm = AudiovisualContentForm.Feature,
            Title = "Example",
            Year = 2024,
            TmdbId = 77
        };
        var series = feature with { ContentForm = AudiovisualContentForm.Series };

        Assert.False(ExactAudiovisualMatcher.IsIdentityMatch(feature, series));
        Assert.NotEqual(
            AudiovisualLibraryKey.Create(
                feature.Kind,
                feature.TmdbId,
                feature.Title,
                feature.Year,
                feature.ContentForm).StableId,
            AudiovisualLibraryKey.Create(
                series.Kind,
                series.TmdbId,
                series.Title,
                series.Year,
                series.ContentForm).StableId);

        var hlsSource = new AudiovisualSourceViewModel(new AudiovisualSource
        {
            ProviderId = "authorized-test",
            ProviderName = "Authorized Test",
            Location = new Uri("https://93.184.216.34/master.m3u8"),
            AccessMode = AudiovisualSourceAccessMode.DirectMedia,
            Authorization = ProviderAuthorization.UserAuthorized,
            Rights = "User-authorized source",
            ContentType = "application/vnd.apple.mpegurl",
            Identity = series,
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }
        });
        Assert.True(hlsSource.CanPlay);
        Assert.True(hlsSource.CanDownload);
        Assert.True(hlsSource.CanWatchViaDownload);

        var dashSource = new AudiovisualSourceViewModel(hlsSource.Source with
        { Location = new Uri("https://93.184.216.34/video.mpd"), ContentType = "application/dash+xml" });
        Assert.True(dashSource.CanPlay);
        Assert.True(dashSource.CanWatchViaDownload);
        Assert.True(dashSource.CanDownload);

        var forgedBuiltInId = new AudiovisualSourceViewModel(hlsSource.Source with
        {
            ProviderId = "internet-archive",
            Location = new Uri("https://93.184.216.34/movie.mp4"),
            ContentType = "video/mp4"
        });
        Assert.True(forgedBuiltInId.CanPlay);
        Assert.True(forgedBuiltInId.CanDownload);

        var trustedBuiltIn = new AudiovisualSourceViewModel(forgedBuiltInId.Source with
        {
            Provenance = AudiovisualSourceProvenance.BuiltInInternetArchive
        });
        Assert.True(trustedBuiltIn.CanPlay);
        Assert.True(trustedBuiltIn.CanDownload);

        var trustedScraperHls = new AudiovisualSourceViewModel(hlsSource.Source with
        {
            Provenance = AudiovisualSourceProvenance.BuiltInScraper,
            UserAgent = "Scraper agent",
            Cookie = "session=secret",
            Referer = "https://embed.example/player",
            RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Origin"] = "https://embed.example"
            }
        });
        Assert.True(trustedScraperHls.CanPlay);
        Assert.True(trustedScraperHls.CanDownload);
        Assert.True(trustedScraperHls.CanWatchViaDownload);
    }

    [Fact]
    public void MainNavigation_ReplacesEveryNonAnimePlaceholderAndKeepsAnimeRouteUntouched()
    {
        string root = FindWorkspaceRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "UniversalMediaOS.WPF",
            "ViewModels",
            "MainViewModel.cs"));

        Assert.Contains("CreateScopedViewModel<SearchViewModel>()", source, StringComparison.Ordinal);
        Assert.Contains("CreateScopedViewModel<MovieCatalogViewModel>()", source, StringComparison.Ordinal);
        Assert.Contains("CreateScopedViewModel<TvCatalogViewModel>()", source, StringComparison.Ordinal);
        Assert.Contains("CreateScopedViewModel<CartoonCatalogViewModel>()", source, StringComparison.Ordinal);
        Assert.Contains("CreateScopedViewModel<BookBrowseViewModel>()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlaceholderMediaViewModel.Create(\"Movies\")",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlaceholderMediaViewModel.Create(\"Books\")",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlaceholderMediaViewModel.Create(\"TV Shows\")",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlaceholderMediaViewModel.Create(\"Cartoons\")",
            source,
            StringComparison.Ordinal);

        string audiovisualViewModel = File.ReadAllText(Path.Combine(
            root,
            "UniversalMediaOS.WPF",
            "ViewModels",
            "AudiovisualCatalogViewModel.cs"));
        Assert.Contains("ShowLibraryAsync", audiovisualViewModel, StringComparison.Ordinal);
        Assert.Contains("ShowFavoritesAsync", audiovisualViewModel, StringComparison.Ordinal);

        string bookView = File.ReadAllText(Path.Combine(
            root,
            "UniversalMediaOS.WPF",
            "Views",
            "BookBrowseView.xaml"));
        Assert.Contains("Continue Reading Books", bookView, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherMediaImplementation_DoesNotReferenceAnimeResolverTypes()
    {
        string root = FindWorkspaceRoot();
        string[] forbidden =
        {
            "FuzzyShieldSearch",
            "DubAvailabilityService",
            "TripleNetHandoff",
            "SeasonDownloader"
        };
        string[] files = Directory.GetFiles(
            Path.Combine(root, "UniversalMediaOS.Core", "OtherMedia"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            foreach (string typeName in forbidden)
            {
                Assert.DoesNotContain(typeName, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void BookReaderWebView_EnablesPdfViewerScriptsWhileBlockingRemoteResourcesAndUnrelatedLocalFiles()
    {
        string root = FindWorkspaceRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "UniversalMediaOS.WPF",
            "Views",
            "BookReaderView.xaml.cs"));

        Assert.Contains("core.Settings.IsScriptEnabled = _viewModel?.IsPdf == true", source, StringComparison.Ordinal);
        Assert.Contains("\"http://*/*\"", source, StringComparison.Ordinal);
        Assert.Contains("\"https://*/*\"", source, StringComparison.Ordinal);
        Assert.Contains("\"file://*/*\"", source, StringComparison.Ordinal);
        Assert.Contains("_allowedFile", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2PermissionState.Deny", source, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookRunBindings_AreExplicitlyOneWayForReadOnlyViewModelProperties()
    {
        string root = FindWorkspaceRoot();
        foreach (string relativePath in new[]
                 {
                     Path.Combine("UniversalMediaOS.WPF", "Views", "BookBrowseView.xaml"),
                     Path.Combine("UniversalMediaOS.WPF", "Views", "BookDetailsView.xaml")
                 })
        {
            string path = Path.Combine(root, relativePath);
            string[] bindingRuns = File.ReadAllLines(path)
                .Where(line => line.Contains("<Run Text=\"{Binding", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(bindingRuns);
            Assert.All(bindingRuns, line =>
                Assert.Contains("Mode=OneWay", line, StringComparison.Ordinal));
        }
    }

    private static string CreateTempRoot(string suffix)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"UniversalMediaOS.OtherMedia{suffix}Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string FindWorkspaceRoot()
    {
        foreach (string startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var candidate = new DirectoryInfo(startingPath);
            while (candidate != null)
            {
                if (File.Exists(Path.Combine(candidate.FullName, "UniversalMediaOS.sln")))
                {
                    return candidate.FullName;
                }

                candidate = candidate.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the UniversalMediaOS workspace root.");
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly string _contentType;
        private readonly Uri? _finalUri;

        public StaticResponseHandler(byte[] payload, string contentType, Uri? finalUri = null)
        {
            _payload = payload;
            _contentType = contentType;
            _finalUri = finalUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = _finalUri == null
                    ? request
                    : new HttpRequestMessage(HttpMethod.Get, _finalUri),
                Content = new ByteArrayContent(_payload)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            response.Content.Headers.ContentLength = _payload.Length;
            return Task.FromResult(response);
        }
    }
}
