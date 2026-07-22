using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🅿️ Failure ISOLATION + VISIBILITY for NodeType compile failures (the recompile-storm
/// wedge cure).
///
/// <para>A NodeType whose source does not compile must NOT be able to wedge the portal: in
/// the stateless, Release-based compile model a broken type that kept re-running Roslyn on
/// every activation / Pending flip would saturate its per-NodeType hub's single-threaded
/// action block. The fix PARKS the failure (bounded + terminal — the type stops
/// recompiling) and NOTIFIES (a bell-databound Notification carrying the type path + error).
/// This test pins all four properties:</para>
/// <list type="bullet">
///   <item>(a) the type lands in the terminal <b>parked</b> state, after exactly ONE compile;</item>
///   <item>(b) a <b>Notification</b> is emitted carrying the type path + error;</item>
///   <item>(c) the hub stays <b>responsive</b> — a healthy node answers promptly afterward;</item>
///   <item>(d) <b>no storm</b> — Roslyn runs exactly once across many activations / enrichments.</item>
/// </list>
/// </summary>
public class NodeTypeCompileParkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "ParkTest";
    private const string NodeTypeId = "ParkBroken";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";

    [Fact(Timeout = 120_000)]
    public async Task BrokenNodeType_Parks_Notifies_StaysBounded_AndKeepsHubResponsive()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

        // 1. A NodeType whose Configuration string is NOT valid C# — the first-build kickoff
        //    compile fails deterministically and the type settles at Error.
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Park Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType (park test).",
                Configuration = "config => this is not valid C# at all ((await ("
            }
        }).Should().Emit();

        // Wait for the compile to settle at Error (cold Roslyn compile budget).
        await workspace.GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("NodeType compile settled at Error.");

        // (a) PARKED terminal state, after EXACTLY ONE compile.
        parkRegistry.IsParked(NodeTypePath)
            .Should().BeTrue("a deterministic compile error must park the type");
        parkRegistry.GetCompileAttemptCount(NodeTypePath)
            .Should().Be(1, "the broken type must compile exactly once before parking");

        // (b) NOTIFY — a bell-databound Notification was emitted carrying the type path + error.
        var notification = await WaitForFailureNotificationAsync(NodeTypePath, ct);
        notification.Should().NotBeNull("a parked type must emit a user-visible notification");
        var content = notification!.Content.Should().BeOfType<Notification>().Subject;
        content.TargetNodePath.Should().Be(NodeTypePath, "the bell must navigate to the failing type");
        content.Message.Should().Contain(NodeTypePath, "the notification must name the failing type");
        content.Title.Should().Contain("failed to compile");
        Output.WriteLine("Failure notification emitted.");

        // (d) NO STORM — hammer the activation/enrichment path 25× on an instance of the
        //     broken type (exactly the churn that storms a faulted compile cache). Roslyn must
        //     NOT run again: the parked type serves its cached error without recompiling.
        var instance = new MeshNode("park-instance", Partition)
        {
            Name = "Park Instance",
            NodeType = NodeTypePath,
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(instance).Should().Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<INodeConfigurationResolver>();
        for (var i = 0; i < 25; i++)
        {
            var enriched = await resolver.ResolveConfiguration(instance)
                .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            enriched.Should().NotBeNull();
        }
        parkRegistry.GetCompileAttemptCount(NodeTypePath)
            .Should().Be(1, "after parking, 25 further enrichments must NOT re-run Roslyn");
        parkRegistry.IsParked(NodeTypePath)
            .Should().BeTrue("the type must still be parked after the hammer");
        Output.WriteLine("25 enrichments — still exactly one compile (no storm).");

        // (c) HUB STAYS RESPONSIVE — a healthy node's hub answers a Ping promptly (not wedged).
        const string healthyPath = $"{Partition}/healthy-doc";
        await NodeFactory.CreateNode(new MeshNode("healthy-doc", Partition)
        {
            Name = "Healthy Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        }).Should().Emit();

        var client = GetClient();
        await client.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(new Address(healthyPath)))
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine("Healthy node answered Ping — the mesh is not wedged.");
    }

    private const string BrokenConfiguration = "config => this is not valid C# at all ((await (";
    private const string ValidConfiguration = "config => config.AddDefaultLayoutAreas()";

    /// <summary>
    /// 🅿️ RETRY through the REAL tool surface un-parks. A parked type whose source has been
    /// FIXED must recompile to Ok when retried via the MCP <c>compile</c> tool
    /// (<see cref="MeshOperations.Compile"/>). The tool routes the trigger through the
    /// RELEASE-REQUEST seam (RequestedReleaseAt + Force) — the single un-park trigger — never
    /// a direct CompilationStatus=Pending flip, which the compile watcher's parked
    /// short-circuit would swallow. Regression for the 2026-07-16 memex incident
    /// (Reinsurance/Layer + BusinessRules/Scope wedged at Pending after one failed compile;
    /// compile/recycle/delete+recreate all failed to heal — only a pod restart did).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ParkedNodeType_CompileToolRetry_AfterFix_SettlesOkAndUnparks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
        const string typePath = $"{Partition}/CompileRetry";

        await CreateBrokenTypeAndAwaitParkAsync("CompileRetry");

        // FIX the source of the failure (the Configuration lambda). Editing alone must NOT
        // un-park — only a deliberate retry does.
        await workspace.GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { Configuration = ValidConfiguration } };
        }).Should().Within(30.Seconds()).Emit();
        parkRegistry.IsParked(typePath).Should().BeTrue(
            "editing the source alone must not un-park — only a deliberate retry does");

        // RETRY via the REAL tool surface — the exact call that wedged on memex.
        var resultJson = await new MeshOperations(Mesh).Compile(typePath)
            .FirstAsync().Timeout(150.Seconds()).ToTask(ct);
        Output.WriteLine($"Compile tool returned: {resultJson}");

        using (var result = JsonDocument.Parse(resultJson))
            result.RootElement.GetProperty("status").GetString().Should().Be("Ok",
                "a deliberate compile retry of a parked type with fixed source must run a REAL compile and succeed");

        parkRegistry.IsParked(typePath).Should().BeFalse(
            "the deliberate retry must have un-parked the type");
        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
    }

    /// <summary>
    /// 🅿️ The recycle-path twin of the compile-tool retry: <see cref="MeshOperations.Recycle"/>
    /// on a parked type with fixed source stamps the release request (surviving the hub bounce
    /// on the node itself), so the reactivated hub's release watcher un-parks and drives a
    /// fresh compile to Ok. Before the fix, recycle flipped CompilationStatus=Pending directly
    /// — the park lives in the mesh-scoped registry, not the hub, so the bounced hub's watcher
    /// swallowed the flip and the type stayed wedged at Pending.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ParkedNodeType_RecycleRetry_AfterFix_SettlesOkAndUnparks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
        const string typePath = $"{Partition}/RecycleRetry";

        await CreateBrokenTypeAndAwaitParkAsync("RecycleRetry");

        await workspace.GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with { Content = def with { Configuration = ValidConfiguration } };
        }).Should().Within(30.Seconds()).Emit();

        var recycleJson = await new MeshOperations(Mesh).Recycle(typePath)
            .FirstAsync().Timeout(60.Seconds()).ToTask(ct);
        Output.WriteLine($"Recycle tool returned: {recycleJson}");
        using (var result = JsonDocument.Parse(recycleJson))
            result.RootElement.GetProperty("status").GetString().Should().Be("Recycled");

        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
        parkRegistry.IsParked(typePath).Should().BeFalse(
            "the recycle's release request must have un-parked the type");
    }

    /// <summary>
    /// 🅿️ Wedge-close for the parked short-circuit: a STRAY direct CompilationStatus=Pending
    /// flip on a parked type (NOT a release request — no un-park) must NOT leave the type
    /// stuck at Pending forever. The compile watcher declines to dispatch Roslyn (containment
    /// holds — no recompile) but re-settles the status to Error with the CACHED parked error,
    /// so every settle-waiter (get_diagnostics, the compile tool, WaitForLatestRelease) gets
    /// an answer instead of hanging to its timeout.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ParkedNodeType_StrayPendingFlip_ResettlesToError_AndStaysParked()
    {
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
        const string typePath = $"{Partition}/StrayFlip";

        await CreateBrokenTypeAndAwaitParkAsync("StrayFlip");
        var attemptsBefore = parkRegistry.GetCompileAttemptCount(typePath);

        // A stray direct Pending flip. The Description marker is written in the SAME update so
        // the assertion below can distinguish the POST-flip re-settled Error from the replayed
        // PRE-flip Error frames (which carry the same status + error text but not the marker).
        const string marker = "stray-flip-discriminator";
        await workspace.GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with
            {
                Content = def with
                {
                    CompilationStatus = CompilationStatus.Pending,
                    Description = marker
                }
            };
        }).Should().Within(30.Seconds()).Emit();

        // The stray trigger must be ANSWERED: re-settled to Error (with the cached parked
        // error), never left hanging at Pending.
        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.Description == marker
                && d.CompilationStatus == CompilationStatus.Error
                && !string.IsNullOrEmpty(d.CompilationError));

        parkRegistry.IsParked(typePath).Should().BeTrue(
            "a stray Pending flip is not a deliberate retry — the park must hold");
        parkRegistry.GetCompileAttemptCount(typePath).Should().Be(attemptsBefore,
            "the parked short-circuit must answer the stray trigger WITHOUT re-running Roslyn");
    }

    /// <summary>
    /// 🅿️ Deleting a NodeType clears its parked compile failure: the park registry is keyed
    /// by PATH in a mesh-scoped singleton that outlives the node, so without the delete-time
    /// un-park a delete+recreate at the same path started PARKED — the fresh type's first
    /// compile trigger fell into the parked short-circuit and the recreated type never
    /// compiled (part of the 2026-07-16 memex incident: delete+recreate did not heal).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DeletedNodeType_ClearsParkedFailure()
    {
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
        const string typePath = $"{Partition}/DeletePark";

        await CreateBrokenTypeAndAwaitParkAsync("DeletePark");

        await NodeFactory.DeleteNode(typePath).Should().Within(30.Seconds()).Emit();

        parkRegistry.IsParked(typePath).Should().BeFalse(
            "deleting a NodeType must clear its parked compile failure so a recreate at the same path starts clean");
    }

    /// <summary>
    /// 🅿️ "Retry only if the sources changed." A parked type whose SOURCE Code node is FIXED
    /// auto-recompiles to Ok and un-parks with NO deliberate Compile/recycle — the redeploy/edit
    /// heal path. Regression for the 2026-07 UWDeepfield case: a GitSync-imported source fix left
    /// the type stuck at Error because the in-memory park only cleared on a deliberate retry, so
    /// the fixed source never recompiled in the running process. The negative half is pinned in
    /// the same test: while the broken source is UNCHANGED, repeated activations never re-run
    /// Roslyn (the park still contains the failure — no storm).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ParkedNodeType_SourceFix_AutoRecompilesAndUnparks_WithoutDeliberateRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
        const string typePath = $"{Partition}/SourceAutoRetry";
        const string sourcePath = $"{Partition}/SourceAutoRetry/Source/code";

        // Plant the deliberately-uncompilable SOURCE Code node FIRST so the type's first-build
        // compile sees it and fails on the SOURCE (not just a broken inline Configuration).
        await NodeFactory.CreateNode(new MeshNode("code", $"{Partition}/SourceAutoRetry/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "this is not valid C#;", Language = "csharp" }
        }).Should().Emit();

        // A NodeType with a VALID Configuration but the broken source above under it.
        await NodeFactory.CreateNode(new MeshNode("SourceAutoRetry", Partition)
        {
            Name = "Source Auto Retry",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Valid config, broken SOURCE node (source-change auto-retry test).",
                Configuration = ValidConfiguration
            }
        }).Should().Emit();

        // Settle at Error + parked (deterministic source error parks on the first failure).
        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        parkRegistry.IsParked(typePath).Should().BeTrue(
            "a deterministic SOURCE compile error must park the type");
        var attemptsAtPark = parkRegistry.GetCompileAttemptCount(typePath);
        Output.WriteLine($"{typePath} parked at Error after {attemptsAtPark} compile(s).");

        // NEGATIVE — source UNCHANGED: hammer activation/enrichment; Roslyn must NOT re-run.
        var instance = new MeshNode("src-instance", Partition)
        {
            Name = "Src Instance",
            NodeType = typePath,
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(instance).Should().Emit();
        var resolver = Mesh.ServiceProvider.GetRequiredService<INodeConfigurationResolver>();
        for (var i = 0; i < 10; i++)
            (await resolver.ResolveConfiguration(instance).FirstAsync().Timeout(30.Seconds()).ToTask(ct))
                .Should().NotBeNull();
        parkRegistry.GetCompileAttemptCount(typePath).Should().Be(attemptsAtPark,
            "an UNCHANGED broken source must not re-run Roslyn — retry only if the sources changed");
        parkRegistry.IsParked(typePath).Should().BeTrue(
            "the type must stay parked while its source is unchanged");
        Output.WriteLine("10 enrichments, unchanged source — still parked, no recompile.");

        // FIX the SOURCE — and DO NOTHING ELSE. No Compile tool, no recycle, no release request.
        // The source-change alone must auto-un-park and drive a fresh compile to Ok.
        await workspace.GetMeshNodeStream(sourcePath).Update(curr =>
                curr with { Content = new CodeConfiguration
                    { Code = "public record AutoRetryOk;", Language = "csharp" } })
            .Should().Within(30.Seconds()).Emit();

        await workspace.GetMeshNodeStream(typePath)
            .Should().Within(120.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
        parkRegistry.IsParked(typePath).Should().BeFalse(
            "fixing the SOURCE must auto-un-park the type WITHOUT a deliberate retry");
        Output.WriteLine("Source fixed → auto-recompiled to Ok and un-parked (no deliberate retry).");
    }

    /// <summary>
    /// Creates a NodeType with a deliberately non-compiling Configuration at
    /// <c>{Partition}/{id}</c>, waits for the first-build compile to settle at Error and
    /// asserts the type is parked (deterministic source errors park on the FIRST failure).
    /// </summary>
    private async Task CreateBrokenTypeAndAwaitParkAsync(string id)
    {
        var typePath = $"{Partition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, Partition)
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType (park retry tests).",
                Configuration = BrokenConfiguration
            }
        }).Should().Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>()
            .IsParked(typePath).Should().BeTrue(
                "a deterministic compile error must park the type");
        Output.WriteLine($"{typePath} settled at Error and is parked.");
    }

    /// <summary>
    /// Reactively polls for the failure Notification (no fixed sleep): the satellite write is
    /// driven by a fire-and-forget Subscribe inside the compile continuation, so it lands
    /// shortly after the compile settles. Filters by the failing type's path so it is robust
    /// to the recipient namespace.
    /// </summary>
    private async Task<MeshNode?> WaitForFailureNotificationAsync(string nodeTypePath, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return await Observable.Interval(TimeSpan.FromMilliseconds(150))
            .StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{NotificationNodeType.NodeType}"))
                .Take(1))
            .Select(result => result.Items.FirstOrDefault(n =>
                n.Content is Notification notif && notif.TargetNodePath == nodeTypePath))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(ct);
    }
}
