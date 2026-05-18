namespace CimianStudio.Views.Build;

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CimianStudio.Core.Models.Builds;
using CimianStudio.Core.Services;
using CimianStudio.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

/// <summary>
/// Hosts the Build tab. Drives project discovery via <see cref="BuildViewModel"/>,
/// builds the four-column Build-Info form in code-behind, populates the payload
/// tree + scripts column for the selected project, and streams live cimipkg
/// output into the console. Auto-save (~700ms debounce) keeps build-info /
/// scripts in sync with disk without a Save button.
/// </summary>
// CA1001: the watcher + cts fields are disposed in the Unloaded handler, which
// fires when WinUI tears the page down — that's the WinUI-idiomatic lifecycle
// hook, not IDisposable. Adding IDisposable on a Page would conflict with the
// framework's own disposal flow.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposal happens in Unloaded — Pages don't implement IDisposable.")]
public sealed partial class BuildPage : Page
{
    private readonly IBuildService _buildService;
    private readonly IBuildSettingsService _buildSettings;

    public BuildViewModel ViewModel { get; }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveDebounceTimer;
    private CancellationTokenSource? _buildCts;

    private FileSystemWatcher? _projectsFolderWatcher;
    private FileSystemWatcher? _payloadWatcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watcherDebounceTimer;

    // Build-info field controls, keyed by a stable identifier so we can pull
    // their values back into BuildInfoData on save without hard-coding each
    // field in the save method.
    private readonly Dictionary<string, FrameworkElement> _buildInfoControls = [];
    private bool _suppressFieldEvents;

    // Maps each script slot file-name to its in-line editor; we read the
    // TextBox.Text back on save and let the service handle deletion when empty.
    private readonly Dictionary<string, TextBox> _scriptEditors = new(StringComparer.OrdinalIgnoreCase);

    public BuildPage(BuildViewModel viewModel, IBuildService buildService, IBuildSettingsService buildSettings)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(buildSettings);
        ViewModel = viewModel;
        _buildService = buildService;
        _buildSettings = buildSettings;
        InitializeComponent();

        _saveDebounceTimer = DispatcherQueue.CreateTimer();
        _saveDebounceTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveDebounceTimer.IsRepeating = false;
        _saveDebounceTimer.Tick += async (_, _) => await FlushAutoSaveAsync().ConfigureAwait(true);

        _watcherDebounceTimer = DispatcherQueue.CreateTimer();
        _watcherDebounceTimer.Interval = TimeSpan.FromMilliseconds(400);
        _watcherDebounceTimer.IsRepeating = false;

