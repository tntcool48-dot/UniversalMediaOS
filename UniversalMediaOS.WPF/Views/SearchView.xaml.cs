using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF.Views
{
    public partial class SearchView : UserControl
    {
        private SearchViewModel? _subscribedViewModel;
        private ScrollViewer? _resultsScrollViewer;
        private bool _isLoaded;

        public SearchView()
        {
            InitializeComponent();
            DataContextChanged += SearchView_DataContextChanged;
            Loaded += SearchView_Loaded;
            Unloaded += SearchView_Unloaded;
        }

        private void SearchView_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            DetachViewModel();

            if (_isLoaded && e.NewValue is SearchViewModel newVm)
            {
                AttachViewModel(newVm);
            }
        }

        private void SearchView_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _isLoaded = true;
            if (_subscribedViewModel == null && DataContext is SearchViewModel vm)
            {
                AttachViewModel(vm);
            }
        }

        private void SearchView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _isLoaded = false;
            DetachViewModel();
        }

        private void AttachViewModel(SearchViewModel vm)
        {
            if (ReferenceEquals(_subscribedViewModel, vm))
            {
                return;
            }

            DetachViewModel();
            _subscribedViewModel = vm;
            vm.SearchResults.CollectionChanged += SearchResults_CollectionChanged;
            vm.SetResultsViewportWidth(ResultsList.ActualWidth);
            QueueLoadMoreCheck();
        }

        private void DetachViewModel()
        {
            if (_subscribedViewModel == null)
            {
                return;
            }

            _subscribedViewModel.SearchResults.CollectionChanged -= SearchResults_CollectionChanged;
            _subscribedViewModel = null;
        }

        private void ResultsList_Loaded(object sender, RoutedEventArgs e)
        {
            _resultsScrollViewer = FindVisualDescendant<ScrollViewer>(ResultsList);
            if (DataContext is SearchViewModel vm)
            {
                vm.SetResultsViewportWidth(ResultsList.ActualWidth);
            }

            QueueLoadMoreCheck();
        }

        private void ResultRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SearchResultRow row } &&
                DataContext is SearchViewModel vm)
            {
                // A row is requested only when WPF realizes it in the recycled
                // viewport. This prevents a trending/search page from launching
                // one provider lookup for every off-screen card.
                vm.RequestDubAvailabilityForVisibleResults(row.Results);
            }
        }

        private void ResultsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext is SearchViewModel vm)
            {
                vm.SetResultsViewportWidth(e.NewSize.Width);
            }
        }

        private void SearchResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            QueueLoadMoreCheck();
        }

        private void ResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            _resultsScrollViewer ??= e.OriginalSource as ScrollViewer;
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            {
                TryLoadMoreIfNeeded();
            }
        }

        private void ResultsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta < 0)
            {
                TryLoadMoreIfNeeded(forceWhenScrollableAreaIsSmall: true);
            }
        }

        private void QueueLoadMoreCheck()
        {
            Dispatcher.BeginInvoke(
                new System.Action(() => TryLoadMoreIfNeeded(forceWhenScrollableAreaIsSmall: true)),
                DispatcherPriority.Background);
        }

        private void TryLoadMoreIfNeeded(bool forceWhenScrollableAreaIsSmall = false)
        {
            if (DataContext is not SearchViewModel vm)
            {
                return;
            }

            _resultsScrollViewer ??= FindVisualDescendant<ScrollViewer>(ResultsList);
            if (_resultsScrollViewer == null)
            {
                return;
            }

            double distanceToBottom = _resultsScrollViewer.ScrollableHeight - _resultsScrollViewer.VerticalOffset;
            bool nearBottom = distanceToBottom <= 2;
            bool needsContentToScroll = forceWhenScrollableAreaIsSmall && _resultsScrollViewer.ScrollableHeight <= 0;

            if ((nearBottom || needsContentToScroll) && vm.LoadMoreAnimeCommand.CanExecute(null))
            {
                vm.LoadMoreAnimeCommand.Execute(null);
            }
        }

        private static T? FindVisualDescendant<T>(DependencyObject parent)
            where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                T? descendant = FindVisualDescendant<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}
