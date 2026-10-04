using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SeasonLibraryDownloadTests
{
    private static AudiovisualIdentity Identity => new()
    { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series, Title = "Series", Year = 2008,
        PrimaryId = new("tvmaze", "show", "169"), ImdbId = "tt0903747" };
    private static AudiovisualUnit Unit(int episode = 1) => new() { SeasonNumber = 1, EpisodeNumber = episode };
    private static AudiovisualSource Source(AudiovisualUnit unit) => new()
    {
        ProviderId = "fixture", AccessMode = AudiovisualSourceAccessMode.DirectMedia,
        Location = new($"https://93.184.216.34/episode-{unit.EpisodeNumber}.mp4?token=private-fixture-token"),
        ContentType = "video/mp4", Identity = Identity, Unit = unit,
        Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = Identity, Unit = unit },
        Cookie = "secret-cookie", RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "secret-header" }
    };
    private static AudiovisualPlaybackContext Context(AudiovisualSource source) =>
        new("av:tvmaze:show:169", Identity, source.Unit, "Series", "", source);
    private static AudiovisualEpisodeMetadata Episode(int episode) => new(new("tvmaze", "episode", (12190 + episode).ToString()),
        $"Episode {episode}", 1, Unit(episode), false);

    [Fact]
    public async Task DownloadsReopensPublishedEpisodeWithCanonicalContextAndOwnedCaptions()
    {
        using var fixture = new Fixture();
        var source = Source(Unit()) with { Subtitles = [new("https://93.184.216.34/captions.vtt")
            { InlineVtt = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nSaved caption\n", Language = "en" }] };
        var saved = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        using var queue = new DownloadQueueService(Path.Combine(fixture.Permanent, "queue.json"), new IdleExecutor());
        var downloads = new DownloadsViewModel(queue, fixture.Config, new Dialog(), new Launcher(), new LocalBookImportService());
        bool plainCallback = false;
        downloads.RegisterPlayMediaAction((_, _) => plainCallback = true);
        await downloads.RefreshDownloadsCommand.ExecuteAsync(null);
        var row = Assert.Single(downloads.InstalledFiles);
        Assert.Contains("S01E01", row.FileName);
        Assert.Contains("Series", row.FileName);
        Assert.Equal(saved.FilePath, row.FullPath);
        object recipient = new();
        PlayMediaMessage? handoff = null;
        WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) => handoff = message);
        try
        {
            await downloads.PlayFileCommand.ExecuteAsync(row);
            Assert.NotNull(handoff);
            Assert.Equal("av:tvmaze:show:169", handoff.AudiovisualContext!.WorkKey);
            Assert.Equal("season:1:episode:1", handoff.AudiovisualContext.UnitKey);
            Assert.Contains("S01E01", handoff.Title);
            Assert.Equal("1", handoff.EpisodeNumber);
            Assert.Equal(saved.CaptionPaths, handoff.LocalCaptionPaths);
            Assert.Contains("unverified", handoff.AudioNotice);
            Assert.False(handoff.IsWebView);
            Assert.Null(handoff.TemporaryWatchLease);
            Assert.False(plainCallback);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task DownloadsHidesOwnedUnpublishedCopiesAndKeepsOrdinaryLocalFileRouting()
    {
        using var fixture = new Fixture();
        string staging = Path.Combine(fixture.Permanent, ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "media.mp4"), [1]);
        string userFolder = Path.Combine(fixture.Permanent, ".partial-user-files");
        Directory.CreateDirectory(userFolder);
        string userFile = Path.Combine(userFolder, "my-film.mp4");
        await File.WriteAllBytesAsync(userFile, [1]);
        using var queue = new DownloadQueueService(Path.Combine(fixture.Permanent, "queue.json"), new IdleExecutor());
        var downloads = new DownloadsViewModel(queue, fixture.Config, new Dialog(), new Launcher(), new LocalBookImportService());
        (string Path, string Title)? opened = null;
        downloads.RegisterPlayMediaAction((path, title) => opened = (path, title));
        await downloads.RefreshDownloadsCommand.ExecuteAsync(null);
        var row = Assert.Single(downloads.InstalledFiles);
        Assert.Equal("my-film.mp4", row.FileName);
        await downloads.PlayFileCommand.ExecuteAsync(row);
        Assert.Equal((userFile, "my-film.mp4"), opened);
        Assert.True(File.Exists(Path.Combine(staging, "media.mp4")));
    }

    [Fact]
    public async Task PublishedLibraryFilesKeepCanonicalIdentityAndSurviveAllTemporaryOwners()
    {
        using var fixture = new Fixture();
        var source = Source(Unit()) with { Subtitles = [new("https://93.184.216.34/captions.vtt") { InlineVtt = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nA saved caption\n", Language = "en" }] };
        var first = await fixture.Download.DownloadTemporaryAsync(source, Context(source));
        using var owner = first.Lease;
        var saved = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(saved.FilePath));
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(saved.FilePath));
        Assert.True(File.Exists(Assert.Single(saved.CaptionPaths)));
        using var metadata = JsonDocument.Parse(File.ReadAllText(saved.MetadataPath));
        Assert.Equal("av:tvmaze:show:169", metadata.RootElement.GetProperty("WorkKey").GetString());
        Assert.Equal(1, metadata.RootElement.GetProperty("Unit").GetProperty("SeasonNumber").GetInt32());
        Assert.Equal(1, metadata.RootElement.GetProperty("Unit").GetProperty("EpisodeNumber").GetInt32());
        Assert.Contains("unverified", metadata.RootElement.GetProperty("AudioNotice").GetString()!);
        string text = File.ReadAllText(saved.MetadataPath);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("private-fixture-token", text);
        var reopened = Assert.IsType<LibraryMediaPlayback>(AuthorizedMediaDownloadService.ReadLibraryPlayback(saved.FilePath));
        Assert.Equal(Context(source).WorkKey, reopened.Context.WorkKey);
        Assert.Equal(Context(source).UnitKey, reopened.Context.UnitKey);
        Assert.Equal(saved.CaptionPaths, reopened.CaptionPaths);
        first.Lease.Dispose();
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.True(File.Exists(saved.FilePath));
        Assert.True(File.Exists(saved.CaptionPaths[0]));
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("work")]
    [InlineData("echo")]
    [InlineData("audio")]
    [InlineData("website")]
    public async Task ConflictingOrUnknownLibrarySourcesNeverTransferOrPublish(string failure)
    {
        using var fixture = new Fixture();
        var source = Source(Unit());
        if (failure == "episode") source = source with { Evidence = source.Evidence! with { Unit = Unit(2) } };
        if (failure == "work") source = source with { Evidence = source.Evidence! with { Identity = Identity with { ImdbId = "tt0944947" } } };
        if (failure == "echo") source = source with { Evidence = source.Evidence! with { Origin = SourceEvidenceOrigin.RequestEcho } };
        if (failure == "website") source = source with { AccessMode = AudiovisualSourceAccessMode.WebPage };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Download.DownloadLibraryAsync(source, Context(source),
            preferredLanguage: failure == "audio" ? "en" : null));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Empty(fixture.Published());
    }

    [Fact]
    public async Task LibraryDestinationInsideTemporaryCacheIsRejectedWithoutTransfer()
    {
        using var fixture = new Fixture();
        fixture.Config.SetSetting("DownloadDirectory", Path.Combine(fixture.Temporary, "user-folder"));
        var source = Source(Unit());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Download.DownloadLibraryAsync(source, Context(source)));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task RevisionAndUnitSavesNeverOverwriteEachOtherOrExistingUserFiles()
    {
        using var fixture = new Fixture();
        var source = Source(Unit());
        var one = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        string userFile = Path.Combine(Path.GetDirectoryName(one.FilePath)!, "keep.txt"); File.WriteAllText(userFile, "user data");
        var again = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        var twoSource = Source(Unit(2));
        var two = await fixture.Download.DownloadLibraryAsync(twoSource, Context(twoSource));
        Assert.Equal(3, new[] { one.FilePath, again.FilePath, two.FilePath }.Distinct().Count());
        Assert.All(new[] { one.FilePath, again.FilePath, two.FilePath }, path => Assert.True(File.Exists(path)));
        Assert.Equal("user data", File.ReadAllText(userFile));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("caption-path")]
    [InlineData("version")]
    [InlineData("different-file")]
    [InlineData("oversize")]
    [InlineData("json")]
    public async Task InvalidOrUnrelatedSavedMetadataCannotReassignLocalPlayback(string failure)
    {
        using var fixture = new Fixture();
        var source = Source(Unit());
        var saved = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(saved.MetadataPath))!;
        if (failure == "unit") json["UnitKey"] = "season:1:episode:2";
        if (failure == "caption-path") json["CaptionFiles"] = new System.Text.Json.Nodes.JsonArray("../outside.en.vtt");
        if (failure == "version") json["SchemaVersion"] = 99;
        if (failure == "different-file") json["MediaFile"] = "other.mp4";
        File.WriteAllText(saved.MetadataPath, failure switch
        { "oversize" => new string(' ', 65537), "json" => "{invalid", _ => json.ToJsonString() });
        Assert.Null(AuthorizedMediaDownloadService.ReadLibraryPlayback(saved.FilePath));
        Assert.True(File.Exists(saved.FilePath));
    }

    [Fact]
    public async Task CancellationBeforePublishRemovesOnlyTheOwnedStagingAndTemporaryJob()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, _) => { started.TrySetResult(); return probe.Task; });
        Directory.CreateDirectory(fixture.Permanent);
        string userFile = Path.Combine(fixture.Permanent, "keep.txt"); File.WriteAllText(userFile, "user data");
        using var cancellation = new CancellationTokenSource();
        var source = Source(Unit());
        var save = fixture.Download.DownloadLibraryAsync(source, Context(source), token: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); probe.SetResult("Audio unverified");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Empty(fixture.Published());
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.Empty(Directory.GetDirectories(fixture.Permanent, ".partial-*", SearchOption.AllDirectories));
        Assert.Equal("user data", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task NumberedSeasonSavesInOrderWithIndependentUnitsAndPreservesPlaybackProgress()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = [Episode(2), Episode(1)];
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        Assert.True(fixture.ViewModel.DownloadSeasonCommand.CanExecute(null));
        var key = AudiovisualLibraryKey.Create(Identity);
        await fixture.Library.RecordProgressAsync(key, "Series", "", 2, 1, 371, 2800);
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 1, 2 }, fixture.Catalog.Requests.Select(request => request.Unit!.EpisodeNumber!.Value));
        Assert.Equal(2, fixture.Published().Length);
        Assert.Contains("Saved 2 episodes", fixture.ViewModel.DownloadStatusText);
        Assert.False(fixture.ViewModel.IsDownloading);
        var progress = await fixture.Library.GetAsync(key);
        Assert.Equal(2, progress!.LastSeasonNumber);
        Assert.Equal(371, progress.PositionSeconds);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        fixture.Catalog.Requests.Clear();
        int transfers = fixture.Handler.Requests;
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Catalog.Requests);
        Assert.Equal(transfers, fixture.Handler.Requests);
        Assert.Equal(2, fixture.Published().Length);
    }

    [Fact]
    public async Task ExistingUnknownAudioOrChangedFileCannotSatisfyARequestedLanguageOrCompletedUnit()
    {
        using var fixture = new Fixture();
        var source = Source(Unit());
        var saved = await fixture.Download.DownloadLibraryAsync(source, Context(source));
        Assert.NotNull(await fixture.Download.FindLibraryDownloadAsync(Context(source)));
        Assert.Null(await fixture.Download.FindLibraryDownloadAsync(Context(source), preferredLanguage: "en"));
        File.AppendAllText(saved.FilePath, "changed");
        Assert.Null(await fixture.Download.FindLibraryDownloadAsync(Context(source)));
        Assert.True(File.Exists(saved.FilePath));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("special")]
    public async Task IncompleteOrAmbiguousSeasonListsDoNotStart(string failure)
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = failure switch
        {
            "unknown" => [Episode(1) with { Unit = null }],
            "duplicate" => [Episode(1), Episode(1) with { Id = new("tvmaze", "episode", "999") }],
            "special" => [Episode(1) with { SeasonNumber = 0, Unit = new() { SeasonNumber = 0, EpisodeNumber = 1 }, IsSpecial = true }],
            _ => [Episode(1)]
        };
        fixture.Catalog.Partial = failure == "partial";
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        Assert.False(fixture.ViewModel.DownloadSeasonCommand.CanExecute(null));
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Catalog.Requests);
        Assert.Empty(fixture.Published());
    }

    [Fact]
    public async Task WrongLaterEpisodeStopsTheSeasonAndKeepsEarlierCompletedSave()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = [Episode(1), Episode(2), Episode(3)];
        fixture.Catalog.Resolve = request => request.Unit!.EpisodeNumber == 2 ? Source(Unit(3)) : Source(request.Unit);
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Single(fixture.Published());
        Assert.Equal(2, fixture.Catalog.Requests.Count);
        Assert.Contains("Stopped at S01E02", fixture.ViewModel.DownloadStatusText);
        Assert.Contains("1/3 completed", fixture.ViewModel.DownloadStatusText);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("season")]
    [InlineData("title")]
    [InlineData("language")]
    public async Task CancelledOrChangedSelectionRejectsAnUncooperativeLateSource(string change)
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = [Episode(1), Episode(2)];
        var pending = new TaskCompletionSource<AudiovisualSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Catalog.Pending = request => { started.TrySetResult(); return pending.Task; };
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        var job = fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "season": fixture.ViewModel.SelectedSeason = new(2); break;
            case "title": fixture.ViewModel.SelectedItem = new(new() { Title = "Other", Identity = Identity with { ImdbId = "tt0944947" } }); break;
            case "language": fixture.ViewModel.PreferredLanguage = "English"; break;
            default: fixture.ViewModel.CancelDownloadCommand.Execute(null); break;
        }
        pending.SetResult(Source(Unit()));
        await job;
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Empty(fixture.Published());
        Assert.False(fixture.ViewModel.IsDownloading);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("truncated")]
    [InlineData("invalid")]
    [InlineData("timeout")]
    public async Task FailedEpisodeSourceUsesAnotherVerifiedSourceAndKeepsUnitsAndProgress(string failure)
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = [Episode(1), Episode(2)];
        fixture.Catalog.Candidates = request => [Source(request.Unit!), Alternate(request.Unit!)];
        fixture.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/episode-1.mp4"
            ? Task.FromException<HttpResponseMessage>(failure switch
            {
                "truncated" => new EndOfStreamException("Remote body ended early."),
                "invalid" => new InvalidDataException("Remote media was invalid."),
                "timeout" => new TimeoutException("Remote body stalled."),
                _ => new HttpRequestException("Remote source is unavailable.", null, HttpStatusCode.NotFound)
            }) : Task.FromResult(Handler.MediaResponse(request));
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        var key = AudiovisualLibraryKey.Create(Identity);
        await fixture.Library.RecordProgressAsync(key, "Series", "", 2, 1, 371, 2800);
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "/episode-1.mp4", "/alternate-episode-1.mp4", "/episode-2.mp4" },
            fixture.Handler.Locations.Select(location => location.AbsolutePath));
        var saved = fixture.Published().Select(path => AuthorizedMediaDownloadService.ReadLibraryPlayback(
            Path.Combine(Path.GetDirectoryName(path)!, "media.mp4"))).ToArray();
        Assert.Equal(2, saved.Length);
        Assert.Contains(saved, value => value!.Context.UnitKey == "season:1:episode:1" && value.Context.ProviderId == "alternate");
        Assert.Contains(saved, value => value!.Context.UnitKey == "season:1:episode:2" && value.Context.ProviderId == "fixture");
        Assert.Contains("Saved 2 episodes", fixture.ViewModel.DownloadStatusText);
        Assert.Equal(371, (await fixture.Library.GetAsync(key))!.PositionSeconds);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.Empty(Directory.GetDirectories(fixture.Permanent, ".partial-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RotatedUrlsAndConflictingOrUnknownAlternativesCannotBypassTheRequestedUnitAndAudio()
    {
        using var fixture = new Fixture();
        static AudiovisualSource English(AudiovisualSource source) => source with
        { Evidence = source.Evidence! with { Audio = new() { Origin = SourceEvidenceOrigin.ObservedStream, Languages = ["en"] } } };
        fixture.Catalog.Candidates = request =>
        {
            var original = English(Source(request.Unit!));
            return [original with { Location = new(original.Location.GetLeftPart(UriPartial.Path) + "?token=rotated-" + fixture.Catalog.Requests.Count) },
                English(Alternate(Unit(2))) with { ProviderId = "wrong-unit" },
                English(Alternate(request.Unit!)) with { ProviderId = "request-echo", Evidence = original.Evidence! with { Origin = SourceEvidenceOrigin.RequestEcho } },
                Alternate(request.Unit!) with { ProviderId = "unknown-audio" },
                English(Alternate(request.Unit!)) with { ProviderId = "website", AccessMode = AudiovisualSourceAccessMode.WebPage },
                English(Alternate(request.Unit!))];
        };
        fixture.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/episode-1.mp4"
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Unavailable."))
            : Task.FromResult(Handler.MediaResponse(request));
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        fixture.ViewModel.PreferredLanguage = "English";
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.Equal(2, fixture.Catalog.Requests.Count);
        string metadata = Assert.Single(fixture.Published());
        using var json = JsonDocument.Parse(File.ReadAllText(metadata));
        Assert.Equal("alternate", json.RootElement.GetProperty("SourceProvider").GetString());
        Assert.Equal("season:1:episode:1", json.RootElement.GetProperty("UnitKey").GetString());
        Assert.DoesNotContain("token=", File.ReadAllText(metadata));
    }

    [Fact]
    public async Task ThreeUnavailableSourcesStopAtTheFailedEpisodeAndRetainEarlierCompletedFiles()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Items = [Episode(1), Episode(2), Episode(3)];
        fixture.Catalog.Candidates = request => Enumerable.Range(0, 4).Select(index => Source(request.Unit!) with
        { ProviderId = "source-" + index, Location = new($"https://93.184.216.34/source-{index}/episode-{request.Unit!.EpisodeNumber}.mp4") }).ToArray();
        fixture.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("episode-2.mp4")
            ? Task.FromException<HttpResponseMessage>(new EndOfStreamException("Remote resource is empty."))
            : Task.FromResult(Handler.MediaResponse(request));
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Single(fixture.Published());
        Assert.Equal(4, fixture.Handler.Requests);
        Assert.Equal(new int?[] { 1, 2, 2, 2 }, fixture.Catalog.Requests.Select(request => request.Unit!.EpisodeNumber));
        Assert.Contains("Stopped at S01E02", fixture.ViewModel.DownloadStatusText);
        Assert.Contains("1/3 completed", fixture.ViewModel.DownloadStatusText);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
        Assert.Empty(Directory.GetDirectories(fixture.Permanent, ".partial-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("access")]
    [InlineData("service")]
    public async Task LocalStorageOrServiceFailureDoesNotDownloadFromMoreProviders(string failure)
    {
        using var fixture = new Fixture((_, _) => Task.FromException<string>(failure switch
        {
            "access" => new UnauthorizedAccessException("Local file cannot be opened."),
            "service" => new InvalidOperationException("Repair FFmpeg services."),
            _ => new IOException("Not enough free space.")
        }));
        fixture.Catalog.Candidates = request => [Source(request.Unit!), Alternate(request.Unit!)];
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Single(fixture.Catalog.Requests);
        Assert.Empty(fixture.Published());
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("season")]
    [InlineData("title")]
    [InlineData("language")]
    public async Task CancellationOrSelectionChangeDuringSourceFailureCannotStartAnAlternative(string change)
    {
        using var fixture = new Fixture();
        fixture.Catalog.Candidates = request => [Source(request.Unit!), Alternate(request.Unit!)];
        fixture.Handler.Respond = (_, _) =>
        {
            switch (change)
            {
                case "season": fixture.ViewModel.SelectedSeason = new(2); break;
                case "title": fixture.ViewModel.SelectedItem = new(new() { Title = "Other", Identity = Identity with { ImdbId = "tt0944947" } }); break;
                case "language": fixture.ViewModel.PreferredLanguage = "English"; break;
                default: fixture.ViewModel.CancelDownloadCommand.Execute(null); break;
            }
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Source failed during cancellation."));
        };
        await fixture.ViewModel.OpenItemAsync(new() { Title = "Series", Identity = Identity });
        await fixture.ViewModel.DownloadSeasonCommand.ExecuteAsync(null);
        Assert.Single(fixture.Catalog.Requests);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(fixture.Published());
        Assert.False(fixture.ViewModel.IsDownloading);
        Assert.Empty(Directory.GetDirectories(fixture.Temporary));
    }

    private static AudiovisualSource Alternate(AudiovisualUnit unit) => Source(unit) with
    { ProviderId = "alternate", Location = new($"https://93.184.216.34/alternate-episode-{unit.EpisodeNumber}.mp4") };

    private sealed class Catalog : IAudiovisualCatalogService, IAudiovisualEpisodeMetadataClient, IAudiovisualSourceUpdates
    {
        public AudiovisualMediaKind Kind => AudiovisualMediaKind.Television;
        public IReadOnlyList<AudiovisualEpisodeMetadata> Items { get; set; } = [Episode(1)];
        public bool Partial { get; set; }
        public Func<SourceSearchRequest, AudiovisualSource> Resolve { get; set; } = request => Source(request.Unit!);
        public Func<SourceSearchRequest, IReadOnlyList<AudiovisualSource>>? Candidates { get; set; }
        public Func<SourceSearchRequest, Task<AudiovisualSource>>? Pending { get; set; }
        public List<SourceSearchRequest> Requests { get; } = [];
        public Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(string query, CancellationToken token = default) => Task.FromResult<IReadOnlyList<AudiovisualMediaItem>>([]);
        public Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(CancellationToken token = default) => SearchAsync("", token);
        public Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(AudiovisualIdentity identity, AudiovisualUnit? unit = null, string? preferredLanguage = null, CancellationToken token = default) => throw new InvalidOperationException("Use progressive sources");
        public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity, int? season = null, CancellationToken token = default) => Task.FromResult(new AudiovisualUnitsResult(Items, [], Partial));
        public async IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request);
            // Deliberately ignore cancellation to test the consumer's late-result guard.
            var sources = Candidates?.Invoke(request) ?? [Pending == null ? Resolve(request) : await Pending(request)];
            foreach (var source in sources)
                yield return new(request.OperationId, "fixture", AudiovisualSourceUpdateKind.SourceReady, source,
                    new(SourceVerificationStatus.Verified, "fixture"));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.SeasonLibraryTests", Guid.NewGuid().ToString("N"));
        public string Temporary => Path.Combine(_root, "temporary");
        public string Permanent => Path.Combine(_root, "permanent");
        public Catalog Catalog { get; } = new();
        public Handler Handler { get; } = new();
        private readonly HttpClient _http;
        public DomainHotSwapper Config { get; }
        public AuthorizedMediaDownloadService Download { get; }
        public AudiovisualLibraryService Library { get; }
        public TestViewModel ViewModel { get; }
        public Fixture(Func<string, CancellationToken, Task<string>>? probe = null)
        {
            Directory.CreateDirectory(_root); _http = new(Handler);
            Config = new(Path.Combine(_root, "config.json")); Config.SetSetting("DownloadDirectory", Permanent);
            Download = new(_http, Config, Temporary, probe ?? ((_, _) => Task.FromResult("Audio language unverified")));
            Library = new(Path.Combine(_root, "library.json"));
            ViewModel = new(Catalog, Library, Download, new(_http), new Dialog(), new Launcher());
        }
        public string[] Published() => Directory.Exists(Permanent) ? Directory.GetFiles(Permanent, "library-item.json", SearchOption.AllDirectories) : [];
        public void Dispose()
        {
            ViewModel.Dispose(); _http.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.SeasonLibraryTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<Uri> Locations { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Locations.Add(request.RequestUri!);
            return Respond?.Invoke(request, token) ?? Task.FromResult(MediaResponse(request));
        }
        public static HttpResponseMessage MediaResponse(HttpRequestMessage request)
        {
            var content = new ByteArrayContent([1, 2, 3]); content.Headers.ContentType = new("video/mp4");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }
    private sealed class TestViewModel(IAudiovisualCatalogService catalog, AudiovisualLibraryService library,
        AuthorizedMediaDownloadService download, OtherMediaSourceSafetyService safety, IDialogService dialog, IExternalLauncher launcher)
        : AudiovisualCatalogViewModel(catalog, library, download, safety, dialog, launcher);
    private sealed class Dialog : IDialogService
    {
        public (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection() => (false, SelectedSourceTier.None);
        public bool ShowConfirmDialog(string message, string title) => false;
        public void ShowErrorDialog(string message, string title) => throw new InvalidOperationException(message);
        public void ShowInfoDialog(string message, string title) { }
    }
    private sealed class Launcher : IExternalLauncher
    {
        public bool OpenUrl(string url) => false;
        public bool OpenFolder(string path) => false;
        public bool OpenFile(string path) => false;
    }
    private sealed class IdleExecutor : IDownloadJobExecutor
    {
        public Task<DownloadExecutionResult> ExecuteAsync(DownloadQueueJob job, Action<string> log,
            Action<double> progress, CancellationToken token) => Task.FromResult(new DownloadExecutionResult(false));
        public Task<bool> PauseActiveAsync(CancellationToken token) => Task.FromResult(true);
        public Task<bool> CancelActiveAsync(CancellationToken token) => Task.FromResult(true);
    }
}
