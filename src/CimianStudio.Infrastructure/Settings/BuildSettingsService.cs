namespace CimianStudio.Infrastructure.Settings;

using CimianStudio.Core.Services;
using CimianStudio.Shared.Settings;

/// <summary>
/// Section-typed accessor over <see cref="ISettingsService"/> for build-tab state.
/// Pulls the cached section from <c>ISettingsService</c> on demand so values reflect
/// the latest save without us holding a stale copy. <see cref="ProjectsFolderChanged"/>
/// fires whenever the section is rewritten so <c>MainWindow</c> can re-evaluate
/// whether the Build nav item should be visible.
/// </summary>
public sealed class BuildSettingsService : IBuildSettingsService
{
    private const string SectionId = "build";

    private readonly ISettingsService _settings;

    public BuildSettingsService(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _settings.SectionChanged += OnSectionChanged;
    }

    public string? ProjectsFolder => _settings.GetSection<BuildSettings>(SectionId).ProjectsFolder;
    public string? CimipkgPath => _settings.GetSection<BuildSettings>(SectionId).CimipkgPath;

    public async Task<bool> HasProjectsFolderAsync(CancellationToken cancellationToken = default)
    {
        // Ensure the settings cache is warm before consulting it — first launch
        // never called LoadAsync from anywhere else.
        await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var path = ProjectsFolder;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    public async Task SetProjectsFolderAsync(string? path, CancellationToken cancellationToken = default)
    {
        var current = _settings.GetSection<BuildSettings>(SectionId);
        current.ProjectsFolder = string.IsNullOrWhiteSpace(path) ? null : path;
        await _settings.SetSectionAsync(SectionId, current, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCimipkgPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        var current = _settings.GetSection<BuildSettings>(SectionId);
        current.CimipkgPath = string.IsNullOrWhiteSpace(path) ? null : path;
        await _settings.SetSectionAsync(SectionId, current, cancellationToken).ConfigureAwait(false);
    }

    public event EventHandler? ProjectsFolderChanged;

    private void OnSectionChanged(object? sender, string sectionId)
    {
        if (string.Equals(sectionId, SectionId, StringComparison.Ordinal))
        {
            ProjectsFolderChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
