namespace CimianStudio.Shared.Settings;

/// <summary>
/// Build-tab settings persisted as a JSON blob under the <c>"build"</c> section id
/// in <see cref="AppSettings.Sections"/>. Loaded / saved by
/// <c>IBuildSettingsService</c>.
/// </summary>
public sealed class BuildSettings
{
    /// <summary>Root folder scanned for cimipkg projects (one project per subdir).</summary>
    public string? ProjectsFolder { get; set; }

    /// <summary>
    /// Explicit path to <c>cimipkg.exe</c>. Empty/null means use the auto-discovery
    /// order (sibling release directory → <c>PATH</c> → <c>%ProgramFiles%\Cimian</c>).
    /// </summary>
    public string? CimipkgPath { get; set; }
}
