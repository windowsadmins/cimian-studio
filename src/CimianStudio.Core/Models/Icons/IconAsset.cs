namespace CimianStudio.Core.Models.Icons;

/// <summary>
/// One icon file in a repository's <c>icons/</c> directory. <see cref="Filename"/> is
/// relative to that directory and preserves any subfolder structure (e.g.
/// <c>vendor/AcmeApp.png</c>) so it can be used as a stable identity and as the
/// key when (re)building <c>_icon_hashes.plist</c>.
/// </summary>
public sealed class IconAsset
{
    public string Filename { get; init; } = string.Empty;

    public long ByteSize { get; init; }

    public string? Sha256 { get; init; }

    public DateTime? LastModifiedUtc { get; init; }
}
