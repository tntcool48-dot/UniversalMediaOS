using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.WPF.ViewModels
{
    public sealed class NavigateToVaDetectMessage
    {
        public NavigateToVaDetectMessage(MediaResult? media)
        {
            Media = media;
        }

        public MediaResult? Media { get; }
    }
}
