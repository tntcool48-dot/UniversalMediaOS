using System.IO;
using System.IO.Compression;
using System.Text;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class EpubSecurityRegressionTests
    {
        [Fact]
        public void LoadEpub_RejectsArchivePathTraversal()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-epub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string epub = Path.Combine(root, "unsafe.epub");
            string escapedName = "escape-" + Guid.NewGuid().ToString("N") + ".txt";
            string escapedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniversalMediaOS",
                "epub_cache",
                escapedName);

            try
            {
                using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("../" + escapedName);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("must not escape");
                }

                var service = new EpubReaderService();
                Assert.Null(service.LoadEpub(epub));
                Assert.False(File.Exists(escapedPath));
            }
            finally
            {
                try { File.Delete(escapedPath); } catch { }
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadEpub_PreservesSpineOrderAndResolvesHrefFragments()
        {
            string root = CreateTestRoot();
            string epub = Path.Combine(root, "ordered.epub");
            CreateEpub(
                epub,
                manifestItems:
                [
                    ("chapter-one", "Text/chapter-1.xhtml"),
                    ("chapter-two", "Text/chapter-2.xhtml#middle")
                ],
                spineIds: ["chapter-two", "chapter-one"]);

            EpubBook? book = null;
            try
            {
                book = new EpubReaderService().LoadEpub(epub);

                Assert.NotNull(book);
                Assert.Equal("Regression Book", book.Title);
                Assert.Collection(
                    book.ChapterFiles,
                    chapter => Assert.Equal("chapter-2.xhtml", Path.GetFileName(chapter)),
                    chapter => Assert.Equal("chapter-1.xhtml", Path.GetFileName(chapter)));
                Assert.All(book.ChapterFiles, chapter => Assert.DoesNotContain('#', chapter));
            }
            finally
            {
                DeleteExtractedBook(book);
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadEpub_RejectsSpineReferenceOutsideExtractedBook()
        {
            string root = CreateTestRoot();
            string epub = Path.Combine(root, "unsafe-spine.epub");
            CreateEpub(
                epub,
                manifestItems: [("escape", "../../outside.xhtml")],
                spineIds: ["escape"]);

            try
            {
                Assert.Null(new EpubReaderService().LoadEpub(epub));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadEpub_AllowsAValidBookWithAnEmptySpine()
        {
            string root = CreateTestRoot();
            string epub = Path.Combine(root, "empty.epub");
            CreateEpub(epub, manifestItems: [], spineIds: []);
            HashSet<string> existingCacheDirectories = GetEpubCacheDirectories();

            EpubBook? book = null;
            try
            {
                book = new EpubReaderService().LoadEpub(epub);

                Assert.NotNull(book);
                Assert.Empty(book.ChapterFiles);
            }
            finally
            {
                DeleteNewCacheDirectories(existingCacheDirectories);
                Directory.Delete(root, recursive: true);
            }
        }

        private static string CreateTestRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-epub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void CreateEpub(
            string path,
            IReadOnlyCollection<(string Id, string Href)> manifestItems,
            IReadOnlyCollection<string> spineIds)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteEntry(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
                </container>
                """);

            string manifest = string.Join(
                Environment.NewLine,
                manifestItems.Select(item =>
                    $"<item id=\"{item.Id}\" href=\"{item.Href}\" media-type=\"application/xhtml+xml\" />"));
            string spine = string.Join(
                Environment.NewLine,
                spineIds.Select(id => $"<itemref idref=\"{id}\" />"));
            WriteEntry(
                archive,
                "OEBPS/content.opf",
                $$"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Regression Book</dc:title></metadata>
                  <manifest>{{manifest}}</manifest>
                  <spine>{{spine}}</spine>
                </package>
                """);

            foreach ((string _, string href) in manifestItems)
            {
                string chapterPath = href.Split('#', '?')[0];
                if (chapterPath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(chapterPath))
                {
                    continue;
                }

                WriteEntry(archive, "OEBPS/" + chapterPath, "<html><body><p>chapter</p></body></html>");
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        private static void DeleteExtractedBook(EpubBook? book)
        {
            string? chapter = book?.ChapterFiles.FirstOrDefault();
            if (chapter == null)
            {
                return;
            }

            var directory = new DirectoryInfo(Path.GetDirectoryName(chapter)!);
            while (directory.Parent != null &&
                   !directory.Parent.Name.Equals("epub_cache", StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Parent;
            }

            if (directory.Parent?.Name.Equals("epub_cache", StringComparison.OrdinalIgnoreCase) == true)
            {
                try { directory.Delete(recursive: true); } catch { }
            }
        }

        private static HashSet<string> GetEpubCacheDirectories()
        {
            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniversalMediaOS",
                "epub_cache");
            return Directory.Exists(cache)
                ? Directory.GetDirectories(cache).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void DeleteNewCacheDirectories(IReadOnlySet<string> existingDirectories)
        {
            foreach (string directory in GetEpubCacheDirectories().Where(path => !existingDirectories.Contains(path)))
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
    }
}
