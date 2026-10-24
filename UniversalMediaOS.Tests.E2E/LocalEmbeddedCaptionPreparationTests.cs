using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class LocalEmbeddedCaptionPreparationTests
{
    [Fact]
    public void UniqueTextTrackKeepsItsFormatAndSelectedLanguage()
    {
        const string json = """
            {"streams":[{"index":2,"codec_name":"subrip","tags":{"language":"spa"}},
            {"index":3,"codec_name":"ass","tags":{"language":"eng","title":"Dialogue"}}]}
            """;
        Assert.Equal((3, "ass"), LocalEmbeddedCaptionPreparation.SelectTrack(json, "en", "Dialogue"));
        Assert.Equal((3, "ass"), LocalEmbeddedCaptionPreparation.SelectTrack(json, "English", "Dialogue"));
        Assert.Null(LocalEmbeddedCaptionPreparation.SelectTrack(json, "ja", "Dialogue"));
        Assert.Null(LocalEmbeddedCaptionPreparation.SelectTrack(json, "en", "Signs"));
    }

    [Fact]
    public void DuplicateLanguageNeedsExactUniqueTitle()
    {
        const string json = """
            {"streams":[{"index":2,"codec_name":"subrip","tags":{"language":"eng","title":"Signs"}},
            {"index":3,"codec_name":"subrip","tags":{"language":"eng","title":"Dialogue"}}]}
            """;
        Assert.Null(LocalEmbeddedCaptionPreparation.SelectTrack(json, "en", ""));
        Assert.Equal((3, "srt"), LocalEmbeddedCaptionPreparation.SelectTrack(json, "en", "Dialogue"));
    }

    [Fact]
    public void DeclaredLanguageNameMatchesCodeWithoutInferringFromDisplayLabel()
    {
        var named = PlaybackTrackOption.Native(3, "Track 2", "English", "", "Subtitle");
        var coded = PlaybackTrackOption.Native(3, "Track 2", "eng", "", "Subtitle");
        Assert.Equal(coded.Key, named.Key);
        Assert.Equal("en", named.NativeLanguage);
        Assert.Empty(PlaybackTrackOption.Native(3, "English", "", "", "Subtitle").NativeLanguage);
    }

    [Theory]
    [InlineData("ger", "de", "German")]
    [InlineData("fre", "fr", "French")]
    [InlineData("dut", "nl", "Dutch")]
    [InlineData("chi", "zh", "Chinese")]
    [InlineData("cze", "cs", "Czech")]
    [InlineData("gre", "el", "Greek")]
    [InlineData("per", "fa", "Persian")]
    [InlineData("rum", "ro", "Romanian")]
    public void BibliographicTrackTagsKeepTheirLanguage(string tag, string code, string name)
    {
        // Sintel's MKV tags its German, French and Dutch captions ger/fre/dut (ISO 639-2/B).
        var tagged = PlaybackTrackOption.Native(2, "Track 1", tag, "", "Subtitle");
        Assert.Equal(code, tagged.NativeLanguage);
        Assert.Equal(PlaybackTrackOption.Native(5, "Track 4", code, "", "Subtitle").Key, tagged.Key);
        Assert.Equal(name, tagged.Label);
    }

    [Fact]
    public void ImageCaptionsAndMissingLanguageStayNative()
    {
        Assert.Null(LocalEmbeddedCaptionPreparation.SelectTrack(
            "{\"streams\":[{\"index\":2,\"codec_name\":\"hdmv_pgs_subtitle\",\"tags\":{\"language\":\"eng\"}}]}", "en", ""));
        Assert.Null(LocalEmbeddedCaptionPreparation.SelectTrack(
            "{\"streams\":[{\"index\":2,\"codec_name\":\"subrip\",\"tags\":{}}]}", "en", ""));
    }

    [Fact]
    public async Task CancelDuringExtractionRemovesOnlyItsOwnedCaption()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "CaptionCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string video = Path.Combine(root, "original.mkv");
        await File.WriteAllTextAsync(video, "original media bytes");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var runner = new CanceledExtractionRunner(cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new LocalEmbeddedCaptionPreparation(runner).PrepareAsync(video, "en", "", root, cancellation.Token));
            Assert.Equal("original media bytes", await File.ReadAllTextAsync(video));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "UniversalMediaOS", "NativeCaptions"), "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CanceledExtractionRunner(CancellationTokenSource cancellation) : IPreparationProcessRunner
    {
        public async Task<PreparationProcessResult> RunAsync(string executable,
            System.Collections.Generic.IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (Path.GetFileNameWithoutExtension(executable) == "ffprobe")
                return new(0, "{\"streams\":[{\"index\":3,\"codec_name\":\"subrip\",\"tags\":{\"language\":\"eng\"}}]}");
            await File.WriteAllTextAsync(arguments[^1], "partial owned caption", token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return new(0, "");
        }
    }
}
