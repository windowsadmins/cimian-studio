namespace CimianStudio.Core.Services;

public interface IPackageInspectorService
{
    Uri DownloadPageUrl { get; }

    bool CanInspect(string filePath);

    string? FindExecutable();

    bool IsInstalled => FindExecutable() is not null;

    Task<bool> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    Task<PackageInspectorInstallResult> InstallLatestAsync(IProgress<PackageInspectorInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record PackageInspectorInstallProgress(string Stage, double? PercentComplete = null);

public sealed record PackageInspectorInstallResult(bool Success, string? InstalledExecutablePath, string? ErrorMessage);
