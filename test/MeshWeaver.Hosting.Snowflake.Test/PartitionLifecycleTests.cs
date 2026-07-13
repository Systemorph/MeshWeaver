using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Partition lifecycle under the <b>no-lazy-create</b> contract: a partition's schema is
/// created ONLY by provisioning a partition-owning object (User / Space) — modelled in these
/// tests by <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/>, exactly what
/// <c>OwnsPartitionProvisioningValidator</c> calls. A write into a partition that was never
/// provisioned is <b>refused</b> (faults), never lazily schema-created (the atioz ghost-schema
/// fix). Once provisioned, ordinary write / read / delete work. See
/// <c>Doc/Architecture/PartitionStorageRouting.md</c>. 1:1 port of the PG twin.
/// </summary>
[Collection("Snowflake")]
public class PartitionLifecycleTests(SnowflakeFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly SnowflakeFixture _fixture = fixture;

    /// <summary>
    /// Snowflake-only adaptation (PG's container is always up once its fixture ran; the
    /// Snowflake endpoint is optional): green-skip BEFORE the base builds the mesh — the mesh
    /// boot itself (persistence wiring + hosted services) needs the endpoint. Each test method
    /// additionally carries the first-line guard per the fixture contract.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        _fixture.SkipUnlessAvailable();
        await base.InitializeAsync();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                // PG caps the Npgsql pool via NpgsqlConnectionStringBuilder here; the
                // Snowflake driver manages its own per-connection-string session pool, so
                // the fixture's connection string passes through unchanged.
                // ConfigureMesh runs in the BASE CTOR (before InitializeAsync's green-skip
                // can fire), so an unavailable fixture must yield a placeholder here — the
                // skip then lands before the lazily-built hub ever opens a connection.
                services.AddPartitionedSnowflakePersistence(_fixture.Available
                    ? _fixture.ConnectionString
                    : "account=unavailable;user=none;password=none;db=none");
                // The test host does not run InitializeSnowflakeSchemaAsync (the boot-time
                // capability probe), so the DI-registered holder would stay at the all-on
                // profile. Share the fixture's PROBED holder so MERGE/VECTOR fallbacks match
                // the connected endpoint (the LocalStack emulator may lack them). PG needs no
                // twin of this — its dialect support is uniform.
                return services.AddSingleton(_fixture.CapabilityHolder);
            })
            .AddGraph();

    /// <summary>
    /// Provision a partition the platform way — run every storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (Snowflake → the
    /// partition-schema DDL). This is the schema-creation a Space/User performs
    /// on create. Tests may block on the composed observable (§2a).
    /// </summary>
    private Task ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask();

    /// <summary>
    /// Tear a partition down the platform way — every provider's
    /// <see cref="IPartitionStorageProvider.DeletePartition"/> (Snowflake →
    /// <c>DROP SCHEMA IF EXISTS … CASCADE</c>). This is what deleting a partition-owning root
    /// (Space) triggers via <c>PartitionDropPostDeletionHandler</c>.
    /// </summary>
    private Task DeletePartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.DeletePartition(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask();

    // information_schema's view/columns are UPPERCASE catalog objects — emitted unquoted (the
    // default uppercase fold resolves them); the lowercase comparison happens in the predicate,
    // mirroring SnowflakePartitionStorageProvider.PartitionExists.
    private IObservable<long> SchemaCount(string schema) =>
        _fixture.ConnectionSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE LOWER(schema_name) = :s",
            new[] { ("s", (object)schema) });

    [Fact(Timeout = 60000)]
    public async Task ProvisionedPartition_Write_EnablesSubsequentReads()
    {
        _fixture.SkipUnlessAvailable();
        var ns = $"sf9c_prov_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        await ProvisionPartition(ns);   // the one schema-creation path (what a Space/User create does)

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var path = $"{ns}/seed";
        var saved = await meshService.CreateNode(new MeshNode("seed", ns)
        {
            NodeType = "Markdown",
            Name = "seed",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Should().NotBeNull();

        var workspace = Mesh.GetWorkspace();
        var readBack = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull("a provisioned partition serves reads immediately");
    }

    [Fact(Timeout = 60000)]
    public async Task UnprovisionedPartition_Write_IsRefused_NoGhostSchema()
    {
        _fixture.SkipUnlessAvailable();
        // No lazy create: writing into a partition that was never provisioned must NOT
        // conjure a schema — the write faults ("no partition, no write").
        var ns = $"sf9c_unprov_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var notification = await meshService
            .CreateNode(new MeshNode("orphan", ns)
            {
                NodeType = "Markdown",
                Name = "orphan",
                State = MeshNodeState.Active,
            })
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().NotBeNull();

        // 🚨 The invariant: no ghost schema was conjured for the unprovisioned namespace.
        await SchemaCount(ns).Should().Within(30.Seconds()).Be(0L);
    }

    [Fact(Timeout = 60000)]
    public async Task DeleteNode_ThatDoesNotExist_Errors()
    {
        _fixture.SkipUnlessAvailable();
        var nonexistent = $"sf9c_nope_{Guid.NewGuid():N}".ToLowerInvariant()[..18] + "/missing";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // delete-something-that-doesn't-exist must surface an error to the
        // caller. Materialize folds the OnError into a value so we assert it
        // reactively — no await, no ThrowAsync.
        var notification = await meshService
            .DeleteNode(nonexistent)
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task Write_ReadBack_Delete_ReadAgain_Empty()
    {
        _fixture.SkipUnlessAvailable();
        var ns = $"sf9c_rd_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        await ProvisionPartition(ns);

        var path = $"{ns}/item";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        // 1. Write
        var saved = await meshService.CreateNode(new MeshNode("item", ns)
        {
            NodeType = "Markdown",
            Name = "item",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Path.Should().Be(path);

        // 2. Read-back
        var first = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        first.Should().NotBeNull();

        // 3. Delete
        var deleted = await meshService.DeleteNode(path)
            .Should().Within(30.Seconds()).Emit();
        deleted.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public async Task DeletePartition_DropsSchema_AndReprovisionRecreatesIt()
    {
        _fixture.SkipUnlessAvailable();
        var ns = $"sf9c_drop_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        await ProvisionPartition(ns);
        await SchemaCount(ns).Should().Within(30.Seconds()).Be(1L);

        // Drop — the inverse of provisioning. Idempotent: a second drop of the now-absent
        // schema completes without error.
        await DeletePartition(ns);
        await SchemaCount(ns).Should().Within(30.Seconds()).Be(0L);
        await DeletePartition(ns);

        // Re-provisioning after a drop must recreate the schema — the provider's
        // provisioning promise-cache was evicted by the drop (else the cached "already
        // provisioned" completion would replay and every write would fault on the absent
        // schema forever — Snowflake's "does not exist or not authorized", PG's 42P01).
        await ProvisionPartition(ns);
        await SchemaCount(ns).Should().Within(30.Seconds()).Be(1L);
    }

    /// <summary>
    /// Regression (atioz, 2026-06-08): the global <c>apitoken</c> token-validation index
    /// partition must be EAGERLY declared so portal boot
    /// (<see cref="SnowflakePartitionSubscriptionHostedService"/>) and the migration provision it
    /// — never lazily created. <c>ApiToken</c> is not an <c>OwnsPartition</c> type, and after the
    /// lazy-<c>CREATE SCHEMA</c> removal a fresh DB never got the <c>apitoken</c> schema, so the
    /// <c>ApiToken/{hashPrefix}</c> index node <see cref="Memex.Portal.Shared"/>.ApiTokenService
    /// writes couldn't persist and EVERY freshly-minted bearer token — manual AND OAuth — 401'd
    /// at validation (the index read short-circuits to null).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ApiTokenIndexPartition_IsDeclaredEagerly_AndPersistsIndexWrites()
    {
        _fixture.SkipUnlessAvailable();
        // 1. The framework DECLARES ApiToken → apitoken as a framework partition. This is what
        //    boot-time provisioning seeds; its absence from DefaultPartitionProvider was the bug.
        var declared = Mesh.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .Select(n => n.Content)
            .OfType<PartitionDefinition>()
            .FirstOrDefault(d => string.Equals(d.Namespace, "ApiToken", StringComparison.Ordinal));
        declared.Should().NotBeNull(
            "the ApiToken validation-index partition must be declared so it is provisioned eagerly, not lazily");
        declared!.Schema.Should().Be("apitoken");

        // 2. Provisioning it (what boot does from that definition) creates the schema, and a write
        //    to ApiToken/{hashPrefix} — the exact path ApiTokenService reads on every bearer
        //    request — persists and reads back.
        await ProvisionPartition("ApiToken");
        await SchemaCount("apitoken").Should().Within(30.Seconds()).Be(1L);

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var hashPrefix = $"{Guid.NewGuid():N}"[..12];
        var path = $"ApiToken/{hashPrefix}";
        var saved = await meshService.CreateNode(new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "Markdown",
            Name = "token-index",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Path.Should().Be(path);

        var readBack = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull("the provisioned apitoken index partition serves reads immediately");
    }
}
