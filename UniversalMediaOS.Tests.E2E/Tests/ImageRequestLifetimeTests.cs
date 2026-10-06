using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UniversalMediaOS.WPF.Controls;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class ImageRequestLifetimeTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task StalledBodyHasDeadlineAndFailureIsNotCached()
    {
        var body = new BlockingStream();
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(new StreamContent(body))), lifetime: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Loader.LoadAsync("https://images.example/slow", 200));
        Assert.True(body.Disposed);
        Assert.Equal(0, fixture.Loader.Active);
        Assert.Equal(0, fixture.Loader.Pending);
        Assert.Equal(0, fixture.Loader.Cached);
        body.Release.TrySetResult(0); // Late read cannot publish or hold the slot.
    }

    [Fact]
    public async Task DuplicateConsumersShareFrozenBitmapAndCancelingOneKeepsOtherOwner()
    {
        var release = Signal();
        using var fixture = new Fixture(async (_, token) => { await release.Task.WaitAsync(token); return Response(); });
        using var firstOwner = new CancellationTokenSource();
        var first = fixture.Loader.LoadAsync("https://images.example/shared", 200, firstOwner.Token);
        var second = fixture.Loader.LoadAsync("https://images.example/shared", 200);
        Assert.Equal(1, fixture.Requests);
        firstOwner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        release.SetResult();
        var bitmap = await second;
        Assert.True(bitmap.IsFrozen);
        Assert.Same(bitmap, await fixture.Loader.LoadAsync("https://images.example/shared", 200));
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task FinalOwnerCancellationReleasesIgnoredBodyAndAllowsImmediateRetry()
    {
        var body = new BlockingStream();
        int attempts = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? Response(new StreamContent(body)) : Response()));
        using var owner = new CancellationTokenSource();
        var first = fixture.Loader.LoadAsync("https://images.example/retry", 200, owner.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        owner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Eventually(() => fixture.Loader.Active == 0);
        Assert.True(body.Disposed);
        Assert.True((await fixture.Loader.LoadAsync("https://images.example/retry", 200)).IsFrozen);
        body.Release.TrySetResult(0);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task QueuedCanceledConsumersDoNotStartNetworkAndActiveWorkIsBounded()
    {
        var release = Signal();
        using var fixture = new Fixture(async (_, token) => { await release.Task.WaitAsync(token); return Response(); });
        using var queuedOwner = new CancellationTokenSource();
        var active = Enumerable.Range(0, 4).Select(i => fixture.Loader.LoadAsync($"https://images.example/active/{i}", 200)).ToArray();
        var queued = Enumerable.Range(0, 50).Select(i => fixture.Loader.LoadAsync($"https://images.example/queued/{i}", 200, queuedOwner.Token)).ToArray();
        Assert.Equal(4, fixture.Requests);
        queuedOwner.Cancel();
        foreach (var task in queued) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        release.SetResult();
        await Task.WhenAll(active);
        Assert.Equal(4, fixture.Requests);
        Assert.Equal(4, fixture.Loader.Peak);
        await Eventually(() => fixture.Loader.Pending == 0);
    }

    [Fact]
    public async Task LargeConsumerBatchCoalescesRequestsAndCacheIsBounded()
    {
        var release = Signal();
        using var fixture = new Fixture(async (_, token) => { await release.Task.WaitAsync(token); return Response(); });
        var tasks = Enumerable.Range(0, 1_000).Select(i => fixture.Loader.LoadAsync($"https://images.example/poster/{i % 200}", 200)).ToArray();
        Assert.Equal(4, fixture.Requests);
        Assert.Equal(200, fixture.Loader.Pending);
        release.SetResult();
        await Task.WhenAll(tasks);
        Assert.Equal(200, fixture.Requests);
        Assert.Equal(4, fixture.Loader.Peak);
        Assert.Equal(150, fixture.Loader.Cached);
        Assert.Equal(0, fixture.Loader.Active);
        Assert.Equal(0, fixture.Loader.Pending);
    }

    [Fact]
    public async Task DifferentDecodeWidthsKeepSeparateImagesAndLargePagesAreNotCached()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response()));
        var small = await fixture.Loader.LoadAsync("https://images.example/poster", 200);
        var wide = await fixture.Loader.LoadAsync("https://images.example/poster", 1000);
        Assert.Equal(200, small.PixelWidth);
        Assert.Equal(1000, wide.PixelWidth);
        Assert.NotSame(small, wide);
        await fixture.Loader.LoadAsync("https://images.example/poster", 1000);
        Assert.Equal(3, fixture.Requests);
        Assert.Equal(1, fixture.Loader.Cached);
    }

    [Theory]
    [InlineData("text/html", false)]
    [InlineData("image/png", true)]
    public async Task ContentTypeAndDeclaredByteLimitsRemainEnforced(string type, bool oversized)
    {
        using var fixture = new Fixture((_, _) =>
        {
            var response = Response();
            response.Content.Headers.ContentType = new(type);
            if (oversized) response.Content.Headers.ContentLength = 25 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Loader.LoadAsync("https://images.example/invalid", 200));
        Assert.Equal(0, fixture.Loader.Cached);
        Assert.Equal(0, fixture.Loader.Active);
    }

    [Fact]
    public async Task ActualStreamingByteLimitIsEnforcedWithoutDeclaredLength()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Response(new StreamContent(new GrowingStream()))));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Loader.LoadAsync("https://images.example/oversized", 200));
        Assert.Equal(0, fixture.Loader.Cached);
        Assert.Equal(0, fixture.Loader.Active);
    }

    [Fact]
    public async Task DetachedControlDoesNotFetchAndReloadRestartsCanceledRequest() => await OnDispatcher(async () =>
    {
        var body = new BlockingStream();
        int attempts = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? Response(new StreamContent(body)) : Response()));
        var image = new Image();
        AsyncImageLoader.SetRequestLoaderForTesting(image, fixture.Loader);
        AsyncImageLoader.SetImageUrl(image, "https://images.example/reload");
        Assert.Equal(0, fixture.Requests);
        Loaded(image);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Unloaded(image);
        await Eventually(() => fixture.Loader.Active == 0);
        Assert.Null(image.Source);
        Loaded(image);
        await Eventually(() => image.Source is BitmapImage);
        Assert.Equal(2, fixture.Requests);
        Assert.True(body.Disposed);
        Unloaded(image);
        Loaded(image);
        Assert.Equal(2, fixture.Requests); // Successful retained image needs no reload.
        body.Release.TrySetResult(0);
    });

    [Fact]
    public async Task RecyclingRejectsOldLateFaultAndDecodeWidthChangeReloadsCurrentImage() => await OnDispatcher(async () =>
    {
        var old = Signal<HttpResponseMessage>();
        using var fixture = new Fixture((request, _) => request.RequestUri!.AbsolutePath == "/old" ? old.Task : Task.FromResult(Response()));
        var image = new Image { ToolTip = "Existing descriptive tooltip" };
        AsyncImageLoader.SetRequestLoaderForTesting(image, fixture.Loader);
        AsyncImageLoader.SetImageUrl(image, "https://images.example/old");
        Loaded(image);
        AsyncImageLoader.SetImageUrl(image, "https://images.example/new");
        await Eventually(() => image.Source is BitmapImage);
        var current = image.Source;
        old.SetException(new HttpRequestException("late recycled failure"));
        await Eventually(() => fixture.Loader.Active == 0);
        Assert.Same(current, image.Source);
        Assert.False(AsyncImageLoader.GetHasError(image));
        Assert.False(AsyncImageLoader.GetIsLoading(image));
        Assert.Equal("Existing descriptive tooltip", image.ToolTip);
        AsyncImageLoader.SetDecodeWidth(image, 320);
        await Eventually(() => image.Source is BitmapImage bitmap && bitmap.PixelWidth == 320);
        Assert.Equal(3, fixture.Requests);
        AsyncImageLoader.SetImageUrl(image, string.Empty);
        Assert.Null(image.Source);
    });

    [Fact]
    public async Task ControlTimeoutShowsFallbackThenReloadRetriesAndClearsOwnedError() => await OnDispatcher(async () =>
    {
        var body = new BlockingStream();
        int attempts = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? Response(new StreamContent(body)) : Response()), lifetime: TimeSpan.FromMilliseconds(100));
        var image = new Image();
        AsyncImageLoader.SetRequestLoaderForTesting(image, fixture.Loader);
        AsyncImageLoader.SetImageUrl(image, "https://images.example/recover");
        Loaded(image);
        await Eventually(() => Equals(image.ToolTip, "Image request timed out."));
        Assert.NotNull(image.Source);
        Assert.Equal(0, fixture.Loader.Cached);
        Unloaded(image);
        Loaded(image);
        await Eventually(() => image.Source is BitmapImage);
        Assert.Null(image.ToolTip);
        Assert.Equal(2, fixture.Requests);
        body.Release.TrySetResult(0);
    });

    [Fact]
    public async Task UnloadingOneControlDoesNotCancelAnotherControlSharingItsImage() => await OnDispatcher(async () =>
    {
        var release = Signal();
        using var fixture = new Fixture(async (_, token) => { await release.Task.WaitAsync(token); return Response(); });
        var images = new[] { new Image(), new Image() };
        foreach (var image in images)
        {
            AsyncImageLoader.SetRequestLoaderForTesting(image, fixture.Loader);
            AsyncImageLoader.SetImageUrl(image, "https://images.example/shared-control");
            Loaded(image);
        }
        Assert.Equal(1, fixture.Requests);
        Unloaded(images[0]);
        release.SetResult();
        await Eventually(() => images[1].Source is BitmapImage);
        Assert.Null(images[0].Source);
        Loaded(images[0]);
        await Eventually(() => images[0].Source != null);
        Assert.Same(images[1].Source, images[0].Source);
        Assert.Equal(1, fixture.Requests);
    });

    [Fact]
    public async Task ExplicitRetryRetainsOwnershipAvoidsDuplicateRequestsAndPreservesDescriptiveTooltip() => await OnDispatcher(async () =>
    {
        var release = Signal();
        int attempts = 0;
        using var fixture = new Fixture(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            await release.Task.WaitAsync(token);
            return Response();
        });
        var image = new Image { ToolTip = "Page one" };
        AsyncImageLoader.SetRequestLoaderForTesting(image, fixture.Loader);
        AsyncImageLoader.SetImageUrl(image, "https://images.example/explicit-retry");
        Loaded(image);
        await Eventually(() => AsyncImageLoader.GetHasError(image) && !AsyncImageLoader.GetIsLoading(image));
        Assert.NotNull(image.Source);
        Assert.Equal("Page one", image.ToolTip);
        AsyncImageLoader.Retry(image);
        AsyncImageLoader.Retry(image);
        Assert.False(AsyncImageLoader.GetHasError(image));
        Assert.True(AsyncImageLoader.GetIsLoading(image));
        Assert.Equal(2, fixture.Requests);
        release.SetResult();
        await Eventually(() => image.Source is BitmapImage && !AsyncImageLoader.GetIsLoading(image));
        Assert.Equal("Page one", image.ToolTip);
        var decoded = image.Source;
        AsyncImageLoader.Retry(image);
        Assert.Same(decoded, image.Source);
        Assert.Equal(2, fixture.Requests);
        Unloaded(image);
        AsyncImageLoader.Retry(image);
        Assert.Equal(2, fixture.Requests);
    });

    private static void Loaded(Image image) => image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    private static void Unloaded(Image image) => image.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    private static HttpResponseMessage Response(HttpContent? content = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content ?? new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static Task OnDispatcher(Func<Task> test)
    {
        var completed = Signal();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); // No visible test window is created.
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Handler _handler;
        private readonly HttpClient _client;
        public AsyncImageLoader.ImageRequestLoader Loader { get; }
        public int Requests => Volatile.Read(ref _handler.Requests);
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, TimeSpan? lifetime = null)
        {
            _handler = new(handler);
            _client = new(_handler) { Timeout = Timeout.InfiniteTimeSpan };
            Loader = new(_client, lifetime);
        }
        public void Dispose() => _client.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); return handler(request, token); }
    }
    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource<int> Release { get; } = Signal<int>();
        public bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Started.TrySetResult(); return await Release.Task; } // Deliberately ignores cancellation.
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class GrowingStream : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(buffer.Length); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
