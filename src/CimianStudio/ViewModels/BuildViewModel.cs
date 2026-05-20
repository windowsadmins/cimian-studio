namespace CimianStudio.ViewModels;

using System.Collections.ObjectModel;
using CimianStudio.Core.Models.Builds;
using CimianStudio.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Drives the Build tab. Holds the project list (filtered), the selected
/// project + its loaded BuildInfo, the per-session BuildOptions, and the
/// console-output buffer that the page streams into.
/// </summary>
public sealed partial class BuildViewModel : ObservableObject
{
    private readonly IBuildService _buildService;
    private readonly IBuildSettingsService _buildSettings;

    public BuildViewModel(IBuildService buildService, IBuildSettingsService buildSettings)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(buildSettings);
        _buildService = buildService;
        _buildSettings = buildSettings;
    }

    /// <summary>All discovered projects (unfiltered).</summary>
    public List<BuildProject> AllProjects { get; private set; } = [];

    /// <summary>Bound to the project list-view; updated as the filter text changes.</summary>
    public ObservableCollection<BuildProject> FilteredProjects { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; }

    [ObservableProperty]
    public partial BuildProject? SelectedProject { get; set; }

    [ObservableProperty]
    public partial BuildInfoData? CurrentBuildInfo { get; set; }

    /// <summary>Persists across project switches (per spec — tab-session state).</summary>
    public BuildOptions Options { get; } = new();

    [ObservableProperty]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial string? LastBuiltProductPath { get; set; }

    public ObservableCollection<string> ConsoleLines { get; } = [];

    public string? ProjectsFolder => _buildSettings.ProjectsFolder;

    public async Task RefreshProjectsAsync(CancellationToken cancellationToken = default)
    {
        var folder = _buildSettings.ProjectsFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            AllProjects = [];
            FilteredProjects.Clear();
            return;
        }

        // Don't use ConfigureAwait(false) here — ApplyFilter mutates FilteredProjects,
        // which is bound as the project ListView's ItemsSource. CollectionChanged must
        // fire on the UI thread or WinUI throws COMException across the marshaller.
        AllProjects = [.. await _buildService.DiscoverProjectsAsync(folder, cancellationToken).ConfigureAwait(true)];
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredProjects.Clear();
        var query = (FilterText ?? string.Empty).Trim();
        var source = string.IsNullOrEmpty(query)
            ? AllProjects
            : AllProjects.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (var project in source) FilteredProjects.Add(project);
    }

    public async Task LoadBuildInfoForSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null)
        {
            CurrentBuildInfo = null;
            return;
        }
        CurrentBuildInfo = await _buildService.LoadBuildInfoAsync(SelectedProject, cancellationToken).ConfigureAwait(false)
            ?? new BuildInfoData();
    }

    public Task SaveCurrentBuildInfoAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null || CurrentBuildInfo is null) return Task.CompletedTask;
        return _buildService.SaveBuildInfoAsync(SelectedProject, CurrentBuildInfo, cancellationToken);
    }
}
