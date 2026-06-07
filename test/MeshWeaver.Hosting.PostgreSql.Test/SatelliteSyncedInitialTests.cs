using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Keystone for retiring the pedestrian <c>StorageAdapterMeshQueryProvider</c> for
/// scoped-satellite reads: proves the per-schema <see cref="PostgreSqlMeshQuery"/>'s
/// synced <c>Query&lt;T&gt;</c> Initial returns a PERMITTED user's pre-existing satellite
/// rows (Thread → <c>threads</c> table).
///
/// <para>The historical "satellite Initial under-returns" claim (which keeps
/// <see cref="PostgreSqlPartitionedMeshQuery"/> routing scoped-satellite to the one-shot
/// pinned fan-out instead of the delegate) turned out to be the access-control filter
/// dropping rows for an <c>Anonymous</c> caller — correct behaviour, not a bug. This test
/// pins the real contract: with partition access + a Read grant, the delegate's Initial
/// DOES return the satellite rows. If it stays green, scoped-satellite can route to the
/// delegate (gaining live deltas) and the pedestrian carve-out can be removed.</para>
/// </summary>
[Collection("PostgreSql")]
public class SatelliteSyncedInitialTests : IAsyncLifetime
{
    private const string Schema = "user_sat_synced_test";
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _adapter = null!;
    private PostgreSqlMeshQuery _query = null!;

    private static readonly PartitionDefinition UserPartition = new()
    {
        Namespace = "User",
        DataSource = "default",
        Schema = Schema,
        TableMappings = PartitionDefinition.StandardTableMappings,
    };

    public SatelliteSyncedInitialTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(Schema, UserPartition, ct);
        _schemaDs = ds;
        _adapter = adapter;
        _query = new PostgreSqlMeshQuery(_adapter);

        // Seed a Thread satellite (routes to the `threads` table) BEFORE the query subscribes.
        await _adapter.WriteAsync(new MeshNode("ns-thread", "User/carol/_Thread")
        {
            Name = "Namespace Thread",
            NodeType = "Thread",
            MainNode = "User/carol/_Thread",
            State = MeshNodeState.Active,
        }, _options, ct);

        // Grant carol exactly what the access filter reads: partition access on this schema
        // (SchemaName is set on the adapter → the partition check applies) + a Read grant whose
        // prefix covers the thread's main_node. Direct INSERTs are deterministic — the uep
        // rebuild proc sources from the `access` satellite, not these rows, so they persist.
        await GrantReadAsync("carol", prefix: "User/carol", ct);
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task SyncedQuery_Initial_ReturnsPreexistingSatelliteRow_ForPermittedUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MeshQueryRequest.FromQuery("namespace:User/carol/_Thread nodeType:Thread", "carol");

        // Await the Initial out-of-band (no blocking reactive assertion inside an async body),
        // then assert synchronously on the materialised snapshot.
        var initial = await _query.Query<MeshNode>(request, _options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

        initial.Items.Should().Contain(n => n.Id == "ns-thread",
            "the per-schema delegate's Initial must return a permitted user's pre-existing " +
            "satellite rows — the premise for routing scoped-satellite to the delegate and " +
            "retiring the pedestrian provider");
    }

    private async Task GrantReadAsync(string userId, string prefix, CancellationToken ct)
    {
        await using (var pa = _fixture.DataSource.CreateCommand(
            "INSERT INTO public.partition_access (user_id, partition) VALUES ($1, $2) ON CONFLICT DO NOTHING"))
        {
            pa.Parameters.AddWithValue(userId);
            pa.Parameters.AddWithValue(Schema);
            await pa.ExecuteNonQueryAsync(ct);
        }
        await using var uep = _schemaDs.CreateCommand(
            "INSERT INTO user_effective_permissions (user_id, node_path_prefix, permission, is_allow) " +
            "VALUES ($1, $2, 'Read', true) ON CONFLICT DO NOTHING");
        uep.Parameters.AddWithValue(userId);
        uep.Parameters.AddWithValue(prefix);
        await uep.ExecuteNonQueryAsync(ct);
    }
}
