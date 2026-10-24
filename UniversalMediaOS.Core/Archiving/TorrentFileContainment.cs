using MonoTorrent;

namespace UniversalMediaOS.Core.Archiving;

/// <summary>A torrent names a file outside its download folder. Retrying the same release cannot help, so the
/// message is shown as the job's outcome.</summary>
internal sealed class TorrentContentOutsideFolderException(string message) : IOException(message);

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
        string root = Root(directory);
        foreach (ITorrentManagerFile file in files)
        {
            foreach (string path in new[] { file.FullPath, file.DownloadCompleteFullPath, file.DownloadIncompleteFullPath })
                if (!IsInside(path, root)) return path;
        }
        return null;
    }

    /// <summary>Returns the first of qBittorrent's file names, which are relative to its save path, that is rooted or
    /// does not resolve strictly inside <paramref name="directory"/>, or null. qBittorrent reports where it wrote;
    /// the app must not probe, publish or play a file elsewhere on its word.</summary>
    internal static string? FindOutside(IEnumerable<string> relativeNames, string directory)
    {
        string root = Root(directory);
        foreach (string name in relativeNames)
        {
            try
            {
                if (Path.IsPathRooted(name) || !IsInside(Path.Combine(directory, name), root)) return name;
            }
            catch (ArgumentException) { return name; }
        }
        return null;
    }

    private static string Root(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsInside(string path, string root)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
