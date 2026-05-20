namespace CimianStudio.Settings;

using CimianStudio.Core.Models.Builds;
using CimianStudio.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

/// <summary>
/// Settings card for the Build tab: configures the projects folder + the
/// cimipkg.exe path, and surfaces the detected tool version. Runs the version
/// detector once on load and again after each path change. Browse buttons use
/// the standard WinUI 3 pickers, which require an owner window handle — sourced
/// from <see cref="App.MainWindowInstance"/>.
/// </summary>
public sealed class BuildSectionProvider : ISettingsSectionProvider
{
    public const string Id = "build";

    private readonly IBuildSettingsService _buildSettings;
    private readonly IBuildService _buildService;

    private TextBox? _folderBox;
    private TextBox? _toolBox;
    private TextBlock? _statusText;

    public BuildSectionProvider(IBuildSettingsService buildSettings, IBuildService buildService)
    {
        ArgumentNullException.ThrowIfNull(buildSettings);
        ArgumentNullException.ThrowIfNull(buildService);
        _buildSettings = buildSettings;
        _buildService = buildService;
    }

    public string SectionId => Id;
    public string DisplayName => "Build";
    public int Order => 20;

    public FrameworkElement BuildContent()
    {
        var panel = new StackPanel { Spacing = 14 };

        panel.Children.Add(BuildProjectsFolderGroup());
        panel.Children.Add(BuildToolGroup());

        _ = RefreshStatusAsync();
        return panel;
    }

    private StackPanel BuildProjectsFolderGroup()
    {
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock
        {
            Text = "Projects folder",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        group.Children.Add(new TextBlock
        {
            Text = "Root folder scanned for cimipkg projects. Each immediate subdirectory that contains a build-info.yaml file becomes one entry in the Build tab's project list.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _folderBox = new TextBox
        {
            PlaceholderText = "C:\\path\\to\\cimipkg-projects",
            Text = _buildSettings.ProjectsFolder ?? string.Empty,
        };
        Grid.SetColumn(_folderBox, 0);
        row.Children.Add(_folderBox);

        var browseBtn = new Button { Content = "Browse…" };
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync().ConfigureAwait(true);
            if (folder is not null)
            {
                _folderBox.Text = folder;
            }
        };
        row.Children.Add(browseBtn);

        group.Children.Add(row);

        var applyBtn = new Button
        {
            Content = "Apply",
            Margin = new Thickness(0, 4, 0, 0),
        };
        applyBtn.Click += async (_, _) =>
        {
            var path = _folderBox.Text?.Trim();
            await _buildSettings.SetProjectsFolderAsync(string.IsNullOrEmpty(path) ? null : path).ConfigureAwait(true);
        };
        group.Children.Add(applyBtn);

        return group;
    }

    private StackPanel BuildToolGroup()
    {
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock
        {
            Text = "Builder tool",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 8, 0, 0),
        });
        group.Children.Add(new TextBlock
        {
            Text = "Executable path for cimipkg.exe. Leave blank to auto-discover (sibling release directory → PATH → C:\\Program Files\\Cimian).",
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
            Text = _buildSettings.CimipkgPath ?? string.Empty,
        };
        Grid.SetColumn(_toolBox, 0);
        row.Children.Add(_toolBox);

        var browseBtn = new Button { Content = "Browse…" };
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Click += async (_, _) =>
        {
            var path = await PickExecutableAsync().ConfigureAwait(true);
            if (path is not null) _toolBox.Text = path;
        };
        row.Children.Add(browseBtn);

        group.Children.Add(row);

        var applyBtn = new Button
        {
            Content = "Apply",
            Margin = new Thickness(0, 4, 0, 0),
        };
        applyBtn.Click += async (_, _) =>
        {
            var path = _toolBox.Text?.Trim();
            await _buildSettings.SetCimipkgPathAsync(string.IsNullOrEmpty(path) ? null : path).ConfigureAwait(true);
            await RefreshStatusAsync().ConfigureAwait(true);
        };
        group.Children.Add(applyBtn);

        _statusText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Detecting…",
        };
        group.Children.Add(_statusText);

        var downloadLink = new HyperlinkButton
        {
            // The "Download & Install" workflow in the brief is unwound here:
            // open the Cimian releases page so the user can grab the MSI.
            // Authenticated GitHub download + UAC-elevated install is a future
            // enhancement (see docs/import-tab.md §8).
            Content = "Open Cimian releases page",
            NavigateUri = new Uri("https://github.com/windowsadmins/cimian/releases"),
            Padding = new Thickness(0),
        };
        group.Children.Add(downloadLink);

        return group;
    }

    private async Task RefreshStatusAsync()
    {
        if (_statusText is null) return;
        var info = await _buildService.GetToolInfoAsync().ConfigureAwait(true);
        if (info.Found)
        {
            var version = string.IsNullOrEmpty(info.Version) ? "(version unknown)" : $"v{info.Version}";
            _statusText.Text = $"Detected cimipkg {version} at {info.Path}";
            _statusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else
        {
            _statusText.Text = info.Detail ?? "cimipkg.exe not found.";
            _statusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        if (App.MainWindowInstance is not { } window) return null;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static async Task<string?> PickExecutableAsync()
    {
        if (App.MainWindowInstance is not { } window) return null;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
