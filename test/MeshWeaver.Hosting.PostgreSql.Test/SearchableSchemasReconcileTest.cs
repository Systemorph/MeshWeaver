using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The searchable-schema sync (<see cref="PostgreSqlCrossSchemaQueryProvider.SyncSearchableSchemasAsync"/>)
/// is the self-healing reconcile behind cross-partition / multi-namespace fan-out queries: every run it
/// rediscovers `public.searchable_schemas` from the live set of schemas that have a `mesh_nodes` table
/// (minus the genuine-infrastructure denylist). These tests pin that contract — the user directive
/// "all errors should be corrected in every sync":
/// <list type="bullet">
///   <item><b>Add</b> — a newly created partition schema becomes searchable on the next sync.</item>
///   <item><b>Remove</b> — a partition that has disappeared (its schema/`mesh_nodes` is gone) is dropped
///     from `searchable_schemas` on the next sync; nothing stale lingers.</item>
///   <item><b>Repair (the memex state)</b> — if `searchable_schemas` is wrong (a real catalog partition
///     missing — exactly the state memex was in for <c>agent</c>), a sync REPAIRS it. No manual SQL,
///     no redeploy beyond the corrected code: the next sync converges.</item>
///   <item><b>Catalog never excluded</b> — a real public catalog partition (<c>agent</c>/<c>skill</c>/…)
///     is always searchable; it must never be confused with a system schema.</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class SearchableSchemasReconcileTest
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public SearchableSchemasReconcileTest(PostgreSqlFixture fixture) => _fixture = fixture;

    private static PartitionDefinition Def(string schema) => new()
    {
        Namespace = schema,
        Schema = schema,
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
        NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
    };

    private async Task<HashSet<string>> SearchableAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        await using var cmd = _fixture.DataSource.CreateCommand("SELECT schema_name FROM public.searchable_schemas");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            set.Add(r.GetString(0));
        return set;
    }

    private PostgreSqlCrossSchemaQueryProvider NewProvider() =>
        new(_fixture.DataSource) { SyncTtl = System.TimeSpan.Zero }; // SyncTtl=0 → every call really re-syncs

    [Fact(Timeout = 60000)]
    public async Task Sync_AddingPartition_BecomesSearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        await _fixture.CreateSchemaAdapterAsync("recon_add", Def("recon_add"));

        await NewProvider().SyncSearchableSchemasAsync(ct);

        (await SearchableAsync(ct)).Should().Contain("recon_add",
            "a newly created partition schema with a mesh_nodes table must be discovered as searchable");
    }

    [Fact(Timeout = 60000)]
    public async Task Sync_RemovingPartition_DropsFromSearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        // Simulate a partition that USED to exist (a stale searchable row) but whose schema is now gone.
        await using (var seed = _fixture.DataSource.CreateCommand(
            "INSERT INTO public.searchable_schemas (schema_name) VALUES ('recon_ghost') ON CONFLICT DO NOTHING"))
            await seed.ExecuteNonQueryAsync(ct);
        (await SearchableAsync(ct)).Should().Contain("recon_ghost", "precondition: the stale entry is present");

        await NewProvider().SyncSearchableSchemasAsync(ct);

        (await SearchableAsync(ct)).Should().NotContain("recon_ghost",
            "a partition no longer present (no mesh_nodes schema) must be removed from searchable on sync");
    }

    [Fact(Timeout = 60000)]
    public async Task Sync_RepairsMissingEntry_TheMemexState()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        // A real partition exists with content...
        await _fixture.CreateSchemaAdapterAsync("recon_repair", Def("recon_repair"));
        await NewProvider().SyncSearchableSchemasAsync(ct);
        (await SearchableAsync(ct)).Should().Contain("recon_repair", "baseline: the partition is searchable");

        // ...but searchable_schemas drifts wrong (the memex 'agent' state: real partition missing from the
        // searchable set). The fix is NOT manual SQL — the next sync must converge.
        await using (var corrupt = _fixture.DataSource.CreateCommand(
            "DELETE FROM public.searchable_schemas WHERE schema_name = 'recon_repair'"))
            await corrupt.ExecuteNonQueryAsync(ct);
        (await SearchableAsync(ct)).Should().NotContain("recon_repair", "precondition: searchable is now wrong");

        await NewProvider().SyncSearchableSchemasAsync(ct);

        (await SearchableAsync(ct)).Should().Contain("recon_repair",
            "re-running the sync REPAIRS a searchable set that wrongly dropped a real partition (repair-by-sync)");
    }

    [Fact(Timeout = 60000)]
    public async Task Sync_CatalogPartition_IsNeverExcluded()
    {
        // The exact memex regression: the platform 'agent' catalog (publicRead, like skill/model/harness)
        // must be searchable — it must never be treated as a system/reserved schema.
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        await _fixture.CreateSchemaAdapterAsync("agent", Def("Agent") with { Schema = "agent" });
        await _fixture.CreateSchemaAdapterAsync("skill", Def("Skill") with { Schema = "skill" });

        await NewProvider().SyncSearchableSchemasAsync(ct);

        var searchable = await SearchableAsync(ct);
        searchable.Should().Contain("agent", "the agent catalog partition must be searchable");
        searchable.Should().Contain("skill", "the skill catalog partition must be searchable");
    }
}
