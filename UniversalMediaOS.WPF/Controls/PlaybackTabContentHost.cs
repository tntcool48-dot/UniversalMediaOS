using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;

namespace UniversalMediaOS.WPF.Controls;

/// <summary>
/// Keeps each open player's HWND and browser document in the visual tree.
/// Manga readers retain their document and scroll position as well.
/// Other catalog/utility views use the ordinary active-content presenter.
/// </summary>
public sealed class PlaybackTabContentHost : Grid
{
    public static readonly DependencyProperty TabsProperty = DependencyProperty.Register(
        nameof(Tabs), typeof(ObservableCollection<MediaTabViewModel>), typeof(PlaybackTabContentHost),
        new PropertyMetadata(null, OnTabsChanged));

    public static readonly DependencyProperty ActiveTabProperty = DependencyProperty.Register(
        nameof(ActiveTab), typeof(MediaTabViewModel), typeof(PlaybackTabContentHost),
        new PropertyMetadata(null, (owner, _) => ((PlaybackTabContentHost)owner).UpdateContent()));

    public static readonly DependencyProperty ContentScaleProperty = DependencyProperty.Register(
        nameof(ContentScale), typeof(double), typeof(PlaybackTabContentHost),
        new PropertyMetadata(1.0, (owner, args) =>
        {
            var scale = ((PlaybackTabContentHost)owner)._otherContentScale;
            scale.ScaleX = scale.ScaleY = (double)args.NewValue;
        }));

    private readonly ContentControl _otherContent = new();
    private readonly ScaleTransform _otherContentScale = new();
    private readonly Dictionary<MediaTabViewModel, PlaybackView> _players = new();
    private readonly Dictionary<MediaTabViewModel, MangaView> _mangaReaders = new();

    public PlaybackTabContentHost()
    {
        // Scaling an ancestor also reaches hidden retained VideoViews. Their
        // separate foreground HWND can retain that scale after tab return.
        _otherContent.LayoutTransform = _otherContentScale;
        Children.Add(_otherContent);
    }

    public double ContentScale
    {
        get => (double)GetValue(ContentScaleProperty);
        set => SetValue(ContentScaleProperty, value);
    }

    public ObservableCollection<MediaTabViewModel>? Tabs
    {
        get => (ObservableCollection<MediaTabViewModel>?)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public MediaTabViewModel? ActiveTab
    {
        get => (MediaTabViewModel?)GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    private static void OnTabsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var host = (PlaybackTabContentHost)owner;
        if (args.OldValue is ObservableCollection<MediaTabViewModel> oldTabs)
            oldTabs.CollectionChanged -= host.Tabs_CollectionChanged;
        if (args.NewValue is ObservableCollection<MediaTabViewModel> newTabs)
            newTabs.CollectionChanged += host.Tabs_CollectionChanged;
        host.UpdateContent();
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateContent();

    private void UpdateContent()
    {
        foreach (var tab in _players.Keys.Where(tab => Tabs?.Contains(tab) != true).ToArray())
        {
            PlaybackView view = _players[tab];
            // Release the view before the tab's DI scope disposes native media.
            view.CloseForTab();
            Children.Remove(view);
            _players.Remove(tab);
        }
        foreach (var tab in _mangaReaders.Keys.Where(tab => Tabs?.Contains(tab) != true).ToArray())
        {
            MangaView view = _mangaReaders[tab];
            view.CloseForTab();
            Children.Remove(view);
            _mangaReaders.Remove(tab);
        }

        MediaTabViewModel? active = Tabs?.Contains(ActiveTab!) == true ? ActiveTab : null;
        foreach (var (tab, view) in _players)
        {
            // Hidden preserves layout/handles and prevents input or overlays
            // from belonging to an inactive tab. Never reparent on selection.
            view.Visibility = ReferenceEquals(tab, active) ? Visibility.Visible : Visibility.Hidden;
        }
        foreach (var (tab, view) in _mangaReaders)
            view.Visibility = ReferenceEquals(tab, active) ? Visibility.Visible : Visibility.Hidden;

        if (active?.ContentViewModel is PlaybackViewModel player)
        {
            _otherContent.Content = null;
            _otherContent.Visibility = Visibility.Collapsed;
            if (!_players.ContainsKey(active))
            {
                var view = new PlaybackView { DataContext = player };
                _players.Add(active, view);
                Children.Add(view);
            }
        }
        else if (active?.ContentViewModel is MangaViewModel manga)
        {
            _otherContent.Content = null;
            _otherContent.Visibility = Visibility.Collapsed;
            if (!_mangaReaders.ContainsKey(active))
            {
                var view = new MangaView { DataContext = manga, LayoutTransform = _otherContentScale };
                _mangaReaders.Add(active, view);
                Children.Add(view);
            }
        }
        else
        {
            _otherContent.Content = active?.ContentViewModel;
            _otherContent.Visibility = Visibility.Visible;
        }
    }
}
