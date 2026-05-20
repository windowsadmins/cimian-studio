namespace CimianStudio.Core.Services;

using CimianStudio.Core.Models.Builds;

/// <summary>
/// Domain-side contract for the Build tab. Implementations enumerate cimipkg
/// projects under a configured root, round-trip <c>build-info.yaml</c>, and
/// stream a live build via <c>cimipkg.exe</c>.
/// </summary>
public interface IBuildService
{
    /// <summary>
    /// Shallowly enumerates immediate child directories of <paramref name="projectsFolder"/>
    /// that contain a <c>build-info.yaml</c> file.
    /// </summary>
    Task<IReadOnlyList<BuildProject>> DiscoverProjectsAsync(string projectsFolder, CancellationToken cancellationToken = default);

    Task<BuildInfoData?> LoadBuildInfoAsync(BuildProject project, CancellationToken cancellationToken = default);

    Task SaveBuildInfoAsync(BuildProject project, BuildInfoData buildInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the canonical script slots cimipkg recognizes (preinstall, postinstall, uninstall)
    /// plus any extra <c>*.ps1</c> files actually present in <c>scripts/</c>.
    /// </summary>
    IReadOnlyList<string> EnumerateScriptSlots(BuildProject project);

    Task<string> ReadScriptAsync(BuildProject project, string scriptName, CancellationToken cancellationToken = default);

    /// <summary>Empty content deletes the file (lets cimipkg fall through to "no script for this phase").</summary>
    Task WriteScriptAsync(BuildProject project, string scriptName, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns cimipkg.exe and streams stdout/stderr line by line. Terminates with a
    /// single <see cref="BuildFinished"/> (or <see cref="BuildFailed"/>) event.
    /// </summary>
    IAsyncEnumerable<BuildEvent> StreamBuildAsync(BuildProject project, BuildOptions options, CancellationToken cancellationToken = default);

    /// <summary>Locates the cimipkg.exe to use (settings override → standard install → PATH).</summary>
    Task<CimipkgToolInfo> GetToolInfoAsync(CancellationToken cancellationToken = default);
}
