using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false, AttributesToSkip = 0
        };
        string prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        var pending = new System.Collections.Generic.Stack<string>();
        pending.Push(_root);
        while (pending.TryPop(out string? directory))
        {
            if (!string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase)) CheckOtherFolder(directory);
            foreach (string child in Directory.EnumerateDirectories(directory, "*", options))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                    continue;
                }

                string target = NormalizePath(child);
                if (!string.Equals(target, _root, StringComparison.OrdinalIgnoreCase) &&
                    !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new TorrentDownloadOwnershipException("A payload folder link points outside this download folder. Existing files were retained; restore a contained folder before retrying.");
                // The target's physical directory is already in this tree; following the alias could create a cycle.
            }
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

    internal static string NormalizePath(string path)
    {
        try
        {
            string resolved = ResolveDirectory(Path.GetFullPath(path), 0);
            return OperatingSystem.IsWindows() ? ResolveWindowsDirectory(resolved) : resolved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TorrentDownloadOwnershipException("The download folder's physical location could not be verified. Existing files were retained; restore its folder links or access before retrying.", error);
        }
    }

    private static string ResolveWindowsDirectory(string path)
    {
        // A SUBST drive is a DOS alias, not a directory reparse point. Ask the
        // existing directory handle for its final name before checking ancestry
        // or pinning a save path; this also expands short directory names.
        string extendedPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        using var handle = CreateFile(extendedPath, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The physical download directory could not be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new IOException("The physical download directory name could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (length >= buffer.Capacity)
            {
                if (length >= 32768) throw new IOException("The physical download directory name is too long.");
                buffer.Capacity = checked((int)length + 1);
                continue;
            }
            string result = buffer.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result[8..];
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
            if (!Path.IsPathFullyQualified(result)) throw new IOException("The physical download directory has no absolute DOS or network name.");
            return Path.TrimEndingDirectorySeparator(result);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint maximum, uint flags);

    private static string ResolveDirectory(string path, int links)
    {
        if (links > 40) throw new IOException("The download folder has too many directory links.");
        string root = Path.GetPathRoot(path) ?? throw new IOException("The download folder has no filesystem root.");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The download filesystem root is unavailable.");
        string current = root;
        foreach (string part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            if (!directory.Exists) throw new DirectoryNotFoundException("A download folder component is unavailable.");
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (directory.ResolveLinkTarget(returnFinalTarget: true) is not DirectoryInfo target || !target.Exists)
                    throw new IOException("A download directory link has no verifiable directory target.");
                current = ResolveDirectory(Path.GetFullPath(target.FullName), links + 1);
            }
            else current = directory.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    internal static bool SamePath(string first, string second)
    {
        try { return string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase); }
        catch (TorrentDownloadOwnershipException) { return false; }
    }
    public void Dispose() => _lease.Dispose();
    private sealed record Owner(string Directory, string Endpoint);
}
