#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins #1950: the <c>Plugins</c> install-records partition must be visible to the REAL,
/// SQL-backed query path — the one every catalog surface and the registry's bundle index issue —
/// for an ordinary signed-in principal.
///
/// <para><b>Why a Postgres test and not another monolith one.</b> <c>PluginsPartitionReadableTest</c>
/// already pins this on the in-memory path and passes, because there the LIVE
/// <c>PermissionEvaluator</c> reads the STATIC <c>PartitionAccessPolicy</c> that
/// <c>AddPluginCatalog</c> registers. Postgres does not: a partition-scoped query is pre-filtered by
/// <c>public.partition_access</c>, whose rows come from <c>rebuild_user_effective_permissions()</c>
/// folding the DURABLE <c>mesh_nodes</c> row for <c>node_type='PartitionAccessPolicy' AND
/// id='_Policy'</c>. A static-only policy has no such row, so the fold projects nothing, no
/// <c>partition_access</c> row exists for <c>plugins</c>, and the whole schema drops out of every
/// query — for EVERY principal, platform admins included, while <c>get Plugins/&lt;id&gt;</c> by exact
/// path still works. That is exactly what took the registry's bundle feed dark on both production
/// databases on 2026-08-20: <c>/api/plugins/bundles/index.json</c> served <c>{"bundles": []}</c> to a
/// correctly-granted consumer, every module decided <c>SkipNoBundle</c>, and nothing logged a thing.</para>
///
/// <para>So the assertion here goes through <c>IMeshService.Query</c> under a plain viewer identity —
/// never a direct <c>IStorageAdapter</c> read, which bypasses the permission fold and would pass
/// against the defect.</para>
/// </summary>
[Collection("PostgreSql")]
public class PluginsRecordsPartitionVisibilityTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    /// <summary>An ordinary signed-in principal: no grants anywhere, no Admin claim. The registry's
    /// bundle index runs under the HTTP request's identity, and the catalog surfaces run under the
    /// viewer's — neither is System.</summary>
    private const string Consumer = "consumer";

    private const string PackageId = "visible-pack";

    /// <summary>
    /// Puts the shared container back into the state this is about — the durable <c>_Policy</c>
    /// row gone and no <c>partition_access</c> rows for <c>plugins</c> — so a leftover from an
    /// earlier run in the same container can never satisfy the assertions and let the test pass
    /// against the very defect it pins. It VERIFIES its own effect, because a reset that cannot
    /// fail is not a reset.
    ///
    /// <para>The schema itself is left alone deliberately: partition provisioning is
    /// promise-cached per mesh, so dropping it out from under a built mesh only produces
    /// <c>3F000</c>.</para>
    ///
    /// <para>🚨 Existence is asked of <c>pg_catalog</c>, never <c>information_schema.schemata</c> —
    /// the latter lists only schemas the CURRENT ROLE owns, so a partition schema created by the
    /// provisioning function reads as ABSENT there. A guard written against it skips the delete
    /// and a count written against it returns 0, which made both the reset and its verification
    /// silently vacuous.</para>
    /// </summary>
    private IObservable<System.Reactive.Unit> ResetPluginsPartition(CancellationToken ct) =>
        _fixture.DataSource.ExecuteNonQuery(
                """
                DO $$ BEGIN
                  IF to_regclass('plugins.mesh_nodes') IS NOT NULL THEN
                    DELETE FROM plugins.mesh_nodes
                    WHERE node_type = 'PartitionAccessPolicy' AND id = '_Policy';
                  END IF;
                END $$;
                """, ct)
            .SelectMany(_ => _fixture.DataSource.ExecuteNonQuery(
                "DELETE FROM public.partition_access WHERE partition = 'plugins'", ct))
            .SelectMany(_ => PolicyRowCount(ct))
            .Do(count => count.Should().Be(0L, "the reset must leave no durable _Policy row"))
            .SelectMany(_ => PartitionAccessRowCount(ct))
            .Do(count => count.Should().Be(0L, "the reset must leave no partition_access row"))
            .Select(_ => System.Reactive.Unit.Default);

    /// <summary>Durable <c>_Policy</c> rows in the <c>plugins</c> schema — 0 when the schema (or
    /// its table) does not exist yet, which is the same answer and an honest one.</summary>
    private IObservable<long> PolicyRowCount(CancellationToken ct) =>
        _fixture.DataSource
            .ScalarLong("SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = 'plugins' AND c.relname = 'mesh_nodes'", ct)
            .SelectMany(table => table == 0
                ? Observable.Return(0L)
                : _fixture.DataSource.ScalarLong(
                    "SELECT COUNT(*) FROM plugins.mesh_nodes "
                    + "WHERE node_type = 'PartitionAccessPolicy' AND id = '_Policy'", ct));

    private IObservable<long> PartitionAccessRowCount(CancellationToken ct) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM public.partition_access WHERE partition = 'plugins'", ct);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddPluginCatalog();
    }

    /// <summary>
    /// End to end on the production shape: install a package (records land in <c>Plugins</c> under
    /// System, so no creator grant is ever minted), then issue the EXACT query
    /// <c>PluginBundleEndpoints.InstalledPackages</c> issues, under a non-System identity.
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task InstalledRecords_AreVisibleToASignedInConsumer_ThroughTheRealQueryPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetPluginsPartition(ct).Should().Within(60.Seconds()).Emit();

        var manifest = new PackageManifest
        {
            Id = PackageId,
            Name = "Visible Pack",
            Kind = PackageKind.Content,
            TargetPartition = "VisibleTarget",
            SourceFolder = PackageId,
            Version = "1.0.0",
            ReleasedVersion = "1.0.0",
        };
        var files = new[]
        {
            new PackageFile($"{PackageId}/Doc.md", "# Doc\n\nInstalled for the visibility pin.")
        };

        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .Should().Within(180.Seconds()).Emit();
        result.Written.Should().Be(1);

        // The record really is there — read straight off the storage adapter, which BYPASSES the
        // permission fold. This separates "the records are missing" from "the records are
        // invisible", the distinction the outage turned on.
        var stored = await _fixture.DataSource.ScalarLong(
                "SELECT COUNT(*) FROM plugins.mesh_nodes WHERE id = @id",
                new[] { ("id", (object)PackageId) }, ct)
            .Should().Within(30.Seconds()).Emit();
        stored.Should().Be(1L, "the install wrote the record — this test is about VISIBILITY");

        // 🚨 THE PIN. The exact query the registry's bundle index issues, under the HTTP request's
        // non-System identity. Denied/invisible nodes are filtered out of the result set, so
        // against the defect this stays empty forever and the index serves [].
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType}",
                Consumer))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(60.Seconds())
            .Match(
                change => change.Items.Any(n => n.Id == PackageId),
                "a signed-in consumer must see the install records the registry's bundle index is "
                + "built from — the records exist, so an empty result is an invisible PARTITION");
    }

    /// <summary>
    /// The MECHANISM, asserted directly: the <c>plugins</c> schema carries the durable
    /// <c>_Policy</c> row the SQL permission fold projects, and therefore
    /// <c>public.partition_access</c> rows. Without those rows the schema is filtered out of every
    /// partition-scoped query before a single node is looked at — which is why no identity, not
    /// even a platform admin, could see the records, and why the live heal was an INSERT plus
    /// <c>rebuild_user_effective_permissions()</c>.
    /// </summary>
    [Fact(Timeout = 300000)]
    public async Task PluginsPartition_CarriesTheDurablePolicyRow_AndIsProjectedIntoPartitionAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetPluginsPartition(ct).Should().Within(60.Seconds()).Emit();

        var manifest = new PackageManifest
        {
            Id = "mechanism-pack",
            Name = "Mechanism Pack",
            Kind = PackageKind.Content,
            TargetPartition = "MechanismTarget",
            SourceFolder = "mechanism-pack",
            Version = "1.0.0",
            ReleasedVersion = "1.0.0",
        };
        await PackageInstaller
            .Install(Mesh, manifest,
                [new PackageFile("mechanism-pack/Doc.md", "# Doc")], "HEAD")
            .Should().Within(180.Seconds()).Emit();

        var policyRows = await PolicyRowCount(ct).Should().Within(30.Seconds()).Emit();
        policyRows.Should().Be(1L,
            "the fold reads mesh_nodes; a policy that exists only as an in-memory static node can "
            + "never reach the SQL side");

        var access = await PartitionAccessRowCount(ct).Should().Within(30.Seconds()).Emit();
        access.Should().BeGreaterThan(0L,
            "no partition_access row for 'plugins' means the whole schema is pre-filtered out of "
            + "every query, for every principal");
    }
}
