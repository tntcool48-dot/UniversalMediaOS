using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Controls
{
    public static class AsyncImageLoader
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private static readonly ImageRequestLoader _requestLoader = new(_httpClient);

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(18)
            };

            // BitmapImage reliably decodes PNG/JPEG on every supported Windows
            // install. Preferring AVIF/WebP can make a server return a format for
            // which the machine has no codec, producing apparently blank posters.
            client.DefaultRequestHeaders.Accept.ParseAdd("image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");
            return client;
        }

        internal static HttpClient CreateHttpClientForTesting() => CreateHttpClient();
        
        private static readonly DependencyProperty LoadStateProperty =
            DependencyProperty.RegisterAttached(
                "LoadState",
                typeof(ImageLoadState),
                typeof(AsyncImageLoader), 
                new PropertyMetadata(null));

        public static readonly DependencyProperty ImageUrlProperty =
            DependencyProperty.RegisterAttached(
                "ImageUrl", 
                typeof(string), 
                typeof(AsyncImageLoader), 
                new PropertyMetadata(string.Empty, OnImageUrlChanged));

        public static string GetImageUrl(DependencyObject obj) => (string)obj.GetValue(ImageUrlProperty);
        public static void SetImageUrl(DependencyObject obj, string value) => obj.SetValue(ImageUrlProperty, value);

        public static readonly DependencyProperty DecodeWidthProperty =
            DependencyProperty.RegisterAttached(
                "DecodeWidth", 
                typeof(int), 
                typeof(AsyncImageLoader), 
                new PropertyMetadata(200, OnImageUrlChanged));

        public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
        public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

        private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image imageControl) return;
            ImageLoadState state = GetState(imageControl);
            CancelPending(state);
            state.CompletedKey = null;
            imageControl.Source = null;
            if (state.FailureToolTip != null && Equals(imageControl.ToolTip, state.FailureToolTip)) imageControl.ToolTip = null;
            state.FailureToolTip = null;
            if (state.IsAttached) _ = LoadImageAsync(imageControl, state);
        }

        private static ImageLoadState GetState(Image image)
        {
            if (image.GetValue(LoadStateProperty) is ImageLoadState existing) return existing;
            var state = new ImageLoadState { IsAttached = image.IsLoaded };
            image.SetValue(LoadStateProperty, state);
            image.Loaded += ImageLoaded;
            image.Unloaded += ImageUnloaded;
            return state;
        }

        private static void ImageLoaded(object sender, RoutedEventArgs e)
        {
            var image = (Image)sender;
            ImageLoadState state = GetState(image);
            state.IsAttached = true;
            string key = ImageRequestLoader.Key(GetImageUrl(image), GetDecodeWidth(image));
            if (state.Active != null || (state.CompletedKey == key && image.Source != null)) return;
            _ = LoadImageAsync(image, state);
        }

        private static void ImageUnloaded(object sender, RoutedEventArgs e)
        {
            ImageLoadState state = GetState((Image)sender);
            state.IsAttached = false;
            CancelPending(state);
        }

        private static void CancelPending(ImageLoadState state)
        {
            state.Active?.Cancel();
            state.Active = null; // The canceled operation disposes its own source in finally.
        }

        private static async Task LoadImageAsync(Image imageControl, ImageLoadState state)
        {
            string url = GetImageUrl(imageControl);
            if (string.IsNullOrWhiteSpace(url)) return;
            int decodeWidth = GetDecodeWidth(imageControl);
            string key = ImageRequestLoader.Key(url, decodeWidth);
            using var operation = new CancellationTokenSource();
            state.Active = operation;
            bool IsCurrent() => state.IsAttached && ReferenceEquals(state.Active, operation) &&
                !operation.IsCancellationRequested && key == ImageRequestLoader.Key(GetImageUrl(imageControl), GetDecodeWidth(imageControl));
            try
            {
                BitmapImage bitmap = await (state.Loader ?? _requestLoader).LoadAsync(url, decodeWidth, operation.Token);
                if (!IsCurrent()) return;
                imageControl.Source = bitmap;
                state.CompletedKey = key;
                if (state.FailureToolTip != null && Equals(imageControl.ToolTip, state.FailureToolTip))
                    imageControl.ToolTip = null;
                state.FailureToolTip = null;
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                // Recycled, detached or superseded consumer; other owners may continue.
            }
            catch (Exception ex)
            {
                if (!IsCurrent()) return;
                AppLogger.Log($"Image load failed for '{url}': {ex.Message}", "WARNING");
                imageControl.Source = CreateFallbackImage();
                if (imageControl.ToolTip == null)
                {
                    state.FailureToolTip = ex is TimeoutException ? "Image request timed out." : "Image failed to load.";
                    imageControl.ToolTip = state.FailureToolTip;
                }
            }
            finally
            {
                if (ReferenceEquals(state.Active, operation)) state.Active = null;
            }
        }

        private sealed class ImageLoadState
        {
            public bool IsAttached;
            public string? CompletedKey;
            public string? FailureToolTip;
            public CancellationTokenSource? Active;
            public ImageRequestLoader? Loader;
        }

        internal static void SetRequestLoaderForTesting(Image image, ImageRequestLoader loader) => GetState(image).Loader = loader;

        internal sealed class ImageRequestLoader
        {
            private const int MaximumImageBytes = 25 * 1024 * 1024;
            private readonly HttpClient _client;
            private readonly TimeSpan _requestLifetime;
            private readonly SemaphoreSlim _concurrency;
            private readonly object _sync = new();
            private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
            private readonly LruCache<string, BitmapImage> _cache = new(150);
            private int _active;
            private int _peak;

            public ImageRequestLoader(HttpClient client, TimeSpan? requestLifetime = null, int concurrency = 4)
            {
                _client = client;
                _requestLifetime = requestLifetime ?? TimeSpan.FromSeconds(18);
                _concurrency = new(Math.Clamp(concurrency, 1, 8));
            }

            internal static string Key(string url, int decodeWidth) => $"{decodeWidth}|{url}";
            internal int Active => Volatile.Read(ref _active);
            internal int Peak => Volatile.Read(ref _peak);
            internal int Pending { get { lock (_sync) return _flights.Count; } }
            internal int Cached => _cache.Count;

            public async Task<BitmapImage> LoadAsync(string url, int decodeWidth, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                string key = Key(url, decodeWidth);
                Flight flight;
                lock (_sync)
                {
                    if (decodeWidth is > 0 and <= 600 && _cache.TryGetValue(key, out var cached)) return cached;
                    if (!_flights.TryGetValue(key, out flight!))
                    {
                        flight = new(f => FetchAndCacheAsync(key, url, decodeWidth, f));
                        _flights.Add(key, flight);
                    }
                    flight.Owners++;
                }
                Task<BitmapImage> task = flight.Task;
                try { return await task.WaitAsync(token).ConfigureAwait(false); }
                finally
                {
                    bool cancel = false;
                    lock (_sync)
                    {
                        if (--flight.Owners == 0 && !task.IsCompleted)
                        {
                            Remove(key, flight);
                            cancel = true;
                        }
                    }
                    if (cancel) flight.Cancel();
                }
            }

            private void Remove(string key, Flight flight)
            {
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            }

            private async Task<BitmapImage> FetchAndCacheAsync(string key, string url, int decodeWidth, Flight flight)
            {
                bool acquired = false;
                try
                {
                    await _concurrency.WaitAsync(flight.Cancellation.Token).ConfigureAwait(false);
                    acquired = true;
                    int active = Interlocked.Increment(ref _active);
                    int prior;
                    do { prior = Volatile.Read(ref _peak); } while (active > prior && Interlocked.CompareExchange(ref _peak, active, prior) != prior);
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(flight.Cancellation.Token);
                    lifetime.CancelAfter(_requestLifetime);
                    CancellationToken token = lifetime.Token;
                    try
                    {
                        using var request = CreateImageRequest(url);
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(contentType) && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Unexpected image content type '{contentType}'.");
                        if (response.Content.Headers.ContentLength is > MaximumImageBytes)
                            throw new InvalidDataException("Image exceeds the 25 MB safety limit.");
                        using var stream = await response.Content.ReadAsStreamAsync(token).WaitAsync(token).ConfigureAwait(false);
                        using var bytes = new MemoryStream();
                        byte[] buffer = new byte[16 * 1024];
                        while (true)
                        {
                            int read = await stream.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
                            if (read == 0) break;
                            if (bytes.Length + read > MaximumImageBytes) throw new InvalidDataException("Image exceeds the 25 MB safety limit.");
                            bytes.Write(buffer, 0, read);
                        }
                        token.ThrowIfCancellationRequested();
                        var bitmap = await Task.Run(() => DecodeBitmap(bytes.ToArray(), decodeWidth, token), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (decodeWidth is > 0 and <= 600) _cache.Add(key, bitmap);
                        return bitmap;
                    }
                    catch (OperationCanceledException) when (!flight.Cancellation.IsCancellationRequested)
                    { throw new TimeoutException("Image headers, body or decode exceeded the request deadline."); }
                }
                finally
                {
                    if (acquired) { Interlocked.Decrement(ref _active); _concurrency.Release(); }
                    lock (_sync) Remove(key, flight);
                    flight.Cancellation.Dispose();
                }
            }

            private sealed class Flight
            {
                public int Owners;
                public CancellationTokenSource Cancellation { get; } = new();
                private readonly Lazy<Task<BitmapImage>> _task;
                public Flight(Func<Flight, Task<BitmapImage>> factory) => _task = new(() => factory(this), LazyThreadSafetyMode.ExecutionAndPublication);
                public Task<BitmapImage> Task => _task.Value;
                public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            }
        }

        internal static BitmapImage DecodeBitmap(byte[] imageBytes, int decodeWidth, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            using var bitmapStream = new MemoryStream(imageBytes);
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = bitmapStream;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();

            token.ThrowIfCancellationRequested();
            return bitmap;
        }

        private static HttpRequestMessage CreateImageRequest(string url) =>
            new(HttpMethod.Get, url);

        internal static HttpRequestMessage CreateImageRequestForTesting(string url) =>
            CreateImageRequest(url);

        private static ImageSource CreateFallbackImage()
        {
            const int width = 32;
            const int height = 32;
            const int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x37;
                pixels[i + 1] = 0x29;
                pixels[i + 2] = 0x20;
                pixels[i + 3] = 0xFF;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            return bitmap;
        }

        private class LruCache<TKey, TValue> where TKey : notnull
        {
            private readonly int _capacity;
            private readonly System.Collections.Generic.Dictionary<TKey, System.Collections.Generic.LinkedListNode<CacheEntry>> _cacheMap = new();
            private readonly System.Collections.Generic.LinkedList<CacheEntry> _lruList = new();
            private readonly object _lock = new();

            private struct CacheEntry
            {
                public TKey Key { get; }
                public TValue Value { get; }
                public CacheEntry(TKey key, TValue value) => (Key, Value) = (key, value);
            }

            public LruCache(int capacity)
            {
                _capacity = capacity;
            }

            public int Count { get { lock (_lock) return _cacheMap.Count; } }

            public bool TryGetValue(TKey key, out TValue value)
            {
                lock (_lock)
                {
                    if (_cacheMap.TryGetValue(key, out var node))
                    {
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }
                    value = default!;
                    return false;
                }
            }

            public void Add(TKey key, TValue value)
            {
                lock (_lock)
                {
                    if (_cacheMap.TryGetValue(key, out var node))
                    {
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        return;
                    }

                    if (_cacheMap.Count >= _capacity)
                    {
                        var lastNode = _lruList.Last;
                        if (lastNode != null)
                        {
                            _cacheMap.Remove(lastNode.Value.Key);
                            _lruList.RemoveLast();
                        }
                    }

                    var newNode = new System.Collections.Generic.LinkedListNode<CacheEntry>(new CacheEntry(key, value));
                    _lruList.AddFirst(newNode);
                    _cacheMap[key] = newNode;
                }
            }
        }
    }
}
