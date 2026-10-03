namespace UniversalMediaOS.WPF.Helpers
{
    public interface IExternalLauncher
    {
        bool OpenUrl(string url);
        bool OpenFolder(string folderPath);
        bool OpenFile(string filePath);
    }
}
