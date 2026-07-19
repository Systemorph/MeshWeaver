using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the query-side permission fold to the documented semantics (AccessControl.md, the live
/// <c>AccessControlPipeline</c> evaluator): <b>per SUBJECT, closest scope wins; then subjects OR
/// together</b>. A deny binds only the subject it names — it masks that subject's own broader
/// allow, never another subject's.
///
/// <para><b>Production failure being guarded (memex-cloud 2026-07-19, schema
/// <c>agenticengineering</c>).</b> The UEP rows were exactly:
/// <list type="bullet">
///   <item><c>roland.buergi | AgenticEngineering | Read | t</c> — user-specific root allow (an
///     entitlement grant),</item>
///   <item><c>Public | AgenticEngineering | Read | t</c> — root cover grant,</item>
///   <item><c>Public | AgenticEngineering/Introduction | Read | f</c> (+20 more child denies) —
///     the store gating darkening children for Public/Anonymous only.</item>
/// </list>
/// The SQL fold mixed ALL subjects into ONE longest-prefix scan
/// (<c>user_id IN (user,'Public') … ORDER BY LENGTH(prefix) DESC LIMIT 1</c>), so the DEEPER
/// Public deny beat the user's ROOT allow — an ENTITLED viewer's queries returned only the
/// public surface: course-install discovery found zero exercises ("This course has no
/// installable exercises or demos"), module/exercise listings were empty for exactly the users
/// who bought access. The live node-open evaluator resolved the same rows correctly, so nodes
/// opened fine while every query hid them.</para>
///
/// <para>These tests seed that exact three-row shape and assert, on ALL THREE query folds —
/// the schema-qualified/unqualified <c>GenerateAccessControlClause</c>, the cross-schema
/// fan-out's <c>BuildPerSchemaAccessClause</c>, and the <c>public.search_across_schemas</c>
/// stored proc — that (a) the entitled user sees the gated child (per-subject fold: the user
/// subject resolves allow at the root; the Public child deny binds only Public), and (b) a
/// merely-Public reader still does NOT see it (the gate keeps gating).</para>
/// </summary>
[Collection("PostgreSql")]
public class PerSubjectAccessFoldTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string Entitled = "rls_roland";
    private const string PublicOnly = "rls_visitor";
    private const string Root = "AgenticEngineering";
    private const string GatedChildPath = "AgenticEngineering/Introduction";

    public PerSubjectAccessFoldTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private Task Exec(string sql, CancellationToken ct)
        => _fixture.DataSource.ExecuteNonQuery(sql, ct).Should().Within(30.Seconds()).Emit();

    // ── 1. Unqualified single-schema query path (GenerateAccessControlClause) ────────────

    /// <summary>
    /// Seeds the three-row shape through the REAL projection (access_control grants →
    /// rebuild_user_effective_permissions) in the public schema and queries through
    /// <see cref="PostgreSqlMeshQuery"/> — the path every structured query takes.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task UserRootAllow_BeatsDeeperPublicDeny_SingleSchemaQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        // Nodes: root + the Public-gated child. "Document" is NOT public_read, so the
        // node-level fold (not the public_read bypass) decides visibility.
        await _fixture.StorageAdapter.Write(new MeshNode(Root)
        {
            Name = "Agentic Engineering", NodeType = "Document", State = MeshNodeState.Active
        }, _options).Should().Within(30.Seconds()).Emit();
        await _fixture.StorageAdapter.Write(new MeshNode("Introduction", Root)
        {
            Name = "Introduction", NodeType = "Document", State = MeshNodeState.Active
        }, _options).Should().Within(30.Seconds()).Emit();

        // The live grant shape via the real projection: user root allow + Public root allow
        // + Public child DENY (each Grant rebuilds user_effective_permissions).
        var ac = _fixture.AccessControl;
        await ac.Grant(Root, Entitled, "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant(Root, "Public", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant(GatedChildPath, "Public", "Read", isAllow: false, ct).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // The entitled user's root allow covers EVERYTHING under the root — the Public child
        // deny binds only the Public subject.
        var entitled = await query.QueryList(
                MeshQueryRequest.FromQuery($"path:{Root} scope:descendants", Entitled), _options, ct)
            .Should().Within(30.Seconds()).Emit();
        entitled.Cast<MeshNode>().Select(n => n.Path).Should().Contain(GatedChildPath,
            "the user-specific root allow must not be overridden by a deeper Public deny (per-subject closest scope, then OR across subjects)");

        // A merely-Public reader resolves the child through the Public subject → deny wins
        // within that subject → still hidden. The gate keeps gating.
        var visitor = await query.QueryList(
                MeshQueryRequest.FromQuery($"path:{Root} scope:descendants", PublicOnly), _options, ct)
            .Should().Within(30.Seconds()).Emit();
        visitor.Cast<MeshNode>().Select(n => n.Path).Should().NotContain(GatedChildPath,
            "for a reader whose only matching subject is Public, the child deny still hides the node");
    }

    // ── 2 + 3. Schema-qualified fan-out + search_across_schemas ──────────────────────────

    private const string Schema = "agenticeng";

    /// <summary>Provisions the partition schema and seeds nodes + the verbatim prod UEP rows.</summary>
    private async Task SeedPartitionShapeAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = Root,
            Schema = Schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(Schema, partitionDef, ct);

        await adapter.WriteAsync(new MeshNode(Root)
        {
            Name = "Agentic Engineering", NodeType = "Document",
            State = MeshNodeState.Active, MainNode = Root
        }, _options, ct);
        await adapter.WriteAsync(new MeshNode("Introduction", Root)
        {
            Name = "Introduction", NodeType = "Document",
            State = MeshNodeState.Active, MainNode = GatedChildPath
        }, _options, ct);

        // The verbatim live rows (drift-seeded like PerSchemaAccessClauseLeakTests — the fold
        // under test reads user_effective_permissions, however it was materialized).
        await Exec(
            $"INSERT INTO \"{Schema}\".user_effective_permissions (user_id, node_path_prefix, permission, is_allow) VALUES " +
            $"('{Entitled}', '{Root}', 'Read', true), " +
            $"('Public', '{Root}', 'Read', true), " +
            $"('Public', '{GatedChildPath}', 'Read', false) " +
            "ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = EXCLUDED.is_allow", ct);
        await Exec(
            "INSERT INTO public.partition_access (user_id, partition) VALUES " +
            $"('{Entitled}', '{Schema}'), ('Public', '{Schema}') ON CONFLICT DO NOTHING", ct);
    }

    /// <summary>The unscoped fan-out path (<c>BuildPerSchemaAccessClause</c>) — the surface the
    /// course-install discovery and every unpinned structured query go through.</summary>
    [Fact(Timeout = 90000)]
    public async Task UserRootAllow_BeatsDeeperPublicDeny_CrossSchemaFanOut()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartitionShapeAsync(ct);

        var provider = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource);
        var query = new QueryParser().Parse("nodeType:Document is:main");

        var entitled = await provider
            .QueryAcrossSchemasAsync(query, _options, [Schema], "mesh_nodes", Entitled, activityUserId: null, ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();
        entitled.Select(n => n.Path).Should().Contain(GatedChildPath,
            "the entitled user's root allow must expose the Public-gated child in the fan-out");

        var visitor = await provider
            .QueryAcrossSchemasAsync(query, _options, [Schema], "mesh_nodes", PublicOnly, activityUserId: null, ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();
        var visitorPaths = visitor.Select(n => n.Path).ToList();
        visitorPaths.Should().Contain(Root, "the Public root cover keeps the public surface visible");
        visitorPaths.Should().NotContain(GatedChildPath,
            "a merely-Public reader must still be gated out of the darkened child");
    }

    /// <summary>The <c>public.search_across_schemas</c> stored proc — global search.</summary>
    [Fact(Timeout = 90000)]
    public async Task UserRootAllow_BeatsDeeperPublicDeny_SearchAcrossSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedPartitionShapeAsync(ct);

        await Exec("DELETE FROM public.searchable_schemas", ct);
        await Exec($"INSERT INTO public.searchable_schemas (schema_name) VALUES ('{Schema}') ON CONFLICT DO NOTHING", ct);

        var entitled = await SearchAcrossSchemasAsync("n.node_type = 'Document'", Entitled, ct);
        entitled.Should().Contain(GatedChildPath,
            "global search must return the gated child to the entitled user");

        var visitor = await SearchAcrossSchemasAsync("n.node_type = 'Document'", PublicOnly, ct);
        visitor.Should().Contain(Root, "the public surface stays searchable");
        visitor.Should().NotContain(GatedChildPath,
            "global search must keep the darkened child hidden from a merely-Public reader");
    }

    private async Task<List<string>> SearchAcrossSchemasAsync(string where, string userId, CancellationToken ct)
    {
        var paths = new List<string>();
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT * FROM public.search_across_schemas(@p_where, @p_user, @p_order, @p_limit) " +
            "AS t(id TEXT, namespace TEXT, name TEXT, node_type TEXT, category TEXT, icon TEXT, " +
            "display_order INT, last_modified TIMESTAMPTZ, version BIGINT, state SMALLINT, " +
            "content JSONB, desired_id TEXT, main_node TEXT)");
        cmd.Parameters.Add(new NpgsqlParameter("@p_where", where));
        cmd.Parameters.Add(new NpgsqlParameter("@p_user", userId));
        cmd.Parameters.Add(new NpgsqlParameter("@p_order", "last_modified DESC"));
        cmd.Parameters.Add(new NpgsqlParameter("@p_limit", 50));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var ns = reader.IsDBNull(1) ? "" : reader.GetString(1);
            paths.Add(string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}");
        }
        return paths;
    }
}
