namespace CimianStudio.Settings;

using CimianStudio.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

/// <summary>
/// Settings card for the Import tab: detects <c>cimiimport.exe</c>, lets the
/// admin override its path, and links to the Cimian releases page when the
/// tool isn't installed. Same shape as <see cref="BuildSectionProvider"/>.
/// </summary>
public sealed class CimiimportSectionProvider : ISettingsSectionProvider
{
    public const string Id = "import";

    private readonly IImportSettingsService _importSettings;
    private readonly ICimiimportService _cimiimportService;

    private TextBox? _toolBox;
    private TextBlock? _statusText;

    public CimiimportSectionProvider(IImportSettingsService importSettings, ICimiimportService cimiimportService)
    {
        ArgumentNullException.ThrowIfNull(importSettings);
        ArgumentNullException.ThrowIfNull(cimiimportService);
        _importSettings = importSettings;
        _cimiimportService = cimiimportService;
    }

    public string SectionId => Id;
    public string DisplayName => "Import";
    public int Order => 30;

    public FrameworkElement BuildContent()
    {
        var panel = new StackPanel { Spacing = 6 };

        panel.Children.Add(new TextBlock
        {
            Text = "cimiimport.exe",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "CimianStudio shells out to cimiimport.exe for every installer import. Leave the path blank to auto-discover (C:\\Program Files\\Cimian → PATH).",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _toolBox = new TextBox
        {
            PlaceholderText = "(auto-discover)",
            Text = _importSettings.CimiimportPath ?? string.Empty,
        };
        Grid.SetColumn(_toolBox, 0);
        row.Children.Add(_toolBox);

        var browseBtn = new Button { Content = "Browse…" };
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Click += async (_, _) =>
        {
            if (App.MainWindowInstance is not { } window) return;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) _toolBox.Text = file.Path;
        };
        row.Children.Add(browseBtn);

        panel.Children.Add(row);

        var applyBtn = new Button
        {
            Content = "Apply",
            Margin = new Thickness(0, 4, 0, 0),
        };
        applyBtn.Click += async (_, _) =>
        {
            var path = _toolBox.Text?.Trim();
            await _importSettings.SetCimiimportPathAsync(string.IsNullOrEmpty(path) ? null : path).ConfigureAwait(true);
            await RefreshStatusAsync().ConfigureAwait(true);
        };
        panel.Children.Add(applyBtn);

        _statusText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Detecting…",
        };
        panel.Children.Add(_statusText);

        panel.Children.Add(new HyperlinkButton
        {
            // Mirrors BuildSectionProvider's link: opens the release page so
            // admins can grab the Cimian MSI when cimiimport.exe is missing.
            // A UAC-elevated downloader is a future enhancement.
            Content = "Open Cimian releases page",
            NavigateUri = new Uri("https://github.com/windowsadmins/cimian/releases"),
            Padding = new Thickness(0),
        });

        _ = RefreshStatusAsync();
        return panel;
    }

    private async Task RefreshStatusAsync()
    {
        if (_statusText is null) return;
        var info = await _cimiimportService.GetToolInfoAsync().ConfigureAwait(true);
        if (info.Found)
        {
            var version = string.IsNullOrEmpty(info.Version) ? "(version unknown)" : $"v{info.Version}";
            _statusText.Text = $"Detected cimiimport {version} at {info.Path}";
            _statusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else
        {
            _statusText.Text = info.Detail ?? "cimiimport.exe not found.";
            _statusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
    }
}
