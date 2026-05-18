namespace CimianStudio.Infrastructure.Yaml;

using System.Collections;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

/// <summary>
/// CimianStudio's YAML serialization layer for pkginfo, manifest, and catalog
/// files. Ported from <c>Cimian.Core.Services.YamlUtils</c> (cimian repo commit
/// 60b8080e) so this app can be cloned and built without a sibling-repo
/// ProjectReference — admins run <c>cimipkg.exe</c> / <c>cimiimport.exe</c> at
/// runtime, not via type-level coupling.
///
/// <b>Drift contract:</b> on-disk pkginfo/manifest/catalog files are
/// authoritative. If the upstream <c>YamlUtils</c> shape changes (key order,
/// quoting, literal-block handling, <c>_metadata</c> placement), update this
/// file to match — that's the canonical form deployment repos store. The
/// canary test in CimianStudio.Infrastructure.Tests guards against drift on
/// the sample-repo fixtures.
/// </summary>
public static class CimianYaml
{
    // Reader: CimianStudio tolerates unknown keys so older saves don't fail
    // when the upstream pkginfo schema grows.
    //
    // No WithNamingConvention(Underscored): YamlDotNet 16.3's
    // NamingConventionTypeInspector wraps YamlAttributesTypeInspector and
    // re-applies the convention to the already-aliased name. That silently
    // rewrites `[YamlMember(Alias="OnDemand")]` into `on_demand` on read.
    // Every Cimian YAML model already carries an explicit Alias on each
    // property, so dropping the convention is safe and fixes the bug.
    public static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    // Writer: OmitNull + OmitEmptyCollections matches the union of every
    // pre-consolidation SerializerBuilder in the cimian codebase. OmitDefaults
    // is deliberately off — pkginfo files in the wild always include
    // `unattended_install: false` explicitly, and OmitDefaults would drop them.
    // LiteralMultilineEmitter forces `|` style for any scalar containing `\n`
    // (YamlDotNet's default `>` folded style would mangle PowerShell scripts).
    public static ISerializer Serializer { get; } = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .WithIndentedSequences()
        .WithEventEmitter(next => new LiteralMultilineEmitter(next))
        .Build();

    // Pkginfo key priority. Anything not listed sorts alphabetically after
    // these three. `_metadata` is always last.
    private static readonly string[] PkgInfoPriorityKeys = ["name", "display_name", "version"];
    private const string MetadataKey = "_metadata";

    /// <summary>
    /// Serializes a pkginfo to the canonical Cimian form:
    /// name → display_name → version → (alphabetical) → _metadata last.
    /// </summary>
    public static string SerializePkgInfo<T>(T pkgInfo) where T : class
    {
        ArgumentNullException.ThrowIfNull(pkgInfo);

        // Normalize multi-line strings in script/description fields so YamlDotNet
        // emits clean `|` blocks (CRLF inside a literal scalar emits as CRLF
        // bytes, causing Windows-vs-Linux churn).
        NormalizeMultilineStrings(pkgInfo);

        var raw = Serializer.Serialize(pkgInfo);

        // If the model declares a `Metadata : Dictionary<string, object?>`
        // property (with [YamlIgnore]), splice its contents in as a
        // `_metadata:` block. Workaround for YamlDotNet 16.3's leading-
        // underscore alias regression — `[YamlMember(Alias="_metadata")]`
        // silently doesn't bind.
        var metadata = ExtractMetadataFromModel(pkgInfo);
        if (metadata is { Count: > 0 } md)
        {
            var metaYaml = Serializer.Serialize(new Dictionary<string, object?> { [MetadataKey] = md });
            raw = raw.TrimEnd('\n') + "\n" + metaYaml;
        }

        return ReorderTopLevelKeys(raw, PkgInfoPriorityKeys, MetadataKey);
    }

    public static T? DeserializePkgInfo<T>(string yaml) where T : class
    {
        if (string.IsNullOrEmpty(yaml)) return null;
        var pkg = Deserializer.Deserialize<T>(yaml);
        if (pkg is null) return null;

        var metadata = ExtractMetadataBlock(yaml);
        if (metadata is { Count: > 0 })
        {
            AssignMetadataToModel(pkg, metadata);
        }
        return pkg;
    }

    /// <summary>
    /// Serializes a manifest, normalizing <c>included_manifests</c> entries
    /// from Windows backslashes to forward slashes — manifests are URL paths
    /// consumed by Cimian agents on every platform.
    /// </summary>
    public static string SerializeManifest<T>(T manifest) where T : class
    {
        ArgumentNullException.ThrowIfNull(manifest);
        NormalizeIncludedManifestPaths(manifest);
        return Serializer.Serialize(manifest);
    }

    public static T? DeserializeManifest<T>(string yaml) where T : class
    {
        if (string.IsNullOrEmpty(yaml)) return null;
        var m = Deserializer.Deserialize<T>(yaml);
        if (m is not null) NormalizeIncludedManifestPaths(m);
        return m;
    }

