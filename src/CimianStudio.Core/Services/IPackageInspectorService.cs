namespace CimianStudio.Core.Services;

public interface IPackageInspectorService
{
    Uri DownloadPageUrl { get; }

    bool CanInspect(string filePath);

    string? FindExecutable();

    bool IsInstalled => FindExecutable() is not null;

    Task<PackageInspectorLaunchResult> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    Task<PackageInspectorInstallResult> InstallLatestAsync(IProgress<PackageInspectorInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record PackageInspectorInstallProgress(string Stage, double? PercentComplete = null);

public sealed record PackageInspectorInstallResult(bool Success, string? InstalledExecutablePath, string? ErrorMessage);

public enum PackageInspectorLaunchFailureKind
{
    None,
    NotInstalled,
    FileMissing,
    UnsignedExecutable,
    DefenderAsrBlock,
    Unknown,
}

public sealed record PackageInspectorLaunchResult(
    bool Success,
    PackageInspectorLaunchFailureKind FailureKind,
    string? Detail);
