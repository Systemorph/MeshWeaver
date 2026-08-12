using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// <see cref="MeshOperationOptions.Timeout"/> is HONOURED by <c>IMeshService</c> — issue #1270,
/// part 2.
///
/// <para><b>The hole.</b> <c>MeshService</c>'s class doc promised "each call is bounded by
/// <c>MeshOperationOptions.Timeout</c> … never a hang", <c>Doc/Architecture/AsynchronousCalls</c>
/// told every author to write <c>.Timeout(OpTimeout); ← NEVER OMIT</c> on this surface, and the
/// option is genuinely load-bearing on the server side (<c>HandleDeleteNodeRequest</c> bounds its
/// fan-out with it). But <c>MeshService.OpTimeout</c> was DECLARED AND NEVER READ — there was no
/// <c>.Timeout(</c> anywhere in the file. So the client half of that contract did not exist, and a
/// composed operation (an update's validation pipeline, a create whose partition bootstrap fans
/// out into nested creates) had no whole-operation bound at all. An advertised bound that nothing
/// enforces is worse than no bound: callers believe they are protected.</para>
///
/// <para><b>The pin.</b> A client hub with an impossible ceiling must see the write fail with a
/// <see cref="TimeoutException"/> that NAMES the operation, the node and the knob. Scoped to that
/// one hub — the mesh (and this fixture's own writes) keep the 30 s default — so the test cannot
/// pass or fail for any reason other than the option being consumed.</para>
///
/// <para><b>RED before the fix:</b> the option is ignored, the create simply succeeds, and no
/// exception is thrown at all.</para>
/// </summary>
public class MeshOperationTimeoutHonouredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task Write_SurfacesTheAdvertisedMeshOperationTimeout()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1 ms is a legitimate configuration ("lower it to fail faster in environments where slow
        // ops are suspicious" — WithMeshOperationTimeout's own doc) and no cross-hub round trip
        // can beat it, so the outcome is not a race.
        var tight = GetClient(c => ConfigureClient(c)
            .WithMeshOperationTimeout(TimeSpan.FromMilliseconds(1)));
        var service = tight.ServiceProvider.GetRequiredService<IMeshService>();

        // A VALID create — a partition-owning Space, exactly as SpaceMenuAndAccessTest writes one.
        // The shape matters: an invalid node is rejected in a few milliseconds, and the test would
        // then be a race between the rejection and the budget instead of a statement about the
        // budget. A real Space create provisions a partition schema, so it is nowhere near 1 ms —
        // and WITHOUT the fix it plainly succeeds, which is the RED this pins.
        var path = $"OpBudget{Guid.NewGuid():N}"[..18];
        var node = MeshNode.FromPath(path) with
        {
            Name = "Budget probe",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };

        var create = () => service.CreateNode(node).FirstAsync().ToTask(ct);

        var thrown = await create.Should().ThrowAsync<TimeoutException>(
            "MeshService advertises MeshOperationOptions.Timeout as a per-operation ceiling, so a "
            + "budget the write cannot possibly meet must surface as a TimeoutException — before "
            + "#1270 the option was read by nothing and the create simply succeeded");
        Output.WriteLine($"create faulted with: {thrown.Which.Message}");

        thrown.Which.Message.Should().Contain("MeshOperationOptions.Timeout",
            "the failure must name the knob, or an operator cannot tell a budget from a stall");
        thrown.Which.Message.Should().Contain(path,
            "the failure must name the node whose write was abandoned");
        thrown.Which.Message.Should().Contain(nameof(IMeshService.CreateNode),
            "the failure must name the operation");
    }
}
