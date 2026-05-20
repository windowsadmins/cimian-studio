namespace CimianStudio.Core.Models.Builds;

/// <summary>
/// A cimipkg project: a directory containing <c>build-info.yaml</c> plus optional
/// <c>payload/</c>, <c>scripts/</c>, and <c>.env</c> children. Discovery is shallow —
/// only direct subdirectories of the configured projects folder are scanned.
/// </summary>
public sealed class BuildProject
{
    public required string Name { get; init; }
    public required string DirectoryPath { get; init; }
    public required string BuildInfoPath { get; init; }
    public string PayloadPath => Path.Combine(DirectoryPath, "payload");
    public string ScriptsPath => Path.Combine(DirectoryPath, "scripts");
    public string BuildOutputPath => Path.Combine(DirectoryPath, "build");
    public string EnvFilePath => Path.Combine(DirectoryPath, ".env");
}
