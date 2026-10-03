using System.Text.Json;
using System.IO;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class AsyncLifecycleRegressionTests
    {
        [Fact]
        public async Task SearchCancellation_DoesNotApplyStaleResultsOrThrow()
        {
            using var sandbox = new ServiceSandbox();
            var searchService = new DelayedAnimeSearch();
            using var viewModel = new SearchViewModel(
                searchService,
                new DubAvailabilityService(sandbox.Config),
                new FavoriteMediaService());
            viewModel.SearchQuery = "old query";

            Task searchTask = viewModel.SearchCommand.ExecuteAsync(null);
            await searchService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.CancelActiveWork();
            searchService.Release.TrySetResult();

            Exception? exception = await Record.ExceptionAsync(() => searchTask);

            Assert.Null(exception);
            Assert.Empty(viewModel.SearchResults);
        }

        [Fact]
        public async Task MangaBackNavigation_DoesNotApplyStaleChaptersOrThrow()
        {
            var mangaService = new DelayedMangaService();
            using var viewModel = new MangaViewModel(mangaService);
            var manga = new MangaSearchResult { Id = "old", Title = "Old Manga" };

            Task readTask = viewModel.ReadCommand.ExecuteAsync(manga);
            await mangaService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.GoBackCommand.Execute(null);
            mangaService.Release.TrySetResult();

            Exception? exception = await Record.ExceptionAsync(() => readTask);

            Assert.Null(exception);
            Assert.Null(viewModel.SelectedManga);
            Assert.Equal(0, viewModel.CurrentViewMode);
            Assert.Empty(viewModel.Chapters);
        }

        [Fact]
        public async Task ExternalMangaChapter_ReleasesCompletedPageOperation()
        {
            using var viewModel = new MangaViewModel(new MangaService());
            var chapter = new MangaChapter
            {
                Id = "external",
                ChapterNumber = "1",
                ExternalUrl = "https://example.com/chapter/1"
            };

            await viewModel.SelectChapterCommand.ExecuteAsync(chapter);

            var pageCtsField = typeof(MangaViewModel).GetField(
                "_pageCts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(pageCtsField);
            Assert.Null(pageCtsField.GetValue(viewModel));
            Assert.Equal(3, viewModel.CurrentViewMode);
        }

        [Fact]
        public async Task DubAudioFilter_KeepsResultsWhenNoDubProviderConfigured()
        {
            using var sandbox = new ServiceSandbox();
            var searchService = new FixedAnimeSearch();
            using var viewModel = new SearchViewModel(
                searchService,
                new DubAvailabilityService(sandbox.Config),
                new FavoriteMediaService());

            viewModel.SearchQuery = "mock";
            viewModel.SelectedAudio = "Dub";

            await viewModel.SearchCommand.ExecuteAsync(null);

            var result = Assert.Single(viewModel.SearchResults);
            Assert.Equal("Mock Anime", result.OfficialTitle);
            Assert.False(result.DubAvailabilityChecked);
            Assert.Equal(0, result.AvailableDubEpisodes);
        }

        [Fact]
        public async Task FilterChangeDuringSearch_RunsLatestFilterAfterActiveSearchCompletes()
        {
            using var sandbox = new ServiceSandbox();
            var searchService = new FilterCoalescingAnimeSearch();
            using var viewModel = new SearchViewModel(
                searchService,
                new DubAvailabilityService(sandbox.Config),
                new FavoriteMediaService());
            viewModel.SearchQuery = "mock";

            Task firstSearch = viewModel.SearchCommand.ExecuteAsync(null);
            await searchService.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            viewModel.SelectedStatus = "Finished";
            await Task.Delay(350);
            Assert.Equal(1, Volatile.Read(ref searchService.CallCount));

            searchService.ReleaseFirst.TrySetResult();
            await firstSearch;
            await searchService.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("Finished", searchService.SecondFilters?.Status);
        }

        private sealed class DelayedAnimeSearch : FuzzyShieldSearch
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async Task<MediaSearchPage> SearchAnimePageAsync(
                string query,
                int page,
                int perPage,
                AnimeSearchFilters? filters = null,
                CancellationToken token = default)
            {
                Started.TrySetResult();
                await Release.Task;
                return new MediaSearchPage(
                    new List<MediaResult> { new() { Id = 1, OfficialTitle = "Stale result" } },
                    HasNextPage: false);
            }
        }

        private sealed class DelayedMangaService : MangaService
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async Task<List<MangaChapter>> GetChaptersAsync(
                string mangaId,
                CancellationToken token = default)
            {
                Started.TrySetResult();
                await Release.Task;
                return new List<MangaChapter>
                {
                    new() { Id = "stale", ChapterNumber = "1", Title = "Stale chapter" }
                };
            }
        }

        private sealed class FixedAnimeSearch : FuzzyShieldSearch
        {
            public override Task<MediaSearchPage> SearchAnimePageAsync(
                string query,
                int page,
                int perPage,
                AnimeSearchFilters? filters = null,
                CancellationToken token = default)
            {
                return Task.FromResult(new MediaSearchPage(
                    new List<MediaResult>
                    {
                        new()
                        {
                            Id = 1,
                            OfficialTitle = "Mock Anime",
                            AvailableSubEpisodes = 12,
                            TotalEpisodes = 12
                        }
                    },
                    HasNextPage: false));
            }
        }

        private sealed class FilterCoalescingAnimeSearch : FuzzyShieldSearch
        {
            public int CallCount;
            public TaskCompletionSource FirstStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ReleaseFirst { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public AnimeSearchFilters? SecondFilters { get; private set; }

            public override async Task<MediaSearchPage> SearchAnimePageAsync(
                string query,
                int page,
                int perPage,
                AnimeSearchFilters? filters = null,
                CancellationToken token = default)
            {
                int call = Interlocked.Increment(ref CallCount);
                if (call == 1)
                {
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(token);
                }
                else
                {
                    SecondFilters = filters;
                    SecondStarted.TrySetResult();
                }

                return new MediaSearchPage(
                    new List<MediaResult>
                    {
                        new()
                        {
                            Id = call,
                            OfficialTitle = $"Mock Anime {call}",
                            AvailableSubEpisodes = 1,
                            TotalEpisodes = 12
                        }
                    },
                    HasNextPage: false);
            }
        }

        private sealed class ServiceSandbox : IDisposable
        {
            private readonly string? _previousAppData;

            public ServiceSandbox()
            {
                _previousAppData = Environment.GetEnvironmentVariable("APPDATA");
                Root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
                string appDir = Path.Combine(Root, "UniversalMediaOS");
                string downloads = Path.Combine(Root, "Downloads");
                Directory.CreateDirectory(appDir);
                string configPath = Path.Combine(appDir, "config.json");
                File.WriteAllText(
                    configPath,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["DownloadDirectory"] = downloads,
                        ["AniListUrl"] = "http://127.0.0.1/unused",
                        ["DubAvailabilityProviders"] = "[]"
                    }));
                Environment.SetEnvironmentVariable("APPDATA", Root);
                Config = new DomainHotSwapper(configPath);
            }

            public string Root { get; }
            public DomainHotSwapper Config { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _previousAppData);
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
