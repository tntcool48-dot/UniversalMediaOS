using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class NavigateToDetailsMessage
    {
        public MediaResult? Media { get; }

        public NavigateToDetailsMessage(MediaResult? media)
        {
            Media = media;
        }
    }

    /// <summary>
    /// Requests that the tab which owns a specific view model be closed. Carrying
    /// the instance avoids closing whichever details tab merely happens to be active.
    /// </summary>
    public sealed class CloseTabMessage
    {
        public CloseTabMessage(CommunityToolkit.Mvvm.ComponentModel.ObservableObject contentViewModel)
        {
            ContentViewModel = contentViewModel;
        }

        public CommunityToolkit.Mvvm.ComponentModel.ObservableObject ContentViewModel { get; }
    }
}
