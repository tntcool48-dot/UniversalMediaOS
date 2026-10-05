using System.Windows;
using System.Windows.Controls;

namespace UniversalMediaOS.WPF.Views
{
    public partial class AnimeDetailsView : UserControl
    {
        public AnimeDetailsView()
        {
            InitializeComponent();
        }

        private void DetailsLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Workspace zoom reduces the available WPF width. Move ancillary
            // cards below the watch/synopsis column instead of squeezing it
            // between two fixed 230-unit panels. The whole body stays scrollable.
            bool compact = e.NewSize.Width < 1000;
            DetailsLayoutGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 140 : 230);
            DetailsLayoutGrid.ColumnDefinitions[3].Width = new GridLength(compact ? 0 : 16);
            DetailsLayoutGrid.ColumnDefinitions[4].Width = new GridLength(compact ? 0 : 230);
            Grid.SetColumn(MetadataPanel, compact ? 2 : 4);
            Grid.SetRow(MetadataPanel, compact ? 1 : 0);
            MetadataPanel.Margin = compact ? new Thickness(0, 14, 0, 0) : new Thickness(0);
            PosterPanel.Height = compact ? 210 : 340;
        }
    }
}
