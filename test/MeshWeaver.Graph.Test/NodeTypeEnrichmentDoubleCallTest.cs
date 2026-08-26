using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Repro for the prod outage where instance hubs of dynamic NodeTypes never
/// reach <c>_hubReady</c> in time. Trace from App Insights:
///
/// <code>
/// 06:33:20.7624  [ACTIVATE] Grain Systemorph/Events activating
/// 06:33:20.7626  [ACTIVATE] Grain Systemorph/Events: no static node with HubConfig, reading from catalog
/// 06:33:50.7665  [ACTIVATE] Grain Systemorph/Events: source emitted node=...
/// </code>
///
/// 30 s from "activating" to "source emitted" — the catalog already ran
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> via
/// <c>INodeConfigurationResolver</c> and hit the slow-path 30 s timeout
/// (compile never settled), then returned a node carrying the
/// <see cref="NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay"/>
/// HubConfiguration but <b>no</b> <c>AssemblyLocation</c>.
///
/// <para>The pre-enriched node is then handed to
/// <see cref="MeshNodeHubFactory.ResolveHubConfiguration"/>, which calls
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> again. The
/// fast-path at line 39 requires <em>both</em> <c>HubConfiguration</c> and
/// <c>AssemblyLocation</c>, so the second call drops to the slow path and
/// waits another 30 s. By the time the second timeout fires, the calling
/// Orleans grain has long since broken its <c>WaitAsync(30s)</c> at
/// <c>MessageHubGrain.cs:248</c>.</para>
///
/// <para>The fix: short-circuit the fast path when the node already carries a
/// <c>HubConfiguration</c> — re-enriching an already-enriched node cannot
/// produce a different result within the same slow-path window.</para>
///
/// <para>🚨 Drives <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> against a REAL mesh
/// hub (<see cref="MonolithMeshTestBase"/>) — never a mocked one (Systemorph/MeshWeaver#1810:
/// AGENTS.md forbids mocking <c>IMessageHub</c>). With a real hub — which always has
/// <c>IMeshQueryCore</c> registered — a NodeType with no registration and no persisted node hits
/// the FAST existence probe (<c>NodeTypeProbeTimeout</c> = 3 s), not the 30-60 s slow path: the
/// probe answers "nothing there" in well under a second and the code applies the SAME
/// compilation-error overlay (with <c>HubConfiguration</c> set) that the slow-path timeout would
/// have produced. That overlay is what makes the double-enrichment short-circuit
/// (<c>if (node.HubConfiguration != null) return node;</c>) the property under test here — the
/// real "unregistered NodeType" failure mode, not an artifact of a hub missing a DI registration.
/// </para>
/// </summary>
public class NodeTypeEnrichmentDoubleCallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The fast-probe budget (mirrors <c>NodeTypeEnrichmentHelpers.NodeTypeProbeTimeout</c> = 3s)
    /// plus observation slack. Kept tight so a regression shows up immediately rather than as a
    /// "slow test".
    /// </summary>
    private static readonly TimeSpan SingleProbeBudget = TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(5);

    private static MeshConfiguration EmptyMeshConfiguration() =>
        new(Array.Empty<MeshNode>());

    /// <summary>
    /// Prod-shape repro, adapted to a real mesh's FAST path: a NodeType with no static
    /// registration and no persisted node at that path drives the existence-probe overlay; the
    /// catalog enriches the instance once (returns an already-enriched node with a
    /// compilation-error overlay HubConfiguration but no AssemblyLocation), then the per-grain
    /// factory enriches it a second time. Total wall time MUST stay within roughly ONE probe
    /// window — a second probe (or worse, a second slow-path wait) pushes the activation past the
    /// caller's timeout and looks like a dead grain to every caller (the prod symptom this repro
    /// is named for, just triggered by the probe path instead of the slow path on a real mesh).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DoubleEnrichment_StaysWithinOneProbeWindow()
    {
        var cfg = EmptyMeshConfiguration();

        var bareInstance = new MeshNode("instance1", TestPartition)
        {
            // Deliberately unregistered — no AddXxxType() call anywhere in this mesh's
            // configuration, and no node persisted at this path either.
            NodeType = $"{TestPartition}/NoSuchEventCalendarType",
        };

        var sw = Stopwatch.StartNew();

        // Pass 1 — catalog-side enrichment via INodeConfigurationResolver. The fast existence
        // probe finds nothing registered, applies the compilation-error overlay.
        var afterCatalog = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(Mesh, cfg, compilationService: null, bareInstance)
            .Take(1)
            .Should().Within(SingleProbeBudget).Emit("the probe path must always emit — fall back to overlay");

        var afterPass1 = sw.Elapsed;
        afterCatalog.Should().NotBeNull("the probe path must always emit — fall back to overlay");
        afterCatalog.HubConfiguration.Should().NotBeNull(
            "WithCompilationErrorOverlay always sets HubConfiguration so callers can build the hub");

        // Pass 2 — MessageHubGrain hands the catalog-enriched node back into
        // ResolveHubConfigurationObservable → EnrichWithNodeType. The double-enrichment
        // short-circuit (node.HubConfiguration != null) must return it UNCHANGED and
        // SYNCHRONOUSLY — never re-probing, never re-entering the slow path.
        await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(Mesh, cfg, compilationService: null, afterCatalog)
            .Take(1)
            .Should().Within(500.Milliseconds()).Emit();

        sw.Stop();

        sw.Elapsed.Should().BeLessThan(
            SingleProbeBudget,
            "double enrichment must not double the wall time — re-enriching an already-enriched node " +
            $"is what makes the prod grain miss its activation deadline. Pass 1 alone took {afterPass1.TotalSeconds:0.0}s.");
    }

    /// <summary>
    /// Direct probe of the fast-path semantic the prod fix turns on: an
    /// already-enriched node (HubConfiguration set, AssemblyLocation null —
    /// the WithCompilationErrorOverlay shape) re-entered into
    /// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> must NOT touch any mesh service
    /// at all. If it does, a hung/absent NodeType turns every downstream re-enrichment into a
    /// fresh probe (or worse, the 30-60s slow path) — the bug.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task PreEnrichedNode_DoesNotReEnterTheProbeOrSlowPath()
    {
        var cfg = EmptyMeshConfiguration();

        var preEnriched = new MeshNode("instance1", TestPartition)
        {
            NodeType = $"{TestPartition}/NoSuchEventCalendarType",
            // The shape WithCompilationErrorOverlay produces: HubConfiguration set
            // (so the caller can instantiate a hub), but no NodeTypeDefinition
            // Content because no DLL was actually emitted.
            HubConfiguration = c => c,
        };

        var sw = Stopwatch.StartNew();
        var result = await NodeTypeEnrichmentHelpers
            .EnrichWithNodeType(Mesh, cfg, compilationService: null, preEnriched)
            .Take(1)
            .Should().Within(5.Seconds()).Emit();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "an already-enriched node must short-circuit synchronously — anything else " +
            "means EnrichWithNodeType is going to redo work whose answer can't change inside " +
            "this window, the very pattern that caused the prod 60 s activation hang.");
        result.HubConfiguration.Should().NotBeNull(
            "the fast path must preserve the HubConfiguration the caller already resolved");
    }
}
