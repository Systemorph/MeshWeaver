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
///
/// <para><b>🚨 This test replaced <c>NodeTypeAssemblyLeakTest</c>, deleted with #1324, and the reason
/// is worth keeping.</b> That test asserted that a NodeType's load context is collected <em>after the
/// mesh is disposed</em> — a shutdown property. A portal never disposes its mesh, so it could not have
/// failed for the retention it was assumed to cover, and for this bug's entire life it read as
/// evidence that recompiles were clean while the process grew 130–340 MB/min in production. The
/// live-mesh statement — contexts stay bounded across N recompiles with nothing torn down — is the one
/// that matters, and it is asserted here. (It also poll-looped on <c>Task.Delay</c> waiting for
/// collection, so a context that was merely slow to die passed identically to one that died at once.)
/// The lesson generalises: an invariant asserted only at teardown is not a guard on steady state.</para>
/// </summary>
public class NodeTypeRecompileAlcLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "AlcLeakTest";
    private const string TypeId = "LeakType";
    private const string TypePath = $"{Partition}/{TypeId}";

    /// <summary>How many times to recompile. Enough to tell "one pinned generation" from "all of them".</summary>
    private const int Recompiles = 3;

    /// <summary>
    /// Ceiling on message hubs RETAINED per recompile.
    ///
    /// <para>🚨 HUBS, NOT ONLY BYTES — because the hub count is what actually names the retainer, and
    /// the byte figure alone sent the first two investigations to the wrong place (an ALC leak, then
    /// Roslyn; heap dumps falsified both). Every retained hub carries its own Autofac lifetime scope,
    /// <c>TypeRegistry</c> and <c>JsonSerializerOptions</c> — about 140 KB — so the two numbers move
    /// together, but only this one says WHERE.</para>
    ///
    /// <para>Measured on this repro: <b>6.2 hubs and 3–5 MB per recompile</b> (5.3 / 7.0 / 6.3 over
    /// three Release runs), down from 12.7 and 7 MB, and from 22 / ~8 MB before that. Attributed by
    /// walking the hosted-hub tree (see <see cref="HubsByParent"/>, printed below), the residual is
    /// now ALMOST ENTIRELY the per-compile activity nodes: the <c>_Activity/compile-&lt;ts&gt;</c>
    /// node hubs +3–5 <c>sync/</c> apiece plus the 2–3 node hubs themselves, and the shared
    /// <c>cache/</c> hub +3. <c>_Activity/compile-state</c> and the NodeType's own hub, which used
    /// to contribute +13 and +3, now contribute <b>nothing</b>. Almost all retained hubs are
    /// <c>sync/{id}</c> sub-hubs — one per <c>SynchronizationStream</c>.</para>
    ///
    /// <para><b>What the 22 → 12 change closed (#1324):</b> <c>Workspace.EvictForPath</c> fires on
    /// EVERY mesh change event — including the echo of the writer's own write — and used to PARK the
    /// mirror that write had used in <c>_evictedRemoteStreams</c> without disposing it, so a
    /// continuously-written path minted a fresh client <c>sync/</c> hub (and its owner-side twin) per
    /// write and never gave one back; instrumented over this loop the parked set grew 1 → 23
    /// monotonically. The eviction itself is load-bearing (disabled, the next write diffs against a
    /// stale snapshot and the owner's MergeGuard refuses it, so the compile never settles) and a
    /// "dispose when the Rx subscriber count hits zero" rule does not fire at all (the reduce chain
    /// <c>CreateExternalClient</c> builds subscribes to the stream itself, so an evicted stream sits
    /// at 2–3 subscribers forever). The cure is a DECLARED holder: everything that keeps a remote
    /// stream past the call that resolved it takes a lease
    /// (<c>Workspace.AcquireRemoteStreamUnchecked</c>) and an evicted stream is disposed the instant
    /// its last lease goes. Measured over this loop: 24 mirrors opened, 23 evicted, <b>18 reclaimed</b>
    /// — the 6 survivors are the one live hydration mirror per path, which is the correct steady
    /// state.</para>
    ///
    /// <para><b>What the 12.7 → 6.2 change closed (#1324): the owner-side INTERMEDIATE reduce.</b>
    /// <c>MeshDataSource</c>'s own-node <c>AddWorkspaceReferenceStream&lt;MeshNode&gt;</c> factory
    /// builds <c>primary.Reduce&lt;InstanceCollection&gt;(CollectionReference("MeshNode"))</c> and
    /// then reduces THAT to the node — and <c>WorkspaceStreams.CreateReducedStream</c> registers each
    /// child for disposal ON ITS PARENT, which here is the data source's primary stream (hub
    /// lifetime). The factory runs uncached on every call that passes a <c>configuration</c> — i.e.
    /// once per inbound <c>SubscribeRequest</c> — so an unsubscribe reaped the subscription's own hub
    /// and left the nameless intermediate behind forever. Same defect shape as #1345 (which memoized
    /// <c>Workspace.GetStream</c>), one layer down at <c>stream.Reduce</c>. The cure is
    /// <c>ISynchronizationStream.ReduceShared</c>: an OPT-IN memoized reduce for an intermediate
    /// nobody owns. Opt-in, not a change to <c>Reduce</c>, because a reduced stream a caller DOES own
    /// and disposes (the Blazor <c>LayoutAreaView</c>'s dialog / progress reduces) must not be shared
    /// — one holder's teardown would kill another's. Measured: <c>compile-state</c> +13 → <b>0</b>
    /// and the NodeType's own hub +3 → <b>0</b>.</para>
    ///
    /// <para><b>What the 8.7 → 6.0 change closed (#1324): the finished activity's mirror was a
    /// KEEP-ALIVE, not merely something nobody had collected yet.</b> The previous pass left the
    /// question open as "is the residual a permanent leak or a ~25-minute bounded retention", because
    /// both existing reclaimers are time-based and invisible to a seconds-long test: the cache's idle
    /// sweep needs zero subscribers AND ten minutes untouched
    /// (<c>MeshNodeStreamCacheOptions.ReadStreamIdleExpiration</c>), and an idle node hub/grain is
    /// collected only after its own idle window. The answer is that the framing was wrong in the
    /// consumer's favour: a mirror posts a <c>HeartBeatEvent</c> to its owner every 45 s
    /// (<c>SyncStreamOptions.HeartbeatInterval</c>) for the express purpose of keeping the hub alive,
    /// and that message re-arms BOTH clocks. A finished compile was not waiting to be reclaimed — it
    /// was preventing its own reclamation, for as long as anything kept touching the path.</para>
    ///
    /// <para>The cure is an EVENT, not a shorter timer: an <c>ActivityLog</c> that reaches a status
    /// where <c>IsTerminal()</c> holds will never be written again, so
    /// <c>ActivityLogAppender.Append</c> releases the path's shared entry as part of that same write
    /// (<c>IMeshNodeStreamCache.ReleaseIfUnwatched</c>, which makes the SAME atomic zero-subscriber
    /// check the sweep makes — a reader still watching the finished activity keeps it). Measured on
    /// this repro, matched runs from an identical 38-hub baseline with four retained activities:
    /// <b>6.5 → 5.0 hubs per compile activity</b>, of which the <c>cache/</c>-side mirror hubs go
    /// <b>3 → 0</b>; totals <b>8.7 → 6.0 per recompile</b>. The direct property — no terminal activity
    /// still holds a warm mirror — is asserted at the end of the test, and fails before the fix.</para>
    ///
    /// <para><b>What is left, and why the bound is 8 rather than ~1.</b> The whole residual is now the
    /// activity's OWN node hub plus the four <c>sync/</c> sub-hubs its data source builds at startup.
    /// Nothing subscribes to it any more, so on Orleans it is finally eligible for the idle
    /// deactivation that the heartbeat used to keep pushing away.</para>
    ///
    /// <para><b>🚨 And that residual is a BOUNDED RETENTION, not a leak — measured, and it corrects
    /// what this comment used to claim.</b> It used to say "the monolith has no idle collection for
    /// node hubs at all … a host-lifecycle question". The general statement is true (the monolith
    /// host has no reaper: <c>MonolithRoutingService.CreateHub</c> creates via
    /// <c>Mesh.GetHostedHub</c> and hosted hubs die only at <c>Quiescing → DisposeHostedHubs</c>) —
    /// but an <b>Activity</b> node hub is one of the few kinds that DOES get one, monolith included:
    /// <c>ActivityNodeType.CreateMeshNode</c> → <c>.AddKernelSubHubHandlers()</c> →
    /// <c>KernelContainer.DisposeOnTimeout</c>, a one-shot idle timer (15 min,
    /// <c>KernelHubOptions.IdleDisconnectTimeout</c>), registered by <c>KernelNodeType.AddKernel()</c>
    /// which <c>AddGraph()</c> includes. It never fired for the same reason #1435 fixed: the timer is
    /// re-armed by EVERY inbound message and the finished activity's warm mirror heart-beat WAS that
    /// message. With the mirror released, the clock runs — and the hub is disposed exactly one window
    /// later. Measured unscaled at production values: flat at 15 hubs through t+885s, 5 at t+900s,
    /// <b>0 at t+915s — 13 hubs reclaimed in 15.25 minutes</b>, with the mesh's total hub count going
    /// 49 → 32. So what this test counts is a TRANSIENT that this seconds-long repro is simply too
    /// short to see expire; the ceiling on it is that window, and
    /// <see cref="CompileActivityHubRetentionTest"/> is the CI-affordable guard on it.</para>
    ///
    /// <para>Do NOT "fix" it by shortening a timer — #1324 prohibits it by name, and #1435's lesson
    /// is precisely that the clocks were correct and something was resetting them.</para>
    ///
    /// <para>So this bound is a RATCHET at the measured value, not an aspiration: it fails the moment
    /// the residual gets worse — 8 sits above the 6.0/6.7 measured here and below the 8.7 the
    /// un-released mirror produced, so a regression of THIS fix reds the test.</para>
    /// </summary>
    private const int MaxHubsPerRecompile = 8;

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
        FullyCollect();
        // Baseline AFTER the first compile, so the comparison is recompile-to-recompile and does not
        // charge the steady-state cost of having one built type to the recompiles.
        var managedBaseline = AfterCollectionBytes();
        var hubBaseline = HubAddresses();
        Output.WriteLine(
            $"initial compile settled at {lastSucceeded:HH:mm:ss.fff} — contexts: {LiveContexts()}, "
            + $"managed baseline = {managedBaseline / (1024 * 1024)} MB, live hubs = {hubBaseline.Count}");

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
            Output.WriteLine(
                $"after recompile {generation}: live contexts = {LiveContexts()}, "
                + $"managed = {AfterCollectionBytes() / (1024 * 1024)} MB");
        }

        var activities = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{TypePath}/_Activity"))
            .Should().Within(30.Seconds()).Emit();

        // 🚨 THE MECHANISM, waited on before anything is counted. A terminal activity releases its
        // shared mirror as part of the write that reports the terminal status
        // (ActivityLogAppender), so this settles as fast as that write lands — but it IS a write,
        // and the NodeType flips to Ok slightly ahead of it, so the loop above can return while the
        // last compile's Complete(...) is still in flight. Waiting on the CONDITION rather than
        // snapshotting into that window is what makes both this assertion and the hub count below
        // deterministic; a fixed sleep would race CI either way (WritingTests.md).
        //
        // `compile-state` is deliberately exempt: it is the type's LIVE compile state, one fixed-id
        // node per NodeType rather than one per compile, so it does not grow with recompiles and a
        // warm mirror on it is correct.
        var compileActivities = (activities?.Items ?? [])
            .Select(n => n.Path)
            .Where(p => !p.EndsWith($"/{NodeTypeCompileStateMirror.StateId}", StringComparison.Ordinal))
            .ToList();
        var cache = (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var stillWarm = compileActivities;
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => stillWarm = compileActivities.Where(p => cache.IsReadStreamLive(p)).ToList())
            .Where(warm => warm.Count == 0)
            .FirstAsync()
            .Timeout(30.Seconds(), Observable.Return(stillWarm))
            .ToTask();

        FullyCollect();
        var live = LiveContexts();
        var managedGrowth = AfterCollectionBytes() - managedBaseline;

        // WHERE the bytes went. The contexts unload (above), so the retained memory belongs to
        // ordinary managed objects — and heap dumps around this exact loop said which: message hubs,
        // almost all of them `sync/{id}` sub-hubs (one per SynchronizationStream), each with its own
        // Autofac scope, TypeRegistry and JsonSerializerOptions. So the hub DELTA is the primary
        // measurement and it is ASSERTED below; the parent attribution is printed so a regression
        // names its own retainer instead of leaving the next investigation to re-derive it.
        var hubsAfter = HubAddresses();
        var hubGrowth = hubsAfter.Count - hubBaseline.Count;
        Output.WriteLine(
            $"WHERE: live hubs {hubBaseline.Count} -> {hubsAfter.Count} (+{hubGrowth} over {Recompiles} "
            + $"recompiles = {(double)hubGrowth / Recompiles:F1} per recompile), compile-activity nodes "
            + $"under {TypePath}/_Activity = {activities?.Items?.Count ?? -1}");
        var newHubs = hubsAfter.Except(hubBaseline).ToHashSet();
        foreach (var line in HubsByParent(newHubs))
            Output.WriteLine($"  {line}");
        // The SHARP number. The total above is a difference of two whole-mesh counts, so it carries
        // the settling of every unrelated framework hub with it — measured 5.3 / 7.0 / 6.3 for the
        // SAME code. This one counts only hubs a compile activity is responsible for (its node hub,
        // anything hosted under it, and the mirror sync hubs under the shared cache), which is the
        // population the retention is about and the one a fix has to move.
        var attributed = AttributedToCompileActivities(newHubs);
        Output.WriteLine(
            $"ATTRIBUTED to compile activities: {attributed.Total} hubs across {attributed.Activities} "
            + $"retained activities = {attributed.PerActivity:F1} PER ACTIVITY — "
            + $"{attributed.OwnerSide} owner-side (the activity node hub + its own streams), "
            + $"{attributed.MirrorSide} mirror-side (the shared cache's client mirror)");
        Output.WriteLine($"FINAL live contexts for {TypePath} after {Recompiles} recompiles: {live}");
        Output.WriteLine(
            $"FINAL managed growth over {Recompiles} recompiles: "
            + $"{managedGrowth / (1024 * 1024)} MB ({managedGrowth / Recompiles / (1024 * 1024)} MB per recompile)");

        // ≤2, not ==1: the newest build is legitimately live, and one predecessor may still be
        // inside its unload (a pin drained, the LoaderAllocator awaiting the next GC). What must
        // NEVER hold is a count that tracks the recompiles — that is a generation pinned per rebuild,
        // which is the 130 MB/min curve measured in production.
        live.Should().BeLessThanOrEqualTo(2,
            $"every superseded DynamicNode_ context must be collectable after unload; {live} alive "
            + $"after {Recompiles} recompiles means a generation is pinned per rebuild");

        // 🚨 BYTES, not just context counts. Contexts unloading proves the ALC bookkeeping is right;
        // it does NOT prove a recompile hands its memory back — Roslyn compilations, metadata and
        // symbol graphs are ordinary managed objects that outlive an unloaded context if anything
        // still references them. On memex-cloud the process grew 130–340 MB/min while contexts stayed
        // bounded, which is exactly the gap this assertion closes.
        //
        // The ceiling is a ceiling, not a target: a trivial `config => config` type compiles to a
        // few KB of IL, so anything approaching this bound means per-compile state is being retained.
        // Measured after three aggressive blocking collections, so transient allocation is excluded.
        //
        // 🚨 WHERE THE BYTES WENT (#1324, answered by heap dumps around this exact loop): NOT Roslyn.
        // The retained objects were message hubs — 63 of them per recompile, +189 over three, each
        // with its own Autofac lifetime scope, TypeRegistry and JsonSerializerOptions. Almost all
        // were `sync/{id}` sub-hubs, one per SynchronizationStream, because
        // `Workspace.GetStream(reference)` reduced the data-source stream AGAIN on every call and
        // registered the result on the PARENT stream — so a hub per call, released only when the
        // owning hub died. The PatchDataRequest handler does that on every cross-hub write and
        // MeshNodeStreamHandle on every own-node read/write, which is why a compile (which writes
        // its NodeType, its `_Activity/compile-state` and a fresh `_Activity/compile-<ts>`) cost 20 MB.
        // Caching local reduced streams the way remote ones were always cached took it to ~9 MB
        // and 22 hubs, and took the mesh's steady-state hub count from 187 to 48.
        //
        // 8 MB, not single digits, is deliberate. Both stream-side retainers are now fixed (#1324 —
        // see MaxHubsPerRecompile): the eviction parking took this from ~8 to ~6–7 MB, and sharing
        // the owner-side intermediate reduce took it to 3–5 MB. What is left belongs to the
        // per-compile activity NODE hubs named there. Tighten this bound when that is fixed — a
        // bound nothing can currently pass is a red test, not a guard. Bytes also move with
        // unrelated allocation, which is exactly why the hub delta below is the primary assertion.
        var perRecompile = managedGrowth / Recompiles;
        perRecompile.Should().BeLessThan(8 * 1024 * 1024,
            $"a recompile of a trivial type must return its memory; {perRecompile / (1024 * 1024)} MB "
            + $"retained per recompile ({managedGrowth / (1024 * 1024)} MB over {Recompiles}) means "
            + "compile state survives the context that owned it");

        // 🚨 AND THE HUBS. The byte bound alone is a coarse instrument — it moves with unrelated
        // allocation and it never says what is being retained, which is why two investigations
        // chased the wrong suspect before heap dumps named message hubs. This is the direct
        // measurement of the same thing, in the unit the fix will be reasoned about, and the
        // per-parent breakdown above turns a failure into a diagnosis. See MaxHubsPerRecompile.
        var hubsPerRecompile = (double)hubGrowth / Recompiles;
        hubsPerRecompile.Should().BeLessThanOrEqualTo(MaxHubsPerRecompile,
            $"a recompile must hand its message hubs back; {hubsPerRecompile:F1} retained per recompile "
            + $"({hubGrowth} over {Recompiles}) — each carries an Autofac scope, a TypeRegistry and a "
            + "JsonSerializerOptions, and in production that is the 130 MB/min curve. The breakdown "
            + "printed above names which parent hub grew");

        // 🚨 AND THE MECHANISM, stated directly rather than inferred from a count. Asserted LAST so
        // that a failure still carries the full attribution printed above — the diagnosis and the
        // verdict arrive in one output.
        stillWarm.Should().BeEmpty(
            "a terminal activity is never written again, so its shared mirror must be released when "
            + "the activity ends — leaving it up does not postpone reclamation, it PREVENTS it: the "
            + "mirror posts a HeartBeatEvent to the owner every 45 s expressly to keep its hub/grain "
            + $"alive, which re-arms every idle clock the platform has. Still warm: {string.Join(", ", stillWarm)}");
    }

    /// <summary>
    /// Every live hub in the mesh's hosted-hub tree, by address. Read from
    /// <c>GetDisposalDiagnostics</c>, which walks the tree recursively and prints one line per hub —
    /// the only public surface that enumerates them.
    /// </summary>
    private List<string> HubAddresses() =>
        System.Text.RegularExpressions.Regex
            .Matches(Mesh.GetDisposalDiagnostics(), @"Hub (\S+) RunLevel")
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// <summary>
    /// Groups the newly-appeared hubs by their PARENT in the hosted-hub tree — the attribution that
    /// turns "+66 hubs" into "+22 under <c>_Activity/compile-state</c>, +17 under <c>cache/</c>, …".
    /// The diagnostics tree is indented two spaces per level, so the enclosing hub is simply the last
    /// address seen at a shallower depth. <c>sync/{id}</c> addresses are bucketed (they are opaque
    /// per-stream ids; their PARENT is the informative half).
    /// </summary>
    private IEnumerable<string> HubsByParent(IReadOnlySet<string> newHubs)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        foreach (var line in Mesh.GetDisposalDiagnostics().Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)Hub (\S+) RunLevel");
            if (!m.Success) continue;
            var depth = m.Groups[1].Value.Length / 2;
            var address = m.Groups[2].Value;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : "<root>";
            stack.Add(address);
            if (!newHubs.Contains(address)) continue;
            var child = address.StartsWith("sync/", StringComparison.Ordinal) ? "sync/*" : address;
            var key = $"{parent}  >>  {child}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.OrderByDescending(kv => kv.Value).Select(kv => $"+{kv.Value,3}  {kv.Key}");
    }

    /// <summary>
    /// Splits the newly-retained hubs into the two halves the compile activities are responsible
    /// for, walking the same indented disposal tree <see cref="HubsByParent"/> does:
    /// <list type="bullet">
    ///   <item><b>owner-side</b> — the <c>_Activity/compile-&lt;ts&gt;</c> node hub itself and every
    ///     hub hosted under it (its own data-source sync streams);</item>
    ///   <item><b>mirror-side</b> — the <c>sync/</c> hubs under the process-wide <c>cache/</c> hub,
    ///     i.e. the client end of the shared mirror the write path opened.</item>
    /// </list>
    /// The mirror side is what the terminal-status release reclaims, and it is what carried the 45 s
    /// heartbeat that stopped the owner side from ever going idle.
    /// </summary>
    private (int Total, int OwnerSide, int MirrorSide, int Activities, double PerActivity)
        AttributedToCompileActivities(IReadOnlySet<string> newHubs)
    {
        const string activityMarker = "/_Activity/compile-";
        var owner = 0;
        var mirror = 0;
        var activities = 0;
        var stack = new List<string>();
        foreach (var line in Mesh.GetDisposalDiagnostics().Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)Hub (\S+) RunLevel");
            if (!m.Success) continue;
            var depth = m.Groups[1].Value.Length / 2;
            var address = m.Groups[2].Value;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : "<root>";
            stack.Add(address);
            if (!newHubs.Contains(address)) continue;
            if (address.Contains(activityMarker, StringComparison.Ordinal))
            {
                activities++;
                owner++;
            }
            else if (parent.Contains(activityMarker, StringComparison.Ordinal))
                owner++;
            else if (parent.StartsWith("cache/", StringComparison.Ordinal))
                mirror++;
        }
        // Normalised PER ACTIVITY, because how many of the loop's activities land inside the
        // baseline-to-final delta varies run to run (3 or 4) — dividing by Recompiles would fold
        // that straight back into the number this metric exists to keep clean.
        return (owner + mirror, owner, mirror, activities,
            activities == 0 ? 0 : (double)(owner + mirror) / activities);
    }

    /// <summary>
    /// Managed bytes with a full collection forced FIRST — the opposite choice from
    /// <c>MemoryDelta</c>, which must never collect because it runs in production. Here the question
    /// is what SURVIVES collection, so paying for the collection is the point.
    /// </summary>
    private static long AfterCollectionBytes() => GC.GetTotalMemory(forceFullCollection: true);

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
