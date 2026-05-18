namespace CimianStudio.Infrastructure.Settings;

using CimianStudio.Core.Services;
using CimianStudio.Shared.Settings;

public sealed class ImportSettingsService : IImportSettingsService
{
    private const string SectionId = "import";

    private readonly ISettingsService _settings;

    public ImportSettingsService(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string? CimiimportPath => _settings.GetSection<ImportSettings>(SectionId).CimiimportPath;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        // GetSection reads from the in-memory cache populated by LoadAsync.
        // Call here to make sure the first property read returns persisted state.
        await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCimiimportPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        var current = _settings.GetSection<ImportSettings>(SectionId);
        current.CimiimportPath = string.IsNullOrWhiteSpace(path) ? null : path;
        await _settings.SetSectionAsync(SectionId, current, cancellationToken).ConfigureAwait(false);
    }
}
