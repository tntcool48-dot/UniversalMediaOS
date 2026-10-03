using System;
using System.IO;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Helpers
{
    public sealed class WpfFolderPicker : IFolderPicker
    {
        public string? PickFolder(string title, string? initialDirectory = null)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = title,
                    InitialDirectory = initialDirectory ?? string.Empty
                };

                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Folder picker failed, falling back to file picker: {ex.Message}", "WARNING");
                return PickFolderViaFileDialog(title, initialDirectory);
            }
        }

        private static string? PickFolderViaFileDialog(string title, string? initialDirectory)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                CheckFileExists = false,
                FileName = "Folder Selection",
                InitialDirectory = initialDirectory ?? string.Empty
            };

            return dialog.ShowDialog() == true
                ? Path.GetDirectoryName(dialog.FileName)
                : null;
        }
    }
}
