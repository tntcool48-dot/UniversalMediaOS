using CommunityToolkit.Mvvm.Messaging.Messages;

namespace UniversalMediaOS.WPF.ViewModels
{
    public class PlayMediaMessage : ValueChangedMessage<string>
    {
        public string Title { get; }

        public PlayMediaMessage(string path, string title) : base(path)
        {
            Title = title;
        }
    }
}
