using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>The GirlFriday item was observed on archive.org on October 23, 2026, trimmed to the fields the
/// provider reads: its metadata describes His Girl Friday (1940), but its only film file is named for
/// The Saint's Girl Friday (1953) and runs 64 minutes. A file's own name that pairs another year with words
/// that are not the item's title declares another film; it cannot be offered as the item's film.</summary>
public sealed class ArchiveForeignFilmFileTests
{
    [Fact]
    public async Task FileNamedForAnotherFilmAndYearIsNotOfferedAsTheItemsFilm()
    {
        Assert.Empty(await FindSourcesAsync(Film("His Girl Friday", 1940), "GirlFriday", "His Girl Friday", "1940",
            File("The_Saint's_Girl_Friday_1953.mp4", "original", "3868.35"),
            File("The_Saint's_Girl_Friday_1953.ogv", "derivative", "3868.35")));
    }

    [Fact]
    public async Task ItemsOwnFilmFileIsChosenOverAFileNamedForAnotherFilm()
    {
        AudiovisualSource source = Assert.Single(await FindSourcesAsync(Film("His Girl Friday", 1940), "GirlFriday",
            "His Girl Friday", "1940",
            File("The_Saint's_Girl_Friday_1953.mp4", "original", "3868.35"),
            File("his_girl_friday.mp4", "derivative", "5502.1")));
        Assert.EndsWith("/GirlFriday/his_girl_friday.mp4", source.Location.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    // A re-release or restoration year beside the item's own title is the same film.
    [InlineData("Reefer Madness", 1936, "Reefer_Madness_1938.mp4")]
    [InlineData("Reefer Madness", 1936, "reefer_madness1938.mp4")]
    [InlineData("Metropolis", 1927, "Metropolis_2010_restoration.mp4")]
    // A title can itself contain a year-like number.
    [InlineData("2001: A Space Odyssey", 1968, "2001_A_Space_Odyssey.mp4")]
    // A bare number is an upload ID, not a declared release.
    [InlineData("Night of the Living Dead", 1968, "1900.mp4")]
    // The item's own year in another film-looking name is not a conflict.
    [InlineData("Night of the Living Dead", 1968, "Night_1968.mp4")]
    public async Task FileNameWithoutAnotherFilmsYearAndTitleIsStillOffered(string title, int year, string file)
    {
        string identifier = "film-item";
        AudiovisualSource source = Assert.Single(await FindSourcesAsync(Film(title, year), identifier, title,
            year.ToString(System.Globalization.CultureInfo.InvariantCulture), File(file, "original", "5400")));
        Assert.EndsWith("/" + Uri.EscapeDataString(file), source.Location.AbsoluteUri, StringComparison.Ordinal);
    }

    private static AudiovisualIdentity Film(string title, int year) => new()
    {
        Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature,
        Title = title,
        Year = year
    };

    private static string File(string name, string source, string length) =>
        $$"""{"name":{{JsonSerializer.Serialize(name)}},"format":"MPEG4","source":"{{source}}","size":"500000000","length":"{{length}}"}""";

    private static async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(AudiovisualIdentity film, string identifier,
        string observedTitle, string date, params string[] files)
    {
        string title = JsonSerializer.Serialize(observedTitle);
        var responses = new Dictionary<string, string>
        {
            ["/advancedsearch.php"] = $$$"""{"response":{"docs":[{"identifier":"{{{identifier}}}","title":{{{title}}},"year":{{{date}}}}]}}""",
            ["/metadata/" + identifier] = $$"""
                {"metadata":{"identifier":"{{identifier}}","mediatype":"movies","title":{{title}},"date":"{{date}}",
                  "subject":"Comedy;Drama;Romance","collection":["Comedy_Films","feature_films","moviesandfilms"]},
                 "files":[{{string.Join(",", files)}}]}
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
            Assert.True(responses.TryGetValue(request.RequestUri!.AbsolutePath, out string? body),
                $"Unexpected request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body!, Encoding.UTF8, "application/json")
            });
        }
    }
}
