using UniversalMediaOS.WPF.Controls;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class AsyncImageLoaderRegressionTests
    {
        private static readonly byte[] OnePixelPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        [Fact]
        public void DecodeBitmap_ReturnsFrozenImage()
        {
            var bitmap = AsyncImageLoader.DecodeBitmap(OnePixelPng, 32, CancellationToken.None);

            Assert.True(bitmap.IsFrozen);
            Assert.Equal(32, bitmap.PixelWidth);
        }

        [Fact]
        public void DecodeBitmap_HonorsCancellationBeforeDecode()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                AsyncImageLoader.DecodeBitmap(OnePixelPng, 32, cts.Token));
        }

        [Fact]
        public void DecodeBitmap_RejectsInvalidImageData()
        {
            Assert.ThrowsAny<Exception>(() =>
                AsyncImageLoader.DecodeBitmap(new byte[] { 1, 2, 3 }, 32, CancellationToken.None));
        }
    }
}
