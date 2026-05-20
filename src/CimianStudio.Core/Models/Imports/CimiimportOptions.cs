namespace CimianStudio.Core.Models.Imports;

/// <summary>
/// Wizard-collected options for one <c>cimiimport.exe</c> run. Maps 1:1 to the
/// flag set the CLI exposes (see <c>cimiimport --help</c>). Anything the
/// wizard doesn't surface stays at cimiimport's defaults.
/// </summary>
public sealed class CimiimportOptions
{
    /// <summary>Required. Path to the installer file (.msi / .exe / .msix / .nupkg).</summary>
    public required string InstallerPath { get; init; }

    /// <summary>Override the Cimian repository path. Required when CimianStudio is
    /// driving the run from a repo other than cimiimport's default config.</summary>
    public string? RepoPath { get; set; }

    /// <summary>Architecture override (e.g. <c>x64</c>, <c>arm64</c>, or <c>x64,arm64</c>).</summary>
    public string? Architecture { get; set; }

    public string? UninstallerPath { get; set; }

    public string? MinimumOsVersion { get; set; }
    public string? MaximumOsVersion { get; set; }
    public string? MinimumCimianVersion { get; set; }

    public string? PreinstallScriptPath { get; set; }
    public string? PostinstallScriptPath { get; set; }
    public string? PreuninstallScriptPath { get; set; }
    public string? PostuninstallScriptPath { get; set; }
    public string? InstallCheckScriptPath { get; set; }
    public string? UninstallCheckScriptPath { get; set; }

    /// <summary>Additional installer-detected paths added to the final pkginfo <c>installs</c> array.</summary>
    public List<string> InstallsArray { get; } = [];

    public bool ExtractIcon { get; set; }
    public string? IconOutputPath { get; set; }

    /// <summary>Always emitted as <c>--nointeractive</c>; the wizard collected every value cimiimport would otherwise prompt for.</summary>
    public bool NonInteractive { get; set; } = true;

    /// <summary>
    /// Renders the options to the argv list cimiimport.exe expects. The
    /// installer path is the positional argument; everything else is flagged.
    /// </summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { InstallerPath };

        if (!string.IsNullOrWhiteSpace(RepoPath))
        {
            args.Add("--repo_path");
            args.Add(RepoPath);
        }
        if (!string.IsNullOrWhiteSpace(Architecture))
        {
            args.Add("--arch");
            args.Add(Architecture);
        }
        if (!string.IsNullOrWhiteSpace(UninstallerPath))
        {
            args.Add("--uninstaller");
            args.Add(UninstallerPath);
        }
        if (!string.IsNullOrWhiteSpace(MinimumOsVersion))
        {
            args.Add("--minimum_os_version");
            args.Add(MinimumOsVersion);
        }
        if (!string.IsNullOrWhiteSpace(MaximumOsVersion))
        {
            args.Add("--maximum_os_version");
            args.Add(MaximumOsVersion);
        }
        if (!string.IsNullOrWhiteSpace(MinimumCimianVersion))
        {
            args.Add("--minimum_cimian_version");
            args.Add(MinimumCimianVersion);
        }

        AddScriptFlag(args, "--preinstall-script", PreinstallScriptPath);
        AddScriptFlag(args, "--postinstall-script", PostinstallScriptPath);
        AddScriptFlag(args, "--preuninstall-script", PreuninstallScriptPath);
        AddScriptFlag(args, "--postuninstall-script", PostuninstallScriptPath);
        AddScriptFlag(args, "--install-check-script", InstallCheckScriptPath);
        AddScriptFlag(args, "--uninstall-check-script", UninstallCheckScriptPath);

        foreach (var path in InstallsArray)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            args.Add("--installs-array");
            args.Add(path);
        }

        if (ExtractIcon) args.Add("--extract-icon");
        if (!string.IsNullOrWhiteSpace(IconOutputPath))
        {
            args.Add("--icon");
            args.Add(IconOutputPath);
        }

        if (NonInteractive) args.Add("--nointeractive");

        return args;
    }

    private static void AddScriptFlag(List<string> args, string flag, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        args.Add(flag);
        args.Add(path);
    }
}
