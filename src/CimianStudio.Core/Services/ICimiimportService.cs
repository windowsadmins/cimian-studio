namespace CimianStudio.Core.Services;

using CimianStudio.Core.Models.Imports;
using CimianStudio.Core.Models.Repository;

/// <summary>
/// Shells out to <c>cimiimport.exe</c> on behalf of the Import tab. Mirrors the
/// <c>IBuildService</c> shape: line-streamed stdout, terminated by a single
/// <see cref="ImportFinished"/> or <see cref="ImportFailed"/> event.
/// CimianStudio does not re-implement metadata extraction, pkginfo emission,
/// or installer copy — those stay in <c>cimiimport.exe</c>, which admins
/// already have installed at <c>C:\Program Files\Cimian\</c>.
/// </summary>
public interface ICimiimportService
{
    /// <summary>
    /// Runs cimiimport.exe in the context of <paramref name="repository"/>
    /// with the supplied options. Streams stdout/stderr as <see cref="ImportLine"/>
    /// events and emits exactly one <see cref="ImportFinished"/> /
    /// <see cref="ImportFailed"/> when the process exits.
    /// </summary>
    IAsyncEnumerable<ImportEvent> RunAsync(
        CimianRepository repository,
        CimiimportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locates the cimiimport.exe to use. Lookup order: settings override →
    /// <c>%PATH%</c> (via <c>where.exe</c>-style resolution) →
    /// <c>C:\Program Files\Cimian\cimiimport.exe</c>.
    /// </summary>
    Task<CimiimportToolInfo> GetToolInfoAsync(CancellationToken cancellationToken = default);
}
