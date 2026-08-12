using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.Loader;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 EVERY RECOMPILE OF A NODETYPE MUST GIVE ITS ASSEMBLY LOAD CONTEXT BACK.
///
/// <para>A NodeType's code is compiled into a COLLECTIBLE <c>AssemblyLoadContext</c>
/// (<c>DynamicNode_&lt;path&gt;</c>). Recompiling supersedes the old one, and
/// <c>CompilationCacheService.EvictSupersededContexts</c> unloads it — but <c>Unload()</c> only makes
/// a context ELIGIBLE for collection. Anything still holding a type, a delegate, or an instance from
/// that assembly pins its LoaderAllocator, and a pinned collectible context is permanent garbage.
/// Nothing in the product notices; the process just gets bigger with every recompile.</para>
///
/// <para><b>Measured on memex-cloud 2026-08-12, from Prometheus (which survived — it has a PVC,
/// unlike Loki):</b> the batch bake is NOT the problem — the pod finished baking all 279 types at
/// 05:54 and sat at <b>2.5 GB</b> at 06:00. It then climbed to <b>24.5 GB</b> by 08:45 across a
/// morning of merges, each firing a GitSync re-import and another round of ACTIVATION-path
/// recompiles: about <b>130 MB per minute, sustained, never returned</b>. The pod before it told the
/// same story from the other side — flat at 1.8–2.7 GB for five hours, then +19 GB in the half hour
/// containing the 04:48 re-import. One linked batch build for 279 types is cheap; per-type recompiles
/// are what leak.</para>
///
/// <para>This test is the deterministic repro. It uses <see cref="AssemblyLoadContext.All"/> — public,
/// no internals needed — to count the contexts still ALIVE for one NodeType after recompiling it
/// several times and forcing full collections. A healthy framework keeps one (the live build). A
/// count that tracks the number of recompiles is the leak, and the number it reaches is how many
/// generations are pinned.</para>
/// </summary>
public class NodeTypeRecompileAlcLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "AlcLeakTest";
    private const string TypeId = "LeakType";
    private const string TypePath = $"{Partition}/{TypeId}";

    /// <summary>How many times to recompile. Enough to tell "one pinned generation" from "all of them".</summary>
    private const int Recompiles = 3;

    /// <summary>
    /// Live collectible contexts for this NodeType. The name is
    /// <c>DynamicNode_{SanitizeNodeName(path)}</c>, so match on the type id rather than reproducing
    /// the sanitiser here.
    /// </summary>
    private static int LiveContexts() =>
        AssemblyLoadContext.All.Count(alc =>
            alc.Name is { } name
            && name.StartsWith("DynamicNode_", StringComparison.Ordinal)
            && name.Contains(TypeId, StringComparison.Ordinal));

    /// <summary>
    /// Collectible-ALC collection is not single-pass: the LoaderAllocator dies on a later GC than the
    /// managed objects that referenced it. Three full passes with finalizers drained is the standard
    /// shape for asserting unloadability — more than that and something is genuinely holding it.
    /// </summary>
    private static void FullyCollect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task RecompilingANodeType_ReleasesEverySupersededLoadContext()
    {
        await NodeFactory.CreateNode(new MeshNode(TypeId, Partition)
        {
            Name = "Leak Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Recompiled repeatedly to prove its load contexts are released.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        // The FIRST compile only happens when the type's own hub is activated — subscribing to its
        // stream is what does that (the compile watcher is installed from the hub's init hook).
        var first = await WhenCompiled(TypePath, d => d.CompilationStatus == CompilationStatus.Ok, null);
        var lastSucceeded = first.LastCompileSucceededAt;
        Output.WriteLine($"initial compile settled at {lastSucceeded:HH:mm:ss.fff} — contexts: {LiveContexts()}");

        for (var i = 1; i <= Recompiles; i++)
        {
            // Drive the rebuild the way the framework does: change the source so the output really
            // differs AND flip CompilationStatus to Pending — the same lever the framework-stale
            // kickoff and the enrichment self-heal pull. A source edit alone does not queue a
            // compile for a Configuration-only type (it has no Code nodes whose versions moved),
            // which is why the first cut of this test timed out waiting for a compile nobody asked
            // for.
            var generation = i;
            await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
                .Update(node => node.Content is NodeTypeDefinition def
                    ? node with
                    {
                        Content = def with
                        {
                            Configuration = $"config => config /* gen {generation} */",
                            CompilationStatus = CompilationStatus.Pending
                        }
                    }
                    : node)
                .FirstAsync()
                .Timeout(30.Seconds())
                .ToTask();

            var settled = await WhenCompiled(TypePath,
                d => d.CompilationStatus == CompilationStatus.Ok, lastSucceeded);
            lastSucceeded = settled.LastCompileSucceededAt;

            FullyCollect();
            Output.WriteLine($"after recompile {generation}: live contexts = {LiveContexts()}");
        }

        FullyCollect();
        var live = LiveContexts();
        Output.WriteLine($"FINAL live contexts for {TypePath} after {Recompiles} recompiles: {live}");

        // ≤2, not ==1: the newest build is legitimately live, and one predecessor may still be
        // inside its unload (a pin drained, the LoaderAllocator awaiting the next GC). What must
        // NEVER hold is a count that tracks the recompiles — that is a generation pinned per rebuild,
        // which is the 130 MB/min curve measured in production.
        live.Should().BeLessThanOrEqualTo(2,
            $"every superseded DynamicNode_ context must be collectable after unload; {live} alive "
            + $"after {Recompiles} recompiles means a generation is pinned per rebuild");
    }

    /// <summary>
    /// 🚨 THE SAME LOOP, BUT WITH A LIVE INSTANCE HUB — which is the shape production actually runs.
    ///
    /// <para>Every instance hub of a NodeType takes a LIFETIME LEASE on that type's load contexts
    /// (<c>MeshDataSource</c> → <c>CompilationCacheService.LeaseNodeContexts</c>), and while a lease is
    /// held <c>Dispose</c> records the unload request and returns WITHOUT unloading. That deferral is
    /// deliberate and correct — <c>Unload()</c> is cooperative, so tearing the LoaderAllocator down
    /// under a hub that still runs its types would break that hub, not free memory.</para>
    ///
    /// <para>But the lease is taken ONCE, on activation (<c>.Take(1)</c>), and released only when the
    /// hub is DISPOSED. So the question this test answers is the one that decides whether the
    /// production curve is explained: after the type is recompiled, is the instance hub that runs the
    /// OLD assembly recycled — releasing its lease so the superseded context can finally unload — or
    /// does it keep that generation pinned for as long as it lives? If nothing recycles it, "deferred"
    /// means "forever" for any long-lived hub, and each recompile pins one more generation. That is
    /// the 130 MB/min curve measured on memex-cloud on 2026-08-12.</para>
    ///
    /// <para>Deliberately asserted the same way as the no-instance case. If this fails while that one
    /// passes, the leak is isolated to exactly this: leases outliving the assembly they pin.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task RecompilingANodeType_WithALiveInstance_StillReleasesSupersededContexts()
    {
        const string instanceId = "LeakInstance";
        var instancePath = $"{Partition}/{instanceId}";

        await NodeFactory.CreateNode(new MeshNode(TypeId, Partition)
        {
            Name = "Leak Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Recompiled repeatedly WHILE an instance hub runs its assembly.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        var first = await WhenCompiled(TypePath, d => d.CompilationStatus == CompilationStatus.Ok, null);
        var lastSucceeded = first.LastCompileSucceededAt;

        // An INSTANCE of the type, and then ACTIVATE its hub — subscribing to its stream is what
        // does that, and activation is what takes the lease.
        await NodeFactory.CreateNode(new MeshNode(instanceId, Partition)
        {
            Name = "Leak Instance",
            NodeType = TypePath
        }).Should().Within(30.Seconds()).Emit();

        var instanceStream = Mesh.GetWorkspace().GetMeshNodeStream(instancePath);
        using var activation = instanceStream.Subscribe(_ => { });
        await instanceStream.Where(n => n is not null).Take(1).Timeout(60.Seconds()).ToTask();
        Output.WriteLine($"instance hub activated — live contexts: {LiveContexts()}");

        for (var i = 1; i <= Recompiles; i++)
        {
            var generation = i;
            await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
                .Update(node => node.Content is NodeTypeDefinition def
                    ? node with
                    {
                        Content = def with
                        {
                            Configuration = $"config => config /* live-instance gen {generation} */",
                            CompilationStatus = CompilationStatus.Pending
                        }
                    }
                    : node)
                .FirstAsync()
                .Timeout(30.Seconds())
                .ToTask();

            var settled = await WhenCompiled(TypePath,
                d => d.CompilationStatus == CompilationStatus.Ok, lastSucceeded);
            lastSucceeded = settled.LastCompileSucceededAt;

            FullyCollect();
            Output.WriteLine($"after recompile {generation} (instance live): live contexts = {LiveContexts()}");
        }

        FullyCollect();
        var live = LiveContexts();
        Output.WriteLine(
            $"FINAL live contexts with a live instance, after {Recompiles} recompiles: {live}");

        live.Should().BeLessThanOrEqualTo(2,
            $"a live instance hub legitimately DEFERS an unload, but nothing may make that deferral "
            + $"permanent: {live} contexts alive after {Recompiles} recompiles means each rebuild pins "
            + "a generation for the hub's lifetime, which is the production memory curve");
    }

    /// <summary>
    /// Waits for a settled compile — optionally one strictly NEWER than
    /// <paramref name="after"/>, so a recompile cannot be satisfied by the previous build's
    /// still-published Ok.
    /// </summary>
    private Task<NodeTypeDefinition> WhenCompiled(
        string path, Func<NodeTypeDefinition, bool> predicate, DateTimeOffset? after) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null
                        && predicate(d!)
                        && (after is null || d!.LastCompileSucceededAt > after))
            .Select(d => d!)
            .Take(1)
            .Timeout(120.Seconds())
            .ToTask();
}
