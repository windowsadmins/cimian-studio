namespace CimianStudio.Infrastructure.Yaml;

using CimianStudio.Core.Models.Builds;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// (De)serializes <see cref="BuildInfoData"/> to/from cimipkg's
/// <c>build-info.yaml</c> shape. Unknown keys at top level and inside the
/// <c>product:</c> map are captured in <see cref="BuildInfoData.PassthroughTop"/>
/// / <see cref="BuildInfoData.PassthroughProduct"/> so a load → edit → save
/// round-trip preserves user-authored fields the typed model doesn't model.
/// </summary>
public static class BuildInfoYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();

    // Top-level keys the typed BuildInfoData understands. Anything else is
    // funneled into PassthroughTop on load and merged back on save.
    private static readonly HashSet<string> KnownTopKeys = new(StringComparer.Ordinal)
    {
        "product", "install_location", "install_arguments", "valid_exit_codes",
        "uninstall_arguments", "software_detection", "postinstall_action",
        "signing_certificate", "signing_thumbprint", "minimum_os_version",
        "category", "icon", "blocking_applications", "override_uninstall_script",
        "upgrade_code", "msi_properties",
        // signature is populated by cimipkg during build; we ignore it on load
        // but pass it through unchanged on save.
        "signature",
    };

    private static readonly HashSet<string> KnownProductKeys = new(StringComparer.Ordinal)
    {
        "name", "version", "developer", "identifier", "description",
        "installer_type", "url", "copyright", "license", "tags",
    };

    public static BuildInfoData Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var data = Deserializer.Deserialize<BuildInfoData>(yaml) ?? new BuildInfoData();

        // Second pass: extract anything outside the typed schema into passthrough
        // dictionaries so we don't drop user-authored fields when we save again.
        var raw = Deserializer.Deserialize<Dictionary<string, object?>>(yaml);
        if (raw is not null)
        {
            foreach (var (key, value) in raw)
            {
                if (string.Equals(key, "product", StringComparison.Ordinal))
                {
                    if (value is Dictionary<object, object?> productMap)
                    {
                        foreach (var (pk, pv) in productMap)
                        {
                            var keyName = pk?.ToString();
                            if (keyName is not null && !KnownProductKeys.Contains(keyName))
                            {
                                data.PassthroughProduct[keyName] = pv;
                            }
                        }
                    }
                }
                else if (!KnownTopKeys.Contains(key))
                {
                    data.PassthroughTop[key] = value;
                }
            }
        }

        return data;
    }

    public static string Serialize(BuildInfoData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Build a flat dictionary we control so passthrough keys can be merged in.
        // Using a dict (not the typed object) keeps key order explicit and avoids
        // YamlDotNet picking the "product" key position based on declaration order.
        var product = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = data.Product.Name,
            ["version"] = data.Product.Version,
            ["identifier"] = data.Product.Identifier,
        };
        if (!string.IsNullOrEmpty(data.Product.Developer)) product["developer"] = data.Product.Developer;
        if (!string.IsNullOrEmpty(data.Product.Description)) product["description"] = data.Product.Description;
        if (!string.IsNullOrEmpty(data.Product.InstallerType)) product["installer_type"] = data.Product.InstallerType;
        if (!string.IsNullOrEmpty(data.Product.Url)) product["url"] = data.Product.Url;
        if (!string.IsNullOrEmpty(data.Product.Copyright)) product["copyright"] = data.Product.Copyright;
        if (!string.IsNullOrEmpty(data.Product.License)) product["license"] = data.Product.License;
        if (data.Product.Tags is { Count: > 0 }) product["tags"] = data.Product.Tags;
        foreach (var (key, value) in data.PassthroughProduct)
        {
            product[key] = value;
        }

        var top = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["product"] = product,
        };
        if (!string.IsNullOrEmpty(data.InstallLocation)) top["install_location"] = data.InstallLocation;
        if (!string.IsNullOrEmpty(data.InstallArguments)) top["install_arguments"] = data.InstallArguments;
        if (!string.IsNullOrEmpty(data.ValidExitCodes)) top["valid_exit_codes"] = data.ValidExitCodes;
        if (!string.IsNullOrEmpty(data.UninstallArguments)) top["uninstall_arguments"] = data.UninstallArguments;
        if (!string.IsNullOrEmpty(data.SoftwareDetection)) top["software_detection"] = data.SoftwareDetection;
        if (!string.IsNullOrEmpty(data.PostinstallAction)) top["postinstall_action"] = data.PostinstallAction;
        if (!string.IsNullOrEmpty(data.SigningCertificate)) top["signing_certificate"] = data.SigningCertificate;
        if (!string.IsNullOrEmpty(data.SigningThumbprint)) top["signing_thumbprint"] = data.SigningThumbprint;
        if (!string.IsNullOrEmpty(data.MinimumOsVersion)) top["minimum_os_version"] = data.MinimumOsVersion;
        if (!string.IsNullOrEmpty(data.Category)) top["category"] = data.Category;
        if (!string.IsNullOrEmpty(data.Icon)) top["icon"] = data.Icon;
        if (data.BlockingApplications is { Count: > 0 }) top["blocking_applications"] = data.BlockingApplications;
        if (data.OverrideUninstallScript) top["override_uninstall_script"] = true;
        if (!string.IsNullOrEmpty(data.UpgradeCode)) top["upgrade_code"] = data.UpgradeCode;
        if (data.MsiProperties is { Count: > 0 }) top["msi_properties"] = data.MsiProperties;
        foreach (var (key, value) in data.PassthroughTop)
        {
            top[key] = value;
        }

        return Serializer.Serialize(top);
    }
}
