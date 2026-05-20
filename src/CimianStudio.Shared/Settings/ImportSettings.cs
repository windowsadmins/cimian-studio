namespace CimianStudio.Shared.Settings;

/// <summary>
/// Import-tab settings persisted as a JSON blob under the <c>"import"</c>
/// section id in <see cref="AppSettings.Sections"/>. Loaded / saved by
/// <c>IImportSettingsService</c>.
/// </summary>
public sealed class ImportSettings
{
    /// <summary>
    /// Explicit path to <c>cimiimport.exe</c>. Empty/null means use the
    /// auto-discovery order (<c>C:\Program Files\Cimian</c> → PATH).
    /// </summary>
    public string? CimiimportPath { get; set; }
}
