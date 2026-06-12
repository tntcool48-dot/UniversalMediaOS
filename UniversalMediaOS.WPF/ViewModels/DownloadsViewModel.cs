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
        private Action<string, string>? _playMediaAction;

        public ObservableCollection<InstalledEpisodeItem> InstalledFiles { get; } = new();

        [ObservableProperty]
        private bool _isEmpty = true;

        public DownloadsViewModel(SeasonDownloader seasonDownloader, DomainHotSwapper config)
        {
            _seasonDownloader = seasonDownloader;
            _config = config;
            RefreshDownloadsCommand.Execute(null);
        }

        public void RegisterPlayMediaAction(Action<string, string> playAction)
        {
            _playMediaAction = playAction;
        }

        [RelayCommand]
        private async Task RefreshDownloads()
        {
            AppLogger.Log("RefreshDownloads command invoked. Scanning downloads directory...");
            InstalledFiles.Clear();
            
            string dDir = _config.GetSetting("DownloadDirectory");
            string downloadsPath = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

            if (!Directory.Exists(downloadsPath)) 
            { 
                AppLogger.Log($"Downloads directory '{downloadsPath}' does not exist.");
                IsEmpty = true; 
                return; 
            }

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

            foreach (var file in files)
                InstalledFiles.Add(file);

            IsEmpty = InstalledFiles.Count == 0;
            AppLogger.Log($"Scan completed. Found {InstalledFiles.Count} media files.");
        }

        [RelayCommand]
        private void OpenDownloadsFolder()
        {
            string dDir = _config.GetSetting("DownloadDirectory");
            string downloadsPath = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;

            AppLogger.Log($"OpenDownloadsFolder command invoked. Path: '{downloadsPath}'");
            if (!Directory.Exists(downloadsPath))
            {
                Directory.CreateDirectory(downloadsPath);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = downloadsPath,
                UseShellExecute = true,
                Verb = "open"
            });
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

        [RelayCommand]
        private async Task DeleteFile(InstalledEpisodeItem item)
        {
            if (item != null)
            {
                AppLogger.Log($"DeleteFile command invoked for: '{item.FileName}'");
                var result = MessageBox.Show($"Are you sure you want to permanently delete '{item.FileName}'?", 
                                             "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    try
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
                        await RefreshDownloads();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Failed to delete file: {ex.Message}", "ERROR");
                        MessageBox.Show($"Failed to delete file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
    }
}
