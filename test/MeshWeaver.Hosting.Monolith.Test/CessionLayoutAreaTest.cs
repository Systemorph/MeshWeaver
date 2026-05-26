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
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
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
        // Stable cache directory — the timestamped-subdir cache (a3ab9909e)
        // gives each compile its own subdir so prior-process DLLs aren't touched.
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverCessionTests");
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            // AddDocumentation registers an embedded-resource partition under
            // the "Doc" namespace; partition-routing persistence is required
            // for that provider to actually serve reads.
            .AddPartitionedInMemoryPersistence()
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
        var resolution = await PathResolver.ResolvePath(MotorXLPath).FirstAsync().ToTask();
        resolution.Should().NotBeNull($"Path '{MotorXLPath}' should resolve");
        Output.WriteLine($"Resolved: Prefix={resolution!.Prefix}, Remainder={resolution.Remainder}");
    }

    [Fact(Timeout = 60000)]
    public async Task MotorXL_LayoutArea_ReturnsContent()
    {
        var resolution = await PathResolver.ResolvePath(MotorXLPath).FirstAsync().ToTask();
        resolution.Should().NotBeNull();

        var address = new Address(resolution!.Prefix.ToString()!);
        var client = GetClient(c => c.AddData(data => data));

        // Initialize hub (triggers NodeType compilation)
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask();

        // Request default layout area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        var value = await stream.Timeout(30.Seconds()).FirstAsync();
        value.Should().NotBe(default(JsonElement), "Layout area should return content, not spin forever");
    }

    [Fact(Timeout = 60000)]
    public async Task Cession_Trace_HubConfiguration()
    {
        var client = GetClient();

        // Resolve path first to get the actual hub address
        var resolution = await PathResolver.ResolvePath(MotorXLPath).FirstAsync().ToTask();
        resolution.Should().NotBeNull($"Path '{MotorXLPath}' should resolve");
        Output.WriteLine($"Resolved: Prefix={resolution!.Prefix}, Remainder={resolution.Remainder}");

        var address = new Address(resolution.Prefix.ToString()!);
        Output.WriteLine($"Hub address: {address}");

        Output.WriteLine($"Initializing hub for {address}...");
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask();
        Output.WriteLine("Hub initialized.");

        var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        hostedHub.Should().NotBeNull("Hub should exist after PingRequest");

        var workspace = hostedHub!.GetWorkspace();
        Output.WriteLine($"Workspace exists: {workspace != null}");

        var uiControlService = hostedHub.ServiceProvider.GetService<IUiControlService>();
        Output.WriteLine($"IUiControlService exists: {uiControlService != null}");
        Output.WriteLine($"LayoutDefinition renderer count: {uiControlService?.LayoutDefinition.Count ?? 0}");

        uiControlService.Should().NotBeNull("IUiControlService should be available");
        uiControlService!.LayoutDefinition.Count.Should().BeGreaterThan(0,
            "Layout definition should have registered renderers (including CessionResults)");
    }

    [Fact(Timeout = 60000)]
    public async Task MotorXL_Overview_ShouldRender()
    {
        var client = GetClient(c => c.AddData(data => data));
        var address = new Address(MotorXLPath);

        Output.WriteLine($"Initializing hub for {MotorXLPath}...");
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask();
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        Output.WriteLine("Waiting for Overview area...");
        var rawValue = await stream.Timeout(TimeSpan.FromSeconds(20)).FirstAsync();
        Output.WriteLine($"Received raw value: {rawValue.Value.ValueKind}");

        rawValue.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview area should return a response for MotorXL");
    }

    /// <summary>
    /// Negative case: pinging a path that doesn't exist must surface as a clear
    /// "node does not exist" error — not a 30s ping timeout. The chain:
    ///   1. PathResolver.ResolvePath returns null (or a resolution with a
    ///      non-empty Remainder) for the missing path.
    ///   2. RoutingServiceBase.PostNotFound returns a DeliveryFailure with
    ///      ErrorType.NotFound + a message naming the missing path.
    ///   3. client.Observe(...) propagates that failure as an exception on the
    ///      Task — callers don't see a generic timeout.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NonExistentPath_Failure()
    {
        const string missingPath = "Doc/Architecture/BusinessRules/Cession/Nonexistent";

        // 1+2: path resolution does NOT find a complete prefix match —
        //      either resolution is null or has a non-empty Remainder.
        var resolution = await PathResolver.ResolvePath(missingPath).FirstAsync().ToTask();
        if (resolution is not null)
            resolution.Remainder.Should().NotBeNullOrEmpty(
                $"Path '{missingPath}' should not resolve completely; closest ancestor + remainder is expected");
        Output.WriteLine($"Resolution: {(resolution is null ? "(null)" : $"Prefix={resolution.Prefix} Remainder={resolution.Remainder}")}");

        // 3+4: PingRequest fails with a DeliveryFailure(NotFound) — the
        //      observable's OnError carries the failure as an exception.
        var address = new Address(missingPath);
        var client = GetClient();
        var act = async () => await client.Observe(new PingRequest(), o => o.WithTarget(address))
            .FirstAsync()
            .ToTask();

        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        Output.WriteLine($"Got exception: {ex.GetType().Name}: {ex.Message}");
        ex.Message.Should().ContainAny(
            new[] { "No node found", "NotFound", "does not exist" },
            $"Expected a NotFound-style failure naming '{missingPath}'");
    }

    [Fact(Timeout = 60000)]
    public async Task BusinessRulesDoc_RelativeReference_ResolvesToMotorXL()
    {
        // The BusinessRules.md contains @@Cession/MotorXL which should resolve
        // relative to its own path Doc/Architecture/BusinessRules
        var ct = TestContext.Current.CancellationToken;
        var docNode = await ReadNodeAsync(BusinessRulesDocPath, ct);

        docNode.Should().NotBeNull("BusinessRules doc node should exist");

        var content = docNode!.Content is MarkdownContent mc ? mc.Content : docNode.Content?.ToString();
        content.Should().Contain("@@Cession/MotorXL",
            "Doc should contain relative layout area reference");

        // Verify the relative path resolves: Doc/Architecture/BusinessRules + Cession/MotorXL
        var resolvedPath = PathUtils.ResolveRelativePath("Cession/MotorXL", BusinessRulesDocPath);
        resolvedPath.Should().Be(MotorXLPath);

        // And the resolved path actually finds a node
        var resolution = await PathResolver.ResolvePath(resolvedPath).FirstAsync().ToTask();
        resolution.Should().NotBeNull($"Resolved path '{resolvedPath}' should find the MotorXL node");
    }
}

