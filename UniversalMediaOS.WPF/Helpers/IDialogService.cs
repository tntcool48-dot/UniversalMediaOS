namespace UniversalMediaOS.WPF.Helpers
{
    public interface IDialogService
    {
        (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection();
        bool ShowConfirmDialog(string message, string title);
        void ShowErrorDialog(string message, string title);
        void ShowInfoDialog(string message, string title);
    }
}
