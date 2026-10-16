using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Offline-first NuGet resolution (#6863): a package closure already installed in the package
/// folder resolves with NO feed call, so a restarted replica loads a recorded build during a feed
/// outage. Each test runs against a private package folder holding hand-laid v3 packages and a
/// single configured feed that refuses every connection — so any feed call fails the test. The
/// negative control takes one package of the closure away and requires the named error that says
/// what was missing and which feed could not be reached.
/// </summary>
public sealed class NuGetOfflineFirstResolutionTest : IDisposable
{
    private const string UnreachableFeed = "http://127.0.0.1:9/v3/index.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-nuget-offline-" + Guid.NewGuid().ToString("N"));
    private string PackagesFolder => Path.Combine(root, "packages");

    public NuGetOfflineFirstResolutionTest()
    {
        Directory.CreateDirectory(PackagesFolder);
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="unreachable" value="{UnreachableFeed}" />
              </packageSources>
            </configuration>
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private NuGetAssemblyResolver ColdResolver() => new(
        NullLogger<NuGetAssemblyResolver>.Instance,
        Settings.LoadSpecificSettings(root, "nuget.config"),
        PackagesFolder);

    /// <summary>Lays a package out exactly as a v3 restore leaves it, completion marker included.</summary>
    private void Install(string id, string version, string? dependsOnId = null, string? dependsOnVersion = null)
    {
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
                <id>{id}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>offline-first fixture</description>
                {dependencies}
              </metadata>
            </package>
            """);
        File.WriteAllBytes(Path.Combine(folder, "lib", "net10.0", id + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(folder, ".nupkg.metadata"),
            """{ "version": 2, "contentHash": "fixture", "source": null }""");
    }

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
            .And.Contain(UnreachableFeed);
    }
}
