using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression guard for the atioz 45-ghost-schema DB corruption. The storage router
/// (<see cref="PostgreSqlPathRoutingAdapter"/>) must <b>NEVER</b> lazily <c>CREATE SCHEMA</c>
/// on a write to an arbitrary path segment — schema creation is gated to partition-owning
/// creates via <see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>
/// (driven by <c>OwnsPartitionProvisioningValidator</c>). A write to an unprovisioned
/// partition must fault / no-op WITHOUT conjuring a schema.
///
/// <para><b>Why the rest of the PG suite missed this:</b> nearly every other PG test calls
/// <c>PostgreSqlFixture.CreateSchemaAdapter</c>, which explicitly <c>CREATE SCHEMA</c>s and
/// builds a per-schema adapter directly — bypassing the path-routing adapter that holds the
/// (removed) lazy-create. These tests drive the router on purpose.</para>
/// </summary>
[Collection("PostgreSql")]
public class GhostSchemaInvariantTests
{
    private readonly PostgreSqlFixture _fixture;

    public GhostSchemaInvariantTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // Construct the real provider over the fixture's container (ensure_partition_schema is
    // installed by PostgreSqlFixture.InitializeAsync). ioPoolRegistry omitted → IoPool.Unbounded.
    private PostgreSqlPartitionStorageProvider NewProvider() =>
        new(_fixture.DataSource, _fixture.ConnectionString, _fixture.Options);

    private IObservable<long> SchemaCount(string schema, CancellationToken ct) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) }, ct);

    [Fact(Timeout = 60000)]
    public async Task WriteToUnprovisionedPartition_CreatesNoSchema_AndDoesNotPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = NewProvider();
        // Valid partition segment (letters/digits, not a NodeType name, not `_`-prefixed):
        // the router WILL resolve a schema name for it — the whole point is that it must
        // NOT create that schema on the write.
        var ghost = $"ghost{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var node = new MeshNode("Foo", ghost) { Name = "Foo", NodeType = "Markdown" };

        // The write either faults with 42P01 (the "no partition, no write" refusal) or no-ops;
        // either way is fine — what must NOT happen is a CREATE SCHEMA. Catch so the expected
        // fault doesn't fail the test; the assertions below pin the real invariant.
        await provider.Adapter.Write(node, JsonSerializerOptions.Default)
            .Catch((Exception _) => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();

        // 🚨 The invariant: no ghost schema was conjured.
        await SchemaCount(ghost, ct).Should().Within(30.Seconds()).Be(0L);

        // And nothing persisted: a read tolerates the absent schema and finds nothing.
        var read = await provider.Adapter.Read($"{ghost}/Foo", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();
        Assert.Null(read);
    }

    [Fact(Timeout = 60000)]
    public async Task EnsurePartitionProvisioned_IsTheOneSchemaCreationPath_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = NewProvider();
        var part = $"prov{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        try
        {
            // Before provisioning: the schema does not exist.
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(0L);

            // The ONE schema-creation path (what OwnsPartitionProvisioningValidator calls).
            await provider.EnsurePartitionProvisioned(part).Should().Within(60.Seconds()).Emit();
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(1L);

            // Idempotent: the promise-cache replays; no second CREATE SCHEMA, no error.
            await provider.EnsurePartitionProvisioned(part).Should().Within(30.Seconds()).Emit();
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(1L);

            // Now a write routes into the provisioned schema and persists.
            var node = new MeshNode("Foo", part) { Name = "Foo", NodeType = "Markdown" };
            await provider.Adapter.Write(node, JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();
            await _fixture.DataSource.ScalarLong($"SELECT COUNT(*) FROM \"{part}\".mesh_nodes", ct)
                .Should().Within(30.Seconds()).Be(1L);
        }
        finally
        {
            await _fixture.DataSource.ExecuteNonQuery($"DROP SCHEMA IF EXISTS \"{part}\" CASCADE", ct)
                .Should().Within(30.Seconds()).Emit();
        }
    }
}
