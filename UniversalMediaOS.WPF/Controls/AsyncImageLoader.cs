using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace UniversalMediaOS.WPF.Controls
{
    public static class AsyncImageLoader
    {
        private static readonly HttpClient _httpClient = new();
        private static readonly LruCache<string, BitmapImage> _imageCache = new(150);
        
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

            // 4. Check cache first
            if (_imageCache.TryGetValue(url, out var cachedImage))
            {
                imageControl.Source = cachedImage;
                imageControl.SetValue(CancellationTokenSourceProperty, null);
                newCts.Dispose();
                return;
            }

            try
            {
                // 5. Fetch stream asynchronously
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(newCts.Token);
                
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, newCts.Token);
                ms.Position = 0;

                newCts.Token.ThrowIfCancellationRequested();

                // 6. Decode on UI thread using DecodePixelWidth constraint (Frozen)
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                int decodeWidth = GetDecodeWidth(imageControl);
                if (decodeWidth > 0)
                {
                    bitmap.DecodePixelWidth = decodeWidth;
                }
                bitmap.EndInit();
                bitmap.Freeze();

                if (!newCts.Token.IsCancellationRequested)
                {
                    _imageCache.Add(url, bitmap);
                    imageControl.Source = bitmap;
                }
            }
            catch (OperationCanceledException)
            {
                // Container was recycled/cancelled. Safe to ignore.
            }
            catch (Exception)
            {
                // Ignore other failures
            }
            finally
            {
                // Ensure CTS is disposed. Only clear the DP if it still belongs to this run.
                if (imageControl.GetValue(CancellationTokenSourceProperty) == newCts)
                {
                    imageControl.SetValue(CancellationTokenSourceProperty, null);
                    newCts.Dispose();
                }
                else
                {
                    newCts.Dispose();
                }
            }
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
