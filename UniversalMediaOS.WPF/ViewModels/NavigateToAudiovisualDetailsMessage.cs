using UniversalMediaOS.Core.OtherMedia;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed class NavigateToAudiovisualDetailsMessage
{
    public NavigateToAudiovisualDetailsMessage(AudiovisualMediaItem media)
    {
        Media = media ?? throw new ArgumentNullException(nameof(media));
    }

    public AudiovisualMediaItem Media { get; }
}
