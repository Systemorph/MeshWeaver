using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// #6826 on the shape it was reported on: the five NodeTypes the documentation ships, served from the
/// embedded <c>Doc</c> partition, are KNOWN to <see cref="ActivationRecycle.Request"/> — and a Doc page
/// that is not a type is not.
///
/// <para>The portal refused all five as "unknown NodeType(s)" from each type's own recycle cascade,
/// on every replica. What made its QUERY miss there is a store/read-model divergence of the production
/// host that this in-process mesh does not reproduce — the discriminating test is
/// <c>AnActivationRecycleKnowsTypesFromTheStoreTest</c> in MeshWeaver.Graph.Test. This one pins that the
/// store read the check now uses reaches the embedded partition for every shipped type. The embedded
/// partition is reached only through the partition-routing persistence, which is why the bootstrap here
/// is the partitioned one.</para>
/// </summary>
public class DocNodeTypesAreKnownToActivationRecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private sealed class PartitionedBootstrap : IMeshBootstrap
    {
        public string Name => "monolith-partitioned";

        public MeshBuilder Bootstrap(MeshBuilder builder)
            => builder.UseMonolithMesh().AddPartitionedInMemoryPersistence();
    }

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new PartitionedBootstrap();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentation();

    private Task<ActivationRecycleTicket> Recycle(params string[] types) =>
        ActivationRecycle.Request(Mesh, new ActivationRecycleRequest
            {
                NodeTypes = types.ToImmutableList(),
                Reason = "test: #6826 — the shipped Doc NodeTypes are known",
                RequestedBy = nameof(DocNodeTypesAreKnownToActivationRecycleTest),
            })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", TestContext.Current.CancellationToken);

    [Fact(Timeout = 120_000)]
    public async Task EveryShippedDocNodeType_IsKnown()
    {
        var ticket = await Recycle(
            "Doc/Architecture/ATypicalNodeType/Budget",
            "Doc/Architecture/BusinessRules/Cession",
            "Doc/DataMesh/PythonPandasNode/PandasExplorer",
            "Doc/DataMesh/SocialMedia/Post",
            "Doc/DataMesh/SocialMedia/Profile");

        ticket.Refusal.Should().BeNull("each of the five is a NodeType the Doc partition serves");
        ticket.Path.Should().StartWith(ActivationRecycleRequest.Namespace + "/");
    }

    /// <summary>The side that must not move: a Doc page and an absent Doc path are not types.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ADocPage_AndAnAbsentDocPath_AreRefusedAsUnknown()
    {
        var page = await Recycle("Doc/DataMesh/SocialMedia/LinkedInPublishing");
        page.Path.Should().BeNull("a Markdown page is a node, not a NodeType");
        page.Refusal.Should().Contain("unknown NodeType");

        var absent = await Recycle("Doc/DataMesh/SocialMedia/NoSuchType");
        absent.Path.Should().BeNull("nothing is served at that path");
        absent.Refusal.Should().Contain("unknown NodeType");
    }
}
