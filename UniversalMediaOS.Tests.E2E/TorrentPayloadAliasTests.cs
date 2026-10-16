using System.Diagnostics;
using System.IO;
using System.Text;
using UniversalMediaOS.Core.Archiving;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TorrentPayloadAliasTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescendantJunctionCannotExposeAnOutsidePayloadFolder(bool reserved)
    {
        using var fixture = new Folders();
        using var other = reserved ? new TorrentDownloadOwnership(fixture.Foreign, "http://127.0.0.1:9101") : null;
        other?.Reserve();
        string? marker = reserved ? File.ReadAllText(Path.Combine(fixture.Foreign, ".umos-torrent-owner.json")) : null;
        fixture.Link("release", fixture.Foreign);

        var error = Assert.Throws<TorrentDownloadOwnershipException>(() =>
        {
            using var unexpected = new TorrentDownloadOwnership(fixture.Downloads, "http://127.0.0.1:9102");
        });

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        if (reserved) Assert.Equal(marker, File.ReadAllText(Path.Combine(fixture.Foreign, ".umos-torrent-owner.json")));
    }

    [Fact]
    public void DescendantJunctionWithinThePhysicalRootRetainsReservationAndBytes()
    {
        using var fixture = new Folders();
        string target = Path.Combine(fixture.Downloads, "media");
        Directory.CreateDirectory(target);
        fixture.Link("release", target);
        using (var owner = new TorrentDownloadOwnership(fixture.Downloads, "http://127.0.0.1:9102")) owner.Reserve();
        byte[] marker = File.ReadAllBytes(Path.Combine(fixture.Downloads, ".umos-torrent-owner.json"));
        using (var reopened = new TorrentDownloadOwnership(fixture.Downloads, "http://127.0.0.1:9102"))
            Assert.True(reopened.WasReserved);
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(fixture.Downloads, ".umos-torrent-owner.json")));
    }

    [Fact]
    public void DescendantJunctionBackToTheRootDoesNotRecurseThroughTheAlias()
    {
        using var fixture = new Folders();
        fixture.Link("loop", fixture.Downloads);
        using var owner = new TorrentDownloadOwnership(fixture.Downloads, "http://127.0.0.1:9102");
        owner.Reserve();
        Assert.True(owner.IsReserved);
    }

    [Fact]
    public void DanglingDescendantJunctionCannotBeDeclaredSafeForPayloadWrites()
    {
        using var fixture = new Folders();
        fixture.Link("release", Path.Combine(fixture.Root, "missing-target"));
        var error = Assert.Throws<TorrentDownloadOwnershipException>(() =>
        {
            using var unexpected = new TorrentDownloadOwnership(fixture.Downloads, "http://127.0.0.1:9102");
        });
        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Folders : IDisposable
    {
        private readonly List<string> _links = [];
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("Retain original and foreign payload bytes.");
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "UniversalMediaOS-payload-alias-" + Guid.NewGuid().ToString("N")));
        public string Downloads => Path.Combine(Root, "downloads");
        public string Foreign => Path.Combine(Root, "foreign");

        public Folders()
        {
            foreach (string directory in new[] { Downloads, Foreign })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "retained.bin"), _bytes);
            }
        }

        public void Link(string name, string target)
        {
            string link = Path.GetFullPath(Path.Combine(Downloads, name));
            AssertOwned(link);
            AssertOwned(target);
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string value in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(5000), "Owned junction creation must finish.");
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
            _links.Add(link);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }

        private void AssertOwned(string path) => Assert.StartsWith(Root + Path.DirectorySeparatorChar,
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            foreach (string directory in new[] { Downloads, Foreign })
                Assert.Equal(_bytes, File.ReadAllBytes(Path.Combine(directory, "retained.bin")));
            foreach (string link in _links.AsEnumerable().Reverse())
            {
                AssertOwned(link);
                Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(link, recursive: false);
            }
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Assert.False((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(Root, recursive: true);
        }
    }
}
