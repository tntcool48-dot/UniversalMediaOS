using System.Collections.Generic;
using UniversalMediaOS.Core.Routing;

namespace UniversalMediaOS.WPF.Helpers
{
    public interface IDialogService
    {
        (bool DialogResult, SelectedSourceTier SelectedTier, TorrentResult? SelectedTorrent) ShowSourceSelection(List<TorrentResult> torrents);
        bool ShowConfirmDialog(string message, string title);
        void ShowErrorDialog(string message, string title);
        void ShowInfoDialog(string message, string title);
    }
}
