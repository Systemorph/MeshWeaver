using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A Space deleted and RECREATED under the same id grants its creator the same access the
/// first create did</b> — the child create that follows is the property under test.
///
/// <para>This is the in-memory shape of MeshWeaver.Plugins'
/// <c>SpaceDeletionPartitionDropTests.DeletingSpace_DropsWholePartition_AndSameIdCanBeRecreated</c>,
/// whose LAST step fails intermittently on CI with
/// <c>Access denied: Create permission required for node '&lt;space&gt;/page'</c> (#4061 finding 2).
/// The first child create is the negative control: without it the assertion below would be reading
/// a constant of the create path rather than a property of the RECREATE.</para>
///
/// <para><b>Measured: green on `main`, every run, in ~0.5 s.</b> A NEGATIVE result, recorded on the
/// issue — the in-memory mesh does not drop a partition SCHEMA, so it does not reproduce the
/// PostgreSQL failure. It stays as a guard because delete-then-recreate-under-the-same-id is a real
/// user operation whose regression would be silent.</para>
/// </summary>
public class SpaceRecreateGrantVisibilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SpaceId = "recreategrant";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    private static MeshNode Space() => new(SpaceId)
    {
        Name = "Recreate Grant",
        NodeType = SpaceNodeType.NodeType,
        State = MeshNodeState.Active,
        Content = new Space(),
    };

    private static MeshNode Child(string id) => new(id, SpaceId)
    {
        Name = id, NodeType = "Markdown", State = MeshNodeState.Active,
    };

    [Fact(Timeout = 180_000)]
    public async Task AChildCreateSucceedsAfterTheSpaceIsDeletedAndRecreated()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await meshService.CreateNode(Space())
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space", cancellationToken: TestContext.Current.CancellationToken);

        // The negative control: the SAME child create, on the FIRST incarnation. If this failed the
        // assertion below would be reading a constant of the create path, not the recreate.
        await meshService.CreateNode(Child("first"))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the creator holds Admin on the Space it just created, so a child create is permitted", cancellationToken: TestContext.Current.CancellationToken);

        await meshService.DeleteNode(SpaceId)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may delete its own Space", cancellationToken: TestContext.Current.CancellationToken);

        await meshService.CreateNode(Space())
            .Should().Within(TestTimeouts.CrossSilo).Emit("the same id may be created again", cancellationToken: TestContext.Current.CancellationToken);

        await meshService.CreateNode(Child("second"))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the RECREATE writes the creator's grant exactly as the first create did, so the "
                + "child create must be permitted on the recreated Space too", cancellationToken: TestContext.Current.CancellationToken);
    }
}
