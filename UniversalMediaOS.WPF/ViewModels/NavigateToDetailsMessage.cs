using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class NavigateToDetailsMessage
    {
        public MediaResult Media { get; }

        public NavigateToDetailsMessage(MediaResult media)
        {
            Media = media;
        }
    }
}
