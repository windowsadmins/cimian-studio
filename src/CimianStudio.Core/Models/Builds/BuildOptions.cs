namespace CimianStudio.Core.Models.Builds;

/// <summary>
/// Maps to cimipkg's CLI flag set. Renders to an argument list via
/// <see cref="ToArguments"/>. Session-scoped (held in <c>BuildViewModel</c>) so
/// options persist as the user clicks between projects.
/// </summary>
public sealed class BuildOptions
{
    public bool BuildNupkg { get; set; }
    public bool BuildIntunewin { get; set; }

    /// <summary>Default true — keeps GUI builds non-interactive (no terminal prompt).</summary>
    public bool SkipImport { get; set; } = true;

    public bool Verbose { get; set; }

    public string? EnvFilePath { get; set; }
    public string? SigningCertificate { get; set; }
    public string? SigningThumbprint { get; set; }

    public IReadOnlyList<string> ToArguments(string projectDirectory)
    {
        var args = new List<string> { projectDirectory };
        if (BuildNupkg) args.Add("--nupkg");
        if (BuildIntunewin) args.Add("--intunewin");
        if (SkipImport) args.Add("--skip-import");
        if (Verbose) args.Add("--verbose");
        if (!string.IsNullOrWhiteSpace(EnvFilePath))
        {
            args.Add("--env");
            args.Add(EnvFilePath);
        }
        if (!string.IsNullOrWhiteSpace(SigningCertificate))
        {
            args.Add("--sign-cert");
            args.Add(SigningCertificate);
        }
        if (!string.IsNullOrWhiteSpace(SigningThumbprint))
        {
            args.Add("--sign-thumbprint");
            args.Add(SigningThumbprint);
        }
        return args;
    }
}
