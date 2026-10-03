using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ProviderOutcomeContractTests
{
    private static readonly Uri Request = new("https://provider.example/items?token=private-value");
    private static AudiovisualProviderDefinition Provider => new()
    {
        Id = "fixture-provider", BaseUrl = "https://provider.example", RequestsPerMinute = 0,
        CacheSeconds = 60, TimeoutSeconds = 1, MaxResponseBytes = 1024
    };

    [Theory]
    [InlineData(200, ProviderOutcomeStatus.Success)]
    [InlineData(204, ProviderOutcomeStatus.Success)]
    [InlineData(401, ProviderOutcomeStatus.InvalidCredentials)]
    [InlineData(403, ProviderOutcomeStatus.InvalidCredentials)]
    [InlineData(404, ProviderOutcomeStatus.NotFound)]
    [InlineData(429, ProviderOutcomeStatus.RateLimited)]
    [InlineData(503, ProviderOutcomeStatus.Unavailable)]
    [InlineData(500, ProviderOutcomeStatus.Unavailable)]
    public async Task HttpStatusAndEmptyBodyHaveDistinctOutcomes(int status, ProviderOutcomeStatus expected)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") })));
        var result = await new ProviderRequestCoordinator(client).FetchAsync(Provider, Request);
        Assert.Equal(expected, result.Outcome.Status);
        Assert.Equal(expected == ProviderOutcomeStatus.Success ? "" : null, result.ResponseText);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result.Outcome));
    }

    [Fact]
    public async Task CachePreservesSuccessMetadataAndCancellationStillPropagates()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        using var client = new HttpClient(handler);
        var coordinator = new ProviderRequestCoordinator(client);
        var first = await coordinator.FetchAsync(Provider, Request);
        var second = await coordinator.FetchAsync(Provider, Request);
        Assert.False(first.IsFromCache);
        Assert.True(second.IsFromCache);
        Assert.Equal(first.CacheExpiresAtUtc, second.CacheExpiresAtUtc);
        Assert.NotNull(second.CacheExpiresAtUtc);
        Assert.Equal("[]", await coordinator.GetStringAsync(Provider, Request));
        Assert.Equal(1, handler.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.FetchAsync(Provider, Request, cancellation.Token));
    }

    [Theory]
    [InlineData(429, ProviderOutcomeStatus.RateLimited)]
    [InlineData(503, ProviderOutcomeStatus.Unavailable)]
    public async Task RetryAfterPreventsRepeatedRequestsWithoutLosingCause(int status, ProviderOutcomeStatus expected)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var coordinator = new ProviderRequestCoordinator(client);
        var first = await coordinator.FetchAsync(Provider, Request);
        var second = await coordinator.FetchAsync(Provider, Request);
        Assert.Equal(expected, second.Outcome.Status);
        Assert.Equal(first.Outcome.RetryAtUtc, second.Outcome.RetryAtUtc);
        Assert.Equal("provider_backoff", second.Outcome.DiagnosticCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisabledAndDisallowedRequestsNeverReachTransport()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not call transport"));
        using var client = new HttpClient(handler);
        var coordinator = new ProviderRequestCoordinator(client);
        Assert.Equal(ProviderOutcomeStatus.Disabled, (await coordinator.FetchAsync(Provider with { Enabled = false }, Request)).Outcome.Status);
        Assert.Equal(ProviderOutcomeStatus.NotConfigured, (await coordinator.FetchAsync(Provider with { Id = "" }, Request)).Outcome.Status);
        Assert.Equal(ProviderOutcomeStatus.Unsupported, (await coordinator.FetchAsync(Provider, new Uri("https://unrelated.example/items"))).Outcome.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizedBodyIsInvalidResponseAndTransportErrorsDoNotExposeMessages()
    {
        using var oversizedClient = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2048)) })));
        Assert.Equal(ProviderOutcomeStatus.InvalidResponse, (await new ProviderRequestCoordinator(oversizedClient).FetchAsync(Provider, Request)).Outcome.Status);
        using var failedClient = new HttpClient(new Handler(_ => throw new HttpRequestException("secret-cookie token=private-value")));
        var result = await new ProviderRequestCoordinator(failedClient).FetchAsync(Provider, Request);
        Assert.Equal(ProviderOutcomeStatus.Unavailable, result.Outcome.Status);
        Assert.DoesNotContain("secret-cookie", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task BodyDeadlineIsTimeoutButCallerCancellationIsThrown()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) })));
        var coordinator = new ProviderRequestCoordinator(client);
        Assert.Equal(ProviderOutcomeStatus.Timeout, (await coordinator.FetchAsync(Provider, Request)).Outcome.Status);
        coordinator.Invalidate();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.FetchAsync(Provider, Request, cancel.Token));
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(token);
        }
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
