namespace CimianStudio.Core.Services;

using CimianStudio.Core.Models.Icons;

/// <summary>
/// Read/write access to the repository's <c>icons/</c> directory plus the
/// <c>_icon_hashes.plist</c> manifest that <c>makecatalogs</c> generates.
/// </summary>
public interface IIconService
{
    /// <summary>
    /// Lists every <c>.png</c> / <c>.jpg</c> / <c>.jpeg</c> / <c>.icns</c> file under the
    /// current repository's icons directory, recursively. Returns an empty list if
    /// no repository is open or the icons directory doesn't exist yet.
    /// </summary>
    Task<IReadOnlyList<IconAsset>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the raw bytes for a single icon. <paramref name="filename"/> is the
    /// repo-relative path returned by <see cref="ListAsync"/>.
    /// </summary>
    Task<byte[]> ReadBytesAsync(string filename, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes SHA-256 for every icon and writes <c>_icon_hashes.plist</c> at the
    /// root of the icons directory. The plist shape mirrors Munki's
    /// <c>makecatalogs</c> output so existing tooling stays compatible.
    /// </summary>
    /// <param name="progress">Reports number of icons hashed so far; safe to ignore.</param>
    Task<IconHashRebuildResult> RebuildHashesAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Raised after a hashes rebuild completes successfully.</summary>
    event EventHandler? IconHashesChanged;
}

/// <summary>Outcome of <see cref="IIconService.RebuildHashesAsync"/>.</summary>
public sealed class IconHashRebuildResult
{
    public bool Success { get; init; }

    public int HashedCount { get; init; }

    public string? PlistPath { get; init; }

    public string? ErrorMessage { get; init; }
}