    public static string SerializeCatalog<T>(T catalog) where T : class
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Serializer.Serialize(catalog);
    }

    public static T? DeserializeCatalog<T>(string yaml) where T : class
    {
        if (string.IsNullOrEmpty(yaml)) return null;
        return Deserializer.Deserialize<T>(yaml);
    }

    /// <summary>
    /// Extracts the <c>_metadata</c> block from raw YAML as a CLR dictionary,
    /// preserving original key order. Canonical workaround for YamlDotNet 16.3
    /// silently dropping any <c>[YamlMember(Alias = "_metadata")]</c> binding.
    /// </summary>
    public static Dictionary<string, object?>? ExtractMetadataBlock(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException)
        {
            return null;
        }

        if (stream.Documents.Count == 0) return null;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return null;

        foreach (var kvp in root.Children)
        {
            if (kvp.Key is YamlScalarNode keyScalar && keyScalar.Value == MetadataKey)
            {
                if (kvp.Value is YamlMappingNode metaMap)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var entry in metaMap.Children)
                    {
                        if (entry.Key is YamlScalarNode k && k.Value is not null)
                        {
                            dict[k.Value] = ConvertNode(entry.Value);
                        }
                    }
                    return dict;
                }
                return null;
            }
        }
        return null;
    }

    private static object? ConvertNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode s:
                return s.Value;
            case YamlSequenceNode seq:
                var list = new List<object?>();
                foreach (var item in seq.Children) list.Add(ConvertNode(item));
                return list;
            case YamlMappingNode map:
                var dict = new Dictionary<string, object?>();
                foreach (var entry in map.Children)
                {
                    if (entry.Key is YamlScalarNode k && k.Value is not null)
                        dict[k.Value] = ConvertNode(entry.Value);
                }
                return dict;
            default:
                return null;
        }
    }

    // Reorders the top-level mapping so priority keys come first (in given
    // order), then everything else alphabetically, then the trailing key
    // (e.g. _metadata) last if present. Parsing via YamlStream preserves
    // nested structure; comments would be lost either way.
    private static string ReorderTopLevelKeys(string yaml, string[] priority, string trailing)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }
        if (stream.Documents.Count == 0) return yaml;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return yaml;

        var byKey = new Dictionary<string, KeyValuePair<YamlNode, YamlNode>>(StringComparer.Ordinal);
        foreach (var kvp in root.Children)
        {
            if (kvp.Key is YamlScalarNode k && k.Value is not null)
            {
                // Skip empty-string scalar values (e.g. `description: ""`).
                // OmitNull doesn't catch these because `""` isn't null, but
                // pre-consolidation cimiimport guarded every field with
                // `!string.IsNullOrEmpty(...)`. Suppress them here so models
                // can keep non-nullable string defaults without leaking empty
                // keys into the output.
                if (kvp.Value is YamlScalarNode sv && string.IsNullOrEmpty(sv.Value))
                    continue;
                byKey[k.Value] = kvp;
            }
        }

        var ordered = new YamlMappingNode();
        foreach (var key in priority)
        {
            if (byKey.TryGetValue(key, out var kvp))
            {
                ordered.Add(kvp.Key, kvp.Value);
                byKey.Remove(key);
            }
        }

        var hasTrailing = byKey.Remove(trailing, out var trailingKvp);

        foreach (var key in byKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var kvp = byKey[key];
            ordered.Add(kvp.Key, kvp.Value);
        }

        if (hasTrailing)
        {
            ordered.Add(trailingKvp.Key, trailingKvp.Value);
        }

        var newDoc = new YamlDocument(ordered);
        var newStream = new YamlStream(newDoc);
        using var writer = new StringWriter();
        newStream.Save(writer, assignAnchors: false);
        // YamlStream.Save emits CRLF on Windows and appends a `...` document-
        // end marker — both unwanted. Normalize to `\n` and strip the marker
        // so output matches the rest of the Cimian YAML corpus.
        var result = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (result.EndsWith("...\n", StringComparison.Ordinal))
            result = result[..^4];
        else if (result.EndsWith("...", StringComparison.Ordinal))
            result = result[..^3];
        result = result.TrimEnd('\n') + "\n";
        return result;
    }

    // Walks direct string properties, normalizing CRLF to LF and collapsing
    // 3+ consecutive blank lines to 2. Keeps multiline `|` blocks clean across
    // Windows/Linux checkouts. Only processes the immediate object's
    // properties — nested objects aren't walked, but PkgsInfo script
    // properties are flat so this is sufficient for current callers.
    private static void NormalizeMultilineStrings(object? obj)
    {
        if (obj is null) return;
        var type = obj.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.PropertyType == typeof(string))
            {
                if (prop.GetValue(obj) is string s && (s.Contains('\r', StringComparison.Ordinal) || s.Contains("\n\n\n", StringComparison.Ordinal)))
                {
                    var normalized = s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                    while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
                        normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
                    prop.SetValue(obj, normalized);
                }
            }
        }
    }

    private static Dictionary<string, object?>? ExtractMetadataFromModel(object obj)
    {
        var prop = obj.GetType().GetProperty("Metadata", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.PropertyType != typeof(Dictionary<string, object?>)) return null;
        return prop.GetValue(obj) as Dictionary<string, object?>;
    }

    private static void AssignMetadataToModel(object obj, Dictionary<string, object?> metadata)
    {
        var prop = obj.GetType().GetProperty("Metadata", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.PropertyType != typeof(Dictionary<string, object?>) || !prop.CanWrite) return;
        prop.SetValue(obj, metadata);
    }

    private static void NormalizeIncludedManifestPaths(object obj)
    {
        var prop = obj.GetType().GetProperty("IncludedManifests", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(obj) is IList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is string s) list[i] = s.Replace('\\', '/');
            }
        }
    }

    /// <summary>
    /// Forces literal <c>|</c> style for any scalar containing a newline.
    /// YamlDotNet defaults to <c>&gt;</c> (folded), which collapses single
    /// newlines into spaces — fatal for embedded PowerShell scripts.
    /// </summary>
    private sealed class LiteralMultilineEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Source.Value is string s
                && s.Contains('\n', StringComparison.Ordinal))
            {
                eventInfo = new ScalarEventInfo(eventInfo.Source)
                {
                    Style = ScalarStyle.Literal,
                    IsPlainImplicit = false,
                    IsQuotedImplicit = false,
                };
            }
            base.Emit(eventInfo, emitter);
        }
    }
}
