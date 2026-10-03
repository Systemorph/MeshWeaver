using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A per-model token-usage satellite lives at <c>{ns}/_Thread/{id}/_Usage/{model}</c>. Placement is
/// by SEGMENT, so the row lands in the <c>threads</c> satellite table (Postgres) and counts as a
/// satellite path (in-memory). The instance-wide Token usage / "AI usage &amp; cost" tab reads it with
/// a PATHLESS <c>nodeType:TokenUsage</c> query — and a pathless query is resolved to a table by its
/// nodeType alone. <c>TokenUsage</c> was in no mapping's <c>NodeTypes</c>, so the query was sent to
/// the PRIMARY table (Postgres) and, in memory, every row was dropped as "a satellite path in a
/// query that does not target satellites": the tab read nothing, on every backend.
///
/// <para>The Postgres half of the same statement is the <c>threads</c> resolution pinned in
/// <see cref="TheTokenUsageNodeType_ResolvesToTheThreadsTable"/> — the provider's table choice is
/// <see cref="PartitionDefinition.ResolveTableByNodeType"/> over the same mapping (the Postgres
/// provider lives in MeshWeaver.Plugins).</para>
/// </summary>
public class TokenUsageSatelliteQueryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A stand-in declaration of the AI module's <c>TokenUsage</c> type (the module lives in
    /// MeshWeaver.Plugins); only the type NAME matters to where the query reads.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(new MeshNode("TokenUsage")
        {
            Name = "Token Usage",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition(),
        });

    [Fact]
    public void TheTokenUsageNodeType_ResolvesToTheThreadsTable()
    {
        PartitionDefinition.IsSatelliteNodeType("TokenUsage").Should().BeTrue(
            "a usage row is a _Thread satellite, so a nodeType-only query must target satellites");
        var definition = new PartitionDefinition
        {
            Namespace = "acme",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
        };
        definition.ResolveTableByNodeType("TokenUsage").Should().Be("threads",
            "the row is placed by its _Thread segment, so the nodeType query must read that table");
    }

    [Fact(Timeout = 60000)]
    public async Task APathlessTokenUsageQuery_AsSystem_FindsTheUsageRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var usagePath = $"{TestPartition}/usage-{suffix}/_Thread/t1/_Usage/model-a";
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(usagePath) with
            {
                Name = "model-a",
                NodeType = "TokenUsage",
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        foreach (var query in new[]
                 {
                     "nodeType:TokenUsage partitions:all",
                     $"namespace:{TestPartition} scope:descendants nodeType:TokenUsage",
                 })
        {
            var found = await access.RunAsSystem(() => MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
                .Select(change => change.Items.Select(n => n.Path).ToList())
                .Where(paths => paths.Contains(usagePath))
                .FirstAsync()
                .Should().Within(TestTimeouts.Convergence)
                .Emit(because: $"'{query}' must return the usage satellite", cancellationToken: ct);
            found.Should().Contain(usagePath);
        }
    }
}
