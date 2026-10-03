using System.Windows;

namespace UniversalMediaOS.WPF
{
    public enum SelectedSourceTier
    {
        None,
        Stream_Auto,
        Stream_WebView
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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedTier = SelectedSourceTier.None;
            DialogResult = false;
        }
    }
}
