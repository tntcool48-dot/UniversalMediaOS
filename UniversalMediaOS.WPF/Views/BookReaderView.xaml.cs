using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF.Views;

public partial class BookReaderView : UserControl
{
    private BookReaderViewModel? _viewModel;
    private string _allowedRoot = string.Empty;
    private string _allowedFile = string.Empty;
    private CoreWebView2? _configuredCore;
    private bool _isLoaded;

    public BookReaderView()
    {
        InitializeComponent();
        DataContextChanged += BookReaderView_DataContextChanged;
        Loaded += BookReaderView_Loaded;
        Unloaded += BookReaderView_Unloaded;
    }

    private void BookReaderView_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (_isLoaded && e.NewValue is BookReaderViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }
    }

    private async void BookReaderView_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        try
        {
            if (DataContext is BookReaderViewModel viewModel)
            {
                AttachViewModel(viewModel);
                if (!string.IsNullOrWhiteSpace(viewModel.CurrentLocation))
                {
                    await NavigateAsync(viewModel.CurrentLocation);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Book reader initialization failed: {ex.Message}", "ERROR");
            if (DataContext is BookReaderViewModel viewModel)
            {
                viewModel.ErrorMessage =
                    "The local book reader could not start. Verify that the Microsoft Edge WebView2 runtime is installed.";
            }
        }
    }

    private async void BookReaderView_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        BookReaderViewModel? viewModel = _viewModel;
        DetachViewModel();
        BookWebReader.CoreWebView2?.Stop();
        try
        {
            if (viewModel != null)
            {
                await viewModel.SaveCurrentProgressAsync();
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Log($"Book reader unload save failed: {ex.Message}", "WARNING");
        }
    }

    private void AttachViewModel(BookReaderViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            UpdateAllowedRoot(viewModel);
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateAllowedRoot(viewModel);
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = null;
        }
    }

    private async void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => ViewModel_PropertyChanged(sender, e));
            return;
        }

        if (!_isLoaded ||
            sender is not BookReaderViewModel viewModel ||
            !ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        try
        {
            if (e.PropertyName == nameof(BookReaderViewModel.Document))
            {
                UpdateAllowedRoot(viewModel);
            }

            if ((e.PropertyName == nameof(BookReaderViewModel.CurrentLocation) ||
                 e.PropertyName == nameof(BookReaderViewModel.Document)) &&
                !string.IsNullOrWhiteSpace(viewModel.CurrentLocation))
            {
                await NavigateAsync(viewModel.CurrentLocation);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Book reader navigation failed: {ex.Message}", "ERROR");
        }
    }

    private async Task NavigateAsync(string location)
    {
        if (!_isLoaded ||
            !Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) ||
            !uri.IsFile ||
            !IsAllowedLocalPath(uri.LocalPath))
        {
            AppLogger.Log($"Book reader refused unsafe navigation: '{location}'", "WARNING");
            return;
        }

        await BookWebReader.EnsureCoreWebView2Async();
        if (!_isLoaded)
        {
            return;
        }

        ConfigureCore(BookWebReader.CoreWebView2);
        BookWebReader.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.IsScriptEnabled = _viewModel?.IsPdf == true;
        if (ReferenceEquals(_configuredCore, core))
        {
            return;
        }

        _configuredCore = core;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
                !uri.IsFile ||
                !IsAllowedLocalPath(uri.LocalPath))
            {
                args.Cancel = true;
            }
        };
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
        };
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
        };
        core.AddWebResourceRequestedFilter(
            "http://*/*",
            CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter(
            "https://*/*",
            CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter(
            "file://*/*",
            CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? resourceUri) &&
                resourceUri.IsFile &&
                IsAllowedLocalPath(resourceUri.LocalPath))
            {
                return;
            }

            args.Response = core.Environment.CreateWebResourceResponse(
                null,
                403,
                "Blocked",
                string.Empty);
        };
    }

    private void UpdateAllowedRoot(BookReaderViewModel viewModel)
    {
        BookReaderDocument? document = viewModel.Document;
        _allowedRoot = string.Empty;
        _allowedFile = string.Empty;
        if (document == null)
        {
            return;
        }

        if (document.Format == BookFileFormat.Pdf &&
            Uri.TryCreate(document.StartLocation, UriKind.Absolute, out Uri? pdfUri) &&
            pdfUri.IsFile)
        {
            _allowedFile = Path.GetFullPath(pdfUri.LocalPath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(document.WorkingDirectory))
        {
            _allowedRoot = EnsureDirectorySeparator(
                Path.GetFullPath(document.WorkingDirectory));
        }
    }

    private bool IsAllowedLocalPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_allowedFile))
        {
            return fullPath.Equals(_allowedFile, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(_allowedRoot) &&
               fullPath.StartsWith(_allowedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

}
