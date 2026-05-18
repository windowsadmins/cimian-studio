namespace CimianStudio.Views.Import;

using System.Text;
using CimianStudio.Core.Models.Imports;
using CimianStudio.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

/// <summary>
/// Front-end for <c>cimiimport.exe</c>. CimianStudio collects a queue of
/// installer files, lets the admin set the CLI flags cimiimport accepts
/// (<c>--arch</c>, <c>--uninstaller</c>, <c>--*_os_version</c>,
/// <c>--*-script</c>, <c>--extract-icon</c>), then shells out per file via
/// <see cref="ICimiimportService"/>. The produced pkginfos can be opened in
/// the existing Package Editor for catalogs / metadata edits the CLI doesn't
/// surface (name, version, developer, blocking_applications, etc.).
/// </summary>
public sealed partial class ImportPage : Page
{
    private readonly IRepositoryService _repositoryService;
    private readonly IPackageService _packageService;
    private readonly ICimiimportService _cimiimportService;

    private readonly Dictionary<string, TextBox> _scriptInputs = new(StringComparer.Ordinal);
    private readonly List<string> _producedPkginfoPaths = [];
    private readonly List<string> _producedInstallerPaths = [];

    public ImportViewModel ViewModel { get; }

    public ImportPage(
        ImportViewModel viewModel,
        IRepositoryService repositoryService,
        IPackageService packageService,
        ICimiimportService cimiimportService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repositoryService);
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(cimiimportService);
        ViewModel = viewModel;
        _repositoryService = repositoryService;
        _packageService = packageService;
        _cimiimportService = cimiimportService;
        InitializeComponent();

