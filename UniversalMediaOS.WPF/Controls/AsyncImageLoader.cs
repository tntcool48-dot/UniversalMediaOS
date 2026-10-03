using System;
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
        private static readonly LruCache<string, ImageSource> _imageCache = new(150);

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
        
        private static readonly DependencyProperty CancellationTokenSourceProperty =
            DependencyProperty.RegisterAttached(
                "CancellationTokenSource", 
                typeof(CancellationTokenSource), 
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
                new PropertyMetadata(200));

        public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
        public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

        private static async void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image imageControl) return;

            // 1. Cancel previous pending request for this recycled container
            if (imageControl.GetValue(CancellationTokenSourceProperty) is CancellationTokenSource oldCts)
            {
                oldCts.Cancel();
            }

            string? url = e.NewValue as string;
            
            // 2. Clear stale image immediately to prevent recycling flashes
            imageControl.Source = null;

            if (string.IsNullOrWhiteSpace(url)) return;

            // 3. Setup new cancellation token
            var newCts = new CancellationTokenSource();
            imageControl.SetValue(CancellationTokenSourceProperty, newCts);
            int decodeWidth = GetDecodeWidth(imageControl);
            string cacheKey = $"{decodeWidth}|{url}";
            bool shouldCache = decodeWidth is > 0 and <= 600;

            // 4. Check cache first
            if (shouldCache && _imageCache.TryGetValue(cacheKey, out var cachedImage))
            {
                imageControl.Source = cachedImage;
                imageControl.SetValue(CancellationTokenSourceProperty, null);
                newCts.Dispose();
                return;
            }

            try
            {
                // 5. Fetch stream asynchronously
                using var request = CreateImageRequest(url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, newCts.Token);
                response.EnsureSuccessStatusCode();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(contentType) &&
                    !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unexpected image content type '{contentType}'.");
                }

                const int maximumImageBytes = 25 * 1024 * 1024;
                if (response.Content.Headers.ContentLength is > maximumImageBytes)
                {
                    throw new InvalidDataException("Image exceeds the 25 MB safety limit.");
                }

                using var stream = await response.Content.ReadAsStreamAsync(newCts.Token);
                using var ms = new MemoryStream();
                byte[] buffer = new byte[16 * 1024];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), newCts.Token);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (ms.Length + read > maximumImageBytes)
                    {
                        throw new InvalidDataException("Image exceeds the 25 MB safety limit.");
                    }

                    await ms.WriteAsync(buffer.AsMemory(0, read), newCts.Token);
                }
                newCts.Token.ThrowIfCancellationRequested();

                byte[] imageBytes = ms.ToArray();
                BitmapImage bitmap = await Task.Run(
                    () => DecodeBitmap(imageBytes, decodeWidth, newCts.Token),
                    newCts.Token);

                if (!newCts.Token.IsCancellationRequested)
                {
                    if (shouldCache)
                    {
                        _imageCache.Add(cacheKey, bitmap);
                    }
                    imageControl.Source = bitmap;
                }
            }
            catch (OperationCanceledException)
            {
                // Container was recycled/cancelled. Safe to ignore.
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Image load failed for '{url}': {ex.Message}", "WARNING");
                if (!newCts.Token.IsCancellationRequested)
                {
                    imageControl.Source = CreateFallbackImage();
                    imageControl.ToolTip ??= "Image failed to load.";
                }
            }
            finally
            {
                // Ensure CTS is disposed. Only clear the DP if it still belongs to this run.
                if (imageControl.GetValue(CancellationTokenSourceProperty) == newCts)
                {
                    imageControl.SetValue(CancellationTokenSourceProperty, null);
                }
                newCts.Dispose();
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
