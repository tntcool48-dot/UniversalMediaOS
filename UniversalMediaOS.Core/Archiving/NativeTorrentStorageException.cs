using MonoTorrent.Client;

namespace UniversalMediaOS.Core.Archiving;

internal sealed class NativeTorrentStorageException(string message, Exception? innerException)
    : IOException(message, innerException)
{
    internal static void ThrowIfFailed(TorrentManager manager)
    {
        if (manager.State != TorrentState.Error) return;
        var error = manager.Error;
        string operation = error?.Reason switch
        {
            Reason.ReadFailure => "read",
            Reason.WriteFailure => "write",
            _ => "access"
        };
        throw new NativeTorrentStorageException(
            $"Cannot {operation} download files. Check the destination's permissions and free space, and close other apps using these files.",
            error?.Exception);
    }
}
