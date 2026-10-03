using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Archiving;

public enum DownloadJobStatus
{
    Pending,
    Running,
    Pausing,
    Paused,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

public partial class DownloadQueueJob : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _audioPreference = "Sub";
    [ObservableProperty] private int? _malId;
    [ObservableProperty] private int _totalEpisodes;
    [ObservableProperty] private DownloadJobStatus _status = DownloadJobStatus.Pending;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = "Waiting in queue";
    [ObservableProperty] private string? _completedFilePath;
    [ObservableProperty] private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    [ObservableProperty] private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    [ObservableProperty] private DateTimeOffset? _completedAt;

    [JsonIgnore]
    public bool CanPause => Status is DownloadJobStatus.Pending or DownloadJobStatus.Running;

    [JsonIgnore]
    public bool CanResume => Status == DownloadJobStatus.Paused;

    [JsonIgnore]
    public bool CanCancel => Status is DownloadJobStatus.Pending or DownloadJobStatus.Running or
        DownloadJobStatus.Pausing or DownloadJobStatus.Paused;

    [JsonIgnore]
    public bool CanRetry => Status is DownloadJobStatus.Failed or DownloadJobStatus.Cancelled;

    [JsonIgnore]
    public bool CanRemove => Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or
        DownloadJobStatus.Cancelled;

    [JsonIgnore]
    public string ProgressText => Status == DownloadJobStatus.Completed
        ? "100%"
        : $"{Math.Clamp(Progress, 0, 100):F0}%";

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadJobStatus.Pending => "Queued",
        DownloadJobStatus.Running => "Downloading",
        DownloadJobStatus.Pausing => "Pausing...",
        DownloadJobStatus.Paused => "Paused",
        DownloadJobStatus.Cancelling => "Cancelling...",
        DownloadJobStatus.Completed => "Completed",
        DownloadJobStatus.Failed => "Failed",
        DownloadJobStatus.Cancelled => "Cancelled",
        _ => Status.ToString()
    };

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));

    partial void OnStatusChanged(DownloadJobStatus value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressText));
    }
}

public sealed record DownloadExecutionResult(bool Success, string? CompletedFilePath = null);

public interface IDownloadJobExecutor
{
    Task<DownloadExecutionResult> ExecuteAsync(
        DownloadQueueJob job,
        Action<string> log,
        Action<double> progress,
        CancellationToken token);

    Task<bool> PauseActiveAsync(CancellationToken token);
    Task<bool> CancelActiveAsync(CancellationToken token);
}

public sealed class SeasonDownloadJobExecutor : IDownloadJobExecutor
{
    private readonly DomainHotSwapper _config;
    private readonly object _sync = new();
    private SeasonDownloader? _activeDownloader;

    public SeasonDownloadJobExecutor(DomainHotSwapper config)
    {
        _config = config;
    }

    public async Task<DownloadExecutionResult> ExecuteAsync(
        DownloadQueueJob job,
        Action<string> log,
        Action<double> progress,
        CancellationToken token)
    {
        using var downloader = new SeasonDownloader(_config);
        lock (_sync) _activeDownloader = downloader;
        try
        {
            bool success = await downloader.DownloadSeasonAsync(
                job.Title,
                log,
                progress,
                token,
                job.AudioPreference);
            return new DownloadExecutionResult(success, downloader.LastCompletedVideoPath);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeDownloader, downloader)) _activeDownloader = null;
            }
        }
    }

    public Task<bool> PauseActiveAsync(CancellationToken token)
    {
        lock (_sync)
        {
            return _activeDownloader?.PauseActiveTransferAsync(token) ?? Task.FromResult(true);
        }
    }

    public Task<bool> CancelActiveAsync(CancellationToken token)
    {
        lock (_sync)
        {
            return _activeDownloader?.CancelActiveTransferAsync(token) ?? Task.FromResult(true);
        }
    }
}

