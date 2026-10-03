using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.Archiving;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class DownloadQueueServiceTests
{
    [Fact]
    public async Task Queue_RunsOneJobAtATime_AndPausedPendingJobWaitsForResume()
    {
        using var sandbox = new QueueSandbox();
        var executor = new ControlledExecutor();
        using var queue = new DownloadQueueService(sandbox.QueuePath, executor);

        DownloadQueueJob first = queue.Enqueue("First show");
        await executor.WaitForStartAsync(first.Id);
        DownloadQueueJob second = queue.Enqueue("Second show", "Dub");

        Assert.True(await queue.PauseAsync(second.Id));
        Assert.Equal(DownloadJobStatus.Paused, second.Status);

        executor.Complete(first.Id, success: true, "episode-1.mkv");
        await WaitUntilAsync(() => first.Status == DownloadJobStatus.Completed);
        Assert.False(executor.HasStarted(second.Id));

        Assert.True(queue.Resume(second.Id));
        await executor.WaitForStartAsync(second.Id);
        executor.Complete(second.Id, success: true);
        await WaitUntilAsync(() => second.Status == DownloadJobStatus.Completed);
    }

    [Fact]
    public async Task PauseActive_StopsExecutorBeforePersistingPausedState()
    {
        using var sandbox = new QueueSandbox();
        var executor = new ControlledExecutor();
        using var queue = new DownloadQueueService(sandbox.QueuePath, executor);
        DownloadQueueJob job = queue.Enqueue("Pause me");
        await executor.WaitForStartAsync(job.Id);

        Assert.True(await queue.PauseAsync(job.Id));
        await WaitUntilAsync(() => job.Status == DownloadJobStatus.Paused);

        Assert.Equal(1, executor.PauseCalls);
        string persisted = await File.ReadAllTextAsync(sandbox.QueuePath);
        Assert.Contains("\"Status\": 3", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelActive_StopsExecutorAndRetainsHistory()
    {
        using var sandbox = new QueueSandbox();
        var executor = new ControlledExecutor();
        using var queue = new DownloadQueueService(sandbox.QueuePath, executor);
        DownloadQueueJob job = queue.Enqueue("Cancel me");
        await executor.WaitForStartAsync(job.Id);

        Assert.True(await queue.CancelAsync(job.Id));
        await WaitUntilAsync(() => job.Status == DownloadJobStatus.Cancelled);

        Assert.Equal(1, executor.CancelCalls);
        Assert.Contains(queue.GetJobsSnapshot(), item => item.Id == job.Id);
        Assert.True(job.CanRetry);
    }

    [Fact]
    public void LoadQueue_RecoversInterruptedRunningJobAsPaused()
    {
        using var sandbox = new QueueSandbox();
        var interrupted = new DownloadQueueJob
        {
            Title = "Interrupted show",
            Status = DownloadJobStatus.Running,
            Progress = 41
        };
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.QueuePath)!);
        File.WriteAllText(sandbox.QueuePath, JsonSerializer.Serialize(new[] { interrupted }));

        using var queue = new DownloadQueueService(sandbox.QueuePath, new ControlledExecutor());
        DownloadQueueJob restored = Assert.Single(queue.GetJobsSnapshot());

        Assert.Equal(DownloadJobStatus.Paused, restored.Status);
        Assert.Equal(41, restored.Progress);
        Assert.Contains("closed", restored.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enqueue_DeduplicatesEquivalentActiveJob()
    {
        using var sandbox = new QueueSandbox();
        using var queue = new DownloadQueueService(sandbox.QueuePath, new ControlledExecutor());

        DownloadQueueJob first = queue.Enqueue("Same Show", "Sub");
        DownloadQueueJob duplicate = queue.Enqueue(" same show ", "sub");

        Assert.Same(first, duplicate);
        Assert.Single(queue.GetJobsSnapshot());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class ControlledExecutor : IDownloadJobExecutor
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DownloadExecutionResult>> _executions = new();
        private readonly SemaphoreSlim _started = new(0);

        public int PauseCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public async Task<DownloadExecutionResult> ExecuteAsync(
            DownloadQueueJob job,
            Action<string> log,
            Action<double> progress,
            CancellationToken token)
        {
            var completion = new TaskCompletionSource<DownloadExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _executions[job.Id] = completion;
            _started.Release();
            log("Controlled test transfer started");
            progress(25);
            return await completion.Task.WaitAsync(token);
        }

        public Task<bool> PauseActiveAsync(CancellationToken token)
        {
            PauseCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> CancelActiveAsync(CancellationToken token)
        {
            CancelCalls++;
            return Task.FromResult(true);
        }

        public bool HasStarted(Guid id) => _executions.ContainsKey(id);

        public async Task WaitForStartAsync(Guid id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!HasStarted(id)) await _started.WaitAsync(timeout.Token);
        }

        public void Complete(Guid id, bool success, string? path = null)
        {
            Assert.True(_executions.TryGetValue(id, out var completion));
            completion.TrySetResult(new DownloadExecutionResult(success, path));
        }
    }

    private sealed class QueueSandbox : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "UniversalMediaOS-queue-tests",
            Guid.NewGuid().ToString("N"));

        public string QueuePath => Path.Combine(_directory, "download-queue.json");

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch { }
        }
    }
}
