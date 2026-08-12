using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A RECYCLING NODE IS NOT A MISSING NODE.
///
/// <para><see cref="ErrorType.ShuttingDown"/> has an explicit contract (Events.cs): "retry-worthy,
/// never terminal … the sender must read this as 'ask again', not 'gone'". Routing mints it
/// DELIBERATELY instead of NotFound for a live-but-recycling address
/// (<c>MonolithRoutingService</c>: <c>isShuttingDown ? ErrorType.ShuttingDown : ErrorType.NotFound</c>),
/// and the live-stream path honours that — <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>
/// classifies "is shutting down" as transient.</para>
///
/// <para><b>The read primitive did not.</b> <c>GetMeshNode</c>'s OnError arm surfaced only
/// <see cref="ErrorType.Unauthorized"/> and collapsed everything else — ShuttingDown included — into
/// the same <c>null</c> a genuine not-found emits. So a caller was told the node does not exist while
/// it was merely recycling, with no way to tell the two apart.</para>
///
/// <para><b>Field evidence.</b> <c>ThreadAgentIntegrationTest</c> on CI: the <c>ACME/ProductLaunch</c>
/// instance hub self-recycled via the overlay self-heal watcher, its parked <c>GetDataRequest</c> was
/// NACKed ShuttingDown, and the test failed on "ACME/ProductLaunch node should exist" 6.4 s into a
/// 60 s budget. Before that NACK existed (9d8880c68) the same race HUNG for the full 60 s — the
/// symptom migrated from a stall into a confident wrong answer, which is worse.</para>
///
/// <para>The sibling <c>DeferredDeliveryNackedOnDisposeTest</c> pins that the NACK is produced and
/// carries ShuttingDown. This pins what the READER does with it.</para>
/// </summary>
public class GetMeshNodeShuttingDownIsNotAbsentTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private static readonly Address RecyclingAddress = new("recycling", "node");

    /// <summary>
    /// Counts how many <c>GetDataRequest</c>s reach the address, so the re-probe is not merely
    /// inferred from the outcome — the count IS the assertion that it happened exactly once.
    /// </summary>
    private int _reads;

    [Fact(Timeout = 120_000)]
    public async Task ShuttingDownNack_IsSurfaced_NeverCollapsedToNull()
    {
        // A hub that ALWAYS answers as one caught mid-recycle — the exact DeliveryFailure shape
        // routing mints for a shutting-down address (MonolithRoutingService:
        // `isShuttingDown ? ErrorType.ShuttingDown : ErrorType.NotFound`). Always, so the test is
        // deterministic: there is no millisecond window to lose, unlike the production race.
        var recycling = Mesh.GetHostedHub(
            RecyclingAddress,
            c => c.WithHandler<GetDataRequest>((hub, delivery) =>
            {
                Interlocked.Increment(ref _reads);
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.ShuttingDown,
                        Message = $"Hub {RecyclingAddress} is shutting down — cannot read. "
                                  + "The address may reactivate (recycle / restart); retry to get "
                                  + "the authoritative answer."
                    },
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            }));
        recycling.Should().NotBeNull();

        var read = Mesh
            .GetMeshNode(RecyclingAddress.ToString(), TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // The whole point: a caller must NOT be handed null. Null is what "node not found" means, and
        // a recycling node is present — that conflation is what made the CI failure claim a node in
        // the test's own fixture did not exist.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => read);

        Output.WriteLine($"surfaced: {failure.GetType().Name}: {failure.Message}");
        failure.Message.Should().Contain("ShuttingDown",
            "the error must name the classification, so the next reader knows to retry rather than "
            + "concluding the node is gone");
        failure.Message.Should().Contain("NOT absent",
            "…and must say explicitly that this is not absence — the misreading this exists to stop");

        // Exactly TWO reads: the original plus ONE re-probe. Not one (that would mean the transient
        // was surfaced without giving the address its documented chance to reactivate) and not three
        // or more (that would be a retry loop, which this repo bans — the re-probe is bounded and
        // terminates on the second answer, whatever it is).
        Volatile.Read(ref _reads).Should().Be(2,
            "ShuttingDown earns exactly one re-probe against a fresh activation — bounded, not a loop");
    }
}
