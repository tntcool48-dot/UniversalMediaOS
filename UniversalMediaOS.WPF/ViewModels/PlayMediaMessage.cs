using CommunityToolkit.Mvvm.Messaging.Messages;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class PlayMediaMessage : ValueChangedMessage<string>
    {
        public string Title { get; }
        public bool IsWebView { get; }
        public string Referer { get; }

        public PlayMediaMessage(string path, string title, bool isWebView = false, string referer = "") : base(path)
        {
            Title = title;
            IsWebView = isWebView;
            Referer = referer;
        }
    }
}
