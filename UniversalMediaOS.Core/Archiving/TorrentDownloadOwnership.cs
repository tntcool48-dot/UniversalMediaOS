using System;
using System.IO;
using System.Text.Json;

namespace UniversalMediaOS.Core.Archiving;

internal sealed class TorrentDownloadOwnershipException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Preserves one torrent engine per download folder across retries and restarts.</summary>
internal sealed class TorrentDownloadOwnership : IDisposable
{
    private const string OwnerName = ".umos-torrent-owner.json";
    private const string LeaseName = ".umos-torrent-writer.lock";
    private readonly string _root;
    private readonly string _endpoint;
    private readonly string _ownerPath;
    private readonly FileStream _lease;
    public bool WasReserved { get; }
    public bool IsReserved { get; private set; }

    public TorrentDownloadOwnership(string directory, string endpoint)
    {
        _root = NormalizePath(directory);
        _endpoint = new Uri(endpoint).AbsoluteUri.TrimEnd('/');
        _ownerPath = Path.Combine(_root, OwnerName);
        try { _lease = new FileStream(Path.Combine(_root, LeaseName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TorrentDownloadOwnershipException("This download folder is already in use or cannot be reserved. Existing files were retained; retry after the other transfer stops.", error);
        }
        try
        {
            CheckOverlappingFolders();
            if (!File.Exists(_ownerPath)) return;
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(_ownerPath));
            if (owner == null || !SamePath(owner.Directory, _root) || owner.Endpoint != _endpoint)
                throw new TorrentDownloadOwnershipException("This download folder is reserved for a different qBittorrent connection. Restore the previous connection before retrying; existing files were retained.");
            WasReserved = IsReserved = true;
        }
        catch (Exception error)
        {
            _lease.Dispose();
            if (error is TorrentDownloadOwnershipException) throw;
            throw new TorrentDownloadOwnershipException("The download folder's torrent ownership could not be verified. Existing files were retained; repair its access before retrying.", error);
        }
    }

    public void Reserve()
    {
        if (IsReserved) return;
        string temporary = _ownerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, new Owner(_root, _endpoint));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _ownerPath);
            IsReserved = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TorrentDownloadOwnershipException("qBittorrent was not started because this download folder's ownership could not be saved. Existing files were retained; repair its access before retrying.", error);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void ReleaseDefiniteFirstRejection()
    {
        if (WasReserved || !IsReserved) return;
        File.Delete(_ownerPath);
        IsReserved = false;
    }

    private void CheckOverlappingFolders()
    {
        for (var ancestor = Directory.GetParent(_root); ancestor != null; ancestor = ancestor.Parent)
            CheckOtherFolder(ancestor.FullName);
        foreach (string path in Directory.EnumerateFiles(_root, ".umos-torrent-*", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            string directory = Path.GetDirectoryName(path)!;
            if (!SamePath(directory, _root)) CheckOtherFolder(directory);
        }
    }

    private static void CheckOtherFolder(string directory)
    {
        if (File.Exists(Path.Combine(directory, OwnerName)))
            throw new TorrentDownloadOwnershipException("An overlapping download folder is reserved for qBittorrent. Use its original download folder and connection; existing files were retained.");
        string lease = Path.Combine(directory, LeaseName);
        if (!File.Exists(lease)) return;
        try { using var unused = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        {
            throw new TorrentDownloadOwnershipException("An overlapping download folder has another active torrent writer. Wait for it to stop before retrying.", error);
        }
    }

    internal static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool SamePath(string first, string second) => string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);
    public void Dispose() => _lease.Dispose();
    private sealed record Owner(string Directory, string Endpoint);
}
