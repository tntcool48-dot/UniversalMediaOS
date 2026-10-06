using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class InstalledEpisodeItem
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string FileSizeText { get; set; } = string.Empty;
        public bool IsEpub =>
            Path.GetExtension(FullPath).Equals(".epub", StringComparison.OrdinalIgnoreCase);
        public bool IsBook
        {
            get
            {
                string extension = Path.GetExtension(FullPath);
                return extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            }
        }
        public string OpenActionText => IsBook ? "Read" : "Play";
    }

    public partial class DownloadsViewModel : ObservableObject
    {
        private readonly DownloadQueueService _downloadQueue;
        private readonly DomainHotSwapper _config;
        private readonly Helpers.IDialogService _dialogService;
        private readonly Helpers.IExternalLauncher _externalLauncher;
        private readonly LocalBookImportService _localBookImportService;
        private Action<string, string>? _playMediaAction;

        public Helpers.ObservableRangeCollection<InstalledEpisodeItem> InstalledFiles { get; } = new();
        public Helpers.ObservableRangeCollection<DownloadQueueJob> DownloadJobs { get; } = new();

        [ObservableProperty]
        private bool _isEmpty = true;

        [ObservableProperty]
        private bool _hasDownloadJobs;

        public DownloadsViewModel(
            DownloadQueueService downloadQueue,
            DomainHotSwapper config,
            Helpers.IDialogService dialogService,
            Helpers.IExternalLauncher externalLauncher,
            LocalBookImportService localBookImportService)
        {
            _downloadQueue = downloadQueue;
            _config = config;
            _dialogService = dialogService;
            _externalLauncher = externalLauncher;
            _localBookImportService = localBookImportService;
            _downloadQueue.JobsChanged += DownloadQueue_JobsChanged;
            RefreshQueueJobs();
            // MainViewModel refreshes this singleton whenever Downloads is opened.
            // Starting the async command here runs its collection updates without a
            // WPF synchronization context and can race the first navigation.
        }

        public void RegisterPlayMediaAction(Action<string, string> playAction)
        {
            _playMediaAction = playAction;
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task RefreshDownloadsAsync()
        {
            AppLogger.Log("RefreshDownloads command invoked. Scanning downloads directory...");
            
            string dDir = _config.GetSetting("DownloadDirectory");
            string downloadsPath = string.IsNullOrEmpty(dDir) ? GetDefaultDownloadsPath() : dDir;

            if (!Directory.Exists(downloadsPath)) 
            { 
                AppLogger.Log($"Downloads directory '{downloadsPath}' does not exist.");
                InstalledFiles.Clear();
                IsEmpty = true; 
                return; 
            }

            try
            {
                // Run heavy disk I/O on a background thread to avoid blocking the UI
                var files = await Task.Run(() =>
                {
                    var incomplete = LegacyTorrentReadiness.FindIncompleteFiles(downloadsPath, NativeTorrentCachePath());
                    return
                    Directory.GetFiles(downloadsPath, "*.*", SearchOption.AllDirectories)
                        .Where(IsSupportedDownloadedFile)
                        .Where(f => !IsLibraryStagingFile(f))
                        .Where(f => !incomplete.Contains(Path.GetFullPath(f)) ||
                            AuthorizedMediaDownloadService.ReadLibraryPlayback(f) != null)
                        .Select(f =>
                        {
                            var fi = new FileInfo(f);
                            string size = fi.Length > 1_000_000
                                ? $"{fi.Length / 1_000_000.0:F1} MB"
                                : $"{fi.Length / 1_000.0:F0} KB";
                            var saved = AuthorizedMediaDownloadService.ReadLibraryPlayback(f);
                            string label = Path.GetFileName(f);
                            if (saved != null)
                                label = LibraryPlaybackTitle(saved);
                            return new InstalledEpisodeItem { FileName = label, FullPath = f, FileSizeText = size };
                        })
                        .ToList();
                });

                // Swap atomically
                InstalledFiles.ReplaceRange(files);
                IsEmpty = InstalledFiles.Count == 0;
                AppLogger.Log($"Scan completed. Found {InstalledFiles.Count} media files.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error scanning downloads folder: {ex.Message}", "ERROR");
            }
        }

        [RelayCommand]
        private void OpenDownloadsFolder()
        {
            string dDir = _config.GetSetting("DownloadDirectory");
            string downloadsPath = string.IsNullOrEmpty(dDir) ? GetDefaultDownloadsPath() : dDir;

            AppLogger.Log($"OpenDownloadsFolder command invoked. Path: '{downloadsPath}'");
            if (!_externalLauncher.OpenFolder(downloadsPath))
            {
                AppLogger.Log($"Failed to open downloads folder: '{downloadsPath}'", "ERROR");
            }
        }

        private void DownloadQueue_JobsChanged(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.InvokeAsync(RefreshQueueJobs);
            else
                RefreshQueueJobs();
        }

        private void RefreshQueueJobs()
        {
            var jobs = _downloadQueue.GetJobsSnapshot();
            if (DownloadJobs.Count != jobs.Count || !DownloadJobs.SequenceEqual(jobs))
                DownloadJobs.ReplaceRange(jobs);
            HasDownloadJobs = DownloadJobs.Count > 0;
            PauseJobCommand.NotifyCanExecuteChanged();
            ResumeJobCommand.NotifyCanExecuteChanged();
            CancelJobCommand.NotifyCanExecuteChanged();
            RetryJobCommand.NotifyCanExecuteChanged();
            RemoveJobCommand.NotifyCanExecuteChanged();
        }

        private static bool CanPauseJob(DownloadQueueJob? job) => job?.CanPause == true;

        [RelayCommand(CanExecute = nameof(CanPauseJob), AllowConcurrentExecutions = false)]
        private async Task PauseJobAsync(DownloadQueueJob? job)
        {
            if (job == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            if (!await _downloadQueue.PauseAsync(job.Id, timeout.Token))
                _dialogService.ShowErrorDialog("The active torrent client did not accept the pause request.", "Unable to Pause");
        }

        private static bool CanResumeJob(DownloadQueueJob? job) => job?.CanResume == true;

        [RelayCommand(CanExecute = nameof(CanResumeJob))]
        private void ResumeJob(DownloadQueueJob? job)
        {
            if (job != null) _downloadQueue.Resume(job.Id);
        }

        private static bool CanCancelJob(DownloadQueueJob? job) => job?.CanCancel == true;

        [RelayCommand(CanExecute = nameof(CanCancelJob), AllowConcurrentExecutions = false)]
        private async Task CancelJobAsync(DownloadQueueJob? job)
        {
            if (job == null) return;
            bool confirmed = _dialogService.ShowConfirmDialog(
                $"Cancel '{job.Title}'? Partial files will be retained so they are not destructively removed.",
                "Cancel Download");
            if (!confirmed) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            if (!await _downloadQueue.CancelAsync(job.Id, timeout.Token))
                _dialogService.ShowErrorDialog("The active torrent client did not accept the cancel request.", "Unable to Cancel");
        }

        private static bool CanRetryJob(DownloadQueueJob? job) => job?.CanRetry == true;

        [RelayCommand(CanExecute = nameof(CanRetryJob))]
        private void RetryJob(DownloadQueueJob? job)
        {
            if (job != null) _downloadQueue.Retry(job.Id);
        }

        private static bool CanRemoveJob(DownloadQueueJob? job) => job?.CanRemove == true;

        [RelayCommand(CanExecute = nameof(CanRemoveJob))]
        private void RemoveJob(DownloadQueueJob? job)
        {
            if (job != null) _downloadQueue.Remove(job.Id);
        }

        private static string GetDefaultDownloadsPath()
        {
            return Path.Combine(
                UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS",
                "Downloads");
        }

        private static string NativeTorrentCachePath() => Path.Combine(
            UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TorrentCache");

        internal static bool IsSupportedDownloadedFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLibraryStagingFile(string filePath)
        {
            string parent = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
            return parent.StartsWith(".partial-", StringComparison.OrdinalIgnoreCase) &&
                   Guid.TryParseExact(parent[9..], "N", out _);
        }

        private static string LibraryPlaybackTitle(LibraryMediaPlayback saved)
        {
            var context = saved.Context;
            if (context.Unit.IsFeature) return context.Title;
            string work = string.IsNullOrWhiteSpace(context.Identity.Title) ? context.Title : context.Identity.Title;
            string episodeTitle = context.Unit.Title ?? "";
            if (string.IsNullOrWhiteSpace(episodeTitle) && context.Title != work) episodeTitle = context.Title;
            return $"{work} · S{context.Unit.SeasonNumber:00}E{context.Unit.EpisodeNumber:00}" +
                   (string.IsNullOrWhiteSpace(episodeTitle) ? "" : $" · {episodeTitle}");
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task PlayFileAsync(InstalledEpisodeItem item)
        {
            if (item != null)
            {
                AppLogger.Log($"PlayFile command invoked. File: '{item.FileName}', FullPath: '{item.FullPath}'");
                if (File.Exists(item.FullPath))
                {
                    if (!item.IsBook && AuthorizedMediaDownloadService.ReadLibraryPlayback(item.FullPath) == null &&
                        await Task.Run(() => LegacyTorrentReadiness.FindIncompleteFiles(
                            Path.GetFullPath(_config.GetSetting("DownloadDirectory") is { Length: > 0 } configured
                                ? configured : GetDefaultDownloadsPath()), NativeTorrentCachePath()).Contains(Path.GetFullPath(item.FullPath))))
                    {
                        _dialogService.ShowErrorDialog(
                            "This file is still a partial torrent download. Resume its queue job before playing it.", "Download Incomplete");
                        await RefreshDownloadsAsync();
                        return;
                    }
                    if (item.IsBook)
                    {
                        try
                        {
                            LocalBookImportResult imported = await _localBookImportService
                                .ImportAsync(item.FullPath);
                            WeakReferenceMessenger.Default.Send(
                                new NavigateToBookReaderMessage(imported.Book, imported.Asset));
                        }
                        catch (Exception ex) when (
                            ex is IOException or InvalidDataException or
                            UnauthorizedAccessException or NotSupportedException)
                        {
                            AppLogger.Log($"Downloaded book could not be opened: {ex.Message}", "WARNING");
                            _dialogService.ShowErrorDialog(
                                ex.Message,
                                "Unable to Open Book");
                        }
                    }
                    else
                    {
                        var saved = AuthorizedMediaDownloadService.ReadLibraryPlayback(item.FullPath);
                        if (saved != null)
                            WeakReferenceMessenger.Default.Send(new PlayMediaMessage(item.FullPath, LibraryPlaybackTitle(saved),
                                episodeNumber: saved.Context.Unit.EpisodeNumber?.ToString() ?? "",
                                audiovisualContext: saved.Context, localCaptionPaths: saved.CaptionPaths,
                                audioNotice: saved.AudioNotice));
                        else
                            _playMediaAction?.Invoke(item.FullPath, item.FileName);
                    }
                }
                else
                {
                    AppLogger.Log($"PlayFile failed. File does not exist at path: '{item.FullPath}'", "WARNING");
                }
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task DeleteFileAsync(InstalledEpisodeItem item)
        {
            if (item == null) return;

            AppLogger.Log($"DeleteFile command invoked for: '{item.FileName}'");
            bool confirmed = _dialogService.ShowConfirmDialog(
                $"Are you sure you want to permanently delete '{item.FileName}'?", 
                "Confirm Delete");

            if (confirmed)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (File.Exists(item.FullPath))
                        {
                            File.Delete(item.FullPath);
                            AppLogger.Log($"Successfully deleted file: '{item.FullPath}'");
                        }
                        else
                        {
                            AppLogger.Log($"DeleteFile failed. File does not exist: '{item.FullPath}'", "WARNING");
                        }
                    });
                    await RefreshDownloadsAsync();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to delete file: {ex.Message}", "ERROR");
                    _dialogService.ShowErrorDialog($"Failed to delete file: {ex.Message}", "Error");
                }
            }
        }
    }
}
