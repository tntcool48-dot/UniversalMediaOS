using System.IO;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using UniversalMediaOS.Core.Archiving;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class TorrentFileContainmentTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("nested")]
    [InlineData("device")]
    [InlineData("device-dot")]
    [InlineData("root-relative")]
    [InlineData("unc")]
    public async Task OnlyEntriesResolvingInsideTheSaveFolderAreAccepted(string kind)
    {
        // Every entry names a place inside this test's temporary root ("outside" is a sibling of "save"; the
        // root-relative entry does so on the temporary folder's drive). No manager starts, so nothing opens them.
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "containment-" + Guid.NewGuid().ToString("N"));
        string save = Path.Combine(root, "save");
        string outside = Path.Combine(root, "outside", "escape.bin");
        Directory.CreateDirectory(save);
        try
        {
            string? expected = kind switch
            {
                "device" => @"\\?\" + outside,
                "device-dot" => @"\\.\" + outside,
                // MonoTorrent rejects "C:\x" and "C:x" but keeps a name rooted at the current drive.
                "root-relative" => outside[2..],
                // The same folder through the local administrative share.
                "unc" => @"\\localhost\" + outside[0] + "$" + outside[2..],
                _ => null
            };
            var entries = new BEncodedList { Entry(4096, "Show - S01E01.mkv") };
            if (kind == "nested") entries.Add(Entry(100, "extras", "Show - S01E01.en.srt"));
            if (expected != null) entries.Insert(0, Entry(100, expected));
            var torrent = Torrent.Load(new BEncodedDictionary
            {
                ["announce"] = (BEncodedString)"http://127.0.0.1:1/announce",
                ["info"] = new BEncodedDictionary
                {
                    ["name"] = (BEncodedString)"Show",
                    ["piece length"] = new BEncodedNumber(16 * 1024),
                    ["pieces"] = new BEncodedString(new byte[20]),
                    ["files"] = entries
                }
            });
            using var engine = new ClientEngine(new EngineSettingsBuilder
            {
                AllowPortForwarding = false, AllowLocalPeerDiscovery = false, AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false, DhtEndPoint = null,
                CacheDirectory = Path.Combine(root, "cache")
            }.ToSettings());
            var manager = await engine.AddAsync(torrent, save);
            try { Assert.Equal(expected, TorrentFileContainment.FindOutside(manager.Files, save)); }
            finally { await engine.RemoveAsync(manager, RemoveMode.KeepAllData); }
            Assert.False(Directory.Exists(Path.GetDirectoryName(outside)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BEncodedDictionary Entry(long length, params string[] path)
    {
        var parts = new BEncodedList();
        foreach (string part in path) parts.Add((BEncodedString)part);
        return new BEncodedDictionary { ["length"] = new BEncodedNumber(length), ["path"] = parts };
    }
}
