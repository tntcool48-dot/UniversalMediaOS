using System.IO;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SpecialEpisodeDownloadIdentityTests
{
    private const string Title = "Mushoku Tensei: Jobless Reincarnation Cour 2 - Eris the Goblin Slayer";
    private const string Alias = "Mushoku Tensei: Jobless Reincarnation Cour 2 Special";

    [Theory]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA", "Sub", true)]
    [InlineData("OVA", 1, 1, "[Group] " + Title + " OVA [Dual Audio]", "Dub", true)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Alias + " [1080p]", "Sub", true)]
    [InlineData("TV", 1, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("", 1, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 2, 1, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 2, "[Group] " + Title + " OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] Goblin Slayer OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] Mushoku Tensei OVA", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " - 13.5", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA 2", "Sub", false)]
    [InlineData("SPECIAL", 1, 1, "[Group] " + Title + " OVA", "Dub", false)]
    public async Task DiscoveryRequiresCatalogFormCountExactWorkAndRequestedAudio(
        string format, int count, int episode, string release, string audio, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Task<List<TorrentResult>> Search(string _, Action<string>? __, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new List<TorrentResult>
            {
                new() { Title = release, MagnetLink = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", Seeders = 5 }
            });
        }
        try
        {
            var service = new TemporaryEpisodeWatchService(Search, root);
            var results = await service.FindCandidatesAsync(Title, [Alias], episode, audio, _ => { },
                CancellationToken.None, format, count);
            Assert.Equal(expected ? 1 : 0, results.Count);
        }
        finally { Directory.Delete(root); }
    }

    [Theory]
    [InlineData("[Group] " + Title + " OVA.mkv", true)]
    [InlineData("[Group] " + Alias + ".mkv", true)]
    [InlineData("[Group] " + Title + " - 01.mkv", true)]
    [InlineData("[Group] Goblin Slayer OVA.mkv", false)]
    [InlineData("[Group] Mushoku Tensei - 01.mkv", false)]
    [InlineData("[Group] " + Title + " - 13.5.mkv", false)]
    [InlineData("[Group] " + Title + " OVA 1.mkv", false)]
    [InlineData("[Group] " + Title + " Complete.mkv", false)]
    public void SelectedFileMustIdentifyTheSpecialWithoutGuessingANonstandardUnit(string file, bool expected)
    {
        Assert.Equal(expected, TemporaryEpisodeWatchService.FileMatchesCatalogEpisode(file, 1, [Title, Alias]));
    }
}
