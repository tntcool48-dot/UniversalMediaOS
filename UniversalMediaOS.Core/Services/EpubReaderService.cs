using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace UniversalMediaOS.Core.Services
{
    public class EpubBook
    {
        public string Title { get; set; } = "Unknown Title";
        public List<string> ChapterFiles { get; set; } = new List<string>();
    }

    public class EpubReaderService
    {
        private const int MaximumArchiveEntries = 5_000;
        private const long MaximumEntryBytes = 128L * 1024 * 1024;
        private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
        private const int MaximumCompressionRatio = 500;

        public EpubBook? LoadEpub(string epubFilePath)
        {
            if (!File.Exists(epubFilePath)) return null;

            string localAppData = Path.Combine(UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory, "UniversalMediaOS");
            string baseTemp = Path.Combine(localAppData, "epub_cache");
            Directory.CreateDirectory(baseTemp);

            string bookId = Guid.NewGuid().ToString("N");
            string extractPath = Path.Combine(baseTemp, bookId);
            Directory.CreateDirectory(extractPath);

            string canonicalExtractPath = Path.GetFullPath(extractPath);
            if (!canonicalExtractPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                canonicalExtractPath += Path.DirectorySeparatorChar;
            }

            bool success = false;
            try
            {
                ExtractEpubSafely(epubFilePath, canonicalExtractPath);

                // Find container.xml to resolve OPF path
                string containerPath = Path.Combine(extractPath, "META-INF", "container.xml");
                if (!File.Exists(containerPath)) return null;

                var containerDoc = XDocument.Load(containerPath);
                XNamespace ns = containerDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                
                var rootfile = containerDoc.Descendants(ns + "rootfile").FirstOrDefault();
                string opfPath = rootfile?.Attribute("full-path")?.Value ?? "";
                if (string.IsNullOrEmpty(opfPath)) return null;

                if (!TryResolveArchiveContentPath(extractPath, canonicalExtractPath, opfPath, out string fullOpfPath) ||
                    !File.Exists(fullOpfPath))
                {
                    return null;
                }

                string opfDir = Path.GetDirectoryName(fullOpfPath) ?? extractPath;

                // Parse OPF manifest and spine
                var opfDoc = XDocument.Load(fullOpfPath);
                XNamespace opfNs = opfDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                string title = "Unknown Title";
                var metadata = opfDoc.Descendants(opfNs + "metadata").FirstOrDefault();
                if (metadata != null)
                {
                    XNamespace dcNs = "http://purl.org/dc/elements/1.1/";
                    var titleEl = metadata.Element(dcNs + "title") ?? metadata.Descendants().FirstOrDefault(d => d.Name.LocalName == "title");
                    if (titleEl != null) title = titleEl.Value;
                }

                // Map ID -> href
                var manifestItems = new Dictionary<string, string>();
                var manifest = opfDoc.Descendants(opfNs + "manifest").FirstOrDefault();
                if (manifest != null)
                {
                    foreach (var item in manifest.Elements(opfNs + "item"))
                    {
                        string id = item.Attribute("id")?.Value ?? "";
                        string href = item.Attribute("href")?.Value ?? "";
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(href))
                        {
                            manifestItems[id] = href;
                        }
                    }
                }

                // Spine order
                var chapters = new List<string>();
                var spine = opfDoc.Descendants(opfNs + "spine").FirstOrDefault();
                if (spine != null)
                {
                    foreach (var itemref in spine.Elements(opfNs + "itemref"))
                    {
                        string idref = itemref.Attribute("idref")?.Value ?? "";
                        if (manifestItems.TryGetValue(idref, out string? relativePath))
                        {
                            if (!TryResolveArchiveContentPath(opfDir, canonicalExtractPath, relativePath, out string fullPath))
                            {
                                throw new InvalidDataException($"EPUB spine item '{idref}' points outside the book archive.");
                            }

                            if (!File.Exists(fullPath))
                            {
                                throw new InvalidDataException($"EPUB spine item '{idref}' points to a missing chapter.");
                            }

                            if (!chapters.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                            {
                                chapters.Add(fullPath);
                            }
                        }
                    }
                }

                success = true;
                return new EpubBook
                {
                    Title = title,
                    ChapterFiles = chapters
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EPUB Parse Error: {ex.Message}");
                return null;
            }
            finally
            {
                if (!success)
                {
                    try
                    {
                        if (Directory.Exists(extractPath))
                        {
                            Directory.Delete(extractPath, true);
                        }
                    }
                    catch { }
                }
            }
        }

        public void CleanCache(string? excludePath = null)
        {
            try
            {
                string localAppData = Path.Combine(UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory, "UniversalMediaOS");
                string baseTemp = Path.Combine(localAppData, "epub_cache");
                if (Directory.Exists(baseTemp))
                {
                    string? canonicalExclude = null;
                    if (!string.IsNullOrEmpty(excludePath))
                    {
                        canonicalExclude = Path.GetFullPath(excludePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }

                    foreach (var dir in Directory.GetDirectories(baseTemp))
                    {
                        string canonicalDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (canonicalExclude == null || !string.Equals(canonicalDir, canonicalExclude, StringComparison.OrdinalIgnoreCase))
                        {
                            try { Directory.Delete(dir, true); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        internal static bool TryResolveArchiveContentPath(
            string contentBasePath,
            string canonicalExtractPath,
            string archiveReference,
            out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(archiveReference))
            {
                return false;
            }

            string referenceWithoutFragment = archiveReference.Trim();
            int suffixIndex = referenceWithoutFragment.IndexOfAny(['#', '?']);
            if (suffixIndex >= 0)
            {
                referenceWithoutFragment = referenceWithoutFragment[..suffixIndex];
            }

            if (string.IsNullOrWhiteSpace(referenceWithoutFragment))
            {
                return false;
            }

            string decoded = Uri.UnescapeDataString(referenceWithoutFragment);
            if (Path.IsPathRooted(decoded) ||
                Uri.TryCreate(decoded, UriKind.Absolute, out _))
            {
                return false;
            }

            string canonicalRoot = Path.GetFullPath(canonicalExtractPath);
            if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                canonicalRoot += Path.DirectorySeparatorChar;
            }

            string candidate = Path.GetFullPath(Path.Combine(contentBasePath, decoded));
            if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }

        private static void ExtractEpubSafely(string epubFilePath, string canonicalExtractPath)
        {
            using var archive = ZipFile.OpenRead(epubFilePath);
            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"EPUB contains more than {MaximumArchiveEntries} archive entries.");
            }

            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > MaximumEntryBytes)
                {
                    throw new InvalidDataException($"EPUB entry '{entry.FullName}' is too large.");
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > MaximumArchiveBytes)
                {
                    throw new InvalidDataException("EPUB expanded content exceeds the 1 GB safety limit.");
                }

                if (entry.CompressedLength > 0 &&
                    entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio)
                {
                    throw new InvalidDataException($"EPUB entry '{entry.FullName}' has an unsafe compression ratio.");
                }

                string destination = Path.GetFullPath(Path.Combine(canonicalExtractPath, entry.FullName));
                if (!destination.StartsWith(canonicalExtractPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"EPUB entry escapes the extraction directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using Stream input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[64 * 1024];
                long written = 0;
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > entry.Length || written > MaximumEntryBytes)
                    {
                        throw new InvalidDataException($"EPUB entry '{entry.FullName}' exceeded its declared size.");
                    }

                    output.Write(buffer, 0, read);
                }
            }
        }
    }
}
