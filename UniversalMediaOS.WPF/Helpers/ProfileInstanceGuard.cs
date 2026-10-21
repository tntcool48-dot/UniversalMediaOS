using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UniversalMediaOS.WPF.Helpers
{
    internal enum ProfileInstanceOutcome
    {
        Acquired,
        ActivatedExisting,
        ExistingNotResponding
    }

    /// <summary>One running app per profile: two copies would share one download queue, settings file and
    /// database. A later launch asks the running copy to bring its window forward and exits. While that copy
    /// is closing it does not answer, so the later launch waits for the profile and then starts.</summary>
    internal sealed class ProfileInstanceGuard : IDisposable
    {
        private const int AnyProcess = -1;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        private readonly Mutex _owner;
        private readonly EventWaitHandle _activate;
        private readonly EventWaitHandle _activated;
        private RegisteredWaitHandle? _listener;
        private bool _disposed;

        private ProfileInstanceGuard(Mutex owner, EventWaitHandle activate, EventWaitHandle activated)
        {
            _owner = owner;
            _activate = activate;
            _activated = activated;
        }

        /// <summary>The same folder spelled differently (case, trailing separator) is the same profile.</summary>
        internal static string ProfileKey(string profileDirectory)
        {
            string normalized = Path.GetFullPath(profileDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
        }

        /// <summary>Returns the guard when this launch owns the profile; otherwise null, with the reason.</summary>
        internal static ProfileInstanceGuard? Acquire(string profileDirectory, TimeSpan handOffWait, TimeSpan closingWait,
            out ProfileInstanceOutcome outcome)
        {
            string key = ProfileKey(profileDirectory);
            var owner = new Mutex(false, @"Local\UniversalMediaOS.Profile." + key);
            var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UniversalMediaOS.Activate." + key);
            var activated = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UniversalMediaOS.Activated." + key);
            try
            {
                if (TryOwn(owner, TimeSpan.Zero))
                {
                    outcome = ProfileInstanceOutcome.Acquired;
                    return new ProfileInstanceGuard(owner, activate, activated);
                }

                // This launch is in the foreground; let the running copy take it when it comes forward.
                AllowSetForegroundWindow(AnyProcess);
                activated.Reset();
                activate.Set();
                if (activated.WaitOne(handOffWait))
                {
                    outcome = ProfileInstanceOutcome.ActivatedExisting;
                    DisposeHandles(owner, activate, activated);
                    return null;
                }

                // Unanswered: usually the running copy is closing. Start once it has let go of the profile.
                activate.Reset();
                if (TryOwn(owner, closingWait))
                {
                    outcome = ProfileInstanceOutcome.Acquired;
                    return new ProfileInstanceGuard(owner, activate, activated);
                }

                outcome = ProfileInstanceOutcome.ExistingNotResponding;
                DisposeHandles(owner, activate, activated);
                return null;
            }
            catch
            {
                DisposeHandles(owner, activate, activated);
                throw;
            }
        }

        /// <summary>Runs <paramref name="bringForward"/> on a pool thread for each later launch. The later launch
        /// exits only when it returns true; return false while closing, so that launch waits for the profile.</summary>
        internal void ListenForLaterLaunches(Func<bool> bringForward)
        {
            _listener = ThreadPool.RegisterWaitForSingleObject(_activate, (_, timedOut) =>
            {
                try
                {
                    if (!timedOut && bringForward()) _activated.Set();
                }
                catch (ObjectDisposedException) { } // A request that arrived while this copy was exiting.
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }

        /// <summary>Call on the thread that acquired the guard, once the app has finished with the profile.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_listener != null)
            {
                // Let a request already being answered finish before its events are disposed.
                using var unregistered = new ManualResetEvent(false);
                if (_listener.Unregister(unregistered)) unregistered.WaitOne(TimeSpan.FromSeconds(2));
            }
            try { _owner.ReleaseMutex(); }
            catch (ApplicationException) { } // Another thread acquired it; process exit releases it instead.
            DisposeHandles(_owner, _activate, _activated);
        }

        private static bool TryOwn(Mutex owner, TimeSpan wait)
        {
            try { return owner.WaitOne(wait); }
            catch (AbandonedMutexException) { return true; } // The previous copy ended without releasing it.
        }

        private static void DisposeHandles(params WaitHandle[] handles)
        {
            foreach (WaitHandle handle in handles) handle.Dispose();
        }
    }
}
