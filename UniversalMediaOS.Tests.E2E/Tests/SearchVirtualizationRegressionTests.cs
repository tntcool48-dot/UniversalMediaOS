using System.IO;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class SearchVirtualizationRegressionTests
    {
        [Theory]
        [InlineData(double.NaN, 1)]
        [InlineData(0, 1)]
        [InlineData(285, 1)]
        [InlineData(552, 2)]
        [InlineData(1_084, 4)]
        public void ResultColumnCount_IsStableAtViewportBoundaries(double width, int expected)
        {
            Assert.Equal(expected, SearchViewModel.CalculateResultColumns(width));
        }

        [Fact]
        public void ResultRows_ReflowWithoutDroppingOrDuplicatingLoadedResults()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-search-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var viewModel = new SearchViewModel(
                    new FuzzyShieldSearch(),
                    new DubAvailabilityService(),
                    new FavoriteMediaService(Path.Combine(root, "favorites.json")));
                viewModel.SetResultsViewportWidth(818); // Three 266px card footprints plus view padding.
                viewModel.SearchResults.AddRange(
                    Enumerable.Range(1, 7).Select(id => new MediaResult { Id = id, OfficialTitle = $"Anime {id}" }));

                Assert.Equal([3, 3, 1], viewModel.SearchResultRows.Select(row => row.Results.Count));
                Assert.Equal(
                    Enumerable.Range(1, 7),
                    viewModel.SearchResultRows.SelectMany(row => row.Results).Select(result => result.Id));
                SearchResultRow stableFirstRow = viewModel.SearchResultRows[0];
                SearchResultRow stableSecondRow = viewModel.SearchResultRows[1];

                viewModel.SearchResults.AddRange(
                    Enumerable.Range(8, 2).Select(id => new MediaResult { Id = id, OfficialTitle = $"Anime {id}" }));

                Assert.Same(stableFirstRow, viewModel.SearchResultRows[0]);
                Assert.Same(stableSecondRow, viewModel.SearchResultRows[1]);
                Assert.Equal(Enumerable.Range(1, 9), viewModel.SearchResultRows.SelectMany(row => row.Results).Select(result => result.Id));

                viewModel.SetResultsViewportWidth(1_084);
                var partialRow = viewModel.SearchResultRows[^1];
                viewModel.SearchResults.ReplaceRange(viewModel.SearchResults.ToArray());
                Assert.Same(partialRow, viewModel.SearchResultRows[^1]);

                viewModel.SetResultsViewportWidth(552);

                Assert.Equal([2, 2, 2, 2, 1], viewModel.SearchResultRows.Select(row => row.Results.Count));
                Assert.Equal(
                    Enumerable.Range(1, 9),
                    viewModel.SearchResultRows.SelectMany(row => row.Results).Select(result => result.Id));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
