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
using MeshWeaver.Messaging;
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
/// <para>🚨 The two arms are two SEPARATE partitions on purpose, and each is measured from a reset
/// census. The shared legs (root scope, memberships) are process-wide cached, so counting only what
/// an arm opened FIRST would charge them to whichever arm ran first and make the two numbers
/// incomparable; counting what each arm ASKS FOR charges both equally.</para>
/// </summary>
public class SecurityQueryScaleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The viewer. Deliberately not the harness admin — an admin short-circuit would make
    /// every arm cost the same for the wrong reason.</summary>
    private const string Viewer = "scale-viewer";

    private const string SmallPartition = "AlphaSpace";
    private const string LargePartition = "BetaSpace";

    /// <summary>Partition for the runtime-grant arm — its own, so a grant written there cannot
    /// perturb the two counting arms.</summary>
    private const string GrantPartition = "GrantSpace";

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
                    new(GrantPartition) { Name = "Grant", NodeType = "Markdown" },
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
    /// <para>Measured on this tree. Pre-fix: <b>13</b> for 4 nodes, <b>69</b> for 32 — the
    /// per-scope walk mints <c>$security-access:{partition}/n{i}</c> and
    /// <c>$security-policy:{partition}/n{i}</c> for every single node, because a node's own path is
    /// a scope on its own chain. Post-fix: <b>5</b> and <b>5</b> — the root scope's two legs, the
    /// membership leg, and the partition's own two.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSecurityFoldsQueryCountDoesNotGrowWithTheNumberOfNodesFiltered()
    {
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
    /// 🚨 THE CORRECTNESS PIN for the cheaper read. The fold no longer asks "what grants exist at
    /// scope S" for each S on the chain; it asks "what grants exist in this PARTITION" once. That
    /// is only sound if the partition read actually RETURNS a grant written at a nested scope —
    /// and a read that came back short would fail CLOSED, silently, with nothing logged.
    ///
    /// <para>Written at RUNTIME rather than declared as a static node, on purpose: a static
    /// <c>AccessAssignment</c> reaches the fold through <c>CollectStaticAccessAssignments</c> and
    /// never touches the query at all, so a suite of static grants would pass no matter what the
    /// query shape did.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARuntimeGrantAtANestedScopeStillResolves()
    {
        const string deepScope = $"{GrantPartition}/Section";
        const string target = $"{deepScope}/Leaf";

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        (await Effective(target)).HasFlag(Permission.Read).Should().BeFalse(
            "the control arm — before the grant is written, the viewer reads nothing here, so the "
            + "assertion below cannot pass for an unrelated reason");

        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(
                    new MeshNode("Section", GrantPartition) { NodeType = "Markdown", Name = "Section" })
                .Should().Emit();
            await meshService.CreateNode(
                    new MeshNode("Leaf", deepScope) { NodeType = "Markdown", Name = "Leaf" })
                .Should().Emit();
            await meshService.CreateNode(AssignmentNodeFactory.UserRole(Viewer, "Viewer", deepScope))
                .Should().Emit();
        }

        // The fold is live, so wait on the VERDICT rather than on a propagation delay.
        var granted = await Mesh.GetEffectivePermissions(target, Viewer)
            .Where(p => p.HasFlag(Permission.Read))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        granted.HasFlag(Permission.Read).Should().BeTrue(
            $"a Viewer grant written at '{deepScope}/_Access' must reach the fold — the partition "
            + "read is a superset of the per-scope walk it replaced, so anchoring it to the "
            + "partition cannot lose a grant (#3093)");
    }

    /// <summary>
    /// The companion measurement that keeps the count pin honest: the filter must still return the
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

    /// <summary>
    /// The security queries one arm ASKS FOR — reset before each arm, so both are charged for the
    /// process-wide legs (root scope, memberships) they both ask for. Counting only what an arm
    /// opened FIRST would charge the shared legs to whichever arm ran first and make the two
    /// numbers incomparable.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> MeasureArm(string partition, int count)
    {
        census.Reset();
        await Filter(Enumerable.Range(0, count)
            .Select(i => new MeshNode($"n{i}", partition))
            .ToArray());
        return census.SecurityQueryIds;
    }

    /// <summary>
    /// The production shape, verbatim: <c>WrapWithPerUserRls</c>'s probe is
    /// <c>hub.CheckPermission(node.Path, userId, Read)</c> and the filter is
    /// <c>FilterByReadPermission</c>. Nothing here re-implements the fold.
    /// </summary>
    private Task<Permission> Effective(string path)
        => Mesh.GetEffectivePermissions(path, Viewer)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

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
