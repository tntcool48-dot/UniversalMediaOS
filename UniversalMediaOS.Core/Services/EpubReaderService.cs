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
        public EpubBook? LoadEpub(string epubFilePath)
        {
            if (!File.Exists(epubFilePath)) return null;

            try
            {
                string baseTemp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "epub_cache");
                Directory.CreateDirectory(baseTemp);

                string bookId = Guid.NewGuid().ToString("N");
                string extractPath = Path.Combine(baseTemp, bookId);
                Directory.CreateDirectory(extractPath);

                // Extract Zip
                ZipFile.ExtractToDirectory(epubFilePath, extractPath, true);

                // Find container.xml to resolve OPF path
                string containerPath = Path.Combine(extractPath, "META-INF", "container.xml");
                if (!File.Exists(containerPath)) return null;

                var containerDoc = XDocument.Load(containerPath);
                XNamespace ns = containerDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                
                var rootfile = containerDoc.Descendants(ns + "rootfile").FirstOrDefault();
                string opfPath = rootfile?.Attribute("full-path")?.Value ?? "";
                if (string.IsNullOrEmpty(opfPath)) return null;

                string fullOpfPath = Path.Combine(extractPath, opfPath);
                if (!File.Exists(fullOpfPath)) return null;

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
                            // Href might be url encoded, decode it
                            href = Uri.UnescapeDataString(href);
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
                            string fullPath = Path.GetFullPath(Path.Combine(opfDir, relativePath));
                            if (File.Exists(fullPath))
                            {
                                chapters.Add(fullPath);
                            }
                        }
                    }
                }

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
        }

        public void CleanCache(string? excludePath = null)
        {
            try
            {
                string baseTemp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "epub_cache");
                if (Directory.Exists(baseTemp))
                {
                    foreach (var dir in Directory.GetDirectories(baseTemp))
                    {
                        if (excludePath == null || !string.Equals(dir, excludePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { Directory.Delete(dir, true); } catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
