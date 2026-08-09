using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Prod repro (prod 2026-07-02/03): an UNPINNED structured query (no <c>namespace:</c>, no
/// <c>path:</c> — e.g. <c>nodeType:Document</c> / <c>nodeType:Markdown</c> from the MCP
/// <c>search</c> tool) issued by a REAL user hung forever — the merged query stream never
/// emitted its Initial, the caller's <c>Take(1)</c> waited indefinitely, and the MCP client
/// aborted at 300s. System-identity queries (boot/background) and partition-pinned queries
/// answered instantly, and PostgreSQL was idle throughout — the emission is lost in the
/// provider→merge pipeline, not in SQL.
///
/// <para>These tests pin the CONTRACT the production flow relies on: an unpinned structured
/// query through the synced-node surface (<c>workspace.GetQuery</c>) must converge promptly
/// — for the auto-admin caller, for an ordinary sample user, and for the System identity
/// alike. A hang shows up as the 20s Rx timeout, well inside the test timeout.</para>
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionRealUserQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddGraph();
    }

    // Stays IObservable — the reactive assertion subscribes it. Bridging to Task here would put
    // ObjectAssertions (not ObservableAssertions) on the other side of .Should().
    //
    // LastOrDefaultAsync collapses the per-provider emissions into ONE terminal value that arrives
    // only after Concat has run EVERY provider to completion — so `.Emit()` means "all providers
    // provisioned", not "the first one did". (`.Complete()` cannot be used: it is
    // IgnoreElements().ToTask(), which throws "Sequence contains no elements" for any source that
    // completes — i.e. for every source it is meant to accept.)
    private IObservable<Unit> ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastOrDefaultAsync();

    /// <summary>
    /// Two provisioned partitions with one Markdown node each — the minimal cross-schema world.
    ///
    /// <para>
    /// 🚨 Every terminal assertion here MUST be awaited. They are the sanctioned Rx→Task bridge
    /// (ObservableAssertions: "the bridge lives in the assertion, never the test body") and they do
    /// NOT block — dropping the <c>await</c> turns provisioning and seeding into fire-and-forget,
    /// so the queries below run against an empty world and every positive assertion fails while a
    /// negative one passes vacuously.
    /// </para>
    /// </summary>
    private async Task<(string NsA, string NsB)> SeedTwoPartitions()
    {
        var nsA = $"xq_{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var nsB = $"xq_{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        await ProvisionPartition(nsA).Should().Within(30.Seconds()).Emit();
        await ProvisionPartition(nsB).Should().Within(30.Seconds()).Emit();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        foreach (var ns in new[] { nsA, nsB })
            await meshService.CreateNode(new MeshNode("doc1", ns)
            {
                Name = $"Doc in {ns}",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }).Should().Within(15.Seconds()).Emit();

        return (nsA, nsB);
    }

    /// <summary>
    /// The unpinned (cross-partition) query the production surfaces issue, through the CANONICAL
    /// synced-node surface <c>workspace.GetQuery(id, query)</c> — the same path the MCP
    /// <c>search</c> tool and every layout area take.
    ///
    /// <para>
    /// 🚨 <paramref name="namespaceScope"/> is not decoration: without it this is a GLOBAL
    /// <c>nodeType:Markdown</c> query capped at <c>limit</c>, so any assertion about a SPECIFIC
    /// seeded row only holds while fewer than <c>limit</c> other Markdown nodes exist anywhere.
    /// Against a shared database with sibling tests writing concurrently the seeded row is simply
    /// paged out — an intermittent failure that lands on PRs which changed nothing (#834). Tests
    /// that assert on a particular row MUST scope; tests that only assert that the query converges
    /// may stay global. Waiting longer cannot fix a paged-out row, so scoping is the root fix and
    /// the <c>Match</c> predicate below is the separate, complementary one.
    /// </para>
    ///
    /// <para>
    /// The cache id carries the scope so each test gets its own entry (GetQuery caches by
    /// <c>(id, userId)</c>) and cannot inherit a sibling's snapshot. Identity comes from the
    /// ambient <see cref="AccessContext"/>, not a request field — callers wrap in
    /// <c>SwitchAccessContext</c>/<c>ImpersonateAsSystem</c>, which is how production drives it.
    /// </para>
    ///
    /// <para>
    /// 🚨 <see cref="Projection"/> is not an optimisation, it is required here. Unlike the one-shot
    /// <c>IMeshService.Query</c> this replaced, <c>GetQuery</c> registers a PERSISTENT synced query
    /// (<c>Replay(1).RefCount()</c>) that stays subscribed for the workspace's lifetime. Two of
    /// these are deliberately UNSCOPED cross-partition reads, so without a projection each one holds
    /// a live cross-schema subscription streaming the full CONTENT of every Markdown node in a
    /// database shared with every other test class in this process. These tests read
    /// <see cref="MeshNode.Path"/> and nothing else.
    /// </para>
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> RunUnpinned(string id, string? namespaceScope = null) =>
        Mesh.GetWorkspace().GetQuery(
            $"test:unpinned:{id}",
            namespaceScope is null
                ? $"nodeType:Markdown limit:10 {Projection}"
                : $"namespace:{namespaceScope} nodeType:Markdown limit:10 {Projection}");

    /// <summary>
    /// 🚨 <c>id</c> and <c>namespace</c>, NOT <c>path</c>: <see cref="MeshNode.Path"/> is a COMPUTED
    /// property (<c>Namespace + "/" + Id</c>), not a stored column, so a projection naming only
    /// <c>path</c> yields a node whose <c>Path</c> is empty and every assertion below silently
    /// compares against the wrong string. <c>content</c> is deliberately absent — a test needing
    /// content reads the node's content reducer stream instead of widening this query.
    /// </summary>
    private const string Projection = "select:id,namespace";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The exact production shape: the MCP search tool builds the request WITHOUT a UserId,
    /// so the ambient caller identity (the auto-admin circuit user here, rbuergi on prod)
    /// drives the secured provider surface. The Initial must arrive promptly.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsAmbientRealUser_EmitsInitial()
    {
        var (nsA, _) = await SeedTwoPartitions();

        // Liveness ONLY, and deliberately GLOBAL (unscoped) — that is the production shape this
        // pins. It must NOT assert a specific row: on an unscoped limit:10 query a concurrently
        // written sibling row pages this one out, which is the flake #834 chased.
        await RunUnpinned(nsA)
            .Should().Within(20.Seconds())
            .Emit("the ambient real user's unpinned query must converge to a snapshot, not hang");
    }

    /// <summary>Explicit non-admin sample user — the per-result RLS path.</summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsExplicitUser_EmitsInitial()
    {
        var (nsA, _) = await SeedTwoPartitions();

        // Liveness ONLY — the original claim. What this pins is the prod repro: the query must
        // CONVERGE (emit a snapshot) rather than hang forever. Whether a non-admin sees these rows
        // is an RLS question this test deliberately does not assert; inventing a content claim here
        // risks a vacuous pass that looks like coverage.
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = "some.user", Name = "some.user" }))
            await RunUnpinned($"{nsA}:explicit")
                .Should().Within(20.Seconds())
                .Emit("a non-admin's unpinned query must converge to a snapshot, not hang");
    }

    /// <summary>Control: the System identity (boot/background queries) — known-good on prod.</summary>
    /// <remarks>
    /// <para>
    /// 🚨 The visibility assertion is FOLDED INTO the predicate rather than asserted after a
    /// one-shot read. The synced query is eventually consistent (CqrsAndContentAccess.md:
    /// "QueryAsync/ObserveQuery are eventually consistent — stale after writes"), so a snapshot
    /// computed before the index caught up with <see cref="SeedTwoPartitions"/>'s just-completed
    /// writes is CORRECT behaviour, not a defect. The previous shape took the first emission and
    /// asserted content on it — a read-your-writes assumption the API never makes, which passed
    /// only while the index won the race and failed once a runner's timing shifted.
    /// <c>Match(predicate)</c> waits for the RIGHT state, which is the whole point
    /// (code skill: "folding the assertion into the predicate … removes the 'wait, then assert'
    /// race that flakes in CI").
    /// </para>
    ///
    /// <para>
    /// The liveness contract this class exists for (the prod hang) is unchanged: the 20 s bound
    /// still fails the test if the query never converges.
    /// </para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsSystem_EmitsInitial()
    {
        var (nsA, _) = await SeedTwoPartitions();

        using (Access.ImpersonateAsSystem())
            await RunUnpinned($"{nsA}:system", namespaceScope: nsA)
                .Should().Within(20.Seconds())
                .Match(items => items.Any(n => n.Path == $"{nsA}/doc1"),
                    "System reads a partition it was never granted — the query is scoped to that "
                    + "partition, so this cannot be crowded out by rows other tests happen to be writing");
    }
}
