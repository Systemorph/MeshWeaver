using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the CLASSIFICATION of a routing reject issued while the mesh is shutting down:
/// <c>MonolithRoutingService.PostNotFound</c> must NACK it as the TRANSIENT
/// <see cref="ErrorType.ShuttingDown"/>, never as a terminal <see cref="ErrorType.Failed"/>.
///
/// <para>Why the member matters — two consumers match on it EXPLICITLY and change behaviour:
/// <c>JsonSynchronizationStream</c> (the SubscribeRequest <c>OnError</c> arm) returns early and
/// keeps the stream + keep-alive ALIVE for the change-feed resubscribe latch, and
/// <c>SynchronizationStream</c>'s <c>DeliveryFailure</c> handler returns <c>Processed()</c>
/// instead of calling <c>OnError</c>. Anything else reads as TERMINAL to both: the stream faults,
/// the resubscribe latch dies with it, and every reader of a mid-recycle NodeType waits to its
/// timeout — CI 30003419841 / <c>NodeTypeCompileParkTest.RecycleRetry</c>, the exact regression
/// the <c>ShuttingDown</c> member exists to prevent. PR #1022 introduced the branch with
/// <c>ErrorType.Failed</c>; this test is what makes that choice impossible to reintroduce.</para>
///
/// <para><b>How the window is opened deterministically</b> (no sleeps, no polling for a state):
/// the reject only happens while <c>Mesh.IsDisposing</c> is true AND
/// <c>RunLevel &lt; DisposeHostedHubs</c> — otherwise routing short-circuits and nothing is
/// NACKed at all. The mesh's single-threaded action block is therefore stalled with a gated
/// handler (the <c>TeardownHubCreationFreezeTest</c> pattern), and the two messages are queued
/// BEHIND it in the order that produces the window:
/// <list type="number">
///   <item>the probe — <c>Observe</c> pre-registers its response callback BEFORE posting, so a
///     pending callback provably exists when the Quiescing phase later probes for one;</item>
///   <item><c>Mesh.Dispose()</c>, which flips <c>IsDisposing</c> SYNCHRONOUSLY and only then
///     posts <c>ShutdownRequest(Quiescing)</c> — i.e. after the probe in FIFO order.</item>
/// </list>
/// Releasing the gate replays exactly that interleaving: the probe routes with
/// <c>IsDisposing</c> already true and <c>RunLevel</c> still below <c>DisposeHostedHubs</c>, and
/// the Quiescing phase cannot race past it because the probe's own pending callback is what
/// Quiescing waits to drain. The NACK answers it, quiescing drains, teardown stays clean.</para>
/// </summary>
public class ShutdownRoutingRejectClassificationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Message whose handler stalls the mesh's action block for the duration of the setup.</summary>
    private record StallMeshActionBlock;

    // Instance gates (never static — they die with the test instance, like every other
    // fixture-owned state in this suite). Test-side gating only: nothing in src/ is timed.
    private readonly ManualResetEventSlim stallEntered = new(false);
    private readonly ManualResetEventSlim releaseStall = new(false);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureHub(c => c
                // The base's 500 ms budget is a LEAKED-callback detector, and it assumes no test
                // deliberately has a request in flight when Quiescing starts. This one does — by
                // construction: the probe's pending callback IS what holds the window open, and it
                // is answered by a routing round trip that crosses two thread-pool hops. 500 ms is
                // the wrong budget for that on a loaded 2-core runner, and blowing it fails the
                // CLASS with a leak report instead of the assertion. Nothing is masked: the probe's
                // own 10 s Rx timeout fires FIRST if the NACK never arrives, so a genuinely
                // unanswered request still fails here, loudly, and on the right assertion.
                .WithQuiesceTimeout(TimeSpan.FromSeconds(20))
                .WithTypes(typeof(StallMeshActionBlock))
                .WithHandler<StallMeshActionBlock>((_, request) =>
                {
                    stallEntered.Set();
                    // Deliberately ignores cancellation (Dispose() calls CancelExecution):
                    // the point is that the ShutdownRequest posted by Dispose() cannot be
                    // processed until the test has queued everything behind it.
                    releaseStall.Wait(TimeSpan.FromSeconds(30));
                    return request.Processed();
                }));

    [Fact(Timeout = 60_000)]
    public async Task RoutingRejectWhileMeshIsShuttingDown_IsTransientShuttingDown_NotTerminalFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/shutdown-reject-node";
        await NodeFactory.CreateNode(
                new MeshNode("shutdown-reject-node", TestPartition) { Name = "target", NodeType = "Markdown" })
            .Should().Emit();

        // Precondition, checked not assumed: the address RESOLVES with an empty remainder.
        // That is what routes the probe into MonolithRoutingService.RouteImpl → CreateHub →
        // PostNotFound (the branch under test) rather than into RoutingServiceBase's own
        // unresolvable-path NotFound. Resolving here also warms the resolver's positive cache
        // so the probe's routing is a cache hit and completes well inside the Quiescing budget.
        var resolution = await Mesh.ServiceProvider.GetRequiredService<IPathResolver>()
            .ResolvePath(path)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
        resolution.Should().NotBeNull("the node was just created — routing must resolve it");
        resolution!.Remainder.Should().BeNullOrEmpty(
            "an exact resolution is what sends the probe down the CreateHub path whose reject we are classifying");

        // The per-node hub must NOT exist yet: RouteInMesh short-circuits to an already-hosted
        // hub and would never reach CreateHub/PostNotFound.
        Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
            .Should().BeNull("the reject under test only fires when the target hub still has to be created");

        Task<IMessageDelivery<GetDataResponse>> probe;
        try
        {
            // 1. Stall the mesh action block. Everything posted from here on queues behind it.
            Mesh.Post(new StallMeshActionBlock(), o => o.WithTarget(Mesh.Address));
            stallEntered.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue(
                "the stalling handler must own the action block before the interleaving is queued");

            // 2. Queue the probe. Observe pre-registers the response callback before posting.
            probe = Mesh.Observe(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(path)))
                .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

            // 3. Dispose: IsDisposing flips synchronously, ShutdownRequest queues after the probe.
            Mesh.Dispose();
            Mesh.IsDisposing.Should().BeTrue("Dispose() must flip the flag before it returns");
        }
        finally
        {
            releaseStall.Set();
        }

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => probe);
        Output.WriteLine($"[reject] {failure.Failure.ErrorType}: {failure.Failure.Message}");

        failure.Failure.Message.Should().Contain("shutting down",
            "the probe must have been rejected by the shutdown branch of PostNotFound, not by an "
            + "ordinary NotFound — otherwise the ErrorType assertion below would pin nothing");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a shutdown reject is TRANSIENT: JsonSynchronizationStream and SynchronizationStream both "
            + "match on ErrorType.ShuttingDown to keep the stream and its change-feed resubscribe latch "
            + "ALIVE. ErrorType.Failed reads as terminal to both, faults the stream, kills the latch and "
            + "wedges every read of a mid-recycle NodeType (CI 30003419841)");
    }
}
