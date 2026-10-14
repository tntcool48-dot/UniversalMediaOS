using System.Globalization;
using System.Text.Json;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services;

// A separate native subtitle input can seek to cues which start before the
// video's keyframe. Derive it only from a uniquely identified local text track.
public sealed class LocalEmbeddedCaptionPreparation
{
    private readonly IPreparationProcessRunner _runner;
    public LocalEmbeddedCaptionPreparation() : this(new PreparationProcessRunner()) { }
    internal LocalEmbeddedCaptionPreparation(IPreparationProcessRunner runner) => _runner = runner;

    public async Task<PreparedLocalCaption?> PrepareAsync(string mediaPath, string language,
        string description, string localBaseDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(mediaPath) || !File.Exists(mediaPath) ||
            string.IsNullOrEmpty(NormalizeLanguage(language))) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        string services = Path.Combine(localBaseDirectory, "UniversalMediaOS", "Services");
        string Tool(string name) => File.Exists(Path.Combine(services, name + ".exe"))
            ? Path.Combine(services, name + ".exe") : name;
        var probe = await _runner.RunAsync(Tool("ffprobe"), ["-v", "error", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language,title", "-of", "json", mediaPath], deadline.Token)
            .ConfigureAwait(false);
        if (probe.ExitCode != 0) return null;
        var selected = SelectTrack(probe.Output, language, description);
        if (selected == null) return null;
        string root = Path.GetFullPath(Path.Combine(localBaseDirectory, "UniversalMediaOS", "NativeCaptions"));
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = new PreparedLocalCaption(root, directory, Path.Combine(directory, "caption." + selected.Value.Format));
        try
        {
            var extraction = await _runner.RunAsync(Tool("ffmpeg"), ["-nostdin", "-hide_banner", "-v", "error",
                "-i", mediaPath, "-map", "0:" + selected.Value.Index.ToString(CultureInfo.InvariantCulture),
                "-c:s", "copy", "-fs", "8388608", "-f", selected.Value.Format, "-y", result.Path], deadline.Token)
                .ConfigureAwait(false);
            var file = new FileInfo(result.Path);
            if (extraction.ExitCode != 0 || !file.Exists || file.Length is <= 0 or >= 8388608)
            {
                result.Dispose();
                return null;
            }
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static (int Index, string Format)? SelectTrack(string json, string language, string description)
    {
        if (string.IsNullOrEmpty(NormalizeLanguage(language))) return null;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;
        var matching = streams.EnumerateArray().Where(stream =>
        {
            if (!stream.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("language", out var tag)) return false;
            if (NormalizeLanguage(tag.GetString() ?? "") != NormalizeLanguage(language)) return false;
            return string.IsNullOrWhiteSpace(description) || tags.TryGetProperty("title", out var title) &&
                string.Equals(title.GetString()?.Trim(), description.Trim(), StringComparison.OrdinalIgnoreCase);
        }).Take(2).ToArray();
        if (matching.Length != 1 || !matching[0].TryGetProperty("index", out var index) ||
            !index.TryGetInt32(out int number) || number < 0 ||
            !matching[0].TryGetProperty("codec_name", out var codec)) return null;
        string? format = codec.GetString() switch { "subrip" => "srt", "ass" => "ass", "ssa" => "ass", "webvtt" => "webvtt", _ => null };
        return format == null ? null : (number, format);
    }

    private static string NormalizeLanguage(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value is "" or "und" or "unknown") return "";
        value = value switch { "ger" => "deu", "fre" => "fra", "dut" => "nld", _ => value };
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c =>
            c.TwoLetterISOLanguageName == value || c.ThreeLetterISOLanguageName == value ||
            c.EnglishName.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            c.NativeName.Equals(value, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName ?? value;
    }
}

public sealed class PreparedLocalCaption : IDisposable
{
    private readonly string _root;
    private readonly string _directory;
    internal PreparedLocalCaption(string root, string directory, string path)
        => (_root, _directory, Path) = (root, directory, path);
    public string Path { get; }
    public void Dispose()
    {
        // Only this generated child belongs to the lease; the original media and
        // downloaded/provider sidecars are never candidates for removal.
        if (!string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_directory)),
            _root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Caption ownership changed.");
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { AppLogger.Log("Owned derived-caption cleanup could not complete.", "WARNING"); }
        catch (UnauthorizedAccessException) { AppLogger.Log("Owned derived-caption cleanup was denied.", "WARNING"); }
    }
}
