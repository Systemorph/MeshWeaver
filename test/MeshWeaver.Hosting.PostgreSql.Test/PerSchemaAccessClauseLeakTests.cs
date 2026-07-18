using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// #471/#385 RC3 — the cross-partition public-read feed leak. The unscoped "Last Edited"
/// (<c>source:activity</c>) fan-out and any unscoped <c>nodeType:Markdown/Thread</c> query
/// route through <see cref="PostgreSqlSqlGenerator"/>'s <c>BuildPerSchemaAccessClause</c>. That
/// clause used to emit <c>public_read OR (partition_access AND node)</c> — the lone outlier that
/// OR'd public_read OUTSIDE the partition gate — so a partition's public-read content (Markdown,
/// Threads, …) leaked into a user's feed even with NO access to that partition (the "listed but
/// access-denied on open" symptom). The documented invariant (AccessControl.md, the
/// <c>search_across_schemas</c> stored proc, the schema-qualified <c>GenerateAccessControlClause</c>)
/// is <c>partition_access AND (public_read OR node)</c>.
///
/// <para>These tests pin BOTH sides: (a) DENY — a partition's public-read Markdown / Thread /
/// activity feed is NOT visible to a user with no access to it; (b) ALLOW — a genuinely GRANTED
/// cross-partition node (including a shared thread) STILL appears; and (c) the GUARDRAIL — the
/// <c>auth</c> global identity directory keeps its public-read visibility (display-name
/// resolution / subject picker / login) even though no user holds a <c>partition_access</c> row
/// for it, while a NON-auth partition's public-read User node stays gated.</para>
/// </summary>
[Collection("PostgreSql")]
public class PerSchemaAccessClauseLeakTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string User = "leak_testuser";

    public PerSchemaAccessClauseLeakTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private static ParsedQuery Parse(string q) => new QueryParser().Parse(q);

    private Task Exec(string sql, CancellationToken ct)
        => _fixture.DataSource.ExecuteNonQuery(sql, ct).Should().Within(30.Seconds()).Emit();

    /// <summary>Runs a fan-out over the given schemas as <paramref name="userId"/> — the exact
    /// path that engages <c>BuildPerSchemaAccessClause</c>.</summary>
    private Task<List<MeshNode>> FanOut(
        ParsedQuery query, string tableName, IReadOnlyList<string> schemas,
        string? userId, CancellationToken ct)
        => new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource)
            .QueryAcrossSchemasAsync(query, _options, schemas, tableName, userId, activityUserId: null, ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

    /// <summary>
    /// Three data partitions (acme = user has access, future = user has NONE, beta = user has a
    /// node-level cross-partition grant) plus the global <c>auth</c> identity directory.
    /// </summary>
    private async Task SetupAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        async Task<PostgreSqlStorageAdapter> Schema(string schema, string ns)
        {
            var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schema, partitionDef with { Namespace = ns, Schema = schema }, ct);
            return adapter;
        }

        var acme = await Schema("acme", "ACME");
        var future = await Schema("future", "FutuRe");
        var beta = await Schema("beta", "Beta");
        var auth = await Schema("auth", "auth");

        // ── acme (user HAS access) ──
        await acme.WriteAsync(new MeshNode("Report", "ACME")
        {
            Name = "ACME Report", NodeType = "Markdown", State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-1)
        }, _options, ct);
        await acme.WriteAsync(new MeshNode("acme-thread-1", "ACME/_Thread")
        {
            Name = "ACME thread", NodeType = "Thread", MainNode = "ACME",
            State = MeshNodeState.Active, Content = new MeshThread { CreatedBy = "someone" }
        }, _options, ct);
        await acme.WriteAsync(new MeshNode("a1", "ACME/Report/_activity")
        {
            Name = "edit", NodeType = "Activity", MainNode = "ACME/Report",
            State = MeshNodeState.Active, Content = new ActivityLog("DataUpdate") { HubPath = "ACME/Report" }
        }, _options, ct);

        // ── future (user has NO access — must NOT leak) ──
        await future.WriteAsync(new MeshNode("Report", "FutuRe")
        {
            Name = "FutuRe Report", NodeType = "Markdown", State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-2)
        }, _options, ct);
        await future.WriteAsync(new MeshNode("future-thread-1", "FutuRe/_Thread")
        {
            Name = "FutuRe thread", NodeType = "Thread", MainNode = "FutuRe",
            State = MeshNodeState.Active, Content = new MeshThread { CreatedBy = "someone" }
        }, _options, ct);
        await future.WriteAsync(new MeshNode("f1", "FutuRe/Report/_activity")
        {
            Name = "edit", NodeType = "Activity", MainNode = "FutuRe/Report",
            State = MeshNodeState.Active, Content = new ActivityLog("DataUpdate") { HubPath = "FutuRe/Report" }
        }, _options, ct);
        // A public-read User node in a DATA partition — must stay gated (deny control).
        await future.WriteAsync(new MeshNode("futureuser")
        {
            Name = "Future User", NodeType = "User", State = MeshNodeState.Active
        }, _options, ct);

        // ── beta (user was GRANTED a node-level cross-partition Read on beta/Secret) ──
        await beta.WriteAsync(new MeshNode("Secret", "Beta")
        {
            Name = "Beta Secret", NodeType = "Document", State = MeshNodeState.Active
        }, _options, ct);
        await beta.WriteAsync(new MeshNode("beta-thread-1", "Beta/_Thread")
        {
            Name = "Beta shared thread", NodeType = "Thread", MainNode = "Beta",
            State = MeshNodeState.Active, Content = new MeshThread { CreatedBy = "someone" }
        }, _options, ct);

        // ── auth (global identity directory) — a public-read User node ──
        await auth.WriteAsync(new MeshNode("shareduser")
        {
            Name = "Shared Directory User", NodeType = "User", State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Security.User { Email = "shared@example.com" }
        }, _options, ct);

        // public_read node types per schema (Markdown/Thread/User; Document is NOT public_read).
        foreach (var (schema, types) in new[]
                 {
                     ("acme", new[] { "Markdown", "Thread" }),
                     ("future", new[] { "Markdown", "Thread", "User" }),
                     ("beta", new[] { "Markdown", "Thread" }),
                     ("auth", new[] { "User", "Group", "Role" }),
                 })
        {
            foreach (var t in types)
                await Exec(
                    $"INSERT INTO \"{schema}\".node_type_permissions (node_type, public_read) " +
                    $"VALUES ('{t}', true) ON CONFLICT (node_type) DO UPDATE SET public_read = true", ct);
        }

        // Access: user has partition + Read on ACME; a node-level grant on beta/Secret (which also
        // grants partition_access to beta). NO access to future. NO partition_access to auth.
        await Exec("DELETE FROM public.partition_access WHERE user_id = '" + User + "'", ct);
        await Exec(
            "INSERT INTO public.partition_access (user_id, partition) VALUES " +
            $"('{User}', 'acme'), ('{User}', 'beta') ON CONFLICT DO NOTHING", ct);
        await Exec(
            "INSERT INTO acme.user_effective_permissions (user_id, node_path_prefix, permission, is_allow) " +
            $"VALUES ('{User}', 'ACME', 'Read', true) ON CONFLICT DO NOTHING", ct);
        await Exec(
            "INSERT INTO beta.user_effective_permissions (user_id, node_path_prefix, permission, is_allow) " +
            $"VALUES ('{User}', 'Beta/Secret', 'Read', true) ON CONFLICT DO NOTHING", ct);
    }

    [Fact(Timeout = 90000)]
    public async Task PublicReadMarkdownFeed_DoesNotLeakUngrantedPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        var results = await FanOut(
            Parse("nodeType:Markdown is:main sort:LastModified-desc"),
            "mesh_nodes", ["acme", "future", "beta"], User, ct);

        var paths = results.Select(n => n.Path).ToList();
        paths.Should().Contain("ACME/Report", "the user has access to ACME");
        paths.Should().NotContain("FutuRe/Report",
            "public_read must NOT bypass the partition gate — the user has no access to future");
    }

    [Fact(Timeout = 90000)]
    public async Task SourceActivityFeed_DoesNotLeakUngrantedPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        var results = await FanOut(
            Parse("source:activity scope:subtree is:main sort:LastModified-desc"),
            "mesh_nodes", ["acme", "future", "beta"], User, ct);

        var paths = results.Select(n => n.Path).ToList();
        paths.Should().Contain("ACME/Report", "the user has access to ACME's activity feed");
        paths.Should().NotContain("FutuRe/Report",
            "the Last-Edited (source:activity) feed must not surface an ungranted partition's public-read node");
    }

    [Fact(Timeout = 90000)]
    public async Task PublicReadThreadFeed_LeaksNothingUngranted_ButShowsGrantedPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        var results = await FanOut(
            Parse("nodeType:Thread sort:LastModified-desc"),
            "threads", ["acme", "future", "beta"], User, ct);

        var paths = results.Select(n => n.Path).ToList();
        paths.Should().Contain("ACME/_Thread/acme-thread-1", "the user has access to ACME");
        paths.Should().Contain("Beta/_Thread/beta-thread-1",
            "a shared thread in a partition the user was granted into stays visible (public_read + partition_access)");
        paths.Should().NotContain("FutuRe/_Thread/future-thread-1",
            "an ungranted partition's public-read thread must not leak into the fan-out");
    }

    [Fact(Timeout = 90000)]
    public async Task GrantedCrossPartitionNode_IsStillVisible()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        // beta/Secret is a Document (NOT public_read) — visible ONLY via the explicit node-level
        // cross-partition Read grant. Proves the fix keeps genuinely-shared content reachable.
        var results = await FanOut(
            Parse("nodeType:Document is:main"),
            "mesh_nodes", ["acme", "future", "beta"], User, ct);

        results.Select(n => n.Path).Should().Contain("Beta/Secret",
            "a genuinely-granted cross-partition node must remain visible");
    }

    [Fact(Timeout = 90000)]
    public async Task AuthDirectory_UserRoute_StaysResolvable_WithoutPartitionAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        // The pinned nodeType:User → auth route. The user holds NO partition_access to auth, yet
        // display-name resolution / subject picker / login require the public identity directory
        // to stay resolvable. The auth exemption preserves exactly that.
        var results = await FanOut(
            Parse("nodeType:User"), "mesh_nodes", ["auth"], User, ct);

        results.Select(n => n.Path).Should().Contain("shareduser",
            "the auth identity directory must stay readable to every authenticated user (name resolution)");
    }

    [Fact(Timeout = 90000)]
    public async Task NonAuthPartition_PublicReadUser_StaysGatedByPartitionAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupAsync(ct);

        // Deny control: the auth exemption is auth-ONLY. A public-read User node living in a
        // DATA partition the user cannot access must still be gated.
        var results = await FanOut(
            Parse("nodeType:User"), "mesh_nodes", ["future"], User, ct);

        results.Select(n => n.Path).Should().NotContain("futureuser",
            "a non-auth partition's public-read User must not bypass the partition gate");
    }
}
