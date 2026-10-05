namespace UniversalMediaOS.Core.Archiving;

/// <summary>A local capacity failure; changing media providers cannot repair it.</summary>
internal sealed class InsufficientDownloadSpaceException(string message) : IOException(message);
