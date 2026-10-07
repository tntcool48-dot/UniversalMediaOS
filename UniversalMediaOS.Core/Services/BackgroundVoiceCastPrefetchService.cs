using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class VoiceCastPrefetchProgressEventArgs : EventArgs
    {
        public VoiceCastPrefetchProgressEventArgs(string message, int processed, int found, int failed, bool isRunning)
        {
            Message = message;
            Processed = processed;
            Found = found;
            Failed = failed;
            IsRunning = isRunning;
        }

        public string Message { get; }
        public int Processed { get; }
        public int Found { get; }
        public int Failed { get; }
        public bool IsRunning { get; }
    }

    public sealed class BackgroundVoiceCastPrefetchService : IDisposable
    {
        private readonly VoiceCastService _voiceCastService;
        private CancellationTokenSource? _cts;
        private Task? _activeTask;
        private readonly object _gate = new();
        private bool _disposed;

        public event EventHandler<VoiceCastPrefetchProgressEventArgs>? ProgressChanged;

        public bool IsRunning { get { lock (_gate) return _activeTask is { IsCompleted: false }; } }

        public BackgroundVoiceCastPrefetchService(VoiceCastService voiceCastService)
        {
            _voiceCastService = voiceCastService;
        }

        public Task StartAsync(VoiceLanguageMode mode, int maxItems = 40, TimeSpan? delayBetweenItems = null)
        {
            lock (_gate)
            {
                if (_disposed || _activeTask is { IsCompleted: false }) return Task.CompletedTask;
                var operation = new CancellationTokenSource();
                var token = operation.Token;
                _cts = operation;
                _activeTask = Task.Run(async () =>
                {
                    try { await RunAsync(mode, Math.Max(1, maxItems), delayBetweenItems ?? TimeSpan.FromSeconds(5), token); }
                    finally
                    {
                        lock (_gate) if (ReferenceEquals(_cts, operation)) _cts = null;
                        operation.Dispose();
                    }
                });
                return Task.CompletedTask;
            }
        }

        public void Stop()
        {
            lock (_gate) _cts?.Cancel();
        }

        private async Task RunAsync(VoiceLanguageMode mode, int maxItems, TimeSpan delay, CancellationToken token)
        {
            int processed = 0;
            int found = 0;
            int failed = 0;

            try
            {
                Raise($"Prefetching missing {mode} VA data...", processed, found, failed, true);
                var entries = await _voiceCastService.GetLibraryEntriesMissingCastAsync(mode, maxItems, token);
                foreach (MalLibraryEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var media = VoiceCastMedia.FromLibraryEntry(entry);
                    Raise($"Fetching {mode} VAs for {media.Title}...", processed, found, failed, true);
                    try
                    {
                        var result = await _voiceCastService.FetchAndCacheCastAsync(media, mode, token: token);
                        if (result.NotFound) failed++;
                        else found++;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        AppLogger.Log($"VA prefetch source unavailable for MAL {media.MalId}: {ex.GetType().Name}", "WARNING");
                    }
                    processed++;
                    Raise($"Prefetched {processed}/{entries.Count}: {media.Title}", processed, found, failed, true);
                    if (processed < entries.Count) await Task.Delay(delay, token);
                }

                Raise($"VA prefetch complete: {found} found, {failed} unavailable.", processed, found, failed, false);
            }
            catch (OperationCanceledException)
            {
                Raise("VA prefetch stopped.", processed, found, failed, false);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"VA prefetch failed: {ex.GetType().Name}", "WARNING");
                Raise("VA prefetch failed. Your saved cast was kept; use Prefetch Missing to retry.", processed, found, failed, false);
            }
        }

        private void Raise(string message, int processed, int found, int failed, bool isRunning)
        {
            ProgressChanged?.Invoke(this, new VoiceCastPrefetchProgressEventArgs(message, processed, found, failed, isRunning));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _cts?.Cancel();
            }
        }
    }
}
