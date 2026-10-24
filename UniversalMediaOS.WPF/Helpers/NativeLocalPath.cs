using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalMediaOS.WPF.Helpers;

// The location the native engine is given for a local media or caption file.
// LibVLC 3 opens local files through Win32 calls limited to MAX_PATH, while .NET
// and ffprobe save and validate longer paths, so a saved episode in a deep
// download folder would be kept but could not be played. Such a path is handed
// over in its 8.3 short form; without one the file cannot be opened natively.
internal static class NativeLocalPath
{
    private const int MaxPath = 260;

    internal static string? ToLocation(string path) => ToLocation(path, ShortForm);

    internal static string? ToLocation(string path, Func<string, string?> shorten)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < MaxPath) return new Uri(full).AbsoluteUri;
        string? shortened = shorten(full);
        return shortened is { Length: > 0 and < MaxPath } ? new Uri(shortened).AbsoluteUri : null;
    }

    private static string? ShortForm(string path)
    {
        // The extended prefix lets Windows resolve the long name itself.
        string extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        var buffer = new StringBuilder(MaxPath);
        uint length = GetShortPathName(extended, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer.Capacity = checked((int)length + 1);
            length = GetShortPathName(extended, buffer, (uint)buffer.Capacity);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        string result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
}
