using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
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

    [Fact]
    public async Task FromQueries_QueryAsync_UnionsBothBranches()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/A1") with { Name = "A1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/A2") with { Name = "A2", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}_other/B1") with { Name = "B1", NodeType = "Markdown" });

        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQueries(new[]
        {
            $"path:{p} nodeType:Markdown scope:descendants",
            $"path:{p}_other nodeType:Markdown scope:descendants",
        })).ToListAsync();

        results.Cast<MeshNode>().Select(n => n.Name).Should().BeEquivalentTo("A1", "A2", "B1");
    }

    [Fact]
    public async Task FromQueries_DedupesNodeMatchedByMultipleBranches()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/Shared") with { Name = "Shared", NodeType = "Markdown" });

        // Both queries match the same node — the engine's path-keyed dedup must
        // collapse it to one entry.
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQueries(new[]
        {
            $"path:{p} nodeType:Markdown scope:descendants",
            $"path:{p} nodeType:Markdown scope:descendants",
        })).ToListAsync();

        results.Should().HaveCount(1);
        (results[0] as MeshNode)!.Name.Should().Be("Shared");
    }

    [Fact]
    public async Task FromQueries_ObserveQuery_EmitsUnionedInitial()
    {
        var p = P();
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}/X") with { Name = "X", NodeType = "Markdown" });
        await NodeFactory.CreateNode(MeshNode.FromPath($"{p}_other/Y") with { Name = "Y", NodeType = "Markdown" });

        var initial = await MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQueries(new[]
            {
                $"path:{p} nodeType:Markdown scope:descendants",
                $"path:{p}_other nodeType:Markdown scope:descendants",
            }))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(System.TimeSpan.FromSeconds(10))
            .ToTask(TestContext.Current.CancellationToken);

        initial.Items.Select(n => n.Name).Should().BeEquivalentTo("X", "Y");
    }
}
