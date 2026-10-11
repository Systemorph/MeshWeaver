using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5731 - a delivery whose message EXPIRED while Orleans was still placing its target grain.
///
/// <para>Orleans' PlacementService cancels such a message with an OperationCanceledException that
/// the caller receives as a CANCELLED task. Two things were wrong on our side: Rx's task bridge
/// replaced the stored exception with a message-less TaskCanceledException ("A task was canceled."),
/// so nothing downstream could recognise the condition, and the classifier would not have known it
/// anyway, so the sender was told a terminal Failed and consumers that ride out ShuttingDown tore
/// down. The facts here are pure and need no cluster; the exception text is quoted verbatim from the
/// production log.</para>
/// </summary>
public class PlacementExpiryClassificationTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    /// <summary>
    /// Verbatim from the production log (#5731, 2026-09-24 13:43:20Z, Orleans.Messaging[100071]).
    /// </summary>
    private const string PlacementExpiredText =
        "Message expired before placement could complete for grain "
        + "messagehub/Signature/Desk/_Activity/compile-20260924134235046d3415968dc0b4b9cad1eddb1be5b8bb6.";

    /// <summary>
    /// The SHAPE Orleans hands the caller: an async completion that throws an OperationCanceledException
    /// ends as a CANCELLED task that still stores it - the same state ValueTask.AsTask produces for a
    /// carried cancellation.
    /// </summary>
    private static async Task<IMessageDelivery> ExpiredBeforePlacement()
    {
        await Task.Yield();
        throw new OperationCanceledException(PlacementExpiredText);
    }

    private static async Task<IMessageDelivery> CancelledForSomeOtherReason()
    {
        await Task.Yield();
        throw new OperationCanceledException("The operation was canceled.");
    }

    [Fact]
    public async Task APlacementExpiry_ReachesTheClassifierWithItsOwnText_AndIsNotResent()
    {
        var calls = 0;
        Exception? caught = null;

        try
        {
            await RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        calls++;
                        return ExpiredBeforePlacement();
                    },
                    grainKey: "messagehub/Signature/Desk/_Activity/compile-1",
                    deliveryId: "placement-expiry-1",
                    logger: NullLogger.Instance,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .Await(TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        calls.Should().Be(1,
            "the message died while being addressed; sending it again would wait out another full "
            + "time-to-live inside placement while holding a routing-pool slot, so the sender is told "
            + "on the first attempt");
        RoutingGrain.IsPlacementExpired(caught).Should().BeTrue(
            "Rx's task bridge turns a cancelled task into a message-less TaskCanceledException, and the "
            + "grain call must hand over the exception the task stored instead - otherwise no classifier "
            + "downstream can see the Orleans sentence");
    }

    [Fact]
    public async Task APlacementExpiry_IsNackedAsTransient_NotTerminal()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.DeliverToGrainRoute(
                grainCall: ExpiredBeforePlacement,
                grainKey: "messagehub/Signature/Desk/_Activity/compile-1",
                addressPath: "Signature/Desk/_Activity/compile-1",
                deliveryId: "placement-expiry-2",
                postFailureToSender: (message, type) => nacks.Add((message, type)),
                logger: NullLogger.Instance,
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .Await(TestContext.Current.CancellationToken);

        nacks.Count.Should().Be(1, "the route answers the sender exactly once");
        nacks[0].Type.Should().Be(ErrorType.ShuttingDown,
            "the message reached no activation - a statement about the cluster, not the target - and the "
            + "consumers with their own recovery machinery ride out ShuttingDown and tear down on Failed");
        nacks[0].Message.Contains(RoutingGrain.PlacementExpiredMarker, StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue("the sender's NACK now names what happened instead of 'A task was canceled.'");
    }

    [Fact]
    public void APlacementExpiry_IsClassifiedTransient_ButNeverResent()
    {
        var expired = new OperationCanceledException(PlacementExpiredText);

        RoutingGrain.ClassifyDeliveryException(expired).Should().Be(ErrorType.ShuttingDown);
        RoutingGrain.ClassifyPodHubDeliveryException(expired).Should().Be(ErrorType.ShuttingDown,
            "the pod-hub leg classifies through the same arms");
        RoutingGrain.IsResendableDeliveryFailure(expired).Should().BeFalse(
            "classification only: a re-send under a persistent placement stall would hold a routing-pool "
            + "slot for a full time-to-live per attempt");
    }

    [Fact]
    public void ItIsFoundBesideAnotherFaultInAnAggregate()
    {
        var twoTransports = new AggregateException(
            new InvalidOperationException("the NACK's other transport also failed"),
            new OperationCanceledException(PlacementExpiredText));

        RoutingGrain.ClassifyDeliveryException(twoTransports).Should().Be(ErrorType.ShuttingDown,
            "which fault sits at index 0 of an aggregate is a race, so the walk reads the whole graph");
    }

    [Fact]
    public async Task AnyOtherCancellation_StaysTerminal()
    {
        RoutingGrain.ClassifyDeliveryException(new OperationCanceledException("The operation was canceled."))
            .Should().Be(ErrorType.Failed,
                "a hub raising a cancellation while it shuts down or times out is a different statement "
                + "from an expiry in placement - only Orleans' one sentence qualifies");

        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.DeliverToGrainRoute(
                grainCall: CancelledForSomeOtherReason,
                grainKey: "messagehub/Signature/Desk/_Activity/compile-1",
                addressPath: "Signature/Desk/_Activity/compile-1",
                deliveryId: "placement-expiry-3",
                postFailureToSender: (message, type) => nacks.Add((message, type)),
                logger: NullLogger.Instance,
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .Await(TestContext.Current.CancellationToken);

        nacks.Count.Should().Be(1);
        nacks[0].Type.Should().Be(ErrorType.Failed,
            "anything this does not recognise stays terminal, so a genuine defect is still reported as one");
    }

    [Fact]
    public void TheMarkerQuotedByAnythingButACancellation_StaysTerminal()
    {
        RoutingGrain.ClassifyDeliveryException(new InvalidOperationException(PlacementExpiredText))
            .Should().Be(ErrorType.Failed,
                "the words alone are not the signal: the type guard is what keeps an unrelated fault that "
                + "quotes them from being demoted to a transient verdict");
    }

    /// <summary>
    /// THE ANTI-INERT PIN. If this fact fails after an Orleans upgrade, IsPlacementExpired has gone
    /// SILENTLY INERT - repair the marker, never delete the test. The phrase is a literal of the SHIPPED
    /// Orleans.Runtime (PlacementService lives there), not a copy of it.
    /// </summary>
    [Fact]
    public void PlacementExpiredMarker_IsStillTheShippedOrleansWording()
    {
        var runtimeAssembly = Path.Combine(AppContext.BaseDirectory, "Orleans.Runtime.dll");

        File.Exists(runtimeAssembly).Should().BeTrue(
            $"this fact reads Orleans' own string literals out of {runtimeAssembly}; without the file it "
            + "would pass having verified nothing, which is precisely the failure mode it exists to prevent");

        ShippedLiteralPresent(runtimeAssembly, RoutingGrain.PlacementExpiredMarker).Should().BeTrue(
            $"'{RoutingGrain.PlacementExpiredMarker}' is the phrase RoutingGrain.IsPlacementExpired matches "
            + "an expiry in Orleans' PlacementService on. If Orleans reworded it, a delivery that expired "
            + "before its target was placed is NACK'd as permanent again (#5731). REPAIR THE MARKER; do not "
            + "delete this");
    }

    /// <summary>
    /// Reads the #US heap at BOTH byte alignments - a managed string literal's UTF-16 payload follows a
    /// compressed-integer length, so a blob can begin at an ODD file offset and decoding from offset 0
    /// alone would miss a phrase that is genuinely present (a false red on the one assertion whose value
    /// is being believed when it fires). OrdinalIgnoreCase, matching the classifier.
    /// </summary>
    private static bool ShippedLiteralPresent(string assemblyPath, string phrase)
    {
        var raw = File.ReadAllBytes(assemblyPath);

        return Encoding.Unicode.GetString(raw)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase)
            || Encoding.Unicode.GetString(raw, 1, raw.Length - 1)
                   .Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }
}