        BuildScriptInputs();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshToolBannerAsync().ConfigureAwait(true);
    }

    private async Task RefreshToolBannerAsync()
    {
        var info = await _cimiimportService.GetToolInfoAsync().ConfigureAwait(true);
        ToolMissingBar.IsOpen = !info.Found;
        if (info.Found)
        {
            ToolMissingBar.Message = string.IsNullOrEmpty(info.Version)
                ? $"Detected: {info.Path}"
                : $"Detected cimiimport {info.Version} at {info.Path}";
        }
        else
        {
            ToolMissingBar.Message = info.Detail ?? "Install Cimian or set the path in Settings → Import.";
        }
    }

    // ----- Drag-drop + file picker ------------------------------------------

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to import queue";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();
        EnqueueAndShow(paths);
    }

    private async void OnPickFileClicked(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is not { } window) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var ext in new[] { ".msi", ".exe", ".nupkg", ".msix", ".msixbundle", ".appx" })
        {
            picker.FileTypeFilter.Add(ext);
        }
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        EnqueueAndShow(files.Select(f => f.Path).ToList());
    }

    /// <summary>
    /// External entry point used by cross-page hand-offs (Build tab forwarding
    /// a built MSI here, Packages tab forwarding a drop). Adds the file paths
    /// to the queue and reveals the queue + options + run panels.
    /// </summary>
    public Task HandleExternalFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnqueueAndShow([.. paths]);
        return Task.CompletedTask;
    }

    private void EnqueueAndShow(List<string> paths)
    {
        if (paths.Count == 0) return;
        ViewModel.EnqueueFiles(paths);
        QueueBorder.Visibility = Visibility.Visible;
        OptionsBorder.Visibility = Visibility.Visible;
        RunBorder.Visibility = Visibility.Visible;
        UpdateRunButtonEnabled();
        RunStatusText.Text = $"{ViewModel.Queue.Count} file{(ViewModel.Queue.Count == 1 ? string.Empty : "s")} ready.";
    }

    // ----- Queue management -------------------------------------------------

    private void OnClearQueueClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.Clear();
        QueueBorder.Visibility = Visibility.Collapsed;
        OptionsBorder.Visibility = Visibility.Collapsed;
        RunBorder.Visibility = Visibility.Collapsed;
        ConsoleBorder.Visibility = Visibility.Collapsed;
        ConsoleText.Text = string.Empty;
        ResultActionsPanel.Visibility = Visibility.Collapsed;
        _producedPkginfoPaths.Clear();
        _producedInstallerPaths.Clear();
        RunStatusText.Text = string.Empty;
        UpdateRunButtonEnabled();
    }

    private void OnRemoveQueueItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            var item = ViewModel.Queue.FirstOrDefault(q => string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (item is not null) ViewModel.Queue.Remove(item);
            if (ViewModel.Queue.Count == 0) OnClearQueueClicked(sender, e);
            else UpdateRunButtonEnabled();
        }
    }

    // ----- Options: uninstaller picker + scripts ----------------------------

    private async void OnPickUninstallerClicked(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance is not { } window) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) UninstallerBox.Text = file.Path;
    }

    private void BuildScriptInputs()
    {
        // cimiimport's six script slots. The form simply collects a path to a
        // .ps1 file for each slot — cimiimport reads the file and embeds its
        // contents in the produced pkginfo.
        var slots = new[]
        {
            ("--preinstall-script", "preinstall"),
            ("--postinstall-script", "postinstall"),
            ("--preuninstall-script", "preuninstall"),
            ("--postuninstall-script", "postuninstall"),
            ("--install-check-script", "installcheck"),
            ("--uninstall-check-script", "uninstallcheck"),
        };
        foreach (var (flag, key) in slots)
        {
            ScriptsPanel.Children.Add(BuildScriptRow(flag, key));
        }
    }

    private Grid BuildScriptRow(string flag, string key)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = flag,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var box = new TextBox { PlaceholderText = "(none)" };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _scriptInputs[key] = box;

        var browse = new Button { Content = "Browse…" };
        Grid.SetColumn(browse, 2);
        browse.Click += async (_, _) =>
        {
            if (App.MainWindowInstance is not { } window) return;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".ps1");
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) box.Text = file.Path;
        };
        row.Children.Add(browse);

        return row;
    }

    private void UpdateRunButtonEnabled()
    {
        var hasQueue = ViewModel.Queue.Any(q => q.Status == ImportViewModel.QueueItemStatus.Pending);
        var hasRepo = _repositoryService.CurrentRepository is not null;
        RunButton.IsEnabled = hasQueue && hasRepo;
    }

    // ----- Run --------------------------------------------------------------

    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (_repositoryService.CurrentRepository is not { } repo)
        {
            RunStatusText.Text = "No repository is open. Open one from the Dashboard first.";
            return;
        }

        RunButton.IsEnabled = false;
        ConsoleBorder.Visibility = Visibility.Visible;
        ResultActionsPanel.Visibility = Visibility.Collapsed;
        var sb = new StringBuilder(ConsoleText.Text);

        _producedPkginfoPaths.Clear();
        _producedInstallerPaths.Clear();

        var pending = ViewModel.Queue.Where(q => q.Status == ImportViewModel.QueueItemStatus.Pending).ToList();
        var doneCount = 0;
        var failedCount = 0;

        foreach (var item in pending)
        {
            item.Status = ImportViewModel.QueueItemStatus.InProgress;
            item.StatusText = "Running cimiimport…";
            RunStatusText.Text = $"Importing {item.FileName}…";

            var options = BuildOptionsForFile(item.FilePath);

            sb.AppendLine();
            sb.Append("$ cimiimport ").AppendJoin(' ', options.ToArguments()).AppendLine();
            ConsoleText.Text = sb.ToString();

            try
            {
                await foreach (var evt in _cimiimportService.RunAsync(repo, options).ConfigureAwait(true))
                {
                    switch (evt)
                    {
                        case ImportLine line:
                            sb.AppendLine(line.Text);
                            ConsoleText.Text = sb.ToString();
                            ConsoleScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
                            break;
                        case ImportFinished done:
                            if (done.ExitCode == 0)
                            {
                                item.Status = ImportViewModel.QueueItemStatus.Done;
                                item.StatusText = done.PkginfoPath is null
                                    ? "Done (pkginfo path not detected)."
                                    : "Imported → " + Path.GetRelativePath(repo.RootPath, done.PkginfoPath).Replace('\\', '/');
                                if (!string.IsNullOrEmpty(done.PkginfoPath))
                                    _producedPkginfoPaths.Add(done.PkginfoPath);
                                if (!string.IsNullOrEmpty(done.InstallerPath))
                                    _producedInstallerPaths.Add(done.InstallerPath);
                                doneCount++;
                            }
                            else
                            {
                                item.Status = ImportViewModel.QueueItemStatus.Error;
                                item.StatusText = $"cimiimport exit {done.ExitCode}";
                                failedCount++;
                            }
                            break;
                        case ImportFailed fail:
                            item.Status = ImportViewModel.QueueItemStatus.Error;
                            item.StatusText = fail.Reason;
                            failedCount++;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                item.Status = ImportViewModel.QueueItemStatus.Error;
                item.StatusText = ex.Message;
                failedCount++;
            }
        }

        // Nudge PackageService so the Packages tab refreshes — cimiimport
        // writes pkginfos directly, bypassing PackageService.CreatePackageAsync.
        if (doneCount > 0)
        {
            _packageService.NotifyPackagesChanged();
        }

        RunStatusText.Text = failedCount == 0
            ? $"Done — {doneCount} imported."
            : $"Done — {doneCount} imported, {failedCount} failed.";

        ResultActionsPanel.Visibility = _producedPkginfoPaths.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateRunButtonEnabled();
    }

    private CimiimportOptions BuildOptionsForFile(string installerPath)
    {
        var options = new CimiimportOptions { InstallerPath = installerPath };

        if (ArchCombo.SelectedItem is ComboBoxItem { Tag: string tag } && !string.IsNullOrEmpty(tag))
        {
            options.Architecture = tag;
        }
        if (!string.IsNullOrWhiteSpace(UninstallerBox.Text)) options.UninstallerPath = UninstallerBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(MinOsBox.Text)) options.MinimumOsVersion = MinOsBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(MaxOsBox.Text)) options.MaximumOsVersion = MaxOsBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(MinCimianBox.Text)) options.MinimumCimianVersion = MinCimianBox.Text.Trim();
        if (ExtractIconCheck.IsChecked == true) options.ExtractIcon = true;

        options.PreinstallScriptPath = NullIfEmpty(_scriptInputs["preinstall"].Text);
        options.PostinstallScriptPath = NullIfEmpty(_scriptInputs["postinstall"].Text);
        options.PreuninstallScriptPath = NullIfEmpty(_scriptInputs["preuninstall"].Text);
        options.PostuninstallScriptPath = NullIfEmpty(_scriptInputs["postuninstall"].Text);
        options.InstallCheckScriptPath = NullIfEmpty(_scriptInputs["installcheck"].Text);
        options.UninstallCheckScriptPath = NullIfEmpty(_scriptInputs["uninstallcheck"].Text);

        return options;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----- Post-run actions --------------------------------------------------

    private async void OnOpenInPackageEditorClicked(object sender, RoutedEventArgs e)
    {
        if (_producedPkginfoPaths.Count == 0) return;
        var latest = _producedPkginfoPaths[^1];
        try
        {
            var package = await _packageService.GetPackageByPathAsync(latest).ConfigureAwait(true);
            App.MainWindowInstance?.NavigateToPackage(package);
        }
        catch (InvalidOperationException)
        {
            // PackageService threw because the produced pkginfo couldn't be
            // loaded — surface as a status update rather than crashing.
            RunStatusText.Text = $"Failed to open {Path.GetFileName(latest)} in the editor.";
        }
    }

    private async void OnOpenInGitClicked(object sender, RoutedEventArgs e)
    {
        var combined = new List<string>(_producedPkginfoPaths.Count + _producedInstallerPaths.Count);
        combined.AddRange(_producedPkginfoPaths);
        combined.AddRange(_producedInstallerPaths);
        if (combined.Count == 0 || App.MainWindowInstance is not { } window) return;

        var subject = _producedPkginfoPaths.Count == 1
            ? $"Import: {Path.GetFileNameWithoutExtension(_producedPkginfoPaths[0])}"
            : $"Import batch: {_producedPkginfoPaths.Count} packages";

        window.NavigateTo("git");
        var gitPage = App.Resolve<GitPage>();
        await gitPage.PrepareCommitAsync(combined, subject).ConfigureAwait(true);
    }
}
