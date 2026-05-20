namespace CimianStudio.Infrastructure.Yaml;

using CimianStudio.Core.Models.Packages;

/// <summary>
/// Thin wrapper around <see cref="CimianYaml.SerializePkgInfo{T}"/> /
/// <see cref="CimianYaml.DeserializePkgInfo{T}"/> that handles the trailing
/// <c>_metadata</c> block. Both <see cref="Package.Metadata"/> assignment and
/// the script trailing-newline normalisation happen here so the rest of the
/// app can think in terms of <see cref="Package"/> values, not YAML text.
/// </summary>
public static class PackageYaml
{
    public static Package? Deserialize(string yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return null;
        var pkg = CimianYaml.DeserializePkgInfo<Package>(yaml);
        if (pkg is null) return null;
        pkg.Metadata = CimianYaml.ExtractMetadataBlock(yaml);
        return pkg;
    }

    public static string Serialize(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);
        EnsureTrailingNewlinesForScripts(package);
        // CimianYaml.SerializePkgInfo handles _metadata splice + key reorder
        // when Package.Metadata is non-empty (reflection-based on the
        // "Metadata" property name).
        return CimianYaml.SerializePkgInfo(package);
    }

    // Without a trailing newline, YamlDotNet picks `|-` (strip) instead of `|`
    // (clip) for the literal block scalar style. Real deployment files use `|`
    // exclusively — clip preserves the single trailing newline scripts almost
    // always have, and round-trips cleanly. Mutates the input package directly
    // (idempotent: re-running adds nothing). Same pattern YamlUtils itself uses
    // in NormalizeMultilineStrings; consider promoting upstream so every Cimian
    // tool gets the same behavior without needing this shim.
    private static void EnsureTrailingNewlinesForScripts(Package p)
    {
        p.PreinstallScript = AppendNewlineIfMissing(p.PreinstallScript);
        p.PostinstallScript = AppendNewlineIfMissing(p.PostinstallScript);
        p.PreuninstallScript = AppendNewlineIfMissing(p.PreuninstallScript);
        p.PostuninstallScript = AppendNewlineIfMissing(p.PostuninstallScript);
        p.InstallCheckScript = AppendNewlineIfMissing(p.InstallCheckScript);
        p.UninstallCheckScript = AppendNewlineIfMissing(p.UninstallCheckScript);
    }

    private static string? AppendNewlineIfMissing(string? s)
        => string.IsNullOrEmpty(s) || s.EndsWith('\n') ? s : s + "\n";
}
