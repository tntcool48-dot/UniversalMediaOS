using System;
using System.Diagnostics;
using System.IO;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Helpers
{
    public sealed class WpfExternalLauncher : IExternalLauncher
    {
        public bool OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            return TryStart(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }

        public bool OpenFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Unable to create folder before launch '{folderPath}': {ex.Message}", "ERROR");
                return false;
            }

            return TryStart(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        public bool OpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            return TryStart(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(filePath),
                UseShellExecute = true,
                Verb = "open"
            });
        }

        private static bool TryStart(ProcessStartInfo startInfo)
        {
            try
            {
                using var process = Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"External launch failed for '{startInfo.FileName}': {ex.Message}", "WARNING");
                return false;
            }
        }
    }
}
