namespace CimianStudio.Views;

using System.ComponentModel;
using System.Globalization;
using CimianStudio.Core.Models.Icons;
using CimianStudio.Core.Services;
using CimianStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

public sealed partial class IconsPage : Page
{
    private readonly IIconService _iconService;
    private CancellationTokenSource? _previewCts;

    public IconsViewModel ViewModel { get; }

    public IconsPage(IconsViewModel viewModel, IIconService iconService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(iconService);
        ViewModel = viewModel;
        _iconService = iconService;
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Formats a byte count for the list-row caption ("123 KB").</summary>
    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }
        double value = bytes;
        string[] units = ["KB", "MB", "GB"];
        var unit = 0;
        value /= 1024;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync().ConfigureAwait(true);
        RefreshHeader();
        RefreshDetail();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IconsViewModel.SelectedIcon):
                RefreshDetail();
                break;
            case nameof(IconsViewModel.Icons):
                RefreshHeader();
                break;
            case nameof(IconsViewModel.StatusMessage):
                if (!string.IsNullOrEmpty(ViewModel.StatusMessage))
                {
                    StatusBar.Title = ViewModel.StatusMessage;
                    // Reset severity — without this, an informational update
                    // after a previous error still renders with error styling.
                    StatusBar.Severity = InfoBarSeverity.Informational;
                    StatusBar.IsOpen = true;
                }
                break;
            case nameof(IconsViewModel.ErrorMessage):
                if (!string.IsNullOrEmpty(ViewModel.ErrorMessage))
                {
                    StatusBar.Title = ViewModel.ErrorMessage;
                    StatusBar.Severity = InfoBarSeverity.Error;
                    StatusBar.IsOpen = true;
                }
                break;
        }
    }

    private void RefreshHeader()
    {
        CountText.Text = string.Create(CultureInfo.InvariantCulture, $"({ViewModel.Icons.Count})");
    }

    private async void RefreshDetail()
    {
        // Cancel any in-flight read so a rapid selection change doesn't show the
        // wrong image as the previous load lands late.
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        var icon = ViewModel.SelectedIcon;
        if (icon is null)
        {
            PreviewImage.Source = null;
            PreviewProgress.IsActive = false;
            EmptyHint.Visibility = Visibility.Visible;
            DetailNameText.Text = string.Empty;
            DetailSizeText.Text = string.Empty;
            DetailHashText.Text = string.Empty;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        DetailNameText.Text = icon.Filename;
        DetailSizeText.Text = FormatByteSize(icon.ByteSize);
        DetailHashText.Text = icon.Sha256 is null
            ? "SHA-256: (not hashed — click Rebuild hashes)"
            : "SHA-256: " + icon.Sha256;

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        PreviewProgress.IsActive = true;
        PreviewImage.Source = null;

        try
        {
            var bytes = await _iconService.ReadBytesAsync(icon.Filename, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            var bitmap = new BitmapImage();
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                _ = await writer.StoreAsync().AsTask(cts.Token).ConfigureAwait(true);
                _ = writer.DetachStream();
            }
            ras.Seek(0);
            await bitmap.SetSourceAsync(ras).AsTask(cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                PreviewImage.Source = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // expected when selection changes mid-load
        }
        catch (Exception ex)
        {
            DetailHashText.Text = $"Preview failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                PreviewProgress.IsActive = false;
            }
        }
    }

    private async void OnRebuildClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RebuildHashesAsync().ConfigureAwait(true);
    }
}
