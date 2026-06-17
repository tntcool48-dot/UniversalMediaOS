using System.Collections.Generic;
using System.Windows;
using UniversalMediaOS.Core.Routing;

namespace UniversalMediaOS.WPF
{
    public enum SelectedSourceTier
    {
        None,
        Stream_Auto,       // Python scraper → HLS proxy → WebView fallback (automatic waterfall)
        Stream_WebView,    // Go directly to WebView2 + uBlock Origin
        Download_Season    // Trigger SeasonDownloader (P2P — separate from streaming)
    }

    public partial class SourceSelectionWindow : Window
    {
        public SelectedSourceTier SelectedTier { get; private set; } = SelectedSourceTier.None;

        public SourceSelectionWindow()
        {
            InitializeComponent();
        }

        private void StreamAutoButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.Stream_Auto;
            DialogResult = true;
        }

        private void StreamWebViewButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.Stream_WebView;
            DialogResult = true;
        }

        private void DownloadSeasonButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.Download_Season;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.None;
            DialogResult = false;
        }
    }
}
