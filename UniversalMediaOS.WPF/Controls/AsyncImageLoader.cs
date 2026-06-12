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
        private static readonly ConcurrentDictionary<string, BitmapImage> _imageCache = new();
        
        // Use ConditionalWeakTable or attached property to track cancellation per Image container
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
                oldCts.Dispose();
            }

            string url = e.NewValue as string;
            
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
                return;
            }

            try
            {
                // 5. Fetch stream asynchronously
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(newCts.Token);
                
                // We need to copy it to a MemoryStream because decoding requires a seekable stream
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, newCts.Token);
                ms.Position = 0;

                newCts.Token.ThrowIfCancellationRequested();

                // 6. Decode on UI thread using DecodePixelWidth constraint (Frozen)
                // To avoid blocking UI thread entirely, we freeze it and only dispatch the initialization
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
                bitmap.Freeze(); // Mandatory Freezable Mandate

                if (!newCts.Token.IsCancellationRequested)
                {
                    _imageCache[url] = bitmap;
                    imageControl.Source = bitmap;
                }
            }
            catch (OperationCanceledException)
            {
                // Container was recycled before load finished. Safe to ignore.
            }
            catch (Exception)
            {
                // Fallback or ignore network failure
            }
        }
    }
}