/// <summary>
/// A durable, single-transfer download queue. Running jobs are recovered as
/// paused after an unclean exit; they only resume after explicit user action.
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<DownloadQueueJob> _jobs = new();
    private readonly IDownloadJobExecutor _executor;
    private readonly string _storagePath;
    private readonly Task _worker;
    private CancellationTokenSource? _activeJobCts;
    private Guid? _activeJobId;
    private bool _disposed;

    public event EventHandler? JobsChanged;
    public event EventHandler<DownloadQueueJob>? JobCompleted;

    public DownloadQueueService(DomainHotSwapper config, IDownloadJobExecutor executor)
        : this(GetDefaultStoragePath(), executor)
    {
    }

    private static string GetDefaultStoragePath()
    {
        string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;
        return Path.Combine(appData, "UniversalMediaOS", "download-queue.json");
    }

    public DownloadQueueService(string storagePath, IDownloadJobExecutor executor)
    {
        _storagePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        LoadQueue();
        _worker = Task.Run(ProcessQueueAsync);
        if (GetJobsSnapshot().Any(job => job.Status == DownloadJobStatus.Pending)) _signal.Release();
    }

    public IReadOnlyList<DownloadQueueJob> GetJobsSnapshot()
    {
        lock (_sync)
        {
            return _jobs.OrderByDescending(job => job.CreatedAt).ToArray();
        }
    }

    public DownloadQueueJob Enqueue(
        string title,
        string? audioPreference = null,
        int? malId = null,
        int totalEpisodes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is required.", nameof(title));

        DownloadQueueJob job;
        bool added = false;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(existing =>
                existing.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existing.AudioPreference.Equals(audioPreference ?? "Sub", StringComparison.OrdinalIgnoreCase) &&
                existing.Status is DownloadJobStatus.Pending or DownloadJobStatus.Running or
                    DownloadJobStatus.Pausing or DownloadJobStatus.Paused) ?? new DownloadQueueJob
                {
                    Title = title.Trim(),
                    AudioPreference = string.IsNullOrWhiteSpace(audioPreference) ? "Sub" : audioPreference.Trim(),
                    MalId = malId,
                    TotalEpisodes = Math.Max(0, totalEpisodes)
                };

            if (!_jobs.Contains(job))
            {
                _jobs.Add(job);
                added = true;
            }
        }

        if (added)
        {
            PersistQueue();
            RaiseJobsChanged();
            _signal.Release();
        }
        return job;
    }

    public async Task<bool> PauseAsync(Guid jobId, CancellationToken token = default)
    {
        DownloadQueueJob? job;
        CancellationTokenSource? activeCts = null;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.Status == DownloadJobStatus.Pending)
            {
                SetStatus(job, DownloadJobStatus.Paused, "Paused before starting");
            }
            else if (job?.Status == DownloadJobStatus.Running && _activeJobId == jobId)
            {
                SetStatus(job, DownloadJobStatus.Pausing, "Stopping the active transfer safely...");
                activeCts = _activeJobCts;
            }
            else
            {
                return false;
            }
        }

        PersistQueue();
        RaiseJobsChanged();
        if (activeCts == null) return true;

        bool stopped = await _executor.PauseActiveAsync(token);
        if (stopped)
        {
            TryCancel(activeCts);
            return true;
        }

        lock (_sync)
        {
            if (job.Status == DownloadJobStatus.Pausing)
                SetStatus(job, DownloadJobStatus.Running, "The torrent client rejected the pause request");
        }
        PersistQueue();
        RaiseJobsChanged();
        return false;
    }

    public bool Resume(Guid jobId)
    {
        DownloadQueueJob? job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.Status != DownloadJobStatus.Paused) return false;
            SetStatus(job, DownloadJobStatus.Pending, "Waiting to resume");
        }
        PersistQueue();
        RaiseJobsChanged();
        _signal.Release();
        return true;
    }

    public bool Retry(Guid jobId)
    {
        DownloadQueueJob? job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.Status is not (DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)) return false;
            job.Progress = 0;
            job.CompletedAt = null;
            job.CompletedFilePath = null;
            SetStatus(job, DownloadJobStatus.Pending, "Waiting to retry");
        }
        PersistQueue();
        RaiseJobsChanged();
        _signal.Release();
        return true;
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken token = default)
    {
        DownloadQueueJob? job;
        CancellationTokenSource? activeCts = null;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job?.Status is DownloadJobStatus.Pending or DownloadJobStatus.Paused)
            {
                SetStatus(job, DownloadJobStatus.Cancelled, "Cancelled; partial files were retained");
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            else if ((job?.Status is DownloadJobStatus.Running or DownloadJobStatus.Pausing) && _activeJobId == jobId)
            {
                SetStatus(job, DownloadJobStatus.Cancelling, "Stopping the active transfer...");
                activeCts = _activeJobCts;
            }
            else
            {
                return false;
            }
        }

        PersistQueue();
        RaiseJobsChanged();
        if (activeCts == null) return true;

        bool stopped = await _executor.CancelActiveAsync(token);
        if (stopped)
        {
            TryCancel(activeCts);
            return true;
        }

        lock (_sync)
        {
            if (job.Status == DownloadJobStatus.Cancelling)
                SetStatus(job, DownloadJobStatus.Running, "The torrent client rejected the cancel request");
        }
        PersistQueue();
        RaiseJobsChanged();
        return false;
    }

    public bool Remove(Guid jobId)
    {
        bool removed;
        lock (_sync)
        {
            int index = _jobs.FindIndex(job => job.Id == jobId && job.CanRemove);
            removed = index >= 0;
            if (removed) _jobs.RemoveAt(index);
        }
        if (removed)
        {
            PersistQueue();
            RaiseJobsChanged();
        }
        return removed;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _signal.WaitAsync(_shutdown.Token);
                DownloadQueueJob? job;
                do
                {
                    lock (_sync)
                    {
                        job = _jobs
                            .Where(item => item.Status == DownloadJobStatus.Pending)
                            .OrderBy(item => item.CreatedAt)
                            .FirstOrDefault();
                        if (job != null)
                        {
                            _activeJobId = job.Id;
                            _activeJobCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                            SetStatus(job, DownloadJobStatus.Running, "Starting download...");
                        }
                    }
                    if (job == null) break;

                    PersistQueue();
                    RaiseJobsChanged();
                    DateTimeOffset lastPersistedAt = DateTimeOffset.MinValue;
                    double lastPersistedProgress = -1;
                    try
                    {
                        DownloadExecutionResult result = await _executor.ExecuteAsync(
                            job,
                            message => UpdateMessage(job, message),
                            progress =>
                            {
                                lock (_sync)
                                {
                                    job.Progress = Math.Clamp(progress, 0, 100);
                                    job.UpdatedAt = DateTimeOffset.UtcNow;
                                }
                                RaiseJobsChanged();
                                if (Math.Abs(job.Progress - lastPersistedProgress) >= 2 ||
                                    DateTimeOffset.UtcNow - lastPersistedAt >= TimeSpan.FromSeconds(10))
                                {
                                    PersistQueue();
                                    lastPersistedProgress = job.Progress;
                                    lastPersistedAt = DateTimeOffset.UtcNow;
                                }
                            },
                            _activeJobCts!.Token);

                        lock (_sync)
                        {
                            if (job.Status == DownloadJobStatus.Pausing)
                                SetStatus(job, DownloadJobStatus.Paused, "Paused; partial data is available for resume");
                            else if (job.Status == DownloadJobStatus.Cancelling)
                                CompleteCancellation(job);
                            else if (result.Success)
                            {
                                job.Progress = 100;
                                job.CompletedFilePath = result.CompletedFilePath;
                                job.CompletedAt = DateTimeOffset.UtcNow;
                                SetStatus(job, DownloadJobStatus.Completed, "Download and validation completed");
                            }
                            else
                            {
                                job.CompletedAt = DateTimeOffset.UtcNow;
                                SetStatus(job, DownloadJobStatus.Failed, "Download failed; retry will reuse any partial data");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_sync)
                        {
                            if (job.Status == DownloadJobStatus.Cancelling) CompleteCancellation(job);
                            else SetStatus(job, DownloadJobStatus.Paused, "Paused; partial data is available for resume");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            job.CompletedAt = DateTimeOffset.UtcNow;
                            SetStatus(job, DownloadJobStatus.Failed, ex.Message);
                        }
                        AppLogger.Log($"Download queue job failed: {ex}", "ERROR");
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _activeJobCts?.Dispose();
                            _activeJobCts = null;
                            _activeJobId = null;
                        }
                        PersistQueue();
                        RaiseJobsChanged();
                    }

                    if (job.Status == DownloadJobStatus.Completed) JobCompleted?.Invoke(this, job);
                }
                while (!_shutdown.IsCancellationRequested);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void UpdateMessage(DownloadQueueJob job, string message)
    {
        lock (_sync)
        {
            if (job.Status == DownloadJobStatus.Running)
            {
                job.StatusMessage = message;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        RaiseJobsChanged();
    }

    private static void SetStatus(DownloadQueueJob job, DownloadJobStatus status, string message)
    {
        job.Status = status;
        job.StatusMessage = message;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void CompleteCancellation(DownloadQueueJob job)
    {
        job.CompletedAt = DateTimeOffset.UtcNow;
        SetStatus(job, DownloadJobStatus.Cancelled, "Cancelled; partial files were retained");
    }

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;
            string json = File.ReadAllText(_storagePath);
            var loaded = JsonSerializer.Deserialize<List<DownloadQueueJob>>(json, JsonOptions) ?? new();
            foreach (DownloadQueueJob job in loaded)
            {
                if (job.Status is DownloadJobStatus.Running or DownloadJobStatus.Pausing)
                {
                    SetStatus(job, DownloadJobStatus.Paused, "The app closed during this job; resume when ready");
                }
                else if (job.Status == DownloadJobStatus.Cancelling)
                {
                    CompleteCancellation(job);
                }
                _jobs.Add(job);
            }
            PersistQueue();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Could not restore download queue: {ex.Message}", "WARNING");
            TryQuarantineInvalidQueue();
        }
    }

    private void PersistQueue()
    {
        _persistenceLock.Wait();
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_jobs, JsonOptions);
            string? directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Could not persist download queue: {ex.Message}", "ERROR");
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    private void TryQuarantineInvalidQueue()
    {
        try
        {
            if (File.Exists(_storagePath))
                File.Move(_storagePath, _storagePath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch
        {
        }
    }

    private void RaiseJobsChanged() => JobsChanged?.Invoke(this, EventArgs.Empty);

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Guid? activeId;
            lock (_sync) activeId = _activeJobId;
            if (activeId.HasValue)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                PauseAsync(activeId.Value, timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Could not pause the active download during shutdown: {ex.Message}", "WARNING");
        }
        finally
        {
            _shutdown.Cancel();
            try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { }
            PersistQueue();
            _shutdown.Dispose();
        }
    }
}
