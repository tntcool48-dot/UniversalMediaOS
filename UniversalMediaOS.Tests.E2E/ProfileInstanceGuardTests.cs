using System.Diagnostics;
using System.IO;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ProfileInstanceGuardTests
{
    private static string NewProfile() =>
        Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "instance-" + Guid.NewGuid().ToString("N"), "Roaming", "UniversalMediaOS");

    [Fact]
    public void LaterLaunchOnTheSameProfileAsksTheRunningCopyAndExits()
    {
        string profile = NewProfile();
        int asked = 0;
        using var running = new RunningCopy(profile, () => { Interlocked.Increment(ref asked); return true; });

        var later = ProfileInstanceGuard.Acquire(profile, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), out var outcome);

        Assert.Null(later);
        Assert.Equal(ProfileInstanceOutcome.ActivatedExisting, outcome);
        Assert.Equal(1, Volatile.Read(ref asked));
    }

    [Fact]
    public void LaterLaunchWaitsForAClosingCopyThenStarts()
    {
        string profile = NewProfile();
        var running = new RunningCopy(profile, () => false); // A closing copy does not answer.
        var clock = Stopwatch.StartNew();
        // The guard stays on this test's thread, as the app keeps it on its UI thread; the close happens elsewhere.
        var closing = new Thread(() => { Thread.Sleep(700); running.Dispose(); });
        closing.Start();

        using var later = ProfileInstanceGuard.Acquire(profile, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), out var outcome);

        Assert.NotNull(later);
        Assert.Equal(ProfileInstanceOutcome.Acquired, outcome);
        Assert.InRange(clock.ElapsedMilliseconds, 600, 9000);
        Assert.True(closing.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LaterLaunchGivesUpOnACopyThatNeitherAnswersNorCloses()
    {
        string profile = NewProfile();
        using var running = new RunningCopy(profile, () => false);

        var later = ProfileInstanceGuard.Acquire(profile, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), out var outcome);

        Assert.Null(later);
        Assert.Equal(ProfileInstanceOutcome.ExistingNotResponding, outcome);
    }

    [Fact]
    public void ProfilesAreIndependentAndOneFolderIsOneProfileHoweverSpelled()
    {
        string profile = NewProfile();
        Assert.Equal(ProfileInstanceGuard.ProfileKey(profile), ProfileInstanceGuard.ProfileKey(profile.ToLowerInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(ProfileInstanceGuard.ProfileKey(profile), ProfileInstanceGuard.ProfileKey(profile + "-other"));

        using var running = new RunningCopy(profile, () => true);
        using var other = ProfileInstanceGuard.Acquire(NewProfile(), TimeSpan.Zero, TimeSpan.Zero, out var outcome);
        Assert.NotNull(other);
        Assert.Equal(ProfileInstanceOutcome.Acquired, outcome);
    }

    [Fact]
    public void ACopyThatEndedWithoutReleasingLeavesTheProfileFree()
    {
        string profile = NewProfile();
        var ended = new Thread(() => ProfileInstanceGuard.Acquire(profile, TimeSpan.Zero, TimeSpan.Zero, out _));
        ended.Start();
        Assert.True(ended.Join(TimeSpan.FromSeconds(5))); // Its thread ends while owning the profile.

        using var later = ProfileInstanceGuard.Acquire(profile, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), out var outcome);
        Assert.NotNull(later);
        Assert.Equal(ProfileInstanceOutcome.Acquired, outcome);
    }

    /// <summary>Owns a profile on its own thread, as a running copy does on its UI thread, until disposed.</summary>
    private sealed class RunningCopy : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _close = new();
        private int _disposed;

        public RunningCopy(string profile, Func<bool> bringForward)
        {
            using var ready = new ManualResetEventSlim();
            ProfileInstanceOutcome outcome = default;
            _thread = new Thread(() =>
            {
                using var guard = ProfileInstanceGuard.Acquire(profile, TimeSpan.Zero, TimeSpan.Zero, out outcome);
                guard?.ListenForLaterLaunches(bringForward);
                ready.Set();
                _close.Wait();
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(ProfileInstanceOutcome.Acquired, outcome);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _close.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _close.Dispose();
        }
    }
}
