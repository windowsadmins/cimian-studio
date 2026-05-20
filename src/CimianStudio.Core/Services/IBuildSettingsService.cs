namespace CimianStudio.Core.Services;

/// <summary>
/// Strongly-typed accessor for the Build section of <c>AppSettings</c>. Wraps
/// <see cref="ISettingsService.GetSection{T}"/> so view-models don't need to know
/// about the section id or JSON marshalling. Singleton — the
/// <see cref="ProjectsFolderChanged"/> event lets <c>MainWindow</c> show/hide the
/// Build nav item without re-resolving each settings load.
/// </summary>
public interface IBuildSettingsService
{
    string? ProjectsFolder { get; }
    string? CimipkgPath { get; }

    Task<bool> HasProjectsFolderAsync(CancellationToken cancellationToken = default);

    Task SetProjectsFolderAsync(string? path, CancellationToken cancellationToken = default);
    Task SetCimipkgPathAsync(string? path, CancellationToken cancellationToken = default);

    /// <summary>Fired (on the calling thread) after <c>ProjectsFolder</c> persists.</summary>
    event EventHandler? ProjectsFolderChanged;
}
