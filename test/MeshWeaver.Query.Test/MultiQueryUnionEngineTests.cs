using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Engine-level coverage of the multi-query union path on the in-memory
/// <c>MeshQueryEngine</c>. Mirrors the Postgres test
/// (<c>MultiQueryUnionTests</c>) but runs against the engine directly so it
/// doesn't need a Docker testcontainer — guarantees the union semantics work
/// in the file-system / in-memory persistence used by the bulk of the
/// integration test suite.
/// </summary>
public class MultiQueryUnionEngineTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// Reactive replacement for <c>QueryAsync(...).ToListAsync()</c>: the first
    /// <see cref="QueryChangeType.Initial"/> emission carries the full snapshot.
    /// </summary>
    private IReadOnlyList<MeshNode> QueryNodes(MeshQueryRequest request)
        => MeshQuery.ObserveQuery<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    [Fact]
    public void FromQueries_QueryAsync_UnionsBothBranches()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/A1") with { Name = "A1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/A2") with { Name = "A2", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}_other/B1") with { Name = "B1", NodeType = "Markdown" }).Should().Emit();

        var results = QueryNodes(MeshQueryRequest.FromQueries(new[]
        {
            $"path:{p} nodeType:Markdown scope:descendants",
            $"path:{p}_other nodeType:Markdown scope:descendants",
        }));

        results.Select(n => n.Name).Should().BeEquivalentTo(new[] { "A1", "A2", "B1" }, Mesh.JsonSerializerOptions);
    }

    [Fact]
    public void FromQueries_DedupesNodeMatchedByMultipleBranches()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Shared") with { Name = "Shared", NodeType = "Markdown" }).Should().Emit();

        // Both queries match the same node — the engine's path-keyed dedup must
        // collapse it to one entry.
        var results = QueryNodes(MeshQueryRequest.FromQueries(new[]
        {
            $"path:{p} nodeType:Markdown scope:descendants",
            $"path:{p} nodeType:Markdown scope:descendants",
        }));

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Shared");
    }

    [Fact]
    public void FromQueries_ObserveQuery_EmitsUnionedInitial()
    {
        var p = P();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/X") with { Name = "X", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}_other/Y") with { Name = "Y", NodeType = "Markdown" }).Should().Emit();

        var initial = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQueries(new[]
            {
                $"path:{p} nodeType:Markdown scope:descendants",
                $"path:{p}_other nodeType:Markdown scope:descendants",
            }))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(10.Seconds()).Emit();

        initial.Items.Select(n => n.Name).Should().BeEquivalentTo(new[] { "X", "Y" }, Mesh.JsonSerializerOptions);
    }
}
