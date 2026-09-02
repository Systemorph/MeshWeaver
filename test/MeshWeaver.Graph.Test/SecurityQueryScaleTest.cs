using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #3093 — the per-user RLS filter on a SHARED synced query
/// (<c>SyncedQueryDataSourceExtensions.FilterByReadPermission</c>) runs one
/// <c>hub.CheckPermission</c> per node in the snapshot, and each of those folds walked the node's
/// whole SCOPE CHAIN, opening a separate process-wide cached mesh query per scope.
///
/// <para><b>The complexity property this pins.</b> A node's own path is always the LEAF of its own
/// scope chain, so the per-scope walk mints at least one <c>$security-access:{path}</c> plus one
/// <c>$security-policy:{path}</c> live query <b>per node in the snapshot</b> — a population that
/// grows with the listing, never shrinks below the cache's idle window, and gates the subscriber's
/// first frame (<c>.ToList()</c>) on all of them. That is O(nodes) by construction, whatever the
/// machine speed, so this test counts QUERIES rather than milliseconds.</para>
///
/// <para><b>Why counting is the honest measurement.</b> Wall-clock here is a guess about how fast
/// one laptop is; the defect is structural. The census below is exactly the quantity the issue's
/// production log measures (<c>[CrossSchema]</c> / per-scope reads on the `access` and
/// `mesh_nodes` relations), reproduced deterministically.</para>
///
/// <para>🚨 The two arms are two SEPARATE partitions on purpose. The shared legs (root scope,
/// memberships, roles, gated types) are process-wide cached, so the first arm warms them and the
/// second arm measures only what its OWN nodes cost. Comparing a small arm with a large one inside
/// one mesh is what makes "does not grow with node count" assertable at all.</para>
/// </summary>
public class SecurityQueryScaleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The viewer. Deliberately not the harness admin — an admin short-circuit would make
    /// every arm cost the same for the wrong reason.</summary>
    private const string Viewer = "scale-viewer";

    private const string SmallPartition = "AlphaSpace";
    private const string LargePartition = "BetaSpace";

    private const int SmallCount = 4;
    private const int LargeCount = 32;

    /// <summary>
    /// The census of security queries the fold opened, as an INSTANCE owned by this test (xUnit
    /// builds one test object per case) — never static state.
    /// </summary>
    private readonly SecurityQueryCensus census = new();

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), under
    // which every identity is Admin everywhere and the fold short-circuits before it reads
    // anything — every arm would then cost zero queries and the test would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Last registration wins over the persistence layer's TryAddSingleton, and the
            // decorator delegates to the real cache — so this observes the production fold,
            // it does not replace it.
            .ConfigureServices(services => services.AddSingleton<IMeshNodeStreamCache>(
                sp => new CountingMeshNodeStreamCache(
                    sp.GetRequiredService<MeshNodeStreamCache>(), census)))
            .AddMeshNodes(
                new MeshNode[]
                {
                    new(SmallPartition) { Name = "Alpha", NodeType = "Markdown" },
                    AssignmentNodeFactory.Policy(SmallPartition, new PartitionAccessPolicy { PublicRead = true }),
                    new(LargePartition) { Name = "Beta", NodeType = "Markdown" },
                    AssignmentNodeFactory.Policy(LargePartition, new PartitionAccessPolicy { PublicRead = true }),
                }
                .Concat(Enumerable.Range(0, SmallCount).Select(i =>
                    new MeshNode($"n{i}", SmallPartition) { Name = $"Alpha {i}", NodeType = "Markdown" }))
                .Concat(Enumerable.Range(0, LargeCount).Select(i =>
                    new MeshNode($"n{i}", LargePartition) { Name = $"Beta {i}", NodeType = "Markdown" }))
                .ToArray());

    /// <summary>
    /// 🚨 THE PIN. Filtering a snapshot of 32 nodes must not open more security queries than
    /// filtering a snapshot of 4 nodes drawn from the same shape of partition.
    ///
    /// <para>Pre-fix this reads 8 vs 64: the per-scope walk mints
    /// <c>$security-access:{partition}/n{i}</c> and <c>$security-policy:{partition}/n{i}</c> for
    /// every single node, because a node's own path is a scope on its own chain.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSecurityFoldsQueryCountDoesNotGrowWithTheNumberOfNodesFiltered()
    {
        // Warm every process-wide leg (root scope, memberships, roles, gated types) so neither arm
        // is charged for what they share. A path in neither partition under test.
        await Filter([new MeshNode("warmup", TestPartition)]);
        census.Reset();

        var small = await MeasureArm(SmallPartition, SmallCount);
        var large = await MeasureArm(LargePartition, LargeCount);

        Output.WriteLine($"#3093 census — {SmallCount} nodes: {small.Count} security queries "
            + $"[{string.Join(", ", small.OrderBy(x => x, StringComparer.Ordinal))}]");
        Output.WriteLine($"#3093 census — {LargeCount} nodes: {large.Count} security queries "
            + $"[{string.Join(", ", large.OrderBy(x => x, StringComparer.Ordinal))}]");

        large.Count.Should().Be(small.Count,
            $"filtering {LargeCount} nodes must cost the same security reads as filtering "
            + $"{SmallCount} — the fold's inputs are the PARTITION's grants and policies, which do "
            + "not multiply by how many of that partition's nodes a listing happens to show "
            + $"(#3093). Measured {small.Count} for {SmallCount} nodes and {large.Count} for "
            + $"{LargeCount}: the cost is proportional to the node count.");
    }

    /// <summary>
    /// The companion measurement that keeps the pin above honest: the filter must still return the
    /// nodes the viewer may read. A fold that opened no queries because it answered nothing would
    /// satisfy the count assertion perfectly.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheFilterStillAdmitsExactlyTheReadableNodes()
    {
        var readable = await Filter(Enumerable.Range(0, SmallCount)
            .Select(i => new MeshNode($"n{i}", SmallPartition))
            .ToArray());

        readable.Should().HaveCount(SmallCount,
            $"{SmallPartition} is PublicRead, so every one of its nodes is readable — a count "
            + "assertion over a filter that admits nothing proves nothing");

        var denied = await Filter([new MeshNode("secret", "ForeignSpace")]);
        denied.Should().BeEmpty(
            "a partition with no policy and no grant stays denied — the cheaper fold must not be "
            + "a wider one");
    }

    private async Task<IReadOnlyCollection<string>> MeasureArm(string partition, int count)
    {
        var before = census.SecurityQueryIds;
        await Filter(Enumerable.Range(0, count)
            .Select(i => new MeshNode($"n{i}", partition))
            .ToArray());
        return census.SecurityQueryIds.Except(before, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The production shape, verbatim: <c>WrapWithPerUserRls</c>'s probe is
    /// <c>hub.CheckPermission(node.Path, userId, Read)</c> and the filter is
    /// <c>FilterByReadPermission</c>. Nothing here re-implements the fold.
    /// </summary>
    private Task<IEnumerable<MeshNode>> Filter(params MeshNode[] snapshot)
        => SyncedQueryDataSourceExtensions
            .FilterByReadPermission(
                Observable.Return<IEnumerable<MeshNode>>(snapshot),
                node => Mesh.CheckPermission(node.Path ?? string.Empty, Viewer, Permission.Read))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// Records the ids of the <c>$security-*</c> queries the permission fold opens. Instance state
    /// on an object the test owns — the ban on static mutable collections applies to test code too.
    /// </summary>
    private sealed class SecurityQueryCensus
    {
        private readonly object gate = new();
        private ImmutableHashSet<string> ids = ImmutableHashSet<string>.Empty;

        /// <summary>Ids seen so far, as a stable snapshot.</summary>
        public ImmutableHashSet<string> SecurityQueryIds
        {
            get { lock (gate) return ids; }
        }

        public void Record(object id)
        {
            var key = id.ToString() ?? string.Empty;
            if (!key.StartsWith("$security-", StringComparison.Ordinal))
                return;
            lock (gate) ids = ids.Add(key);
        }

        public void Reset()
        {
            lock (gate) ids = ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>
    /// A pass-through <see cref="IMeshNodeStreamCache"/> that records which synced queries the
    /// permission fold asks for. Every member delegates — the mesh under test is the real one.
    /// </summary>
    private sealed class CountingMeshNodeStreamCache(IMeshNodeStreamCache inner, SecurityQueryCensus census)
        : IMeshNodeStreamCache
    {
        public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options)
            => inner.GetStream(path, options);

        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update, JsonSerializerOptions options)
            => inner.Update(path, update, options);

        public IObservable<MeshNode> Overwrite(string path, MeshNode node, JsonSerializerOptions options)
            => inner.Overwrite(path, node, options);

        public void Invalidate(string path) => inner.Invalidate(path);

        public bool ReleaseIfUnwatched(string path) => inner.ReleaseIfUnwatched(path);

        IObservable<IEnumerable<MeshNode>> IMeshNodeStreamCache.GetQuery(
            object id, JsonSerializerOptions options, params string[] queries)
        {
            census.Record(id);
            return inner.GetQuery(id, options, queries);
        }

        IObservable<IEnumerable<MeshNode>>? IMeshNodeStreamCache.GetQuery(object id)
            => inner.GetQuery(id);
    }
}
