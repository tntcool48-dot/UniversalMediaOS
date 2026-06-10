using System.Collections.Generic;
using System.Windows;
using UniversalMediaOS.Core.Routing;

namespace UniversalMediaOS.WPF
{
    public enum SelectedSourceTier
    {
        None,
        Tier1_Torrent,
        Tier2_Consumet,
        Tier3_WebProvider
    }

    public partial class SourceSelectionWindow : Window
    {
        public TorrentResult? SelectedTorrent { get; private set; }
        public SelectedSourceTier SelectedTier { get; private set; } = SelectedSourceTier.None;

        public SourceSelectionWindow(List<TorrentResult>? torrents)
        {
            InitializeComponent();
            if (torrents != null)
            {
                TorrentsList.ItemsSource = torrents;
                if (torrents.Count > 0) TorrentsList.SelectedIndex = 0;
            }
        }

        private void SelectTorrent_Click(object sender, RoutedEventArgs e)
        {
            if (TorrentsList.ItemsSource == null)
            {
                SelectedTier = SelectedSourceTier.Tier1_Torrent;
                DialogResult = true;
                return;
            }

            if (TorrentsList.SelectedItem is TorrentResult t)
            {
                SelectedTorrent = t;
                SelectedTier = SelectedSourceTier.Tier1_Torrent;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("Please select a torrent first.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ConsumetButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.Tier2_Consumet;
            DialogResult = true;
        }

        private void WebProviderButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.Tier3_WebProvider;
            DialogResult = true;
        }
    }
}
