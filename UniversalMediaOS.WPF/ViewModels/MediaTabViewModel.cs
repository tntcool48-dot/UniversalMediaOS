using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MediaTabViewModel : ObservableObject
    {
        public MediaTabViewModel(
            string title,
            string kind,
            string icon,
            string accent,
            ObservableObject contentViewModel,
            IDisposable? lifetime = null,
            bool localizeTitle = false)
        {
            Title = title;
            Kind = kind;
            Icon = icon;
            Accent = accent;
            ContentViewModel = contentViewModel;
            Lifetime = lifetime;
            LocalizeTitle = localizeTitle;
        }

        public string Title { get; }
        // Only app-owned navigation labels opt in; media titles retain their exact text.
        public bool LocalizeTitle { get; }
        public string Kind { get; }
        public string Icon { get; }
        public string Accent { get; }
        public ObservableObject ContentViewModel { get; }

        /// <summary>
        /// Owns the dependency-injection scope for transient tab content. Disposing
        /// this releases both the view model and any disposable dependencies it
        /// resolved (for example PlaybackViewModel's DatabaseContext).
        /// Singleton/shared tabs deliberately have no lifetime owner.
        /// </summary>
        public IDisposable? Lifetime { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
