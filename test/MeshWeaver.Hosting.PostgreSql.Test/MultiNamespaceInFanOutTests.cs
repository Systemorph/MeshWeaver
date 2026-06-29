using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the chat agent/model/skill picker "empty for a real user" bug (atioz 2026-06-20).
/// The per-partition registry query is a single string with a <c>namespace:A|B|C</c>
/// alternation (parsed as a <c>namespace IN (...)</c> filter). Reproduced live:
/// <c>namespace:Agent</c> → 11 results, but <c>namespace:rbuergi/Agent|Agent</c> → 0 — i.e.
/// adding a member whose partition has NO schema (an unprovisioned user partition) zeros the
/// WHOLE cross-schema result.
///
/// <para>This test isolates the cross-schema execution: given an EXPLICIT schema list that
/// contains only the EXISTING schema, an <c>IN (...)</c> namespace alternation must match its
/// existing member and return those rows — a non-existent member must contribute zero rows,
/// never poison the query. The companion Equal-namespace assertion is the control (that path
/// already works in prod).</para>
/// </summary>
[Collection("PostgreSql")]
public class MultiNamespaceInFanOutTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public MultiNamespaceInFanOutTests(PostgreSqlFixture fixture) => _fixture = fixture;

    [Fact(Timeout = 60000)]
    public async Task InNamespaceFanOut_WithNonExistentMember_ReturnsExistingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        // One EXISTING partition schema "acme" holding two Agent nodes under namespace "Acme/Agent"
        // (mirrors the platform "Agent" registry namespace).
        var (_, acme) = await _fixture.CreateSchemaAdapterAsync(
            "acme", partitionDef with { Namespace = "Acme", Schema = "acme" });

        await acme.WriteAsync(new MeshNode("Assistant", "Acme/Agent")
        { Name = "Assistant", NodeType = "Agent", State = MeshNodeState.Active }, _options, ct);
        await acme.WriteAsync(new MeshNode("Coder", "Acme/Agent")
        { Name = "Coder", NodeType = "Agent", State = MeshNodeState.Active }, _options, ct);

        var cross = new PostgreSqlStorageAdapter(_fixture.DataSource);
        var parser = new QueryParser();

        // Control: single-namespace Equal across the existing schema → 2 (this path works in prod).
        var equal = await cross.QueryNodesAcrossSchemasAsync(
                parser.Parse("namespace:Acme/Agent nodeType:Agent"), _options, new[] { "acme" }, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();
        // control: Equal-namespace cross-schema query returns the agents.
        equal.Select(n => n.Path).Should().BeEquivalentTo(
            new[] { "Acme/Agent/Assistant", "Acme/Agent/Coder" }, JsonSerializerOptions.Default);

        // The bug: a namespace IN (nonexistent | Acme/Agent) alternation across the SAME schema list
        // must still match the existing member. A non-existent member ("rbuergi/Agent") must NOT zero
        // the whole result — that is the chat agent-picker-empty symptom.
        var inq = await cross.QueryNodesAcrossSchemasAsync(
                parser.Parse("namespace:rbuergi/Agent|Acme/Agent nodeType:Agent"), _options, new[] { "acme" }, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();
        // a namespace IN(...) alternation must match its existing member; a non-existent member
        // ("rbuergi/Agent") must contribute zero rows, never poison the whole cross-schema query.
        inq.Select(n => n.Path).Should().BeEquivalentTo(
            new[] { "Acme/Agent/Assistant", "Acme/Agent/Coder" }, JsonSerializerOptions.Default);
    }

    /// <summary>
    /// THE agent-picker-empty bug (atioz 2026-06-20): the platform <c>agent</c> catalog schema
    /// was in <see cref="PostgreSqlCrossSchemaQueryProvider"/>'s <c>ExcludedSchemas</c> denylist
    /// (a legacy reserved-word). Since the per-partition agent-registry migration the <c>agent</c>
    /// schema is a real public catalog (like <c>skill</c>/<c>model</c>), so excluding it kept it out
    /// of <c>searchable_schemas</c> → the multi-namespace registry fan-out never queried it → the
    /// chat agent picker came back empty. This asserts the catalog schema is now discovered.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AgentCatalogSchema_IsSearchable_NotExcluded()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        // The platform agent catalog lives in schema "agent" (namespace "Agent"), publicRead.
        var (_, agent) = await _fixture.CreateSchemaAdapterAsync(
            "agent", partitionDef with { Namespace = "Agent", Schema = "agent" });
        await agent.WriteAsync(new MeshNode("Assistant", "Agent")
        { Name = "Assistant", NodeType = "Agent", State = MeshNodeState.Active }, _options, ct);

        var cross = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource)
        {
            SyncTtl = System.TimeSpan.Zero // force a real re-sync (bypass the throttle)
        };
        await cross.SyncSearchableSchemasAsync(ct);

        var searchable = new List<string>();
        await using (var cmd = _fixture.DataSource.CreateCommand(
            "SELECT schema_name FROM public.searchable_schemas"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                searchable.Add(r.GetString(0));

        searchable.Should().Contain("agent",
            "the agent catalog partition must be searchable so the multi-namespace agent-registry " +
            "fan-out (namespace:{user}/Agent|{space}/Agent|Agent) finds the platform agents");
    }
}
