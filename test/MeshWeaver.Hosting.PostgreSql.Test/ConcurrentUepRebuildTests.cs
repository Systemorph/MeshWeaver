using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the FULL <c>rebuild_user_effective_permissions()</c> against CONCURRENT execution from
/// DIFFERENT partition schemas — the memex-cloud 2026-07-19 production deadlock.
///
/// <para><b>Production failure being guarded.</b> Two plugin hubs warming concurrently each ran
/// their access-seeding pass; each pass wrote <c>_Access</c> grants into its OWN partition
/// schema's <c>access</c> table, and each write's <c>access_changed</c> trigger ran the full
/// rebuild inside the originating write's transaction. Each rebuild takes ACCESS EXCLUSIVE on
/// its own schema's <c>user_effective_permissions</c> (the atomic-swap
/// <c>ALTER TABLE … RENAME</c>) AND writes the SHARED <c>public.partition_access</c> rows — two
/// transactions touching different schemas' tables but the same shared rows interleave those
/// locks in opposite orders → <c>40P01 deadlock detected</c> → PG kills one and the whole
/// seeding pass of that hub aborts
/// (<c>MeshNode Unknown at 'AgenticEngineering/Start/_Access/Public_Access'</c>).</para>
///
/// <para><b>The fix being pinned.</b> The function's FIRST statement is now
/// <c>PERFORM pg_advisory_xact_lock(hashtext('meshweaver_uep_rebuild'))</c> — one GLOBAL
/// transaction-scoped advisory lock shared by every schema's rebuild, so concurrent rebuilds
/// QUEUE instead of interleaving into a lock cycle (released automatically at
/// commit/rollback). The test asserts the deployed body carries the lock (deterministic-red if
/// it is ever removed) and then hammers interleaved rebuilds across two partitions — with the
/// lock this is deterministic-green; without it the storm may (and in prod did) die with
/// 40P01.</para>
/// </summary>
[Collection("PostgreSql")]
public class ConcurrentUepRebuildTests
{
    private readonly PostgreSqlFixture _fixture;

    // Storage serialization MUST be camelCase — the rebuild reads camelCase JSON keys
    // (content->>'accessObject'), same as the mesh hub's naming policy.
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConcurrentUepRebuildTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<PostgreSqlStorageAdapter> ProvisionProdShapeAdapterAsync(
        string space, string schema, CancellationToken ct)
    {
        await _fixture.DataSource.ExecuteNonQuery(
            $"SELECT public.ensure_partition_schema('{schema}')", ct)
            .Should().Within(60.Seconds()).Emit();

        var def = new PartitionDefinition
        {
            Namespace = space,
            Schema = schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        return new PostgreSqlStorageAdapter(_fixture.DataSource, partitionDefinition: def);
    }

    private async Task SeedPartitionAsync(
        PostgreSqlStorageAdapter adapter, string space, string user, CancellationToken ct)
    {
        await adapter.WriteAsync(new MeshNode(space)
        {
            Name = $"{space} Inc.",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = space,
            Content = new Space()
        }, _options, ct);

        // Grant through the standard path: {space}/_Access/{user}_Access → access satellite
        // table → access_changed trigger → rebuild. Gives the full rebuild real rows to
        // project and a public.partition_access row to sync — the shared state the deadlock
        // cycled on.
        await adapter.WriteAsync(AssignmentNodeFactory.UserRole(user, "Admin", space), _options, ct);
    }

    private async Task RunFullRebuildAsync(string schema, CancellationToken ct)
    {
        // One statement = one transaction = one advisory-lock scope — exactly the lock
        // footprint the access_changed trigger's full-rebuild path holds inside the
        // originating grant write's transaction.
        await using var cmd = _fixture.DataSource.CreateCommand(
            $"SELECT \"{schema}\".rebuild_user_effective_permissions()");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact(Timeout = 90000)]
    public async Task ConcurrentRebuilds_AcrossTwoPartitions_SerializeInsteadOf40P01()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schemaA = "cueprebuilda";
        const string spaceA = "ConcurrentUepA";
        const string alice = "cuep_alice";
        const string schemaB = "cueprebuildb";
        const string spaceB = "ConcurrentUepB";
        const string bob = "cuep_bob";

        var adapterA = await ProvisionProdShapeAdapterAsync(spaceA, schemaA, ct);
        var adapterB = await ProvisionProdShapeAdapterAsync(spaceB, schemaB, ct);
        await SeedPartitionAsync(adapterA, spaceA, alice, ct);
        await SeedPartitionAsync(adapterB, spaceB, bob, ct);

        // Deterministic pin: BOTH provisioned schemas ship the advisory-lock body. Without
        // this the concurrency storm below could go silently green on a lucky interleave
        // even after a regression removed the lock.
        foreach (var schema in new[] { schemaA, schemaB })
        {
            var locked = await _fixture.DataSource.ScalarLong(
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
                $"WHERE p.proname = 'rebuild_user_effective_permissions' AND n.nspname = '{schema}' " +
                "AND p.prosrc LIKE '%meshweaver_uep_rebuild%'", ct)
                .Should().Within(30.Seconds()).Emit();
            locked.Should().Be(1,
                $"\"{schema}\".rebuild_user_effective_permissions must serialize via the global advisory xact lock");
        }

        // The repro shape: interleaved full rebuilds from BOTH partitions racing on the
        // shared public.partition_access rows + their own schemas' atomic swaps. 8 parallel
        // transactions (4 per schema) — with the advisory lock they queue and ALL succeed;
        // unfixed, this interleave is the 40P01 cycle that killed the prod seeding passes.
        var storm = Enumerable.Range(0, 8)
            .Select(i => RunFullRebuildAsync(i % 2 == 0 ? schemaA : schemaB, ct))
            .ToArray();
        await Task.WhenAll(storm);

        // The serialized rebuilds must land on the correct converged state — no lost sync
        // from the concurrent swap storm.
        var aliceAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{alice}' AND partition = '{schemaA}'", ct)
            .Should().Within(30.Seconds()).Emit();
        aliceAccess.Should().Be(1, "the rebuild storm must preserve alice's partition_access row");

        var bobAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{bob}' AND partition = '{schemaB}'", ct)
            .Should().Within(30.Seconds()).Emit();
        bobAccess.Should().Be(1, "the rebuild storm must preserve bob's partition_access row");
    }
}
