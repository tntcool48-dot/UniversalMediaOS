using System.Net;
using System.Net.Http;
using System.Text;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>Uploads observed on archive.org on October 23, 2026, trimmed to the fields the provider reads.
/// Uploaders often append the item's own year to its title ("Title (1964)", "Title [1950]", "Title 1964");
/// that suffix is formatting, not part of the film's name, only when it repeats the item's own year.</summary>
public sealed class ArchiveYearSuffixedTitleTests
{
    [Theory]
    [InlineData("The Last Man on Earth", 1964, "the-last-man-on-earth-1964_202312", "The Last Man on Earth (1964)", "1964-05-06",
        "The Last Man on Earth (1964).mp4", "5223.21")]
    [InlineData("The Last Man on Earth", 1964, "the-last-man-on-earth-1964_202606", "The Last Man On Earth 1964", "1964",
        "The Last Man on Earth -1964-.mp4", "5223.26")]
    [InlineData("D.O.A.", 1950, "doa-1950_202506", "D.O.A. [1950]", "1950-04-21", "DOA 1950.mp4", "4967.66")]
    [InlineData("Dementia 13", 1963, "dementia.-13.1963", "Dementia 13 (1963)", "1963",
        "Dementia 13 (1963)/mp4/Dementia.13.1963.mp4", "4123.37")]
    public async Task UploadTitledWithItsOwnYearIsOfferedForThatFilm(string film, int year, string identifier,
        string observedTitle, string date, string file, string length)
    {
        AudiovisualSource source = Assert.Single(await FindSourcesAsync(Film(film, year), identifier, observedTitle, year, date, file, length));

        Assert.Equal(new Uri($"https://archive.example/download/{Uri.EscapeDataString(identifier)}/" +
            string.Join("/", file.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri, source.Location.AbsoluteUri);
        Assert.Equal(observedTitle, source.Identity.Title);
        Assert.Equal(year, source.Identity.Year);
    }

    [Fact]
    public async Task TrailerTitledWithTheFilmsYearStaysUnmatched()
    {
        Assert.Empty(await FindSourcesAsync(Film("Dementia 13", 1963), "sinema-trailer_dementia-13",
            "Dementia 13 [1963] - Trailer", 1963, "1963", "dementia13_trailer.mp4", "120.5"));
    }

    [Fact]
    public async Task YearSuffixThatIsNotTheItemsOwnYearIsPartOfTheTitle()
    {
        // A 1990 upload titled with 1968 names neither film exactly; it cannot become the 1990 remake.
        Assert.Empty(await FindSourcesAsync(Film("Night of the Living Dead", 1990), "notld-mixed",
            "Night of the Living Dead (1968)", 1990, "1990", "notld.mp4", "5760"));
    }

    [Fact]
    public async Task FilmTitledWithItsOwnYearKeepsItsTitle()
    {
        AudiovisualSource source = Assert.Single(await FindSourcesAsync(Film("1984", 1984), "nineteen-eighty-four",
            "1984", 1984, "1984", "1984.mp4", "6780"));
        Assert.Equal("1984", source.Identity.Title);
    }

    private static AudiovisualIdentity Film(string title, int year) => new()
    {
        Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature,
        Title = title,
        Year = year
    };

    private static async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(AudiovisualIdentity film, string identifier,
        string observedTitle, int year, string date, string file, string length)
    {
        string title = System.Text.Json.JsonSerializer.Serialize(observedTitle);
        string name = System.Text.Json.JsonSerializer.Serialize(file);
        var responses = new Dictionary<string, string>
        {
            ["/advancedsearch.php"] = $$$"""{"response":{"docs":[{"identifier":"{{{identifier}}}","title":{{{title}}},"year":{{{year}}}}]}}""",
            ["/metadata/" + identifier] = $$"""
                {"metadata":{"identifier":"{{identifier}}","mediatype":"movies","title":{{title}},"date":"{{date}}",
                  "collection":["opensource_movies"],"licenseurl":"https://creativecommons.org/publicdomain/mark/1.0/"},
                 "files":[{"name":{{name}},"format":"MPEG4","source":"original","size":"500000000","length":"{{length}}"}]}
                """
        };
        using var http = new HttpClient(new Responses(responses));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions
        {
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        }, new ProviderRequestCoordinator(http));
        return await provider.FindSourcesAsync(film, AudiovisualUnit.Feature);
    }

    private sealed class Responses(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(responses.TryGetValue(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath), out string? body),
                $"Unexpected request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body!, Encoding.UTF8, "application/json")
            });
        }
    }
}
