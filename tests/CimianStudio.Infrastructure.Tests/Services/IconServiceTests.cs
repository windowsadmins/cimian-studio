namespace CimianStudio.Infrastructure.Tests.Services;

using System.Security.Cryptography;
using System.Xml;
using CimianStudio.Infrastructure.Services;
using FluentAssertions;

public sealed class IconServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly RepositoryService _repoService;
    private readonly IconService _iconService;

    public IconServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "CimianStudioIconTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "catalogs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "pkgsinfo"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "pkgs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "icons"));

        _repoService = new RepositoryService();
        _iconService = new IconService(_repoService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSupportedIconsRecursively()
    {
        var iconsDir = Path.Combine(_tempRoot, "icons");
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "Top.png"), [1, 2, 3]);
        var subDir = Path.Combine(iconsDir, "vendor");
        Directory.CreateDirectory(subDir);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "Nested.jpg"), [4, 5]);
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "skip.txt"), [9]);

        await _repoService.OpenRepositoryAsync(_tempRoot);
        var icons = await _iconService.ListAsync();

        icons.Should().HaveCount(2);
        icons.Select(i => i.Filename).Should().BeEquivalentTo("Top.png", "vendor/Nested.jpg");
        icons.Single(i => i.Filename == "Top.png").ByteSize.Should().Be(3);
        icons.Should().BeInAscendingOrder(i => i.Filename, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_WithoutIconsDirectory_ReturnsEmpty()
    {
        Directory.Delete(Path.Combine(_tempRoot, "icons"), recursive: true);
        await _repoService.OpenRepositoryAsync(_tempRoot);

        var icons = await _iconService.ListAsync();
        icons.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildHashesAsync_WritesPlist_WithLowercaseHexSha256()
    {
        var iconsDir = Path.Combine(_tempRoot, "icons");
        var payload = "hello icons"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "AppA.png"), payload);

        await _repoService.OpenRepositoryAsync(_tempRoot);
        var result = await _iconService.RebuildHashesAsync();

        result.Success.Should().BeTrue();
        result.HashedCount.Should().Be(1);

        var plistPath = Path.Combine(iconsDir, "_icon_hashes.plist");
        File.Exists(plistPath).Should().BeTrue();

        // Read it back as an XML dict and confirm the recorded hash matches the
        // canonical SHA-256 of the file. That round-trips both the writer's
        // format and the analyzer's existing-hash loader at once.
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var actual = ReadPlistDict(plistPath);
        actual.Should().ContainKey("AppA.png").WhoseValue.Should().Be(expectedHash);
    }

    [Fact]
    public async Task RebuildHashesAsync_ExcludesNonImageFilesAndThePlistItself()
    {
        var iconsDir = Path.Combine(_tempRoot, "icons");
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "Keep.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "Skip.txt"), [2]);

        await _repoService.OpenRepositoryAsync(_tempRoot);
        var first = await _iconService.RebuildHashesAsync();
        first.Success.Should().BeTrue();
        first.HashedCount.Should().Be(1);

        // Second pass must not see the just-written plist as an icon.
        var second = await _iconService.RebuildHashesAsync();
        second.HashedCount.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_PopulatesSha256FromExistingPlist()
    {
        var iconsDir = Path.Combine(_tempRoot, "icons");
        await File.WriteAllBytesAsync(Path.Combine(iconsDir, "AppA.png"), "x"u8.ToArray());

        await _repoService.OpenRepositoryAsync(_tempRoot);
        var rebuild = await _iconService.RebuildHashesAsync();
        rebuild.Success.Should().BeTrue();

        var icons = await _iconService.ListAsync();
        icons.Single().Sha256.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReadBytesAsync_RejectsPathEscape()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "secret.txt"), "no"u8.ToArray());
        await _repoService.OpenRepositoryAsync(_tempRoot);

        var act = () => _iconService.ReadBytesAsync("../secret.txt");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Dictionary<string, string> ReadPlistDict(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        string? pendingKey = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
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
}
