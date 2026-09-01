using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// A multi-query union must include a STATIC node matched only by a query other than the first.
///
/// <para>🚨 What this closes. <c>MeshQueryRequest.FromQueries</c> builds a union — "matches A OR B OR
/// …" — and fills the single-query <c>Query</c> property with <c>list[0]</c> for backwards
/// compatibility. <see cref="MeshWeaver.Hosting.Persistence.Query.StorageAdapterMeshQueryProvider"/>
/// has always iterated <c>EffectiveQueries</c>; the static-node provider read <c>request.Query</c>,
/// so it only ever evaluated query #1. Any static node matched by query #2 or later was simply
/// missing from the answer — no exception, no log line, a result set that is merely INCOMPLETE.</para>
///
/// <para>It stayed invisible because the unions in the product happened to put their static half
/// first: the skill query set (<c>AgentPickerProjection.BuildSkillQueries</c>) leads with the
/// platform <c>Skill</c> catalog and follows with <c>path:{partition} scope:descendants</c> for the
/// space and node type in view. So the built-in skills always resolved, and a skill served
/// statically from a package's own partition — what a module that ships its skills inside its
/// assembly contributes — never did.</para>
/// </summary>
public class StaticNodeMultiQueryUnionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Two static nodes in two different partitions — the smallest thing a union can span.</summary>
    private sealed class TwoPartitionStaticProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return new MeshNode("alpha", "PartA/Widget") { NodeType = "SmqWidget", Name = "Alpha" };
            yield return new MeshNode("beta", "PartB/Widget") { NodeType = "SmqWidget", Name = "Beta" };
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .ConfigureServices(services => services
                .AddSingleton<IStaticNodeProvider, TwoPartitionStaticProvider>());

    private async Task<IReadOnlyList<MeshNode>> Union(params string[] queries)
        => (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQueries(queries))
            .Should().Within(System.TimeSpan.FromSeconds(30))
            .Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

    [Fact(Timeout = 30000)]
    public async Task AStaticNodeMatchedOnlyByTheSECONDQuery_IsInTheUnion()
    {
        var results = await Union(
            "path:PartA scope:descendants nodeType:SmqWidget",
            "path:PartB scope:descendants nodeType:SmqWidget");

        results.Select(n => n.Path).Should().Contain("PartA/Widget/alpha");
        results.Select(n => n.Path).Should().Contain("PartB/Widget/beta",
            "a union is 'matches A OR B'; a static node reached only by the second query used to be "
            + "dropped silently, which reads as 'that node does not exist' at every caller");
    }

    [Fact(Timeout = 30000)]
    public async Task TheFirstQueryStillAnswersOnItsOwn()
    {
        // The control: the pre-existing single-query behaviour must be untouched.
        var results = await Union("path:PartA scope:descendants nodeType:SmqWidget");

        results.Select(n => n.Path).Should().Contain("PartA/Widget/alpha");
        results.Should().NotContain(n => n.Path == "PartB/Widget/beta");
    }

    [Fact(Timeout = 30000)]
    public async Task ANodeMatchedByBOTHQueries_AppearsOnce()
    {
        var results = await Union(
            "path:PartA scope:descendants nodeType:SmqWidget",
            "nodeType:SmqWidget");

        results.Count(n => n.Path == "PartA/Widget/alpha").Should().Be(1,
            "the union is path-keyed — a node two queries both match is still one node");
        results.Select(n => n.Path).Should().Contain("PartB/Widget/beta");
    }
}
