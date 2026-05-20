namespace CimianStudio.Core.Services;

/// <summary>
/// Strongly-typed accessor for the Import section of <c>AppSettings</c>.
/// Same shape as <see cref="IBuildSettingsService"/>.
/// </summary>
public interface IImportSettingsService
{
    string? CimiimportPath { get; }

    /// <summary>Forces the settings cache warm before subsequent property reads.</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);

    Task SetCimiimportPathAsync(string? path, CancellationToken cancellationToken = default);
}
