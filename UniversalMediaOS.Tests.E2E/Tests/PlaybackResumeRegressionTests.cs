using System.Text.Json;
using System.IO;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class PlaybackResumeRegressionTests
    {
        [Theory]
        [InlineData("dub")]
        [InlineData("sub")]
        public void BrowserAudioIntentSurvivesRetryAndEpisodeContext(string audio)
        {
            using var sandbox = new AppDataSandbox();
            using var player = new PlaybackViewModel(new DatabaseContext());
            var context = new EpisodePlaybackContext(1, 12, (_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null), audio);
            player.LoadEmbed("https://example.invalid/episode", "Audio test", "1", episodeContext: context);
            Assert.Equal(audio, player.BrowserAudioPreference);
            player.RetryPlaybackCommand.Execute(null);
            Assert.Equal(audio, player.BrowserAudioPreference);
            player.LoadEmbed("https://example.invalid/movie", "Unrelated movie");
            Assert.Equal(string.Empty, player.BrowserAudioPreference);
        }

        [Fact]
        public void BrowserRetryRequestsNavigationAgainAfterResettingPlaybackState()
        {
            using var sandbox = new AppDataSandbox();
            using var player = new PlaybackViewModel(new DatabaseContext());
            var navigationStates = new List<(string Url, bool Busy, bool Error)>();
            player.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PlaybackViewModel.EmbedUrl))
                    navigationStates.Add((player.EmbedUrl, player.IsPlaybackBusy, player.HasPlaybackError));
            };
            player.LoadEmbed("https://example.invalid/episode", "Retry test", "1");
            player.ReportPlaybackError("Navigation failed");
            player.RetryPlaybackCommand.Execute(null);

            Assert.Equal(2, navigationStates.Count);
            Assert.All(navigationStates, state =>
            {
                Assert.Equal("https://example.invalid/episode", state.Url);
                Assert.True(state.Busy);
                Assert.False(state.Error);
            });
        }

        [Fact]
        public async Task AudiovisualResumeSurvivesSourceTitleAndPlayerChanges()
        {
            using var sandbox = new AppDataSandbox();
            var context = Context("tt0133093");
            using (var first = new PlaybackViewModel(new DatabaseContext()))
            {
                first.LoadEmbed("https://first.invalid/movie?token=old", "Old title", audiovisualContext: context);
                first.ReportWebPlaybackProgress(123, 600, ended: false);
                Assert.Equal(123, await WaitForResumeStateAsync(context.WorkKey, "feature", 123));
            }
            using var second = new PlaybackViewModel(new DatabaseContext());
            second.LoadMedia("https://second.invalid/new.mp4", "Changed title", audiovisualContext: context);
            second.LoadEmbed("https://third.invalid/embed", "Another title", audiovisualContext: context);
            ResumeDispatcherContentionTests.WaitForResumeLoad(second);
            Assert.True(second.TryConsumePendingWebResumePosition(out double position));
            Assert.Equal(123, position);
        }

        [Theory]
        [InlineData("tt1000001", 1, 1, 91)]
        [InlineData("tt1000001", 2, 1, 182)]
        [InlineData("tt1000002", 1, 1, 273)]
        public void ResumeSeparatesSeasonsAndSameTitleWorks(string id, int season, int episode, double expected)
        {
            using var sandbox = new AppDataSandbox();
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState("av:imdb:title:tt1000001", "season:1:episode:1", 91);
                seed.SaveResumeState("av:imdb:title:tt1000001", "season:2:episode:1", 182);
                seed.SaveResumeState("av:imdb:title:tt1000002", "season:1:episode:1", 273);
            }
            using var vm = new PlaybackViewModel(new DatabaseContext());
            vm.LoadEmbed("https://example.invalid/same", "Same title", episode.ToString(),
                audiovisualContext: Context(id, season, episode));
            ResumeDispatcherContentionTests.WaitForResumeLoad(vm);
            Assert.True(vm.TryConsumePendingWebResumePosition(out double actual));
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void UnresolvedAudiovisualUnitDoesNotGuessOrOverwriteLegacyProgress()
        {
            using var sandbox = new AppDataSandbox();
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState("title:same title", "1", 97);
            }
            var context = new AudiovisualPlaybackContext("av:local:unknown", new()
                { ContentForm = AudiovisualContentForm.Series }, AudiovisualUnit.Feature, "Same title", "", new());
            using var vm = new PlaybackViewModel(new DatabaseContext());
            vm.LoadEmbed("https://example.invalid/embed", "Same title", "1", audiovisualContext: context);
            ResumeDispatcherContentionTests.WaitForResumeLoad(vm);
            Assert.False(vm.TryConsumePendingWebResumePosition(out _));
            vm.ReportWebPlaybackProgress(100, 600, false);
            vm.ReportWebPlaybackProgress(600, 600, true);
            using var verify = new DatabaseContext();
            Assert.Equal(97, verify.GetResumeState("title:same title", "1"));
            Assert.Single(verify.ResumeStates.ToArray());
        }

        [Fact]
        public async Task CompletionCannotBeOverwrittenByAnOlderPendingWrite()
        {
            using var sandbox = new AppDataSandbox();
            var context = Context("tt0133093");
            var progress = new PlaybackProgressService(new(Path.Combine(sandbox.Root, "library.json")));
            var (session, _) = progress.Open(context);
            await progress.SaveAsync(Assert.IsType<PlaybackProgressWrite>(progress.Capture(session, 90, 600, false)));
            Assert.Equal(90, await WaitForResumeStateAsync(context.WorkKey, "feature", 90));
            var older = Assert.IsType<PlaybackProgressWrite>(progress.Capture(session, 180, 600, false));
            var completion = Assert.IsType<PlaybackProgressWrite>(progress.Capture(session, 600, 600, true));
            await progress.SaveAsync(completion);
            // Execute the real captured observation after the newer completion,
            // keeping the same session owner to exercise write ordering itself.
            await progress.SaveAsync(older);
            using var verify = new DatabaseContext();
            Assert.Equal(0, verify.GetResumeState(context.WorkKey, "feature"));
        }

        private static AudiovisualPlaybackContext Context(string id, int? season = null, int? episode = null)
        {
            var identity = new AudiovisualIdentity { PrimaryId = new("imdb", "title", id),
                ContentForm = season == null ? AudiovisualContentForm.Feature : AudiovisualContentForm.Series, Title = "Same title" };
            return new(AudiovisualIdentityKeys.CreateWorkKey(identity), identity,
                new() { SeasonNumber = season, EpisodeNumber = episode }, "Same title", "", new());
        }

        [Fact]
        public void AudiovisualContextSurvivesRetryAndResetsForUnrelatedPlayback()
        {
            using var sandbox = new AppDataSandbox();
            using var viewModel = new PlaybackViewModel(new DatabaseContext());
            var context = new UniversalMediaOS.Core.OtherMedia.AudiovisualPlaybackContext(
                "av:local:fixed", new() { ContentForm = UniversalMediaOS.Core.OtherMedia.AudiovisualContentForm.Feature },
                UniversalMediaOS.Core.OtherMedia.AudiovisualUnit.Feature, "Movie", "", new());
            viewModel.LoadEmbed("https://example.invalid/embed", "Movie", audiovisualContext: context);
            Assert.Same(context, viewModel.AudiovisualContext);
            viewModel.RetryPlaybackCommand.Execute(null);
            Assert.Same(context, viewModel.AudiovisualContext);
            viewModel.LoadMedia("https://example.invalid/file.mp4", "Movie", referer: "https://example.invalid/fallback",
                audiovisualContext: context);
            Assert.Same(context, viewModel.AudiovisualContext);
            viewModel.OpenWebFallbackCommand.Execute(null);
            Assert.True(viewModel.IsWebViewActive);
            Assert.Equal("https://example.invalid/fallback", viewModel.EmbedUrl);
            Assert.Same(context, viewModel.AudiovisualContext);
            viewModel.LoadEmbed("https://example.invalid/unrelated", "Unrelated");
            Assert.Null(viewModel.AudiovisualContext);
        }

        [Fact]
        public void LoadEmbed_ExposesSavedResumePosition()
        {
            using var sandbox = new AppDataSandbox();
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState("42", "3", 91.5);
            }

            using var viewModel = new PlaybackViewModel(new DatabaseContext());
            viewModel.LoadEmbed("https://example.invalid/embed", "Test Show - Ep 3", "3", malId: 42);

            ResumeDispatcherContentionTests.WaitForResumeLoad(viewModel);

            Assert.True(viewModel.TryConsumePendingWebResumePosition(out double seconds));
            Assert.Equal(91.5, seconds, precision: 1);
        }

        [Fact]
        public async Task WebPlaybackProgress_WritesResumeState()
        {
            using var sandbox = new AppDataSandbox();
            using var viewModel = new PlaybackViewModel(new DatabaseContext());
            viewModel.LoadEmbed("https://example.invalid/embed", "Test Show - Ep 3", "3", malId: 42);

            viewModel.ReportWebPlaybackProgress(90, 600, ended: false);

            double saved = await WaitForResumeStateAsync("42", "3", expected: 90);
            Assert.Equal(90, saved, precision: 1);
        }

        [Fact]
        public void WebPlaybackCompletion_ClearsResumeState()
        {
            using var sandbox = new AppDataSandbox();
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState("42", "3", 90);
            }

            using var viewModel = new PlaybackViewModel(new DatabaseContext());
            viewModel.LoadEmbed("https://example.invalid/embed", "Test Show - Ep 3", "3", malId: 42);
            viewModel.ReportWebPlaybackProgress(600, 600, ended: true);

            using var verify = new DatabaseContext();
            Assert.Equal(0, verify.GetResumeState("42", "3"));
        }

        private static async Task<double> WaitForResumeStateAsync(string mediaId, string episodeId, double expected)
        {
            var timeout = DateTime.UtcNow.AddSeconds(3);
            do
            {
                using var verify = new DatabaseContext();
                double value = verify.GetResumeState(mediaId, episodeId);
                if (Math.Abs(value - expected) < 0.1)
                {
                    return value;
                }

                await Task.Delay(25);
            }
            while (DateTime.UtcNow < timeout);

            using var finalVerify = new DatabaseContext();
            return finalVerify.GetResumeState(mediaId, episodeId);
        }

        private sealed class AppDataSandbox : IDisposable
        {
            private readonly string? _previousAppData;
            private readonly string? _previousDataRoot;

            public AppDataSandbox()
            {
                _previousAppData = Environment.GetEnvironmentVariable("APPDATA");
                _previousDataRoot = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
                Root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
                string appDir = Path.Combine(Root, "UniversalMediaOS");
                Directory.CreateDirectory(appDir);
                string databasePath = Path.Combine(Root, "resume.db");
                File.WriteAllText(
                    Path.Combine(appDir, "config.json"),
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["DatabasePath"] = databasePath }));
                Environment.SetEnvironmentVariable("APPDATA", Root);
                Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", Root);
                string overrideDirectory = Path.Combine(Root, "Roaming", "UniversalMediaOS");
                Directory.CreateDirectory(overrideDirectory);
                File.Copy(Path.Combine(appDir, "config.json"), Path.Combine(overrideDirectory, "config.json"));
            }

            public string Root { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _previousAppData);
                Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previousDataRoot);
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
