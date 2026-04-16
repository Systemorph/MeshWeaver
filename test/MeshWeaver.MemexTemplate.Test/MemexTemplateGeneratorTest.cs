using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MeshWeaver.MemexTemplate.Test;

public class MemexTemplateGeneratorTest : IDisposable
{
    private readonly string _outputPath;

    public MemexTemplateGeneratorTest()
    {
        _outputPath = Path.Combine(Path.GetTempPath(), $"memex-template-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputPath))
            Directory.Delete(_outputPath, recursive: true);
    }

    private string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private string GeneratorScript =>
        Path.Combine(RepoRoot, "tools", "generate-memex-template.cs");

    private async Task RunGenerator(string version = "0.0.0-test")
    {
        var psi = new ProcessStartInfo("dotnet", $"run \"{GeneratorScript}\" -- {version} \"{RepoRoot}\" \"{_outputPath}\"")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0, $"generator failed.\nstdout: {stdout}\nstderr: {stderr}");
        _lastStdout = stdout;
        _lastStderr = stderr;
    }

    private string _lastStdout = "";
    private string _lastStderr = "";

    [Fact]
    public async Task GeneratorProducesExpectedStructure()
    {
        await RunGenerator();

        // Core project dirs exist
        Directory.Exists(Path.Combine(_outputPath, "Memex.Portal.Monolith")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputPath, "Memex.Portal.Shared")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputPath, "aspire", "Memex.AppHost")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputPath, "aspire", "Memex.Database.Migration")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputPath, "aspire", "Memex.Portal.Distributed")).Should().BeTrue();
        Directory.Exists(Path.Combine(_outputPath, "aspire", "Memex.Portal.ServiceDefaults")).Should().BeTrue();

        // Static files
        File.Exists(Path.Combine(_outputPath, "nuget.config")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "Directory.Build.props")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "Directory.Packages.props")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "Memex.slnx")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, ".template.config", "template.json")).Should().BeTrue();
    }

    [Fact]
    public async Task ProjectReferencesRewrittenToPackageReferences()
    {
        await RunGenerator();

        var monolithCsproj = Path.Combine(_outputPath, "Memex.Portal.Monolith", "Memex.Portal.Monolith.csproj");
        var csprojContent = File.ReadAllText(monolithCsproj);
        var doc = XDocument.Load(monolithCsproj);

        // Should have PackageReference for MeshWeaver.Hosting.Monolith (was ProjectReference to src/)
        doc.Descendants("PackageReference")
            .Any(e => e.Attribute("Include")?.Value == "MeshWeaver.Hosting.Monolith")
            .Should().BeTrue($"MeshWeaver.Hosting.Monolith should be rewritten from ProjectReference to PackageReference.\nActual csproj content:\n{csprojContent}");

        // Should NOT have any ProjectReference pointing to ../../src/
        doc.Descendants("ProjectReference")
            .Where(e => e.Attribute("Include")?.Value?.Contains("/src/MeshWeaver.") == true
                     || e.Attribute("Include")?.Value?.Contains("\\src\\MeshWeaver.") == true)
            .Should().BeEmpty("all src/ ProjectReferences should be rewritten to PackageReferences");

        // Should NOT have any ProjectReference pointing to samples/
        doc.Descendants("ProjectReference")
            .Where(e => e.Attribute("Include")?.Value?.Contains("samples") == true)
            .Should().BeEmpty("sample ProjectReferences should be removed");

        // Internal memex refs should remain as ProjectReference
        doc.Descendants("ProjectReference")
            .Any(e => e.Attribute("Include")?.Value?.Contains("Memex.Portal.Shared") == true)
            .Should().BeTrue("internal memex ProjectReferences should be preserved");
    }

    [Fact]
    public async Task DirectoryPackagesPropsContainsMeshWeaverAndThirdPartyVersions()
    {
        const string version = "1.2.3-test";
        await RunGenerator(version);

        var propsPath = Path.Combine(_outputPath, "Directory.Packages.props");
        var doc = XDocument.Load(propsPath);

        var packageVersions = doc.Descendants("PackageVersion")
            .ToDictionary(
                e => e.Attribute("Include")!.Value,
                e => e.Attribute("Version")!.Value);

        // MeshWeaver packages should use the generator version
        var propsContent = File.ReadAllText(propsPath);
        packageVersions.Should().ContainKey("MeshWeaver.Hosting.Monolith",
            $"generated Directory.Packages.props should include MeshWeaver packages.\nstdout: {_lastStdout}\nstderr: {_lastStderr}\nProps content:\n{propsContent}");
        packageVersions["MeshWeaver.Hosting.Monolith"].Should().Be(version);

        // Third-party packages should have real versions from root Directory.Packages.props
        packageVersions.Should().ContainKey("Aspire.Hosting.AppHost");
        packageVersions["Aspire.Hosting.AppHost"].Should().NotBeEmpty()
            .And.NotBe(version, "third-party versions come from root Directory.Packages.props, not the generator version arg");
    }

    [Fact]
    public async Task AspireSdkVersionSyncedWithPackageVersion()
    {
        await RunGenerator();

        var appHostCsproj = Path.Combine(_outputPath, "aspire", "Memex.AppHost", "Memex.AppHost.csproj");
        var doc = XDocument.Load(appHostCsproj);

        var sdkVersion = doc.Descendants("Sdk")
            .First(e => e.Attribute("Name")?.Value == "Aspire.AppHost.Sdk")
            .Attribute("Version")!.Value;

        var propsPath = Path.Combine(_outputPath, "Directory.Packages.props");
        var propsDoc = XDocument.Load(propsPath);
        var packageVersion = propsDoc.Descendants("PackageVersion")
            .First(e => e.Attribute("Include")?.Value == "Aspire.Hosting.AppHost")
            .Attribute("Version")!.Value;

        sdkVersion.Should().Be(packageVersion,
            "Aspire.AppHost.Sdk Version must match Aspire.Hosting.AppHost PackageVersion to avoid drift");
    }

    [Fact]
    public async Task SampleDataIncludedWithExclusions()
    {
        await RunGenerator();

        // Users present (DevLogin requires these)
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Alice.json")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Bob.json")).Should().BeTrue();
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "TestUser.json")).Should().BeTrue();

        // Roland and Samuel excluded
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Roland.json")).Should().BeFalse();
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Samuel.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Roland")).Should().BeFalse();
        Directory.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "User", "Samuel")).Should().BeFalse();

        // ACME access — included minus Roland/Samuel
        var accessDir = Path.Combine(_outputPath, "samples", "Graph", "Data", "ACME", "_Access");
        File.Exists(Path.Combine(accessDir, "Alice_Access.json")).Should().BeTrue();
        File.Exists(Path.Combine(accessDir, "Roland_Access.json")).Should().BeFalse();
        File.Exists(Path.Combine(accessDir, "Samuel_Access.json")).Should().BeFalse();

        // ACME users present
        File.Exists(Path.Combine(_outputPath, "samples", "Graph", "Data", "ACME", "User", "Oliver.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AppSettingsDataPathFixedForTemplateLayout()
    {
        await RunGenerator();

        var appSettings = File.ReadAllText(
            Path.Combine(_outputPath, "Memex.Portal.Monolith", "appsettings.Development.json"));

        // Should use single ../ (template layout) not double ../../ (memex/ repo layout)
        appSettings.Should().Contain("../samples/Graph");
        appSettings.Should().NotContain("../../samples/Graph");
    }

    [Fact]
    public async Task SlnxHasCorrectFolderSyntax()
    {
        await RunGenerator();

        var slnx = File.ReadAllText(Path.Combine(_outputPath, "Memex.slnx"));

        // MSBuild requires /folder/ syntax for solution folders
        slnx.Should().Contain("Name=\"/aspire/\"");
        slnx.Should().NotContain("Name=\"aspire\"",
            "unslashed folder names cause MSBuild parse errors");
    }
}
