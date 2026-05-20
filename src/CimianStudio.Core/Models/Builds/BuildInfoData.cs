namespace CimianStudio.Core.Models.Builds;

using YamlDotNet.Serialization;

/// <summary>
/// Mirrors cimipkg's <c>build-info.yaml</c> shape (see <c>cli/cimipkg/Models/BuildInfo.cs</c>
/// upstream). Common keys are first-class; anything the schema doesn't model lives in
/// <see cref="PassthroughTop"/> / <see cref="PassthroughProduct"/> so a load → save round-trip
/// never drops user-authored fields.
/// </summary>
public sealed class BuildInfoData
{
    [YamlMember(Alias = "product")]
    public ProductBlock Product { get; set; } = new();

    [YamlMember(Alias = "install_location")]
    public string? InstallLocation { get; set; }

    [YamlMember(Alias = "install_arguments")]
    public string? InstallArguments { get; set; }

    [YamlMember(Alias = "valid_exit_codes")]
    public string? ValidExitCodes { get; set; }

    [YamlMember(Alias = "uninstall_arguments")]
    public string? UninstallArguments { get; set; }

    [YamlMember(Alias = "software_detection")]
    public string? SoftwareDetection { get; set; }

    [YamlMember(Alias = "postinstall_action")]
    public string? PostinstallAction { get; set; }

    [YamlMember(Alias = "signing_certificate")]
    public string? SigningCertificate { get; set; }

    [YamlMember(Alias = "signing_thumbprint")]
    public string? SigningThumbprint { get; set; }

    [YamlMember(Alias = "minimum_os_version")]
    public string? MinimumOsVersion { get; set; }

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "icon")]
    public string? Icon { get; set; }

    [YamlMember(Alias = "blocking_applications")]
    public List<string>? BlockingApplications { get; set; }

    [YamlMember(Alias = "override_uninstall_script")]
    public bool OverrideUninstallScript { get; set; }

    [YamlMember(Alias = "upgrade_code")]
    public string? UpgradeCode { get; set; }

    [YamlMember(Alias = "msi_properties")]
    public Dictionary<string, string>? MsiProperties { get; set; }

    [YamlIgnore]
    public Dictionary<string, object?> PassthroughTop { get; set; } = [];

    [YamlIgnore]
    public Dictionary<string, object?> PassthroughProduct { get; set; } = [];
}

public sealed class ProductBlock
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    [YamlMember(Alias = "developer")]
    public string? Developer { get; set; }

    [YamlMember(Alias = "identifier")]
    public string Identifier { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "installer_type")]
    public string? InstallerType { get; set; }

    // CA1056 wants System.Uri, but build-info.yaml stores this as an opaque
    // string (often with placeholder substitutions like ${TIMESTAMP}) — round-
    // tripping through Uri would parse-fail or normalise the user's input.
    [YamlMember(Alias = "url")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "YAML-bound DTO; preserves authored value verbatim.")]
    public string? Url { get; set; }

    [YamlMember(Alias = "copyright")]
    public string? Copyright { get; set; }

    [YamlMember(Alias = "license")]
    public string? License { get; set; }

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }
}
