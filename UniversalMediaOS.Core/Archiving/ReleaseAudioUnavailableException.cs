namespace UniversalMediaOS.Core.Archiving;

/// <summary>No release found carries the requested audio label; another release's audio is not substituted.</summary>
internal sealed class ReleaseAudioUnavailableException(string message) : IOException(message);
