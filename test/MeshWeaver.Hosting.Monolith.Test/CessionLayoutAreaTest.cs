using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests that the Cession NodeType (from samples/Graph/Data/Doc/) compiles and
/// the MotorXL instance renders a layout area.
/// Also verifies that the @@Cession/MotorXL relative reference in
/// Doc/Architecture/BusinessRules.md resolves correctly.
/// </summary>
public class CessionLayoutAreaTest : MonolithMeshTestBase
{
    private const string MotorXLPath = "Doc/Architecture/BusinessRules/Cession/MotorXL";
    private const string BusinessRulesDocPath = "Doc/Architecture/BusinessRules";
    private readonly string _cacheDirectory;

    public CessionLayoutAreaTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverCessionTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddDocumentation()
            .ConfigureServices(services =>
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph()
            .AddMeshNodes(TestUsers.PublicAdminAccess());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
            try { Directory.Delete(_cacheDirectory, recursive: true); } catch { }
    }

    [Fact(Timeout = 60000)]
    public async Task MotorXL_PathResolves()
    {
        var resolution = await PathResolver.ResolvePathAsync(MotorXLPath);
        resolution.Should().NotBeNull($"Path '{MotorXLPath}' should resolve");
        Output.WriteLine($"Resolved: Prefix={resolution!.Prefix}, Remainder={resolution.Remainder}");
    }

    [Fact(Timeout = 60000)]
    public async Task MotorXL_LayoutArea_ReturnsContent()
    {
        var resolution = await PathResolver.ResolvePathAsync(MotorXLPath);
        resolution.Should().NotBeNull();

        var address = new Address(resolution!.Prefix.ToString()!);
        var client = GetClient(c => c.AddData(data => data));

        // Initialize hub (triggers NodeType compilation)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        // Request default layout area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        var value = await stream.Timeout(30.Seconds()).FirstAsync();
        value.Should().NotBe(default(JsonElement), "Layout area should return content, not spin forever");
    }

    [Fact(Timeout = 60000)]
    public async Task BusinessRulesDoc_RelativeReference_ResolvesToMotorXL()
    {
        // The BusinessRules.md contains @@Cession/MotorXL which should resolve
        // relative to its own path Doc/Architecture/BusinessRules
        var ct = TestContext.Current.CancellationToken;
        var docNode = await MeshQuery
            .QueryAsync<MeshNode>($"path:{BusinessRulesDocPath}", ct: ct)
            .FirstOrDefaultAsync(ct);

        docNode.Should().NotBeNull("BusinessRules doc node should exist");

        var content = docNode!.Content is MarkdownContent mc ? mc.Content : docNode.Content?.ToString();
        content.Should().Contain("@@Cession/MotorXL",
            "Doc should contain relative layout area reference");

        // Verify the relative path resolves: Doc/Architecture/BusinessRules + Cession/MotorXL
        var resolvedPath = PathUtils.ResolveRelativePath("Cession/MotorXL", BusinessRulesDocPath);
        resolvedPath.Should().Be(MotorXLPath);

        // And the resolved path actually finds a node
        var resolution = await PathResolver.ResolvePathAsync(resolvedPath);
        resolution.Should().NotBeNull($"Resolved path '{resolvedPath}' should find the MotorXL node");
    }
}
