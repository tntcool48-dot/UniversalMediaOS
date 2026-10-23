using System.IO;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class LongLocalMediaPathTests
{
    // A saved season lives at <download folder>\TV Shows\<title>-<hash>\Season 01\S01E03-<hash>\media.mkv.
    // A deep download folder pushes that past MAX_PATH; .NET and ffprobe still save and validate it.
    [Fact]
    public async Task SavedEpisodeBeyondMaxPathDecodesWithItsCaptionInTheNativeEngine()
    {
        string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.LongLocalPathTests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        string folder = directory;
        while (Path.Combine(folder, "media.mkv").Length < 280)
            folder = Path.Combine(folder, "Season 01 " + new string('x', 40));
        Directory.CreateDirectory(folder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            string generated = Path.Combine(directory, "generated.mkv");
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "4",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", generated], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            string media = Path.Combine(folder, "media.mkv");
            File.Move(generated, media);
            string caption = Path.Combine(folder, "caption-01.en.vtt");
            await File.WriteAllTextAsync(caption, "WEBVTT\n\n00:00:00.000 --> 00:00:04.000\nLong path caption\n", deadline.Token);
            Assert.True(media.Length >= 280);

            string? mediaLocation = NativeLocalPath.ToLocation(media);
            string? captionLocation = NativeLocalPath.ToLocation(caption);
            Assert.NotNull(mediaLocation);
            Assert.NotNull(captionLocation);

            LibVLCSharp.Shared.Core.Initialize();
            using var engine = new LibVLC(enableDebugLogs: true, "--vout=dummy", "--aout=dummy");
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            engine.Log += (_, log) => { if (logs.Count < 100) logs.Enqueue(log.Message); };
            using var player = new MediaPlayer(engine);
            using var native = new Media(engine, mediaLocation!, FromType.FromLocation);
            Assert.True(native.AddSlave(MediaSlaveType.Subtitle, 4u, captionLocation!));
            var decoded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.TimeChanged += (_, args) => { if (args.Time >= 500 && player.VoutCount > 0) decoded.TrySetResult(args.Time); };
            player.EncounteredError += (_, _) => decoded.TrySetException(
                new InvalidOperationException("Native engine failed: " + string.Join(" | ", logs)));
            Assert.True(player.Play(native));
            try { await decoded.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token); }
            catch (TimeoutException) { throw new InvalidOperationException("Long-path media did not decode: " + string.Join(" | ", logs)); }
            Assert.InRange(player.Length, 3900, 4100);
            Assert.Contains(player.SpuDescription, track => track.Id >= 0);
            player.Stop();
        }
        finally
        {
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OrdinaryLocalPathsKeepTheirFileUri()
    {
        string path = Path.Combine(Path.GetTempPath(), "UniversalMediaOS", "Season 01", "media.mkv");
        Assert.Equal(new Uri(path).AbsoluteUri, NativeLocalPath.ToLocation(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongPathsWithoutAShortEnoughFormAreRefusedRatherThanHandedToTheEngine(bool shortFormStillTooLong)
    {
        // Volumes can disable 8.3 names; the player then reports the path instead of failing to open it.
        string path = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('x', 60), 5)) + @"\media.mkv";
        Assert.Null(NativeLocalPath.ToLocation(path, _ => shortFormStillTooLong ? path : null));
        Assert.Equal(new Uri(@"C:\SHORT~1\media.mkv").AbsoluteUri, NativeLocalPath.ToLocation(path, _ => @"C:\SHORT~1\media.mkv"));
    }
}
