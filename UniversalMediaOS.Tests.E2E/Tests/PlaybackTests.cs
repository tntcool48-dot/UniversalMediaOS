using System.Windows;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class PlaybackTests
    {
        [Theory]
        [InlineData(-10, 60_000, 0)]
        [InlineData(12_345.9, 60_000, 12_345)]
        [InlineData(90_000, 60_000, 60_000)]
        [InlineData(double.NaN, 60_000, 0)]
        [InlineData(double.PositiveInfinity, 60_000, 0)]
        [InlineData(30_000, double.NaN, 0)]
        public void SeekClamp_IsFiniteAndNeverLeavesMediaBounds(
            double requestedMilliseconds,
            double durationMilliseconds,
            long expectedMilliseconds)
        {
            Assert.Equal(
                expectedMilliseconds,
                PlaybackViewModel.ClampSeekMilliseconds(requestedMilliseconds, durationMilliseconds));
        }

        [Fact]
        public void FullscreenLayout_HidesChromeAndGivesPlaybackTheOnlyStarRow()
        {
            GridLength[] heights = PlaybackView.CreateFullscreenRowHeights(rowCount: 4, playbackRowIndex: 2);

            Assert.Collection(
                heights,
                height => Assert.Equal(new GridLength(0), height),
                height => Assert.Equal(new GridLength(0), height),
                height => Assert.Equal(new GridLength(1, GridUnitType.Star), height),
                height => Assert.Equal(new GridLength(0), height));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, -1)]
        [InlineData(3, 3)]
        public void FullscreenLayout_RejectsMissingPlaybackRow(int rowCount, int playbackRowIndex)
        {
            if (rowCount == 0)
            {
                Assert.Empty(PlaybackView.CreateFullscreenRowHeights(rowCount, playbackRowIndex));
                return;
            }

            Assert.Throws<ArgumentOutOfRangeException>(
                () => PlaybackView.CreateFullscreenRowHeights(rowCount, playbackRowIndex));
        }
    }
}
