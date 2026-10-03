namespace UniversalMediaOS.Core.OtherMedia;

internal static class AudiovisualHttp
{
    public static HttpClient SharedClient { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = OtherMediaHttpClientFactory.Create();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain;q=0.8, */*;q=0.5");
        return client;
    }
}
