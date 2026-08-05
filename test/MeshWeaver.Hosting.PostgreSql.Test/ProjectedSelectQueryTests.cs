using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 PINS "A PROJECTED Query&lt;MeshNode&gt; ANSWERS INSTEAD OF HANGING."
///
/// <para><c>select:</c> makes the query layer project each row to a
/// <c>Dictionary&lt;string,object&gt;</c> — the untyped surface's contract, pinned by
/// <c>QuerySyntaxTests.Select_SingleProperty</c>. A dictionary is not a MeshNode, so the typed
/// surface either DROPPED every row (returning empty for a query that matched) or, in the
/// aggregator's hard <c>(T)(object)</c> cast, threw inside the merge where the fault reached no
/// subscriber — leaving the provider with no Initial and no error, starving the all-providers
/// Initial gate, and hanging the caller in total silence.</para>
///
/// <para>Measured on memex 2026-08-05: every query carrying a <c>select:</c> hung past 120 s —
/// including <c>nodeType:NodeType select:path limit:5</c>, which certainly matches — while the same
/// queries without one answered instantly and Postgres served the underlying 136-schema union in
/// 57 ms.</para>
/// </summary>
[Collection("PostgreSql")]
public class ProjectedSelectQueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ProjectedSelectQueryTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private async Task Seed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        await adapter.Write(new MeshNode("Orchestrator", "Agent")
        { Name = "Orchestrator", NodeType = "Agent", Description = "runs things" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("Coder", "Agent")
        { Name = "Coder", NodeType = "Agent" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await _fixture.AccessControl.Grant("Agent", "Anonymous", "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();
    }

    private async Task<IReadOnlyList<MeshNode>> QueryNodes(string query)
    {
        var meshQuery = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var change = await meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query), _options)
            .Take(1).Should().Within(30.Seconds()).Emit();
        return change.Items.ToList();
    }

    /// <summary>The "does it exist?" projection from the CQRS cheat-sheet.</summary>
    [Fact(Timeout = 60000)]
    public async Task SelectPath_AnswersInsteadOfHanging()
    {
        await Seed();
        var results = await QueryNodes("nodeType:Agent select:path");
        results.Select(n => n.Path).Should().BeEquivalentTo(
            new[] { "Agent/Orchestrator", "Agent/Coder" }, JsonSerializerOptions.Default);
    }

    /// <summary>The "is anything stale?" projection.</summary>
    [Fact(Timeout = 60000)]
    public async Task SelectPathAndVersion_Answers()
    {
        await Seed();
        var results = await QueryNodes("nodeType:Agent select:path,version");
        results.Should().HaveCount(2);
    }

    /// <summary>A projection including name still carries it.</summary>
    [Fact(Timeout = 60000)]
    public async Task SelectWithName_CarriesTheRequestedColumn()
    {
        await Seed();
        var results = await QueryNodes("nodeType:Agent select:path,name");
        results.Select(n => n.Name).Should().BeEquivalentTo(
            new[] { "Orchestrator", "Coder" }, JsonSerializerOptions.Default);
    }

    /// <summary>An unprojected typed query is unaffected.</summary>
    [Fact(Timeout = 60000)]
    public async Task WithoutSelect_TheWholeNodeStillMaterializes()
    {
        await Seed();
        var results = await QueryNodes("nodeType:Agent");
        var orchestrator = results.Single(n => n.Path == "Agent/Orchestrator");
        orchestrator.Name.Should().Be("Orchestrator");
        orchestrator.Description.Should().Be("runs things");
    }

    /// <summary>
    /// The UNTYPED surface keeps its dictionary contract — the fix must not have been achieved by
    /// abolishing projections.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UntypedSurface_StillProjectsToADictionary()
    {
        await Seed();
        var meshQuery = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var results = await meshQuery
            .QueryList(MeshQueryRequest.FromQuery("nodeType:Agent select:name"), _options,
                TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();
        results[0].Should().BeAssignableTo<IDictionary<string, object?>>();
    }
}