        ProjectListView.ItemsSource = ViewModel.FilteredProjects;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ----- Page lifecycle ----------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _buildSettings.ProjectsFolderChanged -= OnProjectsFolderChanged;
        _buildSettings.ProjectsFolderChanged += OnProjectsFolderChanged;
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _buildSettings.ProjectsFolderChanged -= OnProjectsFolderChanged;
        await FlushAutoSaveAsync().ConfigureAwait(true);
        DisposeWatchers();
    }

    private async void OnProjectsFolderChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(async () => await RefreshAllAsync().ConfigureAwait(true));
    }

    private async Task RefreshAllAsync()
    {
        // HasProjectsFolderAsync warms the settings cache on first call so the
        // ProjectsFolder accessor returns the persisted value rather than null.
        _ = await _buildSettings.HasProjectsFolderAsync().ConfigureAwait(true);
        var folder = _buildSettings.ProjectsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            NoProjectsFolderPanel.Visibility = Visibility.Visible;
            BuildInfoBorder.Visibility = Visibility.Collapsed;
            PayloadScriptsGrid.Visibility = Visibility.Collapsed;
            ProjectCountText.Text = string.Empty;
            ViewModel.FilteredProjects.Clear();
            DisposeProjectsFolderWatcher();
            return;
        }

        NoProjectsFolderPanel.Visibility = Visibility.Collapsed;
        await ViewModel.RefreshProjectsAsync().ConfigureAwait(true);
        ProjectCountText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{ViewModel.AllProjects.Count} project{(ViewModel.AllProjects.Count == 1 ? string.Empty : "s")}");
        SetupProjectsFolderWatcher(folder);
    }

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateTo("settings");

    // ----- Project list ------------------------------------------------------

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.FilterText = FilterBox.Text;
    }

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Flush any pending auto-save against the *outgoing* project before we
        // swap models — otherwise typed-but-not-yet-saved edits go to disk
        // against the wrong project. See "Auto-save" in docs/import-tab.md §5.
        await FlushAutoSaveAsync().ConfigureAwait(true);

        ViewModel.SelectedProject = ProjectListView.SelectedItem as BuildProject;
        if (ViewModel.SelectedProject is null)
        {
            ProjectNameText.Text = "No project selected";
            BuildInfoBorder.Visibility = Visibility.Collapsed;
            PayloadScriptsGrid.Visibility = Visibility.Collapsed;
            BuildButton.IsEnabled = false;
            OptionsButton.IsEnabled = false;
            DisposePayloadWatcher();
            return;
        }

        ProjectNameText.Text = ViewModel.SelectedProject.Name;
        BuildButton.IsEnabled = true;
        OptionsButton.IsEnabled = true;
        RevealProductButton.Visibility = Visibility.Collapsed;
        ImportProductButton.Visibility = Visibility.Collapsed;
        ActionStatusText.Text = string.Empty;

        await ViewModel.LoadBuildInfoForSelectedAsync().ConfigureAwait(true);
        PopulateBuildInfoForm();
        PopulatePayloadTree();
        PopulateScriptsPanel();
        SetupPayloadWatcher();

        BuildInfoBorder.Visibility = Visibility.Visible;
        PayloadScriptsGrid.Visibility = Visibility.Visible;
    }

    // ----- Project context menu ---------------------------------------------

    private async void OnContextBuildClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BuildProject project)
        {
            ProjectListView.SelectedItem = project;
            // Selection-changed handler is async; give it a beat before clicking Build.
            await Task.Yield();
            OnBuildClicked(sender, e);
        }
    }

    private void OnContextRevealClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BuildProject project)
        {
            RevealInExplorer(project.DirectoryPath);
        }
    }

    private async void OnContextRenameClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BuildProject project) return;
        var newName = await PromptForTextAsync("Rename project", "New folder name:", project.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, project.Name, StringComparison.Ordinal)) return;

        try
        {
            var parent = Path.GetDirectoryName(project.DirectoryPath);
            if (string.IsNullOrEmpty(parent)) return;
            var target = Path.Combine(parent, newName);
            if (Directory.Exists(target))
            {
                await ShowDialogAsync("Rename failed", $"A folder named '{newName}' already exists.").ConfigureAwait(true);
                return;
            }
            Directory.Move(project.DirectoryPath, target);
            await RefreshAllAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Rename failed", ex.Message).ConfigureAwait(true);
        }
    }

    private async void OnContextDuplicateClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BuildProject project) return;
        var parent = Path.GetDirectoryName(project.DirectoryPath);
        if (string.IsNullOrEmpty(parent)) return;

        // Find an unused "-copy" suffix without prompting — keeps the action
        // single-click. The user can rename afterwards if they want a specific name.
        var candidate = project.Name + "-copy";
        var target = Path.Combine(parent, candidate);
        var n = 2;
        while (Directory.Exists(target))
        {
            candidate = $"{project.Name}-copy{n++}";
            target = Path.Combine(parent, candidate);
        }

        try
        {
            CopyDirectory(project.DirectoryPath, target);
            await RefreshAllAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Duplicate failed", ex.Message).ConfigureAwait(true);
        }
    }

    private async void OnContextDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BuildProject project) return;
        var confirm = new ContentDialog
        {
            Title = "Delete project",
            Content = $"Permanently delete '{project.Name}'? This removes the entire project folder.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            Directory.Delete(project.DirectoryPath, recursive: true);
            await RefreshAllAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Delete failed", ex.Message).ConfigureAwait(true);
        }
    }

    // ----- Build orchestration ----------------------------------------------

    private async void OnBuildClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null || ViewModel.IsBuilding) return;
        await FlushAutoSaveAsync().ConfigureAwait(true);

        ViewModel.IsBuilding = true;
        BuildButton.IsEnabled = false;
        OptionsButton.IsEnabled = false;
        RevealProductButton.Visibility = Visibility.Collapsed;
        ImportProductButton.Visibility = Visibility.Collapsed;
        ActionStatusText.Text = "Building…";
        ConsoleBorder.Visibility = Visibility.Visible;
        ConsoleText.Text = string.Empty;

        var sb = new StringBuilder();
        _buildCts = new CancellationTokenSource();

        try
        {
            await foreach (var evt in _buildService.StreamBuildAsync(ViewModel.SelectedProject, ViewModel.Options, _buildCts.Token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case BuildLine line:
                        sb.AppendLine(line.Text);
                        ConsoleText.Text = sb.ToString();
                        ConsoleScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
                        break;
                    case BuildFinished done:
                        ViewModel.LastBuiltProductPath = done.ProductPath;
                        if (done.ExitCode == 0 && !string.IsNullOrEmpty(done.ProductPath))
                        {
                            ActionStatusText.Text = $"Build succeeded — {Path.GetFileName(done.ProductPath)}";
                            RevealProductButton.Visibility = Visibility.Visible;
                            ImportProductButton.Visibility = Visibility.Visible;
                        }
                        else if (done.ExitCode == 0)
                        {
                            ActionStatusText.Text = "Build succeeded (output package not found in build/).";
                        }
                        else
                        {
                            ActionStatusText.Text = $"Build failed (exit {done.ExitCode}).";
                        }
                        break;
                    case BuildFailed fail:
                        ActionStatusText.Text = fail.Reason;
                        break;
                }
            }
        }
        finally
        {
            _buildCts.Dispose();
            _buildCts = null;
            ViewModel.IsBuilding = false;
            BuildButton.IsEnabled = true;
            OptionsButton.IsEnabled = true;
        }
    }

    private void OnClearConsoleClicked(object sender, RoutedEventArgs e)
    {
        ConsoleText.Text = string.Empty;
    }

    private void OnRevealProductClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.LastBuiltProductPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ViewModel.LastBuiltProductPath}\"") { UseShellExecute = true });
    }

    private async void OnImportProductClicked(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.LastBuiltProductPath;
        if (string.IsNullOrEmpty(path) || App.MainWindowInstance is not { } window) return;

        // Hand the freshly-built artefact to the Import flow — same code path
        // the Packages-drop and standalone drop-zone use.
        window.NavigateTo("import");
        var importPage = App.Resolve<Import.ImportPage>();
        await importPage.HandleExternalFilesAsync(new[] { path }).ConfigureAwait(true);
    }

    // ----- Options popover --------------------------------------------------

    private void OnOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement origin) return;
        var flyout = BuildOptionsFlyout();
        flyout.ShowAt(origin);
    }

    private Flyout BuildOptionsFlyout()
    {
        var options = ViewModel.Options;
        var panel = new StackPanel { Spacing = 10, Width = 360 };

        panel.Children.Add(new TextBlock
        {
            Text = "Build options",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Options persist as you switch projects. Each maps directly to a cimipkg CLI flag.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(BuildOptionCheckBox(
            "Build .nupkg (Chocolatey-compatible)",
            "--nupkg — builds a NuGet package instead of an MSI.",
            options.BuildNupkg,
            v => options.BuildNupkg = v));
        panel.Children.Add(BuildOptionCheckBox(
            "Also build .intunewin",
            "--intunewin — additionally wraps the artefact for Intune deployment.",
            options.BuildIntunewin,
            v => options.BuildIntunewin = v));
        panel.Children.Add(BuildOptionCheckBox(
            "Skip post-build import prompt",
            "--skip-import — keeps the build non-interactive (recommended for GUI use).",
            options.SkipImport,
            v => options.SkipImport = v));
        panel.Children.Add(BuildOptionCheckBox(
            "Verbose output",
            "--verbose — debug-level logging.",
            options.Verbose,
            v => options.Verbose = v));

        panel.Children.Add(BuildOptionTextWithBrowse(
            ".env file path",
            "--env — overrides the default .env discovery.",
            options.EnvFilePath ?? string.Empty,
            v => options.EnvFilePath = string.IsNullOrWhiteSpace(v) ? null : v,
            pickFile: true));
        panel.Children.Add(BuildOptionTextWithBrowse(
            "Signing certificate (subject)",
            "--sign-cert — overrides build-info.yaml's signing_certificate.",
            options.SigningCertificate ?? string.Empty,
            v => options.SigningCertificate = string.IsNullOrWhiteSpace(v) ? null : v,
            pickFile: false));
        panel.Children.Add(BuildOptionTextWithBrowse(
            "Signing thumbprint",
            "--sign-thumbprint — overrides build-info.yaml's signing_thumbprint.",
            options.SigningThumbprint ?? string.Empty,
            v => options.SigningThumbprint = string.IsNullOrWhiteSpace(v) ? null : v,
            pickFile: false));

        return new Flyout { Content = panel };
    }

    private static StackPanel BuildOptionCheckBox(string label, string description, bool initial, Action<bool> setter)
    {
        var panel = new StackPanel { Spacing = 2 };
        var check = new CheckBox { Content = label, IsChecked = initial };
        check.Checked += (_, _) => setter(true);
        check.Unchecked += (_, _) => setter(false);
        panel.Children.Add(check);
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Margin = new Thickness(28, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static StackPanel BuildOptionTextWithBrowse(string label, string description, string initial, Action<string> setter, bool pickFile)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (pickFile) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox { Text = initial };
        box.TextChanged += (_, _) => setter(box.Text);
        Grid.SetColumn(box, 0);
        row.Children.Add(box);

        if (pickFile)
        {
            var btn = new Button { Content = "Browse…" };
            btn.Click += async (_, _) =>
            {
                if (App.MainWindowInstance is not { } window) return;
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
                var file = await picker.PickSingleFileAsync();
                if (file is not null) box.Text = file.Path;
            };
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);
        }

        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    // ----- Build Info form --------------------------------------------------

    private void PopulateBuildInfoForm()
    {
        ColPackage.Children.Clear();
        ColOptions.Children.Clear();
        ColSigning.Children.Clear();
        ColMsi.Children.Clear();
        _buildInfoControls.Clear();

        if (ViewModel.CurrentBuildInfo is not { } info) return;

        _suppressFieldEvents = true;
        try
        {
            ColPackage.Children.Add(SectionHeader("Package"));
            AddText(ColPackage, "name", info.Product.Name, v => info.Product.Name = v);
            AddText(ColPackage, "version", info.Product.Version, v => info.Product.Version = v);
            AddText(ColPackage, "identifier", info.Product.Identifier, v => info.Product.Identifier = v);
            AddText(ColPackage, "developer", info.Product.Developer ?? string.Empty, v => info.Product.Developer = NullIfEmpty(v));
            AddMultiline(ColPackage, "description", info.Product.Description ?? string.Empty, v => info.Product.Description = NullIfEmpty(v));
            AddText(ColPackage, "installer_type", info.Product.InstallerType ?? string.Empty, v => info.Product.InstallerType = NullIfEmpty(v));
            AddText(ColPackage, "url", info.Product.Url ?? string.Empty, v => info.Product.Url = NullIfEmpty(v));
            AddText(ColPackage, "copyright", info.Product.Copyright ?? string.Empty, v => info.Product.Copyright = NullIfEmpty(v));
            AddText(ColPackage, "license", info.Product.License ?? string.Empty, v => info.Product.License = NullIfEmpty(v));
            AddText(ColPackage, "category", info.Category ?? string.Empty, v => info.Category = NullIfEmpty(v));
            AddText(ColPackage, "icon", info.Icon ?? string.Empty, v => info.Icon = NullIfEmpty(v));

            ColOptions.Children.Add(SectionHeader("Install / Options"));
            AddText(ColOptions, "install_location", info.InstallLocation ?? string.Empty, v => info.InstallLocation = NullIfEmpty(v));
            AddText(ColOptions, "install_arguments", info.InstallArguments ?? string.Empty, v => info.InstallArguments = NullIfEmpty(v));
            AddText(ColOptions, "valid_exit_codes", info.ValidExitCodes ?? string.Empty, v => info.ValidExitCodes = NullIfEmpty(v));
            AddText(ColOptions, "uninstall_arguments", info.UninstallArguments ?? string.Empty, v => info.UninstallArguments = NullIfEmpty(v));
            AddText(ColOptions, "software_detection", info.SoftwareDetection ?? string.Empty, v => info.SoftwareDetection = NullIfEmpty(v));
            AddText(ColOptions, "postinstall_action", info.PostinstallAction ?? string.Empty, v => info.PostinstallAction = NullIfEmpty(v));
            AddText(ColOptions, "minimum_os_version", info.MinimumOsVersion ?? string.Empty, v => info.MinimumOsVersion = NullIfEmpty(v));
            AddMultiline(ColOptions, "blocking_applications", string.Join('\n', info.BlockingApplications ?? []), v =>
                info.BlockingApplications = string.IsNullOrWhiteSpace(v)
                    ? null
                    : [.. v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

            ColSigning.Children.Add(SectionHeader("Code signing"));
            AddText(ColSigning, "signing_certificate", info.SigningCertificate ?? string.Empty, v => info.SigningCertificate = NullIfEmpty(v));
            AddText(ColSigning, "signing_thumbprint", info.SigningThumbprint ?? string.Empty, v => info.SigningThumbprint = NullIfEmpty(v));

            ColMsi.Children.Add(SectionHeader("MSI"));
            AddText(ColMsi, "upgrade_code", info.UpgradeCode ?? string.Empty, v => info.UpgradeCode = NullIfEmpty(v));
            AddCheck(ColMsi, "override_uninstall_script", info.OverrideUninstallScript, v => info.OverrideUninstallScript = v);
            AddMultiline(ColMsi, "msi_properties (key=value per line)",
                FormatKeyValues(info.MsiProperties),
                v => info.MsiProperties = ParseKeyValues(v));
        }
        finally
        {
            _suppressFieldEvents = false;
        }
    }

    private static TextBlock SectionHeader(string text) => new TextBlock
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
    };

    private void AddText(StackPanel column, string field, string initial, Action<string> setter)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = field,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var box = new TextBox { Text = initial };
        box.TextChanged += (_, _) =>
        {
            if (_suppressFieldEvents) return;
            setter(box.Text);
            QueueAutoSave();
        };
        panel.Children.Add(box);
        column.Children.Add(panel);
        _buildInfoControls[field] = box;
    }

    private void AddMultiline(StackPanel column, string field, string initial, Action<string> setter)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = field,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
        };
        box.TextChanged += (_, _) =>
        {
            if (_suppressFieldEvents) return;
            setter(box.Text);
            QueueAutoSave();
        };
        panel.Children.Add(box);
        column.Children.Add(panel);
        _buildInfoControls[field] = box;
    }

    private void AddCheck(StackPanel column, string field, bool initial, Action<bool> setter)
    {
        var check = new CheckBox { Content = field, IsChecked = initial };
        check.Checked += (_, _) =>
        {
            if (_suppressFieldEvents) return;
            setter(true);
            QueueAutoSave();
        };
        check.Unchecked += (_, _) =>
        {
            if (_suppressFieldEvents) return;
            setter(false);
            QueueAutoSave();
        };
        column.Children.Add(check);
        _buildInfoControls[field] = check;
    }

    private static string FormatKeyValues(Dictionary<string, string>? dict)
        => dict is null ? string.Empty : string.Join('\n', dict.Select(kv => $"{kv.Key}={kv.Value}"));

    private static Dictionary<string, string>? ParseKeyValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (!string.IsNullOrEmpty(key)) dict[key] = value;
        }
        return dict.Count == 0 ? null : dict;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----- Payload tree -----------------------------------------------------

    private void PopulatePayloadTree()
    {
        PayloadTree.RootNodes.Clear();
        if (ViewModel.SelectedProject is not { } project) return;
        var payload = project.PayloadPath;
        if (!Directory.Exists(payload)) return;

        foreach (var node in BuildNodesForDirectory(payload))
        {
            PayloadTree.RootNodes.Add(node);
        }
    }

    private static IEnumerable<TreeViewNode> BuildNodesForDirectory(string directory)
    {
        // Top-level folders first, then files — matches the order File Explorer uses.
        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            yield return BuildDirectoryNode(dir);
        }
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            yield return BuildFileNode(file);
        }
    }

    private static TreeViewNode BuildDirectoryNode(string directory)
    {
        var node = new TreeViewNode
        {
            Content = new PayloadTreeNode(directory, Path.GetFileName(directory), IsDirectory: true),
            IsExpanded = true,
        };
        foreach (var child in BuildNodesForDirectory(directory))
        {
            node.Children.Add(child);
        }
        return node;
    }

    private static TreeViewNode BuildFileNode(string file)
        => new()
        {
            Content = new PayloadTreeNode(file, Path.GetFileName(file), IsDirectory: false),
        };

    private static PayloadTreeNode? GetNodePayload(TreeViewNode? node) => node?.Content as PayloadTreeNode;

    private string? SelectedPayloadPath()
        => GetNodePayload(PayloadTree.SelectedNode)?.FullPath;

    private string PayloadTargetDir()
    {
        // Default insertion point: the selected folder, or the selected file's
        // parent, or the project payload root if nothing is selected.
        if (ViewModel.SelectedProject is not { } project) return string.Empty;
        var selected = GetNodePayload(PayloadTree.SelectedNode);
        if (selected is null) return project.PayloadPath;
        return selected.IsDirectory ? selected.FullPath : Path.GetDirectoryName(selected.FullPath) ?? project.PayloadPath;
    }

    private async void OnPayloadNewFolderClicked(object sender, RoutedEventArgs e)
        => await CreateFolderInTargetAsync(PayloadTargetDir()).ConfigureAwait(true);

    private async void OnPayloadAddFilesClicked(object sender, RoutedEventArgs e)
        => await AddFilesInTargetAsync(PayloadTargetDir()).ConfigureAwait(true);

    private async void OnPayloadDeleteClicked(object sender, RoutedEventArgs e)
        => await DeletePathAsync(SelectedPayloadPath()).ConfigureAwait(true);

    private void OnPayloadRevealClicked(object sender, RoutedEventArgs e)
    {
        var target = SelectedPayloadPath() ?? ViewModel.SelectedProject?.PayloadPath;
        if (!string.IsNullOrEmpty(target)) RevealInExplorer(target);
    }

    private void OnPayloadExpandToggleClicked(object sender, RoutedEventArgs e)
    {
        // Toggle: if any root is collapsed, expand all; otherwise collapse all.
        var anyCollapsed = false;
        WalkNodes(PayloadTree.RootNodes, n => { if (!n.IsExpanded) anyCollapsed = true; });
        WalkNodes(PayloadTree.RootNodes, n => n.IsExpanded = anyCollapsed);
    }

    private static void WalkNodes(IList<TreeViewNode> nodes, Action<TreeViewNode> visit)
    {
        foreach (var n in nodes)
        {
            visit(n);
            WalkNodes(n.Children, visit);
        }
    }

    private void OnPayloadDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to payload";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnPayloadDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var target = PayloadTargetDir();
        if (string.IsNullOrEmpty(target)) return;
        Directory.CreateDirectory(target);

        foreach (var item in items)
        {
            try
            {
                switch (item)
                {
                    case Windows.Storage.StorageFile file:
                        File.Copy(file.Path, Path.Combine(target, file.Name), overwrite: true);
                        break;
                    case Windows.Storage.StorageFolder folder:
                        CopyDirectory(folder.Path, Path.Combine(target, folder.Name));
                        break;
                }
            }
            catch (IOException ex)
            {
                await ShowDialogAsync("Drop failed", ex.Message).ConfigureAwait(true);
            }
        }
        PopulatePayloadTree();
    }

    private async void OnPayloadDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var node = GetNodePayload(PayloadTree.SelectedNode);
        if (node is null) return;
        if (node.IsDirectory)
        {
            if (PayloadTree.SelectedNode is { } tvn) tvn.IsExpanded = !tvn.IsExpanded;
            return;
        }
        if (LooksLikeText(node.FullPath))
        {
            await EditFileInDialogAsync(node.FullPath).ConfigureAwait(true);
        }
        else
        {
            await OpenWithShellAsync(node.FullPath).ConfigureAwait(true);
        }
    }

    private void OnPayloadRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // WinUI 3 TreeView doesn't fire SelectionChanged on right-click, so we
        // do it manually here by walking up to the TreeViewItem and selecting
        // its node before showing the flyout.
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        if (element is TreeViewItem item && PayloadTree.NodeFromContainer(item) is { } node)
        {
            PayloadTree.SelectedNode = node;
            var flyout = BuildPayloadNodeFlyout(node);
            flyout.ShowAt(item, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(item),
            });
            e.Handled = true;
        }
    }

    private MenuFlyout BuildPayloadNodeFlyout(TreeViewNode node)
    {
        var payload = GetNodePayload(node);
        var flyout = new MenuFlyout();
        if (payload?.IsDirectory == true)
        {
            var newFolder = new MenuFlyoutItem { Text = "New Folder" };
            newFolder.Click += async (_, _) => await CreateFolderInTargetAsync(payload.FullPath).ConfigureAwait(true);
            flyout.Items.Add(newFolder);
            var addFiles = new MenuFlyoutItem { Text = "Add Files…" };
            addFiles.Click += async (_, _) => await AddFilesInTargetAsync(payload.FullPath).ConfigureAwait(true);
            flyout.Items.Add(addFiles);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        if (payload?.IsDirectory == false)
        {
            if (LooksLikeText(payload.FullPath))
            {
                var edit = new MenuFlyoutItem { Text = "Edit" };
                edit.Click += async (_, _) => await EditFileInDialogAsync(payload.FullPath).ConfigureAwait(true);
                flyout.Items.Add(edit);
            }
            var open = new MenuFlyoutItem { Text = "Open" };
            open.Click += async (_, _) => await OpenWithShellAsync(payload.FullPath).ConfigureAwait(true);
            flyout.Items.Add(open);
        }
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += async (_, _) => await RenamePathAsync(payload?.FullPath).ConfigureAwait(true);
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var reveal = new MenuFlyoutItem { Text = "Reveal in File Explorer" };
        reveal.Click += (_, _) => { if (!string.IsNullOrEmpty(payload?.FullPath)) RevealInExplorer(payload.FullPath); };
        flyout.Items.Add(reveal);
        var del = new MenuFlyoutItem { Text = "Delete…" };
        del.Click += async (_, _) => await DeletePathAsync(payload?.FullPath).ConfigureAwait(true);
        flyout.Items.Add(del);
        return flyout;
    }

    // Stubs so the original XAML click handlers compile — they redirect to the
    // dynamic flyout actions above. Kept in case XAML re-adds the static menu.
    private async void OnNodeNewFolderClicked(object sender, RoutedEventArgs e)
        => await CreateFolderInTargetAsync(PayloadTargetDir()).ConfigureAwait(true);
    private async void OnNodeAddFilesClicked(object sender, RoutedEventArgs e)
        => await AddFilesInTargetAsync(PayloadTargetDir()).ConfigureAwait(true);
    private async void OnNodeEditClicked(object sender, RoutedEventArgs e)
    {
        var p = SelectedPayloadPath();
        if (!string.IsNullOrEmpty(p)) await EditFileInDialogAsync(p).ConfigureAwait(true);
    }
    private async void OnNodeOpenClicked(object sender, RoutedEventArgs e)
    {
        var p = SelectedPayloadPath();
        if (!string.IsNullOrEmpty(p)) await OpenWithShellAsync(p).ConfigureAwait(true);
    }
    private async void OnNodeRenameClicked(object sender, RoutedEventArgs e)
        => await RenamePathAsync(SelectedPayloadPath()).ConfigureAwait(true);
    private void OnNodeRevealClicked(object sender, RoutedEventArgs e)
    {
        var p = SelectedPayloadPath();
        if (!string.IsNullOrEmpty(p)) RevealInExplorer(p);
    }
    private async void OnNodeDeleteClicked(object sender, RoutedEventArgs e)
        => await DeletePathAsync(SelectedPayloadPath()).ConfigureAwait(true);

    private async Task CreateFolderInTargetAsync(string targetDir)
    {
        if (string.IsNullOrEmpty(targetDir)) return;
        var name = await PromptForTextAsync("New folder", "Folder name:", "New Folder").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(targetDir, name));
            PopulatePayloadTree();
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Create folder failed", ex.Message).ConfigureAwait(true);
        }
    }

    private async Task AddFilesInTargetAsync(string targetDir)
    {
        if (App.MainWindowInstance is not { } window || string.IsNullOrEmpty(targetDir)) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        Directory.CreateDirectory(targetDir);
        foreach (var file in files)
        {
            try { File.Copy(file.Path, Path.Combine(targetDir, file.Name), overwrite: true); }
            catch (IOException ex) { await ShowDialogAsync("Copy failed", ex.Message).ConfigureAwait(true); }
        }
        PopulatePayloadTree();
    }

    private async Task DeletePathAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) return;
        var confirm = new ContentDialog
        {
            Title = "Delete",
            Content = $"Permanently delete '{Path.GetFileName(path)}'?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            PopulatePayloadTree();
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Delete failed", ex.Message).ConfigureAwait(true);
        }
    }

    private async Task RenamePathAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var current = Path.GetFileName(path);
        var newName = await PromptForTextAsync("Rename", "New name:", current).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, current, StringComparison.Ordinal)) return;
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return;
            var target = Path.Combine(parent, newName);
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            PopulatePayloadTree();
        }
        catch (IOException ex)
        {
            await ShowDialogAsync("Rename failed", ex.Message).ConfigureAwait(true);
        }
    }

    private async Task EditFileInDialogAsync(string path)
    {
        string content;
        try { content = await File.ReadAllTextAsync(path).ConfigureAwait(true); }
        catch (IOException ex) { await ShowDialogAsync("Open failed", ex.Message).ConfigureAwait(true); return; }

        var box = new TextBox
        {
            Text = content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            MinHeight = 480,
            MinWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = Path.GetFileName(path),
            Content = new ScrollViewer { Content = box, MinWidth = 720, MinHeight = 480 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try { await File.WriteAllTextAsync(path, box.Text).ConfigureAwait(true); }
            catch (IOException ex) { await ShowDialogAsync("Save failed", ex.Message).ConfigureAwait(true); }
        }
    }

    private async Task OpenWithShellAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Open failed", ex.Message).ConfigureAwait(true);
        }
    }

    // ----- Scripts panel ----------------------------------------------------

    private void PopulateScriptsPanel()
    {
        ScriptsPanel.Children.Clear();
        _scriptEditors.Clear();

        if (ViewModel.SelectedProject is not { } project) return;
        var slots = _buildService.EnumerateScriptSlots(project);
        foreach (var slot in slots)
        {
            ScriptsPanel.Children.Add(BuildScriptSlot(project, slot));
        }
    }

    private StackPanel BuildScriptSlot(BuildProject project, string slotName)
    {
        var panel = new StackPanel { Spacing = 4 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = slotName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var expand = new Button { Content = "Expand" };
        Grid.SetColumn(expand, 1);
        header.Children.Add(expand);
        panel.Children.Add(header);

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MinHeight = 140,
            MaxHeight = 220,
        };

        // Async load on the dispatcher — preserves the order of slots in the
        // panel and avoids a synchronous file read during page activation.
        _ = Task.Run(async () =>
        {
            var content = await _buildService.ReadScriptAsync(project, slotName).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() =>
            {
                _suppressFieldEvents = true;
                try { box.Text = content; }
                finally { _suppressFieldEvents = false; }
            });
        });

        box.TextChanged += (_, _) =>
        {
            if (_suppressFieldEvents) return;
            QueueAutoSave();
        };
        expand.Click += async (_, _) =>
        {
            var enlarged = new TextBox
            {
                Text = box.Text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                MinHeight = 480,
                MinWidth = 720,
            };
            var dialog = new ContentDialog
            {
                Title = slotName,
                Content = new ScrollViewer { Content = enlarged, MinWidth = 720, MinHeight = 480 },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                box.Text = enlarged.Text;
            }
        };

        panel.Children.Add(box);
        _scriptEditors[slotName] = box;
        return panel;
    }

    // ----- Auto-save --------------------------------------------------------

    private void QueueAutoSave()
    {
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private async Task FlushAutoSaveAsync()
    {
        _saveDebounceTimer.Stop();
        if (ViewModel.SelectedProject is not { } project) return;

        try
        {
            await ViewModel.SaveCurrentBuildInfoAsync().ConfigureAwait(true);
            foreach (var (name, editor) in _scriptEditors)
            {
                await _buildService.WriteScriptAsync(project, name, editor.Text).ConfigureAwait(true);
            }
        }
        catch (IOException ex)
        {
            ActionStatusText.Text = $"Auto-save failed: {ex.Message}";
        }
    }

    // ----- File watchers ----------------------------------------------------

    private void SetupProjectsFolderWatcher(string folder)
    {
        DisposeProjectsFolderWatcher();
        try
        {
            _projectsFolderWatcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _projectsFolderWatcher.Created += OnProjectsFolderChangedEvent;
            _projectsFolderWatcher.Deleted += OnProjectsFolderChangedEvent;
            _projectsFolderWatcher.Renamed += OnProjectsFolderChangedEvent;
        }
        catch (IOException)
        {
            // Folder vanished / lock contention — best-effort.
        }
    }

    private void OnProjectsFolderChangedEvent(object sender, FileSystemEventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _watcherDebounceTimer!.Stop();
            _watcherDebounceTimer.Tick -= OnWatcherTickRefresh;
            _watcherDebounceTimer.Tick += OnWatcherTickRefresh;
            _watcherDebounceTimer.Start();
        });
    }

    private async void OnWatcherTickRefresh(object? sender, object e)
    {
        if (_watcherDebounceTimer is not null) _watcherDebounceTimer.Tick -= OnWatcherTickRefresh;
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private void SetupPayloadWatcher()
    {
        DisposePayloadWatcher();
        if (ViewModel.SelectedProject is not { } project) return;
        var payload = project.PayloadPath;
        if (!Directory.Exists(payload)) return;
        try
        {
            _payloadWatcher = new FileSystemWatcher(payload)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _payloadWatcher.Created += OnPayloadChangedEvent;
            _payloadWatcher.Deleted += OnPayloadChangedEvent;
            _payloadWatcher.Renamed += OnPayloadChangedEvent;
            _payloadWatcher.Changed += OnPayloadChangedEvent;
        }
        catch (IOException) { }
    }

    private void OnPayloadChangedEvent(object sender, FileSystemEventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _watcherDebounceTimer!.Stop();
            _watcherDebounceTimer.Tick -= OnWatcherTickRepopulatePayload;
            _watcherDebounceTimer.Tick += OnWatcherTickRepopulatePayload;
            _watcherDebounceTimer.Start();
        });
    }

    private void OnWatcherTickRepopulatePayload(object? sender, object e)
    {
        if (_watcherDebounceTimer is not null) _watcherDebounceTimer.Tick -= OnWatcherTickRepopulatePayload;
        PopulatePayloadTree();
    }

    private void DisposeProjectsFolderWatcher()
    {
        if (_projectsFolderWatcher is null) return;
        _projectsFolderWatcher.EnableRaisingEvents = false;
        _projectsFolderWatcher.Created -= OnProjectsFolderChangedEvent;
        _projectsFolderWatcher.Deleted -= OnProjectsFolderChangedEvent;
        _projectsFolderWatcher.Renamed -= OnProjectsFolderChangedEvent;
        _projectsFolderWatcher.Dispose();
        _projectsFolderWatcher = null;
    }

    private void DisposePayloadWatcher()
    {
        if (_payloadWatcher is null) return;
        _payloadWatcher.EnableRaisingEvents = false;
        _payloadWatcher.Created -= OnPayloadChangedEvent;
        _payloadWatcher.Deleted -= OnPayloadChangedEvent;
        _payloadWatcher.Renamed -= OnPayloadChangedEvent;
        _payloadWatcher.Changed -= OnPayloadChangedEvent;
        _payloadWatcher.Dispose();
        _payloadWatcher = null;
    }

    private void DisposeWatchers()
    {
        DisposeProjectsFolderWatcher();
        DisposePayloadWatcher();
    }

    // ----- Misc helpers -----------------------------------------------------

    private static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* user has no explorer.exe — ignore */ }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".ps1", ".py", ".md", ".txt", ".json", ".yaml", ".yml",
        ".xml", ".ini", ".cfg", ".conf", ".env", ".log", ".csv",
    };

    private static bool LooksLikeText(string path)
        => TextExtensions.Contains(Path.GetExtension(path));

    private async Task<string?> PromptForTextAsync(string title, string label, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 320 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
