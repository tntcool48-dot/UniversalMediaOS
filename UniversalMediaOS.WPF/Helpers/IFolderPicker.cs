namespace UniversalMediaOS.WPF.Helpers
{
    public interface IFolderPicker
    {
        string? PickFolder(string title, string? initialDirectory = null);
    }
}
