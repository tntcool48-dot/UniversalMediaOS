using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Streaming;

// These limits are internal so controlled stalled-transfer tests can use short
// deadlines without changing the application's production policy.
internal sealed record ProxyRequestLimits
{
    public int MaximumSessions { get; init; } = 64;
    public int MaximumRequests { get; init; } = 32;
    public int MaximumSessionRequests { get; init; } = 8;
    public TimeSpan SessionIdleLifetime { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan ManifestDeadline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan KeyDeadline { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SubtitleDeadline { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeaderDeadline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MediaDeadline { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MediaIdleDeadline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownDeadline { get; init; } = TimeSpan.FromSeconds(2);
    public long MaximumSegmentBytes { get; init; } = 64 * 1024 * 1024;
    public long MaximumDirectMediaBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public TimeSpan DirectMediaDeadline { get; init; } = TimeSpan.FromHours(24);
}

public sealed partial class HlsLoopbackProxy
{
    private sealed class RegisteredSession(ProxySession session)
    {
        internal ProxySession Value { get; } = session;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal DateTime LastActivityUtc { get; set; } = session.CreatedAt < DateTime.UtcNow
            ? session.CreatedAt : DateTime.UtcNow;
        internal int ActiveRequests { get; set; }
    }

    private readonly HttpClient _remoteHttp;
    private readonly ProxyRequestLimits _limits;
    private CancellationTokenSource? _runCancellation;
    private Task _acceptTask = Task.CompletedTask;
    private TaskCompletionSource _idleCompletion = CompletedIdle();
    private int _activeRequests;

    internal int ActiveRequestCount { get { lock (_lifecycleLock) return _activeRequests; } }
    internal int SessionCount => _sessions.Count;

    private static TaskCompletionSource CompletedIdle()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }

    private bool IsExpired(RegisteredSession session) => session.ActiveRequests == 0 &&
        session.LastActivityUtc < DateTime.UtcNow - _limits.SessionIdleLifetime;

    private bool RemoveSession(string id)
    {
        // All session acquisition/removal takes this lock. A request links its
        // token before removal can cancel/dispose the owning source.
        lock (_lifecycleLock)
        {
            if (!_sessions.TryRemove(id, out var session)) return false;
            _ = CancelAndDisposeAsync(session.Cancellation);
            return true;
        }
    }

    private void StopCore()
    {
        var run = _runCancellation;
        _runCancellation = null;
        if (run != null) _ = CancelAndDisposeAsync(run);
        foreach (string id in _sessions.Keys) RemoveSession(id);
        CloseListener();
        ListeningUri = null;
    }

    public async Task StopAsync()
    {
        Task pending;
        lock (_lifecycleLock)
        {
            StopCore();
            pending = Task.WhenAll(_acceptTask, _idleCompletion.Task);
        }
        try { await pending.WaitAsync(_limits.ShutdownDeadline).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Synchronous UI disposal never waits for network work. This async
            // drain also stays bounded if a transport fails to honor cancellation.
            AppLogger.Log("[HLS Proxy] Shutdown drain reached its deadline.", "WARNING");
        }
    }

    private bool TryAdmitRequest(CancellationToken runToken)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || runToken.IsCancellationRequested || _activeRequests >= _limits.MaximumRequests)
                return false;
            if (_activeRequests++ == 0)
                _idleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void CompleteRequest(RegisteredSession? session)
    {
        lock (_lifecycleLock)
        {
            if (session != null)
            {
                session.ActiveRequests--;
                session.LastActivityUtc = DateTime.UtcNow;
            }
            if (--_activeRequests == 0) _idleCompletion.TrySetResult();
        }
    }

    private static void RejectRequest(System.Net.HttpListenerResponse response, int status)
    {
        try { response.StatusCode = status; response.ContentLength64 = 0; response.Close(); }
        catch { try { response.Abort(); } catch { } }
    }

    internal static async Task<long> CopyMediaBodyAsync(Stream input, Stream output,
        ReadOnlyMemory<byte> prefix, long maximumBytes, long? expectedLength,
        TimeSpan idleDeadline, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        byte[] buffer = new byte[16 * 1024];
        long total = 0;
        ReadOnlyMemory<byte> next = prefix;
        while (true)
        {
            if (next.Length > 0)
            {
                if (total + next.Length > maximumBytes)
                    throw new InvalidDataException("The media resource exceeded its size limit.");
                total += next.Length;
                idle.CancelAfter(idleDeadline);
                await output.WriteAsync(next, idle.Token).ConfigureAwait(false);
            }
            idle.CancelAfter(idleDeadline);
            int read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read == 0) break;
            next = buffer.AsMemory(0, read);
        }
        if (total == 0 || expectedLength is { } expected && total != expected)
            throw new EndOfStreamException("The media resource was empty or ended before its declared length.");
        return total;
    }
}
