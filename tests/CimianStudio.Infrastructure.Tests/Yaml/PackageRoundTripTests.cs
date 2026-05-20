namespace CimianStudio.Infrastructure.Tests.Yaml;

using CimianStudio.Infrastructure.Yaml;
using CimianStudio.Core.Models.Packages;
using FluentAssertions;

public class PackageRoundTripTests
{
    [Fact]
    public void Firefox_Sample_DeserializesAndPreservesCoreFields()
    {
        var path = Path.Combine(TestPaths.SampleRepository, "pkgsinfo", "Mozilla", "Firefox.yaml");
        var text = File.ReadAllText(path);

        var pkg = CimianYaml.DeserializePkgInfo<Package>(text);

        pkg.Should().NotBeNull();
        pkg!.Name.Should().Be("firefox");
        pkg.DisplayName.Should().Be("Mozilla Firefox");
        pkg.Version.Should().Be("120.0");
        pkg.Catalogs.Should().BeEquivalentTo("testing", "production");
        pkg.Installer.Should().NotBeNull();
        pkg.Installer!.Type.Should().Be("exe");
        pkg.Installer.Arguments.Should().BeEquivalentTo("/S");
        pkg.Installs.Should().NotBeNull().And.HaveCount(1);
        pkg.Installs![0].Path.Should().Contain("firefox.exe");
        pkg.SupportedArchitectures.Should().BeEquivalentTo("x64", "x86");
        pkg.BlockingApplications.Should().BeEquivalentTo("firefox.exe");
    }

    [Fact]
    public void Firefox_Sample_RoundTripsWithoutDataLoss()
    {
        var path = Path.Combine(TestPaths.SampleRepository, "pkgsinfo", "Mozilla", "Firefox.yaml");
        var original = CimianYaml.DeserializePkgInfo<Package>(File.ReadAllText(path))!;

        var yaml = CimianYaml.SerializePkgInfo(original);
        var roundTripped = CimianYaml.DeserializePkgInfo<Package>(yaml)!;

        roundTripped.Should().BeEquivalentTo(original, options => options
            .Excluding(p => p.FilePath)
            .Excluding(p => p.LastModified));
    }
}
