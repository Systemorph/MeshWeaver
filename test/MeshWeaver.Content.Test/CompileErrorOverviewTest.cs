using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// 🚨 THE anti-wedge contract the 2026-06-18 incident violated: when a node's NodeType
/// CANNOT be resolved/compiled, its per-instance hub must still ACTIVATE under the emergency
/// compilation-error overlay (<see cref="NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay"/>),
/// and its <b>Overview display area must come back</b> — within a bounded time — <b>SAYING it had a
/// compilation error</b>. It must never hang / wedge / time out (the failure mode that took atioz down).
///
/// <para>This is the integration-level companion to the pure
/// <c>CompileErrorPageTest</c> (which unit-tests the NodeType Progress page builder): here we drive a
/// REAL broken NodeType through compilation and assert the instance Overview renders the error rather
/// than parking the caller. A regression that reintroduces the hang fails this test by TIMEOUT.</para>
/// </summary>
public class CompileErrorOverviewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string CacheDirectory = Path.Combine(
        Path.GetTempPath(), "MeshWeaverCompileErrorOverviewTests", ".mesh-cache");

    private const string Partition = "TestCompileErrorOverview";
    private const string NodeTypeId = "BrokenOverviewType";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";
    private const string InstancePath = $"{Partition}/broken-overview-instance";

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
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 120_000)]
    public async Task BrokenNodeType_InstanceOverview_ComesBack_SayingCompilationError()
    {
        var workspace = Mesh.GetWorkspace();

        // 1. A NodeType whose Configuration is not valid C# — the kickoff compile fails and
        //    CompilationStatus settles at Error (the hub can produce no usable configuration).
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Broken Overview Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType for the Overview-comes-back contract.",
                Configuration = "config => this is not valid C# at all ((await ("
            }
        }).Should().Emit();

        // The compile must settle at Error (cold Roslyn budget). This itself is a non-hang assertion:
        // a broken compile reaches a TERMINAL state, it doesn't spin forever.
        await workspace.GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("NodeType compile settled at Error.");

        // 2. An instance of the broken type — accessing it must activate the emergency overlay hub.
        await NodeFactory.CreateNode(new MeshNode("broken-overview-instance", Partition)
        {
            Name = "Broken Overview Instance",
            NodeType = NodeTypePath,
            State = MeshNodeState.Active
        }).Should().Emit();
        Output.WriteLine("Instance created.");

        var client = GetClient();
        var clientWorkspace = client.GetWorkspace();
        var address = new Address(InstancePath);
        var overviewRef = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = clientWorkspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, overviewRef);

        // 3. THE CONTRACT: the Overview display area COMES BACK — a non-null control within a bounded
        //    time. If the per-instance hub failed to activate / parked the subscription (the wedge),
        //    this never emits and the test fails by timeout instead of hanging the suite forever.
        var control = await stream.GetControlStream(overviewRef.Area!)
            .Should().Within(60.Seconds()).Match(c => c is not null);
        Output.WriteLine($"Overview area came back as: {control?.GetType().Name}");
        control.Should().BeOfType<StackControl>(
            "the emergency compilation-error overlay renders its Overview as a Stack — the area must " +
            "come back with a control, never hang");

        // 4. The Overview must SAY it had a compilation error. The overlay's Stack wraps a single
        //    MarkdownControl (BuildCompilationErrorMarkdown) carrying the ⚠ header + the error text;
        //    it is the first child area of the Stack.
        var childArea = $"{overviewRef.Area}/1";
        var markdown = await stream.GetControlStream(childArea)
            .Should().Within(30.Seconds()).Match(c => c is MarkdownControl);
        var text = ((MarkdownControl)markdown!).Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"Overview markdown:\n{text}");

        text.Should().Contain("⚠",
            "the Overview overlay must visibly flag a problem, not render blank");
        text.ToLowerInvariant().Should().Contain("compilation",
            "the Overview must tell the user it had a COMPILATION error (or whatever its problem is)");
    }
}
