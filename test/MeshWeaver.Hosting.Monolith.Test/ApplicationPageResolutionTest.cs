using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Reproduces the ApplicationPage behavior:
/// NavigationService.ProcessLocationChangeAsync calls IPathResolver.ResolvePathAsync(path)
/// to resolve the URL path to an address. When this returns null, the page shows "Page Not Found".
/// These tests verify that FutuRe paths resolve correctly.
/// </summary>
public class ApplicationPageResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddFutuRe()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Emulates ApplicationPage navigating to "FutuRe".
    /// This is the exact flow: NavigationService.InitializeAsync() -> ProcessLocationChangeAsync("FutuRe")
    /// -> _meshCatalog.ResolvePathAsync("FutuRe").
    /// When this returns null, the page shows "Page Not Found".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ResolvePathAsync_FutuRe_ShouldNotReturnNull()
    {
        var resolution = await PathResolver.ResolvePathAsync("FutuRe");

        Output.WriteLine($"Resolution: {resolution?.Prefix ?? "NULL"}, Remainder: {resolution?.Remainder ?? "NULL"}");
        resolution.Should().NotBeNull("FutuRe should resolve — it has an index.md in the data directory");
        resolution!.Prefix.Should().Be("FutuRe");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Tests resolution of FutuRe child paths as ApplicationPage would encounter them.
    /// </summary>
    [Theory(Timeout = 10000)]
    [InlineData("FutuRe/EuropeRe", "FutuRe/EuropeRe", null)]
    [InlineData("FutuRe/Analysis", "FutuRe/Analysis", null)]
    [InlineData("FutuRe/EuropeRe/LineOfBusiness", "FutuRe/EuropeRe/LineOfBusiness", null)]
    [InlineData("FutuRe/EuropeRe/Overview", "FutuRe/EuropeRe", "Overview")]
    public async Task ResolvePathAsync_FutuReSubPaths_ShouldResolve(
        string path, string expectedPrefix, string? expectedRemainder)
    {
        var resolution = await PathResolver.ResolvePathAsync(path);

        Output.WriteLine($"Path: {path} => Prefix: {resolution?.Prefix ?? "NULL"}, Remainder: {resolution?.Remainder ?? "NULL"}");
        resolution.Should().NotBeNull($"'{path}' should resolve to a valid address");
        resolution!.Prefix.Should().Be(expectedPrefix);
        resolution.Remainder.Should().Be(expectedRemainder);
    }

    /// <summary>
    /// Verifies that truly unknown paths still return null (error case is correct).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ResolvePathAsync_UnknownPath_ShouldReturnNull()
    {
        var resolution = await PathResolver.ResolvePathAsync("CompletelyUnknownPath");

        resolution.Should().BeNull("unknown paths should return null");
    }
}
