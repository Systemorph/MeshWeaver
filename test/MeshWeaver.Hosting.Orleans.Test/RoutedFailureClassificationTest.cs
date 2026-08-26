using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The shard-0 Orleans flake, root-caused: a delivery that raced its target hub's disposal was
/// reported to the sender as TERMINAL.
///
/// <para><b>What actually happened.</b> A hub past <c>DisposeHostedHubs</c> drops an inbound
/// delivery and returns <c>Failed("Hub is shutting down")</c>. <c>OrleansRoutingService</c> saw
/// <c>State == Failed</c>, lifted that text out of the delivery's <c>Error</c> property, and called
/// <c>SendDeliveryFailure</c> — whose <c>errorType</c> parameter DEFAULTS to
/// <see cref="ErrorType.Failed"/>, and whose own documentation says that value is terminal. Its
/// comment read "Grain returned a non-transient failure (e.g. node doesn't exist)", which is
/// precisely the assumption that does not hold here.</para>
///
/// <para><b>Why it presented as a flake.</b> The hub ALSO answers, correctly, through its parent
/// with <see cref="ErrorType.ShuttingDown"/>. Two answers for one request, identical prose,
/// contradictory classification — and <c>Observe</c> resolves on whichever lands first. So
/// <c>OrleansMeshTests.HubWorksAfterDisposal</c>, which retries only on <c>ShuttingDown</c> exactly
/// as the NACK contract prescribes, gave up at random. Its failures took ~1.7 s against a 20 s
/// budget; the 4-second failure recorded in the cluster notes on 2026-08-10 was the same tell, and
/// it is why five load-shaped diagnoses (teardown starvation, <c>IoPool(2)</c>, cold Roslyn,
/// <c>maxParallelThreads</c>, "deterministic") all missed: nothing is starved in four seconds, and
/// the defect was never about load. It could not be reproduced by adding load because load was
/// never the mechanism — only the race WINNER varied, while both answers were always sent.</para>
///
/// <para>The fix makes the fourth layer agree with the three that already classify this phrase as
/// transient, so whichever answer wins, the caller reads the same verdict.</para>
/// </summary>
public class RoutedFailureClassificationTest
{
    [Theory]
    [InlineData("Hub is shutting down")]
    [InlineData("Hub app/Kernel is shutting down (RunLevel=DisposeHostedHubs) — cannot process "
        + "PingRequest; the address may reactivate (recycle / restart). Rejecting now.")]
    [InlineData("HUB IS SHUTTING DOWN")]
    public void AShuttingDownHubIsTransient_SoTheCallerRePropes(string message)
    {
        // 🚨 The whole defect in one assertion. Terminal here means every correct caller stops
        // re-probing an address that is coming back — a per-node hub recycle is routine.
        OrleansRoutingService.ClassifyRoutedFailure(message).Should().Be(ErrorType.ShuttingDown);
    }

    [Theory]
    [InlineData("No node found at 'Doc/Missing'")]
    [InlineData("Delivery failed to app/Thing")]
    [InlineData("Validation failed: name is required")]
    public void EverythingElseStaysTerminal(string message)
    {
        // The other half, and it matters just as much: "No node found" says the address does not
        // exist, and retrying THAT forever is the 2026-06-14 message storm that wedged a partition.
        // Widening the transient verdict to all failures would trade a flake for an outage.
        OrleansRoutingService.ClassifyRoutedFailure(message).Should().Be(ErrorType.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAbsentMessageStaysTerminal(string? message)
    {
        // No evidence of a recycle is not evidence of one. Defaulting an unknown failure to
        // retryable would hide real terminal failures behind a retry loop.
        OrleansRoutingService.ClassifyRoutedFailure(message).Should().Be(ErrorType.Failed);
    }
}
