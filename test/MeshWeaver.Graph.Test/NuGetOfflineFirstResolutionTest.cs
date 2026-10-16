using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Versioning;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Offline-first NuGet resolution (#6863): a package closure already installed in the package
/// folder resolves with NO feed call, so a restarted replica loads a recorded build during a feed
/// outage. Each test runs against a private package folder holding hand-laid v3 packages. The first
/// configured feed refuses every connection, so any call to it fails. Some tests add a second,
/// local folder feed that really serves a package. The negative control takes one package of the
/// closure away and requires the named error that says what was missing and which feed failed.
/// </summary>
public sealed class NuGetOfflineFirstResolutionTest : IDisposable
{
    private const string UnreachableFeed = "http://127.0.0.1:9/v3/index.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-nuget-offline-" + Guid.NewGuid().ToString("N"));
    private string PackagesFolder => Path.Combine(root, "packages");
    private string LocalFeed => Path.Combine(root, "feed");

    public NuGetOfflineFirstResolutionTest()
    {
        Directory.CreateDirectory(PackagesFolder);
        WriteConfig(withLocalFeed: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>The unreachable feed always comes FIRST; the local feed, when present, second.</summary>
    private void WriteConfig(bool withLocalFeed)
    {
        Directory.CreateDirectory(LocalFeed);
        var local = withLocalFeed ? $"""<add key="local" value="{LocalFeed}" />""" : string.Empty;
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="unreachable" value="{UnreachableFeed}" />
                {local}
              </packageSources>
            </configuration>
            """);
    }

    private NuGetAssemblyResolver ColdResolver() => new(
        NullLogger<NuGetAssemblyResolver>.Instance,
        Settings.LoadSpecificSettings(root, "nuget.config"),
        PackagesFolder);

    /// <summary>
    /// Lays a package folder out as a v3 restore leaves it. <paramref name="declaredId"/> lets the
    /// nuspec and the dll claim ANOTHER package (a poisoned folder); <paramref name="complete"/>
    /// false leaves out the completion marker and the dll (a half-extracted folder).
    /// </summary>
    private void Install(string id, string version, string? dependsOnId = null, string? dependsOnVersion = null,
        string? declaredId = null, bool complete = true)
    {
        var claimed = declaredId ?? id;
        var folder = Path.Combine(PackagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
        Directory.CreateDirectory(Path.Combine(folder, "lib", "net10.0"));
        var dependencies = dependsOnId is null
            ? string.Empty
            : $"""
              <dependencies>
                <group targetFramework="net8.0">
                  <dependency id="{dependsOnId}" version="{dependsOnVersion}" />
                </group>
              </dependencies>
              """;
        File.WriteAllText(Path.Combine(folder, id.ToLowerInvariant() + ".nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{claimed}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>offline-first fixture</description>
                {dependencies}
              </metadata>
            </package>
            """);
        if (!complete) return;
        File.WriteAllBytes(Path.Combine(folder, "lib", "net10.0", claimed + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(folder, ".nupkg.metadata"),
            """{ "version": 2, "contentHash": "fixture", "source": null }""");
    }

    /// <summary>Publishes a real .nupkg of <paramref name="id"/> into the local folder feed.</summary>
    private void Publish(string id, string version)
    {
        var dll = Path.Combine(root, id + ".dll");
        File.WriteAllBytes(dll, [0x4D, 0x5A]);
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = "offline-first fixture",
        };
        builder.Authors.Add("test");
        builder.Files.Add(new PhysicalPackageFile { SourcePath = dll, TargetPath = $"lib/net10.0/{id}.dll" });
        using var stream = File.Create(Path.Combine(LocalFeed, $"{id.ToLowerInvariant()}.{version}.nupkg"));
        builder.Save(stream);
    }

    private string InstallFolder(string id, string version) =>
        Path.Combine(PackagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());

    [Fact(Timeout = 30_000)]
    public async Task WarmFolder_UnreachableFeed_ResolvesTheWholeClosureFromTheFolder()
    {
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.ResolvedVersions.Should().ContainKey("Acme.Offline.A").WhoseValue.Should().Be("1.0.0");
        result.ResolvedVersions.Should().ContainKey("Acme.Offline.B").WhoseValue.Should().Be("1.0.0");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.A.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().OnlyContain(p => p.StartsWith(PackagesFolder, StringComparison.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task ColdDependency_UnreachableFeed_FailsNamingThePackageAndTheFeed()
    {
        // The root is warm; its dependency is not — so the feed is the only way to resolve it.
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");

        var act = () => ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Acme.Offline.B 1.0.0")
            .And.Contain("is not installed")
            .And.Contain("failed")
            .And.Contain($"'{UnreachableFeed}'");
    }

    /// <summary>
    /// One failing feed is not the answer while another configured feed holds the package: the
    /// unreachable feed is listed first, and the local feed behind it must still be asked.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FirstFeedFails_SecondFeedServesTheMissingPackage()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.ResolvedVersions.Should().ContainKey("Acme.Offline.B").WhoseValue.Should().Be("1.0.0");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// A half-extracted folder (no completion marker, no assembly) is refused by the walk AND by the
    /// install: it is reinstalled from the feed, never handed to asset selection as it stands.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task IncompleteFolder_IsReinstalledFromTheFeed()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", complete: false);
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        File.Exists(Path.Combine(InstallFolder("Acme.Offline.B", "1.0.0"), ".nupkg.metadata"))
            .Should().BeTrue("the repaired folder is a complete v3 install");
    }

    /// <summary>
    /// A complete-looking folder whose nuspec and assembly belong to ANOTHER package is refused by
    /// the walk AND by the install: the right package is installed in its place, and the wrong
    /// assembly never reaches the compile set.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FolderHoldingAnotherPackage_IsReinstalledFromTheFeed()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", declaredId: "Acme.Intruder");
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().NotContain(p => p.EndsWith("Acme.Intruder.dll", StringComparison.Ordinal));
        Directory.EnumerateFiles(InstallFolder("Acme.Offline.B", "1.0.0"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Should().Equal("Acme.Offline.B.dll");
    }
}
