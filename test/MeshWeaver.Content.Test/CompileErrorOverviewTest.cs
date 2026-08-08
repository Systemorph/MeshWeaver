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
using MeshWeaver.Hosting.Persistence;
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

        // 3. THE CONTRACT: the Overview display area COMES BACK as the emergency overlay Stack within
        //    a bounded time. If the per-instance hub failed to activate / parked the subscription (the
        //    wedge), this never emits and the assertion fails by timeout instead of hanging forever.
        await stream.GetControlStream(overviewRef.Area!)
            .Should().Within(60.Seconds()).Match(c => c is StackControl);
        Output.WriteLine("Overview area came back as the overlay Stack.");

        // 4. The Overview must SAY it had a compilation error. Reactive: wait until a MarkdownControl
        //    that actually carries the ⚠ + "compilation" text arrives (the overlay's first child area),
        //    rather than snapshotting the first emission and asserting once — so an early/intermediate
        //    frame can't race the content check.
        var childArea = $"{overviewRef.Area}/1";
        await stream.GetControlStream(childArea)
            .Should().Within(30.Seconds())
            .Match(c => c is MarkdownControl md
                && (md.Markdown?.ToString() ?? string.Empty) is var t
                && t.Contains('⚠')
                && t.Contains("compilation", StringComparison.OrdinalIgnoreCase));
        Output.WriteLine("Overview markdown says it had a compilation error.");
    }

    [Fact(Timeout = 120_000)]
    public async Task TimedOutNodeType_InstanceOverview_ReadsAsRetry_AndLinksTheCompileLog()
    {
        // 🚨 #641: a LOOKUP/SETTLE TIMEOUT is not a compile failure. A NodeType whose state
        // could not be determined persists CompilationStatus.Unavailable (written by
        // NodeTypeContractHandler when its settle wait elapses), and every instance of it
        // must render the retry copy — never "There was a compilation error… Please correct
        // the code" for source that was never even read. It must also LINK the compile log:
        // on a timeout that log is the only evidence of how far the build actually got.
        const string typeId = "TimedOutOverviewType";
        const string typePath = $"{Partition}/{typeId}";
        const string activityPath = $"{typePath}/_Activity/compile-timeout";
        const string instanceId = "timed-out-overview-instance";

        var workspace = Mesh.GetWorkspace();

        await NodeFactory.CreateNode(new MeshNode(typeId, Partition)
        {
            Name = "Timed Out Overview Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "A type whose compile state could not be determined.",
                // No Configuration / Sources: nothing here is broken, and nothing will
                // recompile it — the state is simply UNDETERMINED.
                CompilationStatus = CompilationStatus.Unavailable,
                CompilationError =
                    "The compile state of 'TimedOutOverviewType' could not be determined: "
                    + "it did not settle within 60s. This is a TIMEOUT, not a compilation error.",
                LastCompilationActivityPath = activityPath
            }
        }).Should().Emit();

        // The seeded state must survive as written — no watcher may reinterpret an
        // undetermined state as a compile (the kickoff gates on CompilationStatus == null).
        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Unavailable);
        Output.WriteLine("NodeType is parked at Unavailable.");

        await NodeFactory.CreateNode(new MeshNode(instanceId, Partition)
        {
            Name = "Timed Out Overview Instance",
            NodeType = typePath,
            State = MeshNodeState.Active
        }).Should().Emit();

        var client = GetClient();
        var overviewRef = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address($"{Partition}/{instanceId}"), overviewRef);

        // Same non-hang contract as the broken-type test: the overlay hub activates and the
        // Overview comes back — but with the RETRY story, not the code-fix one.
        await stream.GetControlStream($"{overviewRef.Area}/1")
            .Should().Within(60.Seconds())
            .Match(c => c is MarkdownControl md
                && (md.Markdown?.ToString() ?? string.Empty) is var t
                && t.Contains("No code change is needed", StringComparison.OrdinalIgnoreCase)
                && !t.Contains("Please correct the code", StringComparison.OrdinalIgnoreCase)
                && t.Contains($"](/{activityPath})", StringComparison.Ordinal));
        Output.WriteLine("Instance Overview reads as a timeout, links the compile log, and never says 'correct the code'.");
    }

    [Fact(Timeout = 120_000)]
    public async Task BrokenNodeType_InstanceOverview_StillAsksForACodeFix_AndLinksTheCompileLog()
    {
        // The counterpart guarantee: distinguishing timeouts must not soften a REAL compile
        // failure. A genuine Roslyn error still shows the diagnostics AND asks for a code fix
        // — and now also links the compile activity from the INSTANCE page.
        const string typeId = "BrokenLinkedType";
        const string typePath = $"{Partition}/{typeId}";
        const string instanceId = "broken-linked-instance";

        var workspace = Mesh.GetWorkspace();

        await NodeFactory.CreateNode(new MeshNode(typeId, Partition)
        {
            Name = "Broken Linked Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling — the genuine-failure counterpart.",
                Configuration = "config => still not valid C# (((["
            }
        }).Should().Emit();

        // Wait for the REAL compile to fail AND stamp the activity it wrote its log to.
        var failed = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error
                && !string.IsNullOrEmpty(d.LastCompilationActivityPath));
        var activityPath = ((NodeTypeDefinition)failed.Content!).LastCompilationActivityPath!;
        Output.WriteLine($"Compile settled at Error with activity {activityPath}.");

        await NodeFactory.CreateNode(new MeshNode(instanceId, Partition)
        {
            Name = "Broken Linked Instance",
            NodeType = typePath,
            State = MeshNodeState.Active
        }).Should().Emit();

        var client = GetClient();
        var overviewRef = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address($"{Partition}/{instanceId}"), overviewRef);

        await stream.GetControlStream($"{overviewRef.Area}/1")
            .Should().Within(60.Seconds())
            .Match(c => c is MarkdownControl md
                && (md.Markdown?.ToString() ?? string.Empty) is var t
                && t.Contains("Please correct the code", StringComparison.OrdinalIgnoreCase)
                && t.Contains($"](/{activityPath})", StringComparison.Ordinal));
        Output.WriteLine("Genuine failure still asks for a code fix — and now links its compile log.");
    }

    [Fact(Timeout = 120_000)]
    public async Task OneBrokenNodeType_DoesNotBreak_OtherNodes()
    {
        // 🚨 Isolation contract: a single non-compiling NodeType must NOT take down the rest of the
        // portal. The wedge was precisely this failing — one broken node's parked subscriptions
        // saturated the single-threaded hub and everything stopped responding. With a broken type
        // failing CLOSED (Status=Error + overlay, no park), unrelated nodes keep rendering.
        var workspace = Mesh.GetWorkspace();

        // A broken NodeType, present and settled at Error (its hubs serve the overlay only).
        await NodeFactory.CreateNode(new MeshNode("IsolationBroken", Partition)
        {
            Name = "Isolation Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = "config => not valid c# (((" }
        }).Should().Emit();
        await workspace.GetMeshNodeStream($"{Partition}/IsolationBroken")
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("Broken NodeType settled at Error.");

        // A healthy, unrelated node (built-in Markdown type — no Roslyn compile) created AFTER the
        // broken one. Its display area must still come back promptly — proving the broken type did
        // not wedge the mesh.
        await NodeFactory.CreateNode(new MeshNode("healthy-doc", Partition)
        {
            Name = "Healthy Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Healthy\nStill works." }
        }).Should().Emit();

        var client = GetClient();
        var healthyAddress = new Address($"{Partition}/healthy-doc");
        var overviewRef = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        // Reactive: the healthy node's display area must emit a control within a bounded time while
        // the broken NodeType sits in Error. A timeout here = the broken type wedged the mesh — the
        // exact failure this guards against. One bad type must never break the whole portal.
        await client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(healthyAddress, overviewRef)
            .GetControlStream(overviewRef.Area!)
            .Should().Within(30.Seconds()).Match(c => c is not null);
        Output.WriteLine("Healthy node Overview came back — mesh not wedged by the broken type.");
    }
}
