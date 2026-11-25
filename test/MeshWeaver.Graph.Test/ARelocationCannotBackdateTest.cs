using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A relocation carries the STORED source's dates — never the message's.</b> A person who may
/// move a source can name it in <c>CreateNodeRequest.AuthorshipFrom</c>; the create then keeps the
/// source's authorship. The dates must come from the same stored source: otherwise a crafted
/// <c>createdDate</c> in the message would ride the relocation branch and backdate a record, which
/// is exactly the hole the server-clock stamp closes for an ordinary create.
///
/// <para>Negative control: a real MOVE keeps the source's original <c>createdDate</c>.</para>
/// </summary>
public class ARelocationCannotBackdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly AccessContext Mallory = new() { ObjectId = "mallory", Name = "Mallory" };
    private static readonly DateTimeOffset SourceBorn = new(2019, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Crafted = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private async Task SeedMalloryHomeWithSource(string id)
    {
        await SeedTopLevel(new MeshNode("mallory")
        {
            Name = "Mallory's home",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        });
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("mallory_Access", "mallory/_Access")
            {
                Name = "Mallory owns her home",
                NodeType = AccessAssignmentGuard.AccessAssignmentNodeType,
                MainNode = "mallory",
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = "mallory",
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the platform grants Mallory her own home");

        // The platform preserves carried stamps: the source is born in 2019.
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(id, "mallory")
            {
                Name = id,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                CreatedDate = SourceBorn,
                LastModified = SourceBorn,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the platform seeds the source");
    }

    [Fact]
    public async Task APersonsRelocatingCreate_WithACraftedCreatedDate_EndsWithTheSourcesDates()
    {
        await SeedMalloryHomeWithSource("src");

        var response = await Access.RunAs(Mallory, () => ObserveNodeOperation(
                new CreateNodeRequest(new MeshNode("dst", "mallory")
                {
                    Name = "dst",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                    CreatedDate = Crafted,
                    LastModified = Crafted,
                })
                { AuthorshipFrom = "mallory/src" },
                o => o.WithAccessContext(Mallory)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit("the create must answer");

        response.Success.Should().BeTrue($"Mallory may create and move in her own home — {response.Error}");
        response.Node!.CreatedDate.Should().Be(MeshNode.StorageStable(SourceBorn),
            "a relocation carries the stored source's createdDate, never the message's");
        response.Node.LastModified.Should().Be(MeshNode.StorageStable(SourceBorn));
        response.Node.CreatedDate.Should().NotBe(Crafted);
    }

    /// <summary>Negative control: a real move keeps the source's original createdDate.</summary>
    [Fact]
    public async Task ARealMove_KeepsTheSourcesOriginalCreatedDate()
    {
        await SeedMalloryHomeWithSource("mv");

        var moved = await Access.RunAs(Mallory, () => ObserveNodeOperation(
                new MoveNodeRequest("mallory/mv", "mallory/moved"),
                o => o.WithAccessContext(Mallory)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit("the move must answer");

        moved.Success.Should().BeTrue($"Mallory may move within her home — {moved.Error}");
        var target = moved.Node!;
        target.CreatedDate.Should().Be(MeshNode.StorageStable(SourceBorn));
    }
}
