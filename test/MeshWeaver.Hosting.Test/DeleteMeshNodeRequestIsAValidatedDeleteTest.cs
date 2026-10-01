using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

#pragma warning disable CS0618 // DeleteMeshNodeRequest is obsolete — posting it is the subject under test

/// <summary>
/// 🚨 <see cref="DeleteMeshNodeRequest"/> is not a back door around the delete checks.
///
/// <para>Every per-node hub handles it, and its handler used to call <c>IStorageAdapter.Delete</c>
/// on whatever path the message named — no permission check, no delete validator. Nothing in the
/// platform posts it, but every ingress can deliver it: the SignalR and gRPC connection hubs forward
/// any delivery to any address (an unauthenticated client as Anonymous), and in-mesh code holds a
/// hub. The handler now forwards to the validated delete under the delivery's own identity.</para>
///
/// <para>Each refusal is paired with its control in the same mesh: an identity that MAY delete
/// posts the same message to a sibling and it is removed — so the message reaches its handler and
/// the wait below is long enough to have seen a delete.</para>
/// </summary>
public class DeleteMeshNodeRequestIsAValidatedDeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ViewerId = "delete-request-viewer";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>The viewer reads the partition and holds no Delete anywhere. Built from
    /// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — the default mesh grants Public → Admin,
    /// under which every identity holds Delete.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode($"{ViewerId}_Viewer", $"{TestPartition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{ViewerId} — Viewer",
                MainNode = TestPartition,
                Content = new AccessAssignment
                {
                    AccessObject = ViewerId,
                    DisplayName = ViewerId,
                    Roles = [new RoleAssignment { Role = Role.Viewer.Id }],
                },
            });

    [Theory(Timeout = 60000)]
    [InlineData(ViewerId)]
    [InlineData(WellKnownUsers.Anonymous)]
    public async Task ACallerWithoutDelete_PostingIt_DeletesNothing_WhileAnAdminPostingItDoes(string callerId)
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateNode(NewPath("target"));
        var control = await CreateNode(NewPath("control"));

        var client = GetClient();
        client.Post(new DeleteMeshNodeRequest(target),
            o => o.WithTarget(new Address(target)).WithAccessContext(Identity(callerId)));
        client.Post(new DeleteMeshNodeRequest(control),
            o => o.WithTarget(new Address(control)).WithAccessContext(TestUsers.Admin));

        // The control: an identity that may delete posts the same message, and the node goes.
        await Gone(control).Should().Within(TestTimeouts.Convergence)
            .Emit("an admin's DeleteMeshNodeRequest is forwarded to the validated delete, which removes it",
                cancellationToken: TestContext.Current.CancellationToken);

        // The refusal: same message, same window, a caller without Delete — the node stays.
        await Gone(target).Should().NotEmit(TestTimeouts.Quick,
            $"'{callerId}' holds no Delete on {target}; the request must not delete it raw");
        (await StoredAt(target)).Should().NotBeNull("the refused request left the node in storage");
    }

    /// <summary>Emits once storage no longer holds <paramref name="path"/> — the sanctioned re-query
    /// shape for a source with no change signal of its own.</summary>
    private IObservable<bool> Gone(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(node => node is null)
            .Select(_ => true)
            .Take(1);

    private Task<MeshNode?> StoredAt(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<string> CreateNode(string path)
    {
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };
}
