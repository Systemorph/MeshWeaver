using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// A thread MUST be creatable from a (properly onboarded) user's home — and the created thread MUST be
/// REACHABLE (its node stream resolves, no hung subscribe / "target hub not found"). This pins the bug
/// the 10-message GUI test surfaced: chatting from the home failed because the thread create was denied
/// on the system <c>User</c> partition (the user's own partition didn't exist / wasn't the target), and
/// when it did land, subscribing to it timed out — "not reachable", which IS a wedge.
///
/// <para>The owner (<see cref="TestUsers.Admin"/> = "Roland") owns the writable <c>Roland</c> partition;
/// a thread started from their home belongs there (<c>Roland/_Thread/{id}</c>), never the system
/// <c>User</c> partition. Deterministic — real mesh, no GUI/portal.</para>
/// </summary>
public class ThreadCreatableFromHomeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnerId = "Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    [Fact(Timeout = 90_000)]
    public async Task Thread_IsCreatableFromOwnerHome_AndReachable()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(TestUsers.Admin); // the owner — must be able to create threads in their partition

        var client = GetClient();
        // The owner's partition must be live (onboarded ⇒ partition exists). A PingRequest activates it;
        // if the partition does not exist this fails fast — the "partition must be created at onboarding"
        // precondition.
        await client.Observe(new PingRequest(), o => o.WithTarget(new Address(OwnerId)))
            .Should().Within(30.Seconds()).Emit();

        // Start a thread from the home, exactly as the composer's Send does (StartThread is THE surface).
        var tcs = new TaskCompletionSource<MeshNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StartThread(
            namespacePath: OwnerId,
            userText: "Hello from the home composer",
            createdBy: OwnerId,
            authorName: OwnerId,
            onCreated: node => tcs.TrySetResult(node),
            onError: err => tcs.TrySetException(new InvalidOperationException($"Thread NOT creatable: {err}")));

        var threadNode = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // It must land in the OWNER's writable partition, never the system User partition.
        threadNode.Path.Should().StartWith($"{OwnerId}/_Thread/",
            "a thread started from the owner's home belongs in their writable partition, not the system 'User' partition");

        // REACHABILITY (the "not reachable = wedging" guard): the created thread's node stream must
        // resolve. A SubscribeRequest that times out ("target hub not found") is exactly the wedge.
        var workspace = client.GetWorkspace();
        var read = await workspace.GetMeshNodeStream(threadNode.Path)
            .Where(n => n is not null && n.NodeType == ThreadNodeType.NodeType)
            .Take(1).Timeout(TimeSpan.FromSeconds(20)).ToTask();
        read.Should().NotBeNull(
            "the created thread MUST be reachable — subscribing must resolve, never hang or NotFound (that is the wedge)");
    }
}
