using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeTlsCapacityProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void ClosedProductionPlayersAreCollectibleWhileTheSharedEngineRemainsWarm()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(parent, "NativeCollectibility-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", root);
        try
        {
            var config = new DomainHotSwapper(Path.Combine(root, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(config.SetSettings(new Dictionary<string, string>
            {
                ["DatabasePath"] = Path.Combine(root, "media_os.db"), ["AutoSyncMal"] = "false"
            }));
            using (var db = new DatabaseContext()) db.Database.EnsureCreated();
            using var engine = NativePlaybackEngine.ForProcess.Acquire();
            WeakReference[] closed = Enumerable.Range(0, 8).Select(_ => OpenAndDispose()).ToArray();
            for (int attempt = 0; attempt < 3 && closed.Any(reference => reference.IsAlive); attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Thread.Sleep(100);
            }
            Assert.NotEqual(IntPtr.Zero, engine.Engine.NativeReference);
            output.WriteLine($"Closed player models still alive: {closed.Count(reference => reference.IsAlive)}/8.");
            Assert.All(closed, reference => Assert.False(reference.IsAlive));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", previous);
            SqliteConnection.ClearAllPools();
            Assert.StartsWith(parent + Path.DirectorySeparatorChar, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(root).LinkTarget);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndDispose()
    {
        using var player = new PlaybackViewModel(new DatabaseContext()) { Volume = 0 };
        return new WeakReference(player);
    }

    [Fact]
    public void NativeEngineShutdownWaitsForTheFinalIndependentPlayerLease()
    {
        var owner = new NativePlaybackEngine();
        using var first = owner.Acquire();
        using var second = owner.Acquire();
        LibVLC engine = first.Engine;
        using var firstPlayer = new MediaPlayer(first.Engine) { Volume = 0 };
        using var secondPlayer = new MediaPlayer(second.Engine) { Volume = 0 };
        try
        {
            Assert.Same(engine, second.Engine);
            Assert.NotSame(firstPlayer, secondPlayer);
            firstPlayer.Dispose();
            first.Dispose();
            first.Dispose();
            owner.RequestShutdown();
            Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
            Assert.NotEqual(IntPtr.Zero, second.Engine.NativeReference);
            Assert.NotEqual(IntPtr.Zero, secondPlayer.NativeReference);
            Assert.Throws<ObjectDisposedException>(() => first.Engine);
        }
        finally
        {
            firstPlayer.Dispose();
            secondPlayer.Dispose();
            first.Dispose();
            second.Dispose();
            owner.RequestShutdown();
        }
        Assert.Equal(IntPtr.Zero, engine.NativeReference);
        owner.RequestShutdown();
    }

    [Fact]
    public void ClosedProductionNativePlayersRetainWindowsTlsCapacity()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(parent, "NativeTlsCapacity-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", root);
        try
        {
            var config = new DomainHotSwapper(Path.Combine(root, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(config.SetSettings(new Dictionary<string, string>
            {
                ["DatabasePath"] = Path.Combine(root, "media_os.db"), ["AutoSyncMal"] = "false"
            }));
            using (var db = new DatabaseContext()) db.Database.EnsureCreated();
            void OpenAndClose()
            {
                using var player = new PlaybackViewModel(new DatabaseContext()) { Volume = 0 };
            }
            OpenAndClose();
            int baseline = AvailableSlots();
            output.WriteLine($"Warm production player: {baseline} available Windows TLS slots.");
            for (int cycle = 1; cycle <= 24; cycle++)
            {
                OpenAndClose();
                int available = AvailableSlots();
                output.WriteLine($"Closed production player {cycle}: {available} available TLS slots; baseline {baseline}.");
                Assert.Equal(baseline, available);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", previous);
            SqliteConnection.ClearAllPools();
            if (Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int AvailableSlots()
    {
        // Measuring only the first free slot misses leaks above a surviving
        // low-numbered hole. Return every slot held by this isolated probe.
        var slots = new List<uint>(2048);
        try
        {
            for (int index = 0; index < 4096; index++)
            {
                uint slot = TlsAlloc();
                if (slot == uint.MaxValue) return slots.Count;
                slots.Add(slot);
            }
            throw new InvalidOperationException("The bounded TLS probe did not find Windows' allocation limit.");
        }
        finally
        {
            bool returned = true;
            foreach (uint slot in slots) returned &= TlsFree(slot);
            Assert.True(returned);
        }
    }

    [DllImport("kernel32.dll")] private static extern uint TlsAlloc();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TlsFree(uint index);
}
