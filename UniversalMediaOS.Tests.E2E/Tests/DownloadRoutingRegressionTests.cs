using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class DownloadRoutingRegressionTests
    {
        [Theory]
        [InlineData("episode.MP4")]
        [InlineData("episode.mkv")]
        [InlineData("episode.WEBM")]
        [InlineData("movie.MOV")]
        [InlineData("movie.M4V")]
        [InlineData("archive.OGV")]
        [InlineData("book.EPUB")]
        [InlineData("book.PDF")]
        public void DownloadScan_AcceptsSupportedExtensionsCaseInsensitively(string fileName)
        {
            Assert.True(DownloadsViewModel.IsSupportedDownloadedFile(fileName));
        }

        [Theory]
        [InlineData("notes.txt")]
        [InlineData("archive.zip")]
        [InlineData("fake.mp4.exe")]
        public void DownloadScan_RejectsUnrelatedFiles(string fileName)
        {
            Assert.False(DownloadsViewModel.IsSupportedDownloadedFile(fileName));
        }

        [Fact]
        public void EpubDownload_UsesReadActionInsteadOfVideoPlaybackLabel()
        {
            var item = new InstalledEpisodeItem { FullPath = "sample.EPUB" };

            Assert.True(item.IsEpub);
            Assert.True(item.IsBook);
            Assert.Equal("Read", item.OpenActionText);
        }

        [Fact]
        public void PdfDownload_UsesBuiltInReadAction()
        {
            var item = new InstalledEpisodeItem { FullPath = "sample.PDF" };

            Assert.False(item.IsEpub);
            Assert.True(item.IsBook);
            Assert.Equal("Read", item.OpenActionText);
        }
    }
}
