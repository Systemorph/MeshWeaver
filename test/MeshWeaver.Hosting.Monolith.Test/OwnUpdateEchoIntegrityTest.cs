using System;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the write-echo contract of <c>MeshNodeStreamHandle.UpdateOwn</c> — the
/// primitive behind every own-hub <c>workspace.GetMeshNodeStream().Update(...)</c>.
///
/// <para><b>The defect this pins (FrameworkStaleInstanceRenderTest CI flake, run
/// 29749071939):</b> UpdateOwn used to complete on the FIRST own-stream emission
/// whose stream version exceeded a baseline captured at subscribe time — without
/// verifying the emission reflected the caller's own write. Any concurrent write
/// (another writer on the same node, or a satellite create in the same collection)
/// landing in the window between Subscribe and the caller's update lambda made the
/// observable complete with a PRE-WRITE state, before the lambda even ran. In
/// production that misfired <c>HandleDispatchCompile</c>'s <c>weTransitioned</c>
/// read → the compile dispatch was skipped → the NodeType wedged at
/// <c>Compiling</c> forever. Under CI load the window was hit routinely; locally
/// almost never — the definition of the flake.</para>
///
/// <para>The assertion here is VALUE-based, not timing-based: an Update's emission
/// must always contain that Update's own write. Post-fix this holds by
/// construction (the echo is gated on the writer's own stamped Version); pre-fix
/// the interleaving loop below violates it within a few iterations.</para>
/// </summary>
public class OwnUpdateEchoIntegrityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentOwnUpdates_EachEmissionContainsItsOwnWrite()
    {
        var nodeId = $"echo-race-{Guid.NewGuid():N}";
        var nodePath = $"{TestPartition}/{nodeId}";

        await NodeFactory.CreateNode(
                new MeshNode(nodeId, TestPartition) { Name = "seed", NodeType = "Markdown" })
            .Should().Emit();

        // Activate the per-node hub and grab it — UpdateOwn only runs on the
        // owning hub's workspace (a pathed handle from another hub routes remote).
        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();
        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("node hub must exist after ping");

        var workspace = nodeHub!.GetWorkspace();

        // Two concurrent writers per iteration, each touching a DIFFERENT field.
        // Whatever interleaving the scheduler produces, writer A's emission must
        // carry A's Name value and writer B's emission must carry B's Description
        // value — any state at-or-past a writer's own commit contains that
        // writer's field, because only that writer touches it. The old
        // baseline-version echo detection returned the OTHER writer's (or the
        // pre-write) state here within a few iterations.
        const int iterations = 200;
        for (var i = 0; i < iterations; i++)
        {
            var expectedName = $"name-{i}";
            var expectedDescription = $"desc-{i}";

            var writerA = workspace.GetMeshNodeStream()
                .Update(curr => curr with { Name = expectedName })
                .Should().Within(TimeSpan.FromSeconds(30)).Emit(
                    $"writer A's update (iteration {i}) must land and echo");
            var writerB = workspace.GetMeshNodeStream()
                .Update(curr => curr with { Description = expectedDescription })
                .Should().Within(TimeSpan.FromSeconds(30)).Emit(
                    $"writer B's update (iteration {i}) must land and echo");

            var emittedA = await writerA;
            var emittedB = await writerB;

            emittedA.Path.Should().Be(nodePath,
                $"iteration {i}: writer A's echo must be the target node, never a sibling");
            emittedB.Path.Should().Be(nodePath,
                $"iteration {i}: writer B's echo must be the target node, never a sibling");
            emittedA.Name.Should().Be(expectedName,
                $"iteration {i}: writer A's emission must contain writer A's own write "
                + "(a pre-write or foreign echo means the Update completed before its lambda ran)");
            emittedB.Description.Should().Be(expectedDescription,
                $"iteration {i}: writer B's emission must contain writer B's own write");
        }
    }
}
