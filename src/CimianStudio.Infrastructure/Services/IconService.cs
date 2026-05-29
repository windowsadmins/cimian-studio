namespace CimianStudio.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Xml;
using CimianStudio.Core.Models.Icons;
using CimianStudio.Core.Services;

/// <summary>
/// Filesystem-backed <see cref="IIconService"/>. Walks <c>icons/</c> recursively,
/// reads bytes on demand, and emits the same <c>_icon_hashes.plist</c> shape
/// Munki's <c>makecatalogs</c> produces (a single top-level dict keyed by the
/// repo-relative icon filename, value = lowercase hex SHA-256).
/// </summary>
public sealed class IconService : IIconService
{
    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".icns"];

    private const string PlistFilename = "_icon_hashes.plist";

    private readonly IRepositoryService _repositoryService;

    public IconService(IRepositoryService repositoryService)
    {
        ArgumentNullException.ThrowIfNull(repositoryService);
        _repositoryService = repositoryService;
    }

    public event EventHandler? IconHashesChanged;

    public Task<IReadOnlyList<IconAsset>> ListAsync(CancellationToken cancellationToken = default)
    {
        var repo = _repositoryService.CurrentRepository;
        if (repo is null || !Directory.Exists(repo.IconsPath))
        {
            return Task.FromResult<IReadOnlyList<IconAsset>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hashes = TryLoadExistingHashes(repo.IconsPath);

        var iconsRoot = repo.IconsPath;
        var assets = new List<IconAsset>();
        foreach (var file in Directory.EnumerateFiles(iconsRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupported(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(iconsRoot, file).Replace('\\', '/');
            // The hashes plist is bookkeeping, not an asset — never surface it.
            if (string.Equals(relative, PlistFilename, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(file);
            assets.Add(new IconAsset
            {
                Filename = relative,
                ByteSize = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                Sha256 = hashes.TryGetValue(relative, out var h) ? h : null,
            });
        }

        IReadOnlyList<IconAsset> sorted =
            [.. assets.OrderBy(a => a.Filename, StringComparer.OrdinalIgnoreCase)];
        return Task.FromResult(sorted);
    }

    public async Task<byte[]> ReadBytesAsync(string filename, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var path = RequireSafeIconPath(filename);
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IconHashRebuildResult> RebuildHashesAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repo = _repositoryService.CurrentRepository;
        if (repo is null)
        {
            return new IconHashRebuildResult
            {
                Success = false,
                ErrorMessage = "No repository is currently open.",
            };
        }

        if (!Directory.Exists(repo.IconsPath))
        {
            return new IconHashRebuildResult
            {
                Success = false,
                ErrorMessage = $"Icons directory not found: {repo.IconsPath}",
            };
        }

        var iconsRoot = repo.IconsPath;
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashed = 0;

        foreach (var file in Directory.EnumerateFiles(iconsRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupported(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(iconsRoot, file).Replace('\\', '/');
            if (string.Equals(relative, PlistFilename, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileStream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            byte[] hash;
            await using (fileStream.ConfigureAwait(false))
            {
                hash = await SHA256.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }
            hashes[relative] = Convert.ToHexStringLower(hash);
            hashed++;
            progress?.Report(hashed);
        }

        var plistPath = Path.Combine(iconsRoot, PlistFilename);
        try
        {
            await WritePlistAsync(plistPath, hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new IconHashRebuildResult
            {
                Success = false,
                HashedCount = hashed,
                PlistPath = plistPath,
                ErrorMessage = ex.Message,
            };
        }

        IconHashesChanged?.Invoke(this, EventArgs.Empty);
        return new IconHashRebuildResult
        {
            Success = true,
            HashedCount = hashed,
            PlistPath = plistPath,
        };
    }

    /// <summary>
    /// Verifies that <paramref name="filename"/> resolves to a path *inside* the
    /// repository's icons directory — refusing inputs like <c>../escape.png</c>
    /// or absolute paths so a malicious YAML field can't trick us into reading
    /// arbitrary disk locations.
    /// </summary>
    private string RequireSafeIconPath(string filename)
    {
        var repo = _repositoryService.CurrentRepository
            ?? throw new InvalidOperationException("No repository is currently open.");

        var iconsRoot = Path.GetFullPath(repo.IconsPath);
        var candidate = Path.GetFullPath(Path.Combine(iconsRoot, filename));
        if (!candidate.StartsWith(iconsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, iconsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Icon path escapes repository: {filename}");
        }
        return candidate;
    }

    private static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedExtensions.Any(s => s.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> TryLoadExistingHashes(string iconsRoot)
    {
        var plistPath = Path.Combine(iconsRoot, PlistFilename);
        if (!File.Exists(plistPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // The plist is a single top-level <dict> of <key>filename</key><string>hash</string>
        // pairs — flat and small. Parse with XmlReader to avoid pulling a plist library
        // for what's effectively grade-school XML.
        try
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = XmlReader.Create(plistPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            string? pendingKey = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                switch (reader.Name)
                {
                    case "key":
                        pendingKey = reader.ReadElementContentAsString();
                        break;
                    case "string" when pendingKey is not null:
                        result[pendingKey] = reader.ReadElementContentAsString();
                        pendingKey = null;
                        break;
                }
            }
            return result;
        }
        catch (XmlException)
        {
            // A corrupted plist shouldn't break the icons list — fall back to no
            // cached hashes; the user can regenerate via the toolbar button.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task WritePlistAsync(
        string plistPath,
        IDictionary<string, string> hashes,
        CancellationToken cancellationToken)
    {
        // Write to a temp sibling then atomically replace, so a crash mid-write
        // can't leave the repo with a half-written plist.
        var tempPath = plistPath + ".tmp";

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
        };

        var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (fileStream.ConfigureAwait(false))
        {
            var writer = XmlWriter.Create(fileStream, settings);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteStartDocumentAsync().ConfigureAwait(false);
                await writer.WriteDocTypeAsync(
                    "plist",
                    "-//Apple//DTD PLIST 1.0//EN",
                    "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
                    subset: null).ConfigureAwait(false);
                await writer.WriteStartElementAsync(prefix: null, "plist", ns: null).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(prefix: null, "version", ns: null, "1.0").ConfigureAwait(false);
                await writer.WriteStartElementAsync(prefix: null, "dict", ns: null).ConfigureAwait(false);
                foreach (var pair in hashes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteElementStringAsync(prefix: null, "key", ns: null, pair.Key).ConfigureAwait(false);
                    await writer.WriteElementStringAsync(prefix: null, "string", ns: null, pair.Value).ConfigureAwait(false);
                }
                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndDocumentAsync().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        File.Move(tempPath, plistPath, overwrite: true);
    }
}
