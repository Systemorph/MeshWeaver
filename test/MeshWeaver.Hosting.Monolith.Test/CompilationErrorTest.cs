using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests that compilation errors are surfaced as error controls in the Overview layout area.
/// </summary>
public class CompilationErrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string CacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverCompilationErrorTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(CacheDirectory);

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = CacheDirectory;
                    o.EnableDiskCache = true;
                });
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Seeds the in-memory persistence with a NodeType that has broken code,
    /// then verifies the Overview area returns an error control.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Overview_ShouldShowCompilationError_WhenCodeIsBroken()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var ct = TestContext.Current.CancellationToken;

        // 1. Create a NodeType definition with broken code
        var nodeTypePath = "type/BrokenType";
        var nodeTypeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = "Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition()
        };
        await persistence.SaveNodeAsync(nodeTypeNode, ct);

        // Code with a compile error: missing required parameter
        var codeNode = new MeshNode("BrokenCode", $"{nodeTypePath}/Code")
        {
            NodeType = "Code",
            Name = "Broken Code",
            Content = new CodeConfiguration
            {
                Id = "BrokenCode",
                Code = @"
public record BrokenType
{
    public string Name { get; init; } = string.Empty;
    // This line has a deliberate syntax error
    public string Oops { get; init; } = UNDEFINED_SYMBOL;
}"
            }
        };
        await persistence.SaveNodeAsync(codeNode, ct);

        // 2. Create an instance node of the broken type
        var instanceNode = MeshNode.FromPath("test/broken-instance") with
        {
            Name = "Broken Instance",
            NodeType = nodeTypePath,
            LastModified = DateTimeOffset.UtcNow
        };
        await persistence.SaveNodeAsync(instanceNode, ct);

        // 3. Initialize the hub — this triggers compilation
        var client = GetClient();
        var address = new Address("test/broken-instance");

        Output.WriteLine("Initializing hub for test/broken-instance...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            ct);
        Output.WriteLine("Hub initialized.");

        // 4. Request the Overview layout area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for Overview area...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");

        // 5. Verify we got an error control (StackControl containing HTML with error message)
        control.Should().NotBeNull("Overview should return an error control when compilation fails");
        control.Should().BeOfType<StackControl>("Error should be displayed in a Stack");

        var stack = (StackControl)control!;
        stack.Areas.Should().NotBeEmpty("Error stack should have child areas");
    }
}
