using System.IO;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ScraperUnicodeTransportTests
{
    [Theory]
    [InlineData("Frieren: Beyond Journey’s End")]
    [InlineData("蟲師 — Mushishi")]
    public async Task AWindowsLegacyPipeEncodingCannotChangeSearchTitlesOrProgress(string title)
    {
        using var fixture = new Fixture();
        var progress = new List<string>();
        var result = Assert.Single(await fixture.Engine.SearchAsync(title, progressLog: progress.Add));
        Assert.Equal(title, result.Title);
        Assert.Equal("fixture", result.Provider);
        Assert.Equal("https://fixture.invalid/entry", result.Url);
        Assert.Contains(title, progress);
        Assert.Equal("cp1252", Environment.GetEnvironmentVariable("PYTHONIOENCODING"));
    }

    [Fact]
    public async Task NativeHandoffKeepsUnicodeCaptionTextAndUnknownAudioThroughTheRealPythonPipe()
    {
        using var fixture = new Fixture();
        var result = await fixture.Engine.ExtractAsync("https://fixture.invalid/episode");
        Assert.NotNull(result);
        var caption = Assert.Single(result.Subtitles!);
        Assert.Equal("Français · 日本語", caption.Label);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nCafé — 蟲師\n", caption.InlineVtt);
        Assert.Equal(new[] { "und" }, result.AudioLanguages);
        Assert.Equal("sub", result.SelectedAudio);
        Assert.Equal("https://fixture.invalid/video.mp4", result.Url);
        Assert.Equal("cp1252", Environment.GetEnvironmentVariable("PYTHONIOENCODING"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ScraperUnicodeTests", Guid.NewGuid().ToString("N"));
        private readonly string? _previousEncoding = Environment.GetEnvironmentVariable("PYTHONIOENCODING");
        private readonly PythonBootstrapper _python;
        public ScraperEngine Engine { get; }

        public Fixture()
        {
            // Reproduce a legacy Windows pipe regardless of the host's locale.
            // The production launcher must establish only its child's protocol.
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "cp1252");
            string source = Path.Combine(_root, "source");
            Directory.CreateDirectory(source);
            const string script = """
                import json, sys
                if sys.argv[1] == 'search':
                    title = sys.argv[2]
                    print(title, file=sys.stderr)
                    print(json.dumps([{'title': title, 'provider': 'fixture', 'url': 'https://fixture.invalid/entry'}], ensure_ascii=False))
                else:
                    print(json.dumps({'url': 'https://fixture.invalid/video.mp4',
                        'subtitles': [{'url': 'https://fixture.invalid/caption.vtt', 'label': 'Français · 日本語',
                            'language': 'fr', 'inline_vtt': 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nCafé — 蟲師\n'}],
                        'audio_languages': ['und'], 'selected_audio': 'sub'}, ensure_ascii=False))
                """;
            File.WriteAllText(Path.Combine(source, "scraper.py"), script);
            foreach (string other in new[] { "book_scraper.py", "audiovisual_scraper.py" })
                File.WriteAllText(Path.Combine(source, other), "# unused preparation fixture\n");
            _python = new(new PreparedRuntime(), source, Path.Combine(_root, "services"), ["python.exe"], TimeSpan.FromSeconds(10));
            Engine = new(_python);
        }

        public void Dispose()
        {
            _python.Dispose();
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", _previousEncoding);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ScraperUnicodeTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }

    // Preparation is already covered elsewhere. Only the actual scraper
    // operation launches Python; no package installation or external lookup.
    private sealed class PreparedRuntime : IPreparationProcessRunner
    {
        public Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
            => Task.FromResult(new PreparationProcessResult(0, arguments[0] == "--version" ? "Python 3.12.0" :
                arguments[1].StartsWith("import ast", StringComparison.Ordinal) ? "" : "[]"));
    }
}
