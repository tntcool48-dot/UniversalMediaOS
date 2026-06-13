using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class InstalledEpisodeItem
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string FileSizeText { get; set; } = string.Empty;
    }

    public partial class DownloadsViewModel : ObservableObject
    {
        private readonly SeasonDownloader _seasonDownloader;
        private readonly DomainHotSwapper _config;
        private readonly Helpers.IDialogService _dialogService;
        private Action<string, string>? _playMediaAction;

        public Helpers.ObservableRangeCollection<InstalledEpisodeItem> InstalledFiles { get; } = new();

        [ObservableProperty]
        private bool _isEmpty = true;

        public DownloadsViewModel(SeasonDownloader seasonDownloader, DomainHotSwapper config, Helpers.IDialogService dialogService)
        {
            _seasonDownloader = seasonDownloader;
            _config = config;
            _dialogService = dialogService;
            
            // Run load on a background thread to prevent blocking the UI thread on startup
            _ = Task.Run(() => RefreshDownloadsAsync());
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
            string downloadsPath = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

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
                    Directory.GetFiles(downloadsPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".mp4") || f.EndsWith(".mkv") || f.EndsWith(".avi") || f.EndsWith(".epub"))
                        .Select(f =>
                        {
                            var fi = new FileInfo(f);
                            string size = fi.Length > 1_000_000
                                ? $"{fi.Length / 1_000_000.0:F1} MB"
                                : $"{fi.Length / 1_000.0:F0} KB";
                            return new InstalledEpisodeItem { FileName = Path.GetFileName(f), FullPath = f, FileSizeText = size };
                        })
                        .ToList());

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
            string downloadsPath = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

            AppLogger.Log($"OpenDownloadsFolder command invoked. Path: '{downloadsPath}'");
            try
            {
                if (!Directory.Exists(downloadsPath))
                {
                    Directory.CreateDirectory(downloadsPath);
                }
                
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = downloadsPath,
                    UseShellExecute = true,
                    Verb = "open"
                }))
                {
                    // Cleanly close OS process handle immediately
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to open downloads folder: {ex.Message}", "ERROR");
            }
        }

        [RelayCommand]
        private void PlayFile(InstalledEpisodeItem item)
        {
            if (item != null)
            {
                AppLogger.Log($"PlayFile command invoked. File: '{item.FileName}', FullPath: '{item.FullPath}'");
                if (File.Exists(item.FullPath))
                {
                    _playMediaAction?.Invoke(item.FullPath, item.FileName);
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
