using System.Collections.Generic;
using System.Windows;
using UniversalMediaOS.Core.Routing;

namespace UniversalMediaOS.WPF.Helpers
{
    public class WpfDialogService : IDialogService
    {
        public (bool DialogResult, SelectedSourceTier SelectedTier, TorrentResult? SelectedTorrent) ShowSourceSelection(List<TorrentResult> torrents)
        {
            bool result = false;
            SelectedSourceTier tier = SelectedSourceTier.None;
            TorrentResult? torrent = null;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = new SourceSelectionWindow(torrents)
                {
                    Owner = Application.Current.MainWindow
                };
                if (window.ShowDialog() == true)
                {
                    result = true;
                    tier = window.SelectedTier;
                    torrent = window.SelectedTorrent;
                }
            });

            return (result, tier, torrent);
        }

        public bool ShowConfirmDialog(string message, string title)
        {
            bool result = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            });
            return result;
        }

        public void ShowErrorDialog(string message, string title)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        public void ShowInfoDialog(string message, string title)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }
    }
}
