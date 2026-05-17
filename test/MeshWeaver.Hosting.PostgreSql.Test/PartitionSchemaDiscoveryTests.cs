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

    /// <summary>
    /// Obsolete after Matches() removal. Schema-discovery semantics will be
    /// covered by PartitionLifecycleTests (Stage 9c) — that suite exercises
    /// create / write / read / delete end-to-end and pins the same invariant
    /// (a schema-bearing partition must route after startup).
    /// </summary>
    [Fact(Skip = "Replaced by PartitionLifecycleTests (Stage 9c) after Matches() removal")]
    public Task ExistingSchema_WithoutAdminPartitionMeshNode_RegistersOnStartup() => Task.CompletedTask;
}
