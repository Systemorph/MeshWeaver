using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression: <see cref="PostgreSqlPartitionStorageProvider.Matches"/> must
/// return <c>true</c> for first segments whose schemas exist in Postgres,
/// even when no <c>Admin/Partition/{ns}</c> MeshNode has been streamed yet.
///
/// <para>The pre-Stage-0 <c>IPartitionedStoreFactory</c> created stores
/// lazily on first touch, so user partitions (V10 per-user-partition
/// migration) routed correctly even though that migration only writes a
/// <c>Source/{userId}</c> MeshDataSource record — never an
/// <c>Admin/Partition</c> entry. The new provider requires pre-registration
/// via the <c>Admin/Partition/*</c> stream; without an information_schema
/// discovery pass on startup the prod portal renders
/// "Error loading area: No node found at 'rbuergi'." on every poll tick.
/// This test exercises
/// <see cref="PostgreSqlPartitionSubscriptionHostedService.DiscoverAndRegisterSchemasAsync"/>
/// directly so a regression in either the SQL query OR the reserved-schema
/// filter trips it.</para>
/// </summary>
[Collection("PostgreSql")]
public class PartitionSchemaDiscoveryTests
{
    private readonly PostgreSqlFixture _fixture;

    public PartitionSchemaDiscoveryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60000)]
    public async Task ExistingSchema_WithoutAdminPartitionMeshNode_RegistersOnStartup()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange: create a per-user-style schema with mesh_nodes (the
        // fixture helper provisions the partition tables — schema, mesh_nodes,
        // the satellites — but we deliberately do NOT register the
        // PartitionDefinition with our provider-under-test, simulating
        // a prod cold-start where the schema pre-exists from a prior run.
        const string schemaName = "discoverytest";
        var (schemaDs, _) = await _fixture.CreateSchemaAdapterAsync(schemaName, partitionDef: null, ct);

        try
        {
            // Act: fresh provider with NO seeded partitions, then run the
            // exact discovery logic the production hosted service runs.
            using var provider = new PostgreSqlPartitionStorageProvider(
                _fixture.DataSource,
                _fixture.ConnectionString,
                new PostgreSqlStorageOptions { ConnectionString = _fixture.ConnectionString },
                partitions: null);

            var discovered = await PostgreSqlPartitionSubscriptionHostedService
                .DiscoverAndRegisterSchemasAsync(provider, logger: null, ct);

            // Assert: the freshly-discovered partition routes. Without
            // schema discovery this would return false and every path
            // under `discoverytest/...` would fault with "No node found".
            discovered.Should().BeGreaterThan(0,
                "at least the test schema must have been discovered");
            (await provider.Matches($"{schemaName}/some-node").FirstAsync().ToTask(ct)).Should().BeTrue(
                $"a {schemaName}.mesh_nodes-bearing schema must route even without an Admin/Partition/{schemaName} MeshNode");
            (await provider.Matches($"{schemaName}/_UserActivity/{schemaName}").FirstAsync().ToTask(ct)).Should().BeTrue(
                "satellite paths under a discovered partition must also route");
        }
        finally
        {
            await schemaDs.DisposeAsync();
        }
    }
}
