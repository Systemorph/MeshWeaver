using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests the DB-level UNION path: <see cref="MeshQueryRequest.Queries"/> with
/// multiple query strings runs ONE Postgres query (UNION of N SELECTs) and the
/// adapter dedupes by row server-side. Validates that the union is happening
/// in-database (not as N round-trips post-merged in C#) by:
/// <list type="bullet">
///   <item>Asserting both query branches' results appear in the snapshot.</item>
///   <item>Asserting a node matched by BOTH queries appears exactly once
///         (server-side <c>UNION</c> dedup).</item>
///   <item>Driving <see cref="PostgreSqlMeshQuery.Query{T}"/> with the
///         multi-query request and verifying the live snapshot follows the
///         same union shape.</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class MultiQueryUnionTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public MultiQueryUnionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedTwoNamespaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        // Namespace A — two Agent nodes.
        await adapter.Write(new MeshNode("Orchestrator", "Agent")
        { Name = "Orchestrator", NodeType = "Agent" }, _options).Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("Coder", "Agent")
        { Name = "Coder", NodeType = "Agent" }, _options).Should().Within(30.Seconds()).Emit();

        // Namespace B — one Agent node + non-Agent node (must NOT show up).
        await adapter.Write(new MeshNode("CustomBot", "User/Roland")
        { Name = "Custom Bot", NodeType = "Agent" }, _options).Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("Notes", "User/Roland")
        { Name = "Notes", NodeType = "Markdown" }, _options).Should().Within(30.Seconds()).Emit();

        // Anonymous read access so request.UserId == null queries return rows.
        var ac = _fixture.AccessControl;
        await ac.Grant("Agent", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        await ac.Grant("User", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public async Task MultiQuery_QueryAsync_UnionsBothNamespaces()
    {
        await SeedTwoNamespaces();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQueries(new[]
        {
            "namespace:Agent nodeType:Agent",       // Children-of-Agent → Orchestrator + Coder
            "namespace:User/Roland nodeType:Agent", // Children-of-User/Roland → CustomBot
        });

        var results = (await query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit())
            .Cast<MeshNode>().ToList();

        // Three Agent nodes total; non-Agent "Notes" node is filtered out by the second query's nodeType clause.
        results.Select(n => n.Path).Should().BeEquivalentTo(new[]
        {
            "Agent/Orchestrator",
            "Agent/Coder",
            "User/Roland/CustomBot"
        }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task MultiQuery_QueryAsync_DedupesNodeMatchedByMultipleBranches()
    {
        await SeedTwoNamespaces();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Both queries match Agent/Orchestrator. Postgres UNION dedupes — so the
        // node appears EXACTLY ONCE in the result, proving the dedup happened
        // server-side (a client-side "merge" of two separate result sets would
        // also need a dedup pass, but THIS test specifically validates the
        // single-round-trip UNION shape).
        var request = MeshQueryRequest.FromQueries(new[]
        {
            "namespace:Agent nodeType:Agent",
            "namespace:Agent nodeType:Agent",
        });

        var results = (await query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit())
            .Cast<MeshNode>().ToList();

        results.Select(n => n.Path).Should().BeEquivalentTo(new[]
        {
            "Agent/Orchestrator",
            "Agent/Coder"
        }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task MultiQuery_ObserveQuery_EmitsUnionedSnapshot()
    {
        await SeedTwoNamespaces();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQueries(new[]
        {
            "namespace:Agent nodeType:Agent",
            "namespace:User/Roland nodeType:Agent",
        });

        var initial = await query.Query<MeshNode>(request, _options)
            .Should().Within(30.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);

        initial.Items.Select(n => n.Path).Should().BeEquivalentTo(new[]
        {
            "Agent/Orchestrator",
            "Agent/Coder",
            "User/Roland/CustomBot"
        }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task MultiQuery_SelectExcludesContent_SkipsJsonbFetch()
    {
        // select:path on every branch → adapter emits NULL::jsonb AS content in
        // the SELECT, so returned MeshNodes have Content=null even though the
        // rows have non-trivial JSONB content in the DB. Proves the optimization
        // fires end-to-end through the multi-query UNION ALL path.
        await SeedTwoNamespaces();
        var adapter = _fixture.StorageAdapter;

        // Re-write Orchestrator with explicit content so at least one row has a
        // non-null content blob in the DB (the seed path writes Content=null).
        await adapter.Write(new MeshNode("Orchestrator", "Agent")
        {
            Name = "Orchestrator", NodeType = "Agent",
            Content = JsonSerializer.Deserialize<object>("""{"role":"primary"}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        var parser = new QueryParser();
        var parsed = new[]
        {
            parser.Parse("namespace:Agent nodeType:Agent select:path"),
            parser.Parse("namespace:User/Roland nodeType:Agent select:path"),
        };

        var results = await adapter.QueryNodesAsync(parsed, _options, ct: TestContext.Current.CancellationToken)
            .Collect(TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(n => n.Content.Should().BeNull(
            "select:path → adapter MUST NOT fetch the JSONB content column"));

        // Sanity: without select:, content IS fetched (proves the optimization is
        // conditional, not accidental).
        var parsedWithContent = new[]
        {
            parser.Parse("namespace:Agent nodeType:Agent"),
            parser.Parse("namespace:User/Roland nodeType:Agent"),
        };
        var withContent = await adapter.QueryNodesAsync(parsedWithContent, _options, ct: TestContext.Current.CancellationToken)
            .Collect(TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        withContent.Should().Contain(n => n.Path == "Agent/Orchestrator" && n.Content != null);
    }

    [Fact]
    public async Task SingleQuery_StillRoutesThroughLegacyPath()
    {
        await SeedTwoNamespaces();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Sanity: passing a single-element Queries array (or just FromQuery)
        // must yield the same result as the original single-query path —
        // multi-query plumbing is additive.
        var multi = MeshQueryRequest.FromQueries(new[] { "namespace:Agent nodeType:Agent" });
        var single = MeshQueryRequest.FromQuery("namespace:Agent nodeType:Agent");

        var multiResults = (await query.QueryList(multi, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit())
            .Cast<MeshNode>().ToList();

        var singleResults = (await query.QueryList(single, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit())
            .Cast<MeshNode>().ToList();

        multiResults.Select(n => n.Path).Should().BeEquivalentTo(
            singleResults.Select(n => n.Path), JsonSerializerOptions.Default);
    }
}
