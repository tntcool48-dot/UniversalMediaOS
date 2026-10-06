using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MonoTorrent.BEncoding;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class LegacyTorrentReadinessTests
{
    [Fact]
    public async Task RefreshAndStalePlayRejectOwnedLegacyPartialWithoutChangingBytesOrResumeCache()
    {
        using var fixture = new Fixture();
        string partial = fixture.Add("legacy-episode.mkv", complete: false);
        string completed = fixture.Add("completed-episode.mkv", complete: true);
        string ordinary = Path.Combine(fixture.Downloads, "user-film.mkv");
        File.WriteAllText(ordinary, "unrelated permanent file");
        var before = fixture.Snapshot();
        using var queue = new DownloadQueueService(Path.Combine(fixture.Root, "queue.json"), new IdleExecutor());
        var dialog = new Dialog();
        var viewModel = new DownloadsViewModel(queue, fixture.Config, dialog, new Launcher(), new LocalBookImportService());
        string? played = null;
        viewModel.RegisterPlayMediaAction((path, _) => played = path);
        await viewModel.RefreshDownloadsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(viewModel.InstalledFiles, file => file.FullPath == partial);
        Assert.Equal(new[] { completed, ordinary }.Order(), viewModel.InstalledFiles.Select(file => file.FullPath).Order());
        await viewModel.PlayFileCommand.ExecuteAsync(new InstalledEpisodeItem { FullPath = partial, FileName = "old visible row" });
        Assert.Null(played);
        Assert.Contains("partial", Assert.Single(dialog.Errors), StringComparison.OrdinalIgnoreCase);
        await viewModel.PlayFileCommand.ExecuteAsync(viewModel.InstalledFiles.Single(file => file.FullPath == completed));
        Assert.Equal(completed, played);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task PublishedLibraryEntryRemainsVisibleThroughConflictingOldTorrentCache()
    {
        using var fixture = new Fixture();
        string file = fixture.Add("published-episode.mkv", complete: false);
        File.WriteAllText(Path.Combine(fixture.Downloads, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, MediaFile = Path.GetFileName(file), WorkKey = "av:fixture:legacy", Title = "Published episode",
            Identity = new AudiovisualIdentity { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series, Title = "Published series" },
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }, UnitKey = "season:1:episode:1",
            CaptionFiles = Array.Empty<string>(), SourceProvider = "fixture", AudioNotice = "Audio language unverified."
        }));
        Assert.NotNull(AuthorizedMediaDownloadService.ReadLibraryPlayback(file));
        var before = fixture.Snapshot();
        using var queue = new DownloadQueueService(Path.Combine(fixture.Root, "queue.json"), new IdleExecutor());
        var downloads = new DownloadsViewModel(queue, fixture.Config, new Dialog(), new Launcher(), new LocalBookImportService());
        await downloads.RefreshDownloadsCommand.ExecuteAsync(null);
        Assert.Equal(file, Assert.Single(downloads.InstalledFiles).FullPath);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("partial", true)]
    [InlineData("truncated", true)]
    [InlineData("multi-file", true)]
    [InlineData("complete-with-stale-resume", false)]
    [InlineData("unrelated-bytes", false)]
    [InlineData("wrong-infohash", false)]
    [InlineData("invalid-bitfield", false)]
    [InlineData("corrupt-resume", false)]
    [InlineData("missing-resume", false)]
    [InlineData("unsafe-name", false)]
    public void CacheClaimsAloneCannotHideCompleteUnrelatedOrUnownedFiles(string scenario, bool expected)
    {
        using var fixture = new Fixture();
        string file = fixture.Add("episode.mkv", scenario == "complete-with-stale-resume", scenario);
        var before = fixture.Snapshot();
        Assert.Equal(expected, LegacyTorrentReadiness.FindIncompleteFiles(fixture.Downloads, fixture.Cache).Contains(file));
        Assert.Equal(before, fixture.Snapshot());
    }

    private sealed class Fixture : IDisposable
    {
        private const int PieceLength = 16_384;
        private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.LegacyReadinessTests", Guid.NewGuid().ToString("N"));
        public string Downloads => Path.Combine(Root, "Downloads");
        public string Cache => Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TorrentCache");
        public DomainHotSwapper Config { get; }
        public Fixture()
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Root);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(Path.Combine(Cache, "metadata"));
            Directory.CreateDirectory(Path.Combine(Cache, "fastresume"));
            Config = new(Path.Combine(Root, "config.json"));
            Config.SetSetting("DownloadDirectory", Downloads);
        }
        public string Add(string name, bool complete, string scenario = "partial")
        {
            bool multi = scenario == "multi-file";
            byte[] bytes = new byte[PieceLength * (multi ? 6 : 3)];
            new Random(123).NextBytes(bytes);
            byte[] pieces = Enumerable.Range(0, bytes.Length / PieceLength).SelectMany(index => SHA1.HashData(bytes.AsSpan(index * PieceLength, PieceLength))).ToArray();
            var info = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(scenario == "unsafe-name" ? "../episode.mkv" : name),
                ["length"] = new BEncodedNumber(bytes.Length), ["piece length"] = new BEncodedNumber(PieceLength),
                ["pieces"] = new BEncodedString(pieces)
            };
            if (multi)
            {
                info["name"] = new BEncodedString("Batch");
                info.Remove("length");
                info["files"] = new BEncodedList
                {
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(PieceLength * 3), ["path"] = new BEncodedList { new BEncodedString(name) } },
                    new BEncodedDictionary { ["length"] = new BEncodedNumber(PieceLength * 3), ["path"] = new BEncodedList { new BEncodedString("adjacent.mkv") } }
                };
            }
            byte[] hash = SHA1.HashData(info.Encode());
            string hashName = Convert.ToHexString(hash);
            File.WriteAllBytes(Path.Combine(Cache, "metadata", hashName + ".torrent"), new BEncodedDictionary { ["info"] = info }.Encode());
            var resume = new BEncodedDictionary
            {
                ["version"] = new BEncodedNumber(2), ["infohash"] = new BEncodedString(scenario == "wrong-infohash" ? new byte[20] : hash),
                ["bitfield_length"] = new BEncodedNumber(scenario == "invalid-bitfield" ? 100 : multi ? 6 : 3),
                ["bitfield"] = new BEncodedString(new byte[] { multi ? (byte)144 : (byte)128 }), ["unhashed_pieces"] = new BEncodedString(new byte[] { 0 })
            };
            string resumePath = Path.Combine(Cache, "fastresume", hashName + ".fresume");
            if (scenario != "missing-resume") File.WriteAllBytes(resumePath,
                scenario == "corrupt-resume" ? [1, 2, 3] : resume.Encode());
            if (!complete) Array.Clear(bytes, PieceLength, PieceLength * 2);
            if (multi) Array.Clear(bytes, PieceLength * 4, PieceLength * 2);
            if (scenario == "truncated") Array.Resize(ref bytes, PieceLength);
            if (scenario == "unrelated-bytes") Array.Clear(bytes);
            string path = Path.Combine(multi ? Path.Combine(Downloads, "Batch") : Downloads, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, multi ? bytes[..(PieceLength * 3)] : bytes);
            if (multi) File.WriteAllBytes(Path.Combine(Downloads, "Batch", "adjacent.mkv"), bytes[(PieceLength * 3)..]);
            return path;
        }
        public string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => path.StartsWith(Cache, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Downloads, StringComparison.OrdinalIgnoreCase))
            .Order().Select(path => path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _oldRoot);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.LegacyReadinessTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root, true);
        }
    }
    private sealed class Dialog : IDialogService
    {
        public List<string> Errors { get; } = [];
        public (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection() => (false, SelectedSourceTier.None);
        public bool ShowConfirmDialog(string message, string title) => false;
        public void ShowErrorDialog(string message, string title) => Errors.Add(message);
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
        public Task<DownloadExecutionResult> ExecuteAsync(DownloadQueueJob job, Action<string> log, Action<double> progress, CancellationToken token) => Task.FromResult(new DownloadExecutionResult(false));
        public Task<bool> PauseActiveAsync(CancellationToken token) => Task.FromResult(true);
        public Task<bool> CancelActiveAsync(CancellationToken token) => Task.FromResult(true);
    }
}
