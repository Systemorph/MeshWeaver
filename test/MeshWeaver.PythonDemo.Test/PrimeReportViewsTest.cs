using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
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

namespace MeshWeaver.PythonDemo.Test;

/// <summary>
/// End-to-end test for the PythonDemo/PrimeReport sample (the "Calling Python from MeshWeaver"
/// documentation walkthrough). Compiles the node type from Source/, renders the <c>Report</c>
/// layout area against a real client, and asserts the graceful-degradation contract:
/// with python3 on PATH the area renders the Python-computed prime table; without it the area
/// renders the informative notice — it never errors either way.
/// </summary>
public class PrimeReportViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverPythonDemoTests",
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

    /// <summary>Mirrors the sample's PATH probe so the test knows which outcome to expect.</summary>
    private static bool PythonAvailable()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "python3.exe", "python.exe" }
            : new[] { "python3" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .Any(File.Exists);
    }

    [Fact(Timeout = 120_000)]
    public async Task Report_ShouldRenderPythonOutputOrGracefulNotice()
    {
        var pythonAvailable = PythonAvailable();
        // With python3: the Python script prints "### First 25 primes — computed by Python x.y.z".
        // "computed by Python" can ONLY come from the script's stdout (the node name alone,
        // "First 25 primes", could leak into area metadata without Python ever running).
        // Without python3: the area renders the informative notice instead of erroring.
        var expectedMarker = pythonAvailable
            ? "computed by Python"
            : "Python is not available on this host";

        var client = GetClient();
        var address = new Address("PythonDemo/PrimeReport/First25");

        // Initialize the hub first — required for routing to hit the per-node hub.
        await client.Observe(new PingRequest(), o => o.WithTarget(address))
            .Should().Within(TimeSpan.FromSeconds(90)).Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Report");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        Output.WriteLine($"python3 on PATH: {pythonAvailable}; waiting for Report to render '{expectedMarker}'…");
        await stream.Should().Within(TimeSpan.FromSeconds(90))
            .Match(item => item.Value.ValueKind != JsonValueKind.Undefined
                           && item.Value.GetRawText().Contains(expectedMarker),
                $"Report layout area should render '{expectedMarker}' — proves the node type compiled " +
                "and PrimeReportLayoutAreas.Report executed python3 through the Process IIoPool " +
                "(or degraded gracefully when python3 is absent).");

        Output.WriteLine("Report view rendered with the expected content.");
    }
}
