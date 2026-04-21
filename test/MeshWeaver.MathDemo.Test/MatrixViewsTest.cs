using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.MathDemo.Test;

/// <summary>
/// End-to-end test for the MathDemo/Matrix sample. Exercises dynamic node-type compilation
/// with a <c>#r "nuget:MathNet.Numerics, ..."</c> directive and renders the compiled
/// <c>Inverse</c> layout area against a real client — the same path the portal uses.
///
/// Requires network access to api.nuget.org on first run; the persistent NuGet cache makes
/// subsequent runs instant.
/// </summary>
public class MatrixViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("MESHWEAVER_SKIP_NUGET") == "1";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverMathDemoTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

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
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 120_000)]
    public async Task Inverse_ShouldRenderForMatrixExample()
    {
        if (ShouldSkip) return;

        var client = GetClient();
        var address = new Address("MathDemo/Matrix/Example");

        // Initialize the hub first — required for routing to hit the per-node hub.
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Inverse");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        Output.WriteLine("Waiting for Inverse view to render (cold run resolves MathNet.Numerics from NuGet)…");
        var value = await stream.Timeout(TimeSpan.FromSeconds(90)).FirstAsync();

        Output.WriteLine("Inverse view rendered.");
        value.Should().NotBe(default(JsonElement),
            "Inverse layout area should render — proves #r \"nuget:MathNet.Numerics, ...\" is resolved " +
            "and MatrixLayoutAreas.Inverse executed against the sample instance.");
    }
}
