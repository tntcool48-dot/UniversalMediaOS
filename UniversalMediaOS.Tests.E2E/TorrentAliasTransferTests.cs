using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Helpers;
using Xunit;
using LocalSeeder = UniversalMediaOS.Tests.E2E.NativeTemporaryDownloadBoundaryTests.LocalSeeder;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>Real loopback season transfers through folder aliases, and retargeting while the engine writes.</summary>
public sealed class TorrentAliasTransferTests
{
    private static readonly string Episode = Path.Combine("source", LocalSeeder.Title + " - S01E01.mkv");

    [Theory]
    [InlineData("junction")]
    [InlineData("subst")]
    public async Task PermanentSeasonTransfersThroughAFolderAliasIntoItsPhysicalFolder(string form)
    {
        await using var seeder = await LocalSeeder.StartAsync(1024 * 1024);
        using var subst = form == "subst" ? new TorrentDosDriveAliasTests.OwnedSubstFolder() : null;
        string physical = subst?.Physical ?? Path.Combine(seeder.Root, "physical-library");
        Directory.CreateDirectory(physical);
        string alias = subst != null ? subst.Map(physical) : Junction(Path.Combine(seeder.Root, "alias-library"), physical);
        using var dataRoot = new DataRoot(seeder.Root);
        try
        {
            DownloadReleaseSelection? pinned = null;
            using var downloader = new SeasonDownloader(seeder.SeasonConfig(alias));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Assert.True(await downloader.DownloadSeasonAsync(LocalSeeder.Title, seeder.Logs.Add, token: deadline.Token,
                commitReleaseSelection: selection => pinned = selection), string.Join(Environment.NewLine, seeder.Logs.TakeLast(15)));

            string expected = Path.Combine(TorrentDownloadOwnership.NormalizePath(physical), Episode);
            Assert.Equal(expected, downloader.LastCompletedVideoPath, ignoreCase: true);
            Assert.Equal(TorrentDownloadOwnership.NormalizePath(physical), pinned?.DownloadDirectory, ignoreCase: true);
            Assert.Equal(Hash(seeder.VideoPath), Hash(expected));
        }
        finally { if (subst == null) RemoveJunction(alias); }
    }

    [Fact]
    public async Task RetargetingTheConfiguredJunctionDuringATransferCannotMoveItsWrites()
    {
        await using var seeder = await LocalSeeder.StartAsync(256 * 1024);
        string physical = Path.Combine(seeder.Root, "physical-library");
        string foreign = Path.Combine(seeder.Root, "foreign");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "retained.txt"), "foreign sentinel");
        string alias = Junction(Path.Combine(seeder.Root, "alias-library"), physical);
        using var dataRoot = new DataRoot(seeder.Root);
        try
        {
            using var downloader = new SeasonDownloader(seeder.SeasonConfig(alias));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var transfer = downloader.DownloadSeasonAsync(LocalSeeder.Title, seeder.Logs.Add, token: deadline.Token);
            await WaitForPartialAsync(physical, transfer);

            RemoveJunction(alias);
            Junction(alias, foreign);
            Assert.True(await transfer, string.Join(Environment.NewLine, seeder.Logs.TakeLast(15)));

            Assert.Equal(Path.Combine(TorrentDownloadOwnership.NormalizePath(physical), Episode), downloader.LastCompletedVideoPath, ignoreCase: true);
            Assert.Equal(new[] { Path.Combine(foreign, "retained.txt") }, Directory.GetFiles(foreign, "*", SearchOption.AllDirectories));
        }
        finally { RemoveJunction(alias); }
    }

    /// <summary>Moving the physical folder away and putting a junction to a foreign folder in its place would send
    /// the engine's later opens (and its final rename) through that junction.</summary>
    [Fact]
    public async Task ReplacingThePhysicalFolderWithAJunctionDuringATransferCannotRedirectItsWrites()
    {
        await using var seeder = await LocalSeeder.StartAsync(256 * 1024);
        string vault = Path.Combine(seeder.Root, "vault");
        string physical = Path.Combine(vault, "library");
        string foreign = Path.Combine(seeder.Root, "foreign");
        Directory.CreateDirectory(physical);
        Directory.CreateDirectory(Path.Combine(foreign, "library"));
        File.WriteAllText(Path.Combine(foreign, "retained.txt"), "foreign sentinel");
        using var dataRoot = new DataRoot(seeder.Root);
        bool replaced = false;
        try
        {
            using var downloader = new SeasonDownloader(seeder.SeasonConfig(physical));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var transfer = downloader.DownloadSeasonAsync(LocalSeeder.Title, seeder.Logs.Add, token: deadline.Token);
            await WaitForPartialAsync(physical, transfer);

            try
            {
                Directory.Move(vault, vault + "-moved");
                Junction(vault, foreign);
                replaced = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Console.WriteLine(replaced ? "Windows allowed the folder to be replaced mid-transfer." : "Windows refused to move the folder mid-transfer.");

            Exception? error = await Record.ExceptionAsync(() => transfer);
            Assert.Empty(Directory.GetFiles(foreign, "*" + LocalSeeder.Title + "*", SearchOption.AllDirectories));
            Assert.Equal("foreign sentinel", File.ReadAllText(Path.Combine(foreign, "retained.txt")));
            if (replaced)
            {
                Assert.True(error != null, "The transfer completed through the replaced folder: " + downloader.LastCompletedVideoPath);
                Assert.Contains("download folder", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Null(error);
        }
        finally
        {
            if (replaced) RemoveJunction(vault);
        }
    }

    private static async Task WaitForPartialAsync(string physical, Task<bool> transfer)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!Directory.EnumerateFiles(physical, "*.!mt", SearchOption.AllDirectories).Any(path => new FileInfo(path).Length > 0))
        {
            Assert.False(transfer.IsCompleted, "The transfer ended before writing a partial.");
            await Task.Delay(50, limit.Token);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000), "Owned junction creation must finish.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return link;
    }

    private static void RemoveJunction(string link)
    {
        if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(link, recursive: false);
    }

    private sealed class DataRoot : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        public DataRoot(string root) => Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
        public void Dispose() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previous);
    }
}
