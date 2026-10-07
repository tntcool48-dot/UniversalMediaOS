using System;
using System.Threading;
using LibVLCSharp.Shared;

namespace UniversalMediaOS.WPF.Helpers;

// The bundled Windows plugins consume TLS slots on each complete engine
// unload/reload. Keep the engine for the process; players and media stay owned
// by their individual tabs. Shutdown releases it after the final player lease.
internal sealed class NativePlaybackEngine
{
    private static readonly Lazy<NativePlaybackEngine> ProcessOwner = new(() =>
    {
        var owner = new NativePlaybackEngine();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => owner.RequestShutdown();
        return owner;
    });
    private readonly object _gate = new();
    private LibVLC? _engine;
    private int _leases;
    private bool _shutdown;

    internal static NativePlaybackEngine ForProcess => ProcessOwner.Value;
    internal static void ShutdownProcess()
    {
        if (ProcessOwner.IsValueCreated) ProcessOwner.Value.RequestShutdown();
    }

    internal Lease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown, this);
            if (_engine == null)
            {
                LibVLCSharp.Shared.Core.Initialize();
                _engine = new LibVLC(enableDebugLogs: false);
            }
            var lease = new Lease(this, _engine);
            _leases++;
            return lease;
        }
    }

    internal void RequestShutdown()
    {
        LibVLC? retired;
        lock (_gate)
        {
            _shutdown = true;
            retired = RetireIfDrained();
        }
        retired?.Dispose();
    }

    private void Release()
    {
        LibVLC? retired;
        lock (_gate)
        {
            _leases--;
            retired = RetireIfDrained();
        }
        retired?.Dispose();
    }

    private LibVLC? RetireIfDrained()
    {
        if (!_shutdown || _leases != 0) return null;
        LibVLC? engine = _engine;
        _engine = null;
        return engine;
    }

    internal sealed class Lease(NativePlaybackEngine owner, LibVLC engine) : IDisposable
    {
        private NativePlaybackEngine? _owner = owner;
        internal LibVLC Engine
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _owner) == null, this);
                return engine;
            }
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
