using MonoTorrent;

namespace UniversalMediaOS.Core.Archiving;

/// <summary>Torrent metadata comes from peers. MonoTorrent 3.0.2 rejects ".." segments, drive-qualified names and
/// names starting with "/", but accepts names rooted at the current drive ("\x"), UNC names and device paths, which
/// all resolve outside the save folder. Check every entry before the engine starts, hash-checks or sets priorities,
/// not only the selected ones: once some files of a torrent exist, its start-up hash check creates any missing entry
/// that shares a wanted piece, and a transfer writes piece bytes into neighbouring files.</summary>
internal static class TorrentFileContainment
{
    /// <summary>Returns the first file path that is not strictly inside <paramref name="directory"/>, or null.</summary>
    internal static string? FindOutside(IEnumerable<ITorrentManagerFile> files, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (ITorrentManagerFile file in files)
        {
            foreach (string path in new[] { file.FullPath, file.DownloadCompleteFullPath, file.DownloadIncompleteFullPath })
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return path;
                }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        return null;
    }
}
