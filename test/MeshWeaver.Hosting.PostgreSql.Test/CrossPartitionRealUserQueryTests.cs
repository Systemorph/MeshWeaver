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
/// Prod repro (atioz 2026-07-02/03): an UNPINNED structured query (no <c>namespace:</c>, no
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
    /// The unpinned structured query through the CANONICAL synced-node surface,
    /// <c>workspace.GetQuery(id, query)</c> — the same path the MCP <c>search</c> tool and every
    /// layout area take. The id is per-namespace so each test gets its own cache entry (GetQuery
    /// caches by <c>(id, userId)</c>) and cannot inherit a sibling test's snapshot.
    ///
    /// <para>
    /// Identity comes from the ambient <see cref="AccessContext"/>, not a request field — callers
    /// wrap in <c>SwitchAccessContext</c>/<c>ImpersonateAsSystem</c>, which is exactly how
    /// production drives it. Content is NOT requested: these tests assert on <c>Path</c> only. If a
    /// test ever needs node content, take it from the node's content reducer stream rather than
    /// widening this query.
    /// </para>
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> RunUnpinned(string id) =>
        Mesh.GetWorkspace().GetQuery($"test:unpinned:{id}", "nodeType:Markdown limit:10");

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

        // The ambient caller is the auto-admin circuit user, so it sees the seeded row.
        await RunUnpinned(nsA)
            .Should().Within(20.Seconds())
            .Match(items => items.Any(n => n.Path == $"{nsA}/doc1"),
                "the ambient real user's unpinned query must converge, not hang");
    }

    /// <summary>Explicit non-admin sample user — the per-result RLS path.</summary>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsExplicitUser_EmitsInitial()
    {
        var (nsA, _) = await SeedTwoPartitions();

        // Liveness ONLY — the original claim. What this pins is the atioz repro: the query must
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
    /// The liveness contract this class exists for (the atioz hang) is unchanged: the 20 s bound
    /// still fails the test if the query never converges.
    /// </para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task UnpinnedStructuredQuery_AsSystem_EmitsInitial()
    {
        var (nsA, nsB) = await SeedTwoPartitions();

        using (Access.ImpersonateAsSystem())
            await RunUnpinned($"{nsA}:system")
                .Should().Within(20.Seconds())
                .Match(
                    items => items.Any(n => n.Path == $"{nsA}/doc1")
                          && items.Any(n => n.Path == $"{nsB}/doc1"),
                    "System sees every partition's rows");
    }
}
