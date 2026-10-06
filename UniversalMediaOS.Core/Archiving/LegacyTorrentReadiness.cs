using System.Security.Cryptography;
using MonoTorrent.BEncoding;

namespace UniversalMediaOS.Core.Archiving;

/// <summary>Read-only recognition of unsuffixed partials from the native client's cache.</summary>
public static class LegacyTorrentReadiness
{
    public static HashSet<string> FindIncompleteFiles(string downloadDirectory, string cacheDirectory)
    {
        var incomplete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string metadataDirectory = Path.Combine(cacheDirectory, "metadata");
        if (!Directory.Exists(metadataDirectory)) return incomplete;
        long remainingBytes = 32 * 1024 * 1024;
        try
        {
            string root = Path.GetFullPath(downloadDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string metadataPath in Directory.EnumerateFiles(metadataDirectory, "*.torrent").Take(256))
            {
                if (remainingBytes <= 0) break;
                try
                {
                    string hash = Path.GetFileNameWithoutExtension(metadataPath);
                    if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) continue;
                    string resumePath = Path.Combine(cacheDirectory, "fastresume", hash + ".fresume");
                    if (!SmallFile(metadataPath) || !SmallFile(resumePath)) continue;
                    remainingBytes -= new FileInfo(metadataPath).Length + new FileInfo(resumePath).Length;
                    if (remainingBytes <= 0) break;
                    var metadata = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(metadataPath));
                    var info = (BEncodedDictionary)metadata["info"];
                    byte[] infoHash = SHA1.HashData(info.Encode());
                    if (!Convert.ToHexString(infoHash).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                    var resume = BEncodedValue.Decode<BEncodedDictionary>(File.ReadAllBytes(resumePath));
                    if (!((BEncodedString)resume["infohash"]).AsMemory().Span.SequenceEqual(infoHash)) continue;
                    var pieces = ((BEncodedString)info["pieces"]).AsMemory();
                    int pieceLength = checked((int)((BEncodedNumber)info["piece length"]).Number);
                    int pieceCount = checked((int)((BEncodedNumber)resume["bitfield_length"]).Number);
                    var bits = ((BEncodedString)resume["bitfield"]).AsMemory();
                    if (pieceLength <= 0 || pieceLength > 4 * 1024 * 1024 || pieceCount <= 0 ||
                        pieces.Length != (long)pieceCount * 20 || bits.Length != (pieceCount + 7L) / 8) continue;

                    string name = ((BEncodedString)info["name"]).Text;
                    var files = new List<(string Path, long Length, long Offset)>();
                    long offset = 0;
                    if (info.TryGetValue("files", out var value))
                    {
                        foreach (BEncodedDictionary file in (BEncodedList)value)
                        {
                            string[] parts = ((BEncodedList)file["path"]).Cast<BEncodedString>().Select(part => part.Text).ToArray();
                            long length = ((BEncodedNumber)file["length"]).Number;
                            files.Add((SafePath(root, [name, .. parts]), length, offset));
                            offset = checked(offset + length);
                        }
                    }
                    else
                    {
                        offset = ((BEncodedNumber)info["length"]).Number;
                        files.Add((SafePath(root, [name]), offset, 0));
                    }
                    if (files.Any(file => file.Length < 0) || offset <= 0 ||
                        (offset - 1) / pieceLength + 1 != pieceCount) continue;
                    foreach (var file in files)
                    {
                        if (remainingBytes <= 0) break;
                        if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path) ||
                            new FileInfo(file.Path).Length > file.Length || HasReparseParent(root, file.Path)) continue;
                        if (!new[] { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".m4v", ".ogv" }
                            .Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase)) continue;
                        // A matching completed piece binds the local bytes to this
                        // torrent. A differing missing piece establishes incompleteness;
                        // stale resume metadata alone must not hide a complete file.
                        int first = checked((int)((file.Offset + pieceLength - 1) / pieceLength));
                        int last = checked((int)((file.Offset + file.Length) / pieceLength - 1));
                        if (file.Offset + file.Length == offset) last = pieceCount - 1;
                        if (first > last) continue; // Shared boundary-only files stay unknown.
                        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        bool bound = false, missing = false;
                        int probes = 0;
                        for (int index = first; index <= last && probes < 8; index++)
                        {
                            bool claimedComplete = (bits.Span[index / 8] & (128 >> (index % 8))) != 0;
                            if (claimedComplete ? bound : missing) continue;
                            int count = checked((int)Math.Min(pieceLength, offset - (long)index * pieceLength));
                            if (count > remainingBytes) break;
                            remainingBytes -= count;
                            probes++;
                            long position = (long)index * pieceLength - file.Offset;
                            bool matches = false;
                            if (position + count <= stream.Length)
                            {
                                byte[] bytes = new byte[count];
                                stream.Position = position;
                                stream.ReadExactly(bytes);
                                matches = SHA1.HashData(bytes).AsSpan().SequenceEqual(pieces.Span.Slice(index * 20, 20));
                            }
                            if (claimedComplete && matches) bound = true;
                            if (!claimedComplete && !matches) missing = true;
                            if (bound && missing) { incomplete.Add(file.Path); break; }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    ArgumentException or InvalidCastException or KeyNotFoundException or OverflowException or FormatException or BEncodingException)
                { /* Unreadable/unsupported cache does not establish ownership. */ }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return incomplete;
    }

    private static bool SmallFile(string path) => File.Exists(path) && new FileInfo(path).Length is > 0 and <= 2 * 1024 * 1024;

    private static string SafePath(string root, string[] parts)
    {
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".." ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return string.Empty;
        string path = Path.GetFullPath(Path.Combine([root, .. parts]));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : string.Empty;
    }

    private static bool HasReparseParent(string root, string path)
    {
        for (string? current = path; current != null && current.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length;
            current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
