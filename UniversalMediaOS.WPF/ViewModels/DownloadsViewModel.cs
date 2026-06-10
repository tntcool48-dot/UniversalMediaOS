using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;

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
        private readonly string _downloadsPath;
        private Action<string, string>? _playMediaAction;

        public ObservableCollection<InstalledEpisodeItem> InstalledFiles { get; } = new();

        [ObservableProperty]
        private bool _isEmpty = true;

        public DownloadsViewModel(SeasonDownloader seasonDownloader)
        {
            _seasonDownloader = seasonDownloader;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _downloadsPath = Path.Combine(appData, "UniversalMediaOS", "Downloads");
            RefreshDownloadsCommand.Execute(null);
        }

        public void RegisterPlayMediaAction(Action<string, string> playAction)
        {
            _playMediaAction = playAction;
        }

        [RelayCommand]
        private void RefreshDownloads()
        {
            InstalledFiles.Clear();
            if (Directory.Exists(_downloadsPath))
            {
                var files = Directory.GetFiles(_downloadsPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mp4") || f.EndsWith(".mkv") || f.EndsWith(".avi") || f.EndsWith(".epub"))
                    .Select(f =>
                    {
                        var fi = new FileInfo(f);
                        string size = fi.Length > 1_000_000 
                            ? $"{fi.Length / 1_000_000.0:F1} MB" 
                            : $"{fi.Length / 1_000.0:F0} KB";
                        return new InstalledEpisodeItem { FileName = Path.GetFileName(f), FullPath = f, FileSizeText = size };
                    })
                    .ToList();

                foreach (var file in files)
                {
                    InstalledFiles.Add(file);
                }
            }
            IsEmpty = InstalledFiles.Count == 0;
        }

        [RelayCommand]
        private void OpenDownloadsFolder()
        {
            if (!Directory.Exists(_downloadsPath))
            {
                Directory.CreateDirectory(_downloadsPath);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = _downloadsPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        [RelayCommand]
        private void PlayFile(InstalledEpisodeItem item)
        {
            if (item != null && File.Exists(item.FullPath))
            {
                _playMediaAction?.Invoke(item.FullPath, item.FileName);
            }
        }
    }
}
