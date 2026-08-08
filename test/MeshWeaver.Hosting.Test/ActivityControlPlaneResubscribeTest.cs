using System;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Repro + contract for the atioz compile-activity resubscribe storm (2026-06-10):
/// <c>ActivityControlPlaneExtensions.WatchControlPlane</c>/<c>WatchSubmission</c> subscribe
/// to the hub's OWN node; when that node is gone/unroutable the read faults with a routing
/// <see cref="ErrorType.NotFound"/>, and the OLD code re-established every 1 s forever — a
/// ~1 Hz storm of doomed cross-hub SubscribeRequests through the single RoutingGrain that
/// starved unrelated subscriptions (rsalzmann's thread subscribe timed out). A terminal
/// NotFound on the own node must STOP the watcher, not resubscribe.
/// </summary>
public class ActivityControlPlaneResubscribeTest
{
    private static DeliveryFailureException NotFoundOnOwnNode()
    {
        var addr = new Address("mesh", "1");
        var delivery = new MessageDelivery<DisposeRequest>(addr, addr, new DisposeRequest(), new JsonSerializerOptions());
        return new DeliveryFailureException(new DeliveryFailure(delivery) { ErrorType = ErrorType.NotFound });
    }

    [Fact]
    public void TerminalNotFound_OnOwnNode_DoesNotResubscribe()
    {
        // The storm: a NotFound on the hub's own node is terminal, so the watcher must
        // subscribe exactly once and never loop. The synchronous test seam means any
        // re-establish would run immediately — so a count > 1 here IS the storm.
        var notFound = NotFoundOnOwnNode();
        var subscriptions = 0;
        var reEstablishes = 0;

        // Bounded synchronous seam: with the fix the terminal NotFound never schedules a
        // re-establish (subscriptions stays 1). WITHOUT the fix it would resubscribe up to
        // the bound — a clean count assertion that proves the storm, not a stack overflow.
        using var _ = ActivityControlPlaneExtensions.SubscribeWithReEstablish<int>(
            () => Observable.Defer(() => { subscriptions++; return Observable.Throw<int>(notFound); }),
            _ => { },
            new Address("mesh", "1"),
            logger: null,
            faultLogContext: "test",
            scheduleReEstablish: reEstablish => { if (reEstablishes++ < 5) reEstablish(); });

        subscriptions.Should().Be(1,
            "a terminal NotFound on the own node must NOT trigger a resubscribe storm");
    }

    [Fact]
    public void TransientFault_StillResubscribes()
    {
        // Companion guard: a non-NotFound (transient) fault must still re-establish so a
        // live activity is never left unobserved — bounded here to avoid infinite recursion.
        var transient = new InvalidOperationException("transient hub hiccup");
        var subscriptions = 0;
        var scheduled = 0;

        using var _ = ActivityControlPlaneExtensions.SubscribeWithReEstablish<int>(
            () => Observable.Defer(() => { subscriptions++; return Observable.Throw<int>(transient); }),
            _ => { },
            new Address("mesh", "1"),
            logger: null,
            faultLogContext: "test",
            scheduleReEstablish: reEstablish => { if (scheduled++ < 2) reEstablish(); });

        subscriptions.Should().Be(3,
            "a transient fault must re-establish (initial subscribe + 2 bounded re-establishes)");
    }

    [Fact]
    public void PoisonedContent_DoesNotResubscribe_AndReachesTheVisibleSink()
    {
        // #891: a MeshNodeStreamException(Deserialization) is the per-emission typing fault the
        // stream handle raises for undeserializable node content. The faulted stream REPLAYS the
        // same emission, so a re-establish is a 1 Hz poison loop — the watcher must stop after
        // exactly one subscription AND surface the fault through the visible sink (the compile
        // watchers park the NodeType there), never die on a single log line.
        var poison = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Deserialization, "mesh/1", "cannot materialize NodeTypeDefinition"));
        var subscriptions = 0;
        var reEstablishes = 0;
        Exception? sunk = null;
        var sinkCalls = 0;

        using var _ = ActivityControlPlaneExtensions.SubscribeWithReEstablish<int>(
            () => Observable.Defer(() => { subscriptions++; return Observable.Throw<int>(poison); }),
            _ => { },
            new Address("mesh", "1"),
            logger: null,
            faultLogContext: "test",
            scheduleReEstablish: reEstablish => { if (reEstablishes++ < 5) reEstablish(); },
            onPoisonedContent: ex => { sinkCalls++; sunk = ex; });

        subscriptions.Should().Be(1,
            "poisoned content replays on every resubscribe — re-establishing is a poison loop");
        sinkCalls.Should().Be(1, "the fault must land in the visible sink exactly once");
        sunk.Should().BeSameAs(poison);
    }

    [Fact]
    public void IsPoisonedContent_ClassifiesOnlyDeserializationFaults()
    {
        var poison = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Deserialization, "mesh/1", "bad content"));
        ActivityControlPlaneExtensions.IsPoisonedContent(poison).Should().BeTrue();

        // Wrapped one level down — still detected.
        ActivityControlPlaneExtensions.IsPoisonedContent(
            new InvalidOperationException("outer", poison)).Should().BeTrue();

        // Other MeshNode error codes are NOT poison — a NotFound goes through the
        // own-node-gone classification, a Conflict is transient.
        ActivityControlPlaneExtensions.IsPoisonedContent(
            new MeshNodeStreamException(new MeshNodeError(
                MeshNodeErrorCode.Conflict, "mesh/1", "version conflict"))).Should().BeFalse();
        ActivityControlPlaneExtensions.IsPoisonedContent(
            new InvalidOperationException("hub hiccup")).Should().BeFalse();
    }

    [Fact]
    public void IsOwnNodeGone_TypedNotFound_And_RewrappedMessage_BothDetected()
    {
        // Typed path (Failure.ErrorType == NotFound).
        ActivityControlPlaneExtensions.IsOwnNodeGone(NotFoundOnOwnNode()).Should().BeTrue();

        // Fallback: the cross-hub failure is sometimes re-wrapped without the typed
        // Failure (the doubled "DeliveryFailureException: ... No node found" seen in prod).
        var rewrapped = new InvalidOperationException(
            "No node found at 'Doc/DataMesh/SocialMedia/Post/_Activity/compile-x'");
        ActivityControlPlaneExtensions.IsOwnNodeGone(rewrapped).Should().BeTrue();

        // A genuinely transient fault is NOT treated as terminal.
        ActivityControlPlaneExtensions.IsOwnNodeGone(new InvalidOperationException("hub hiccup"))
            .Should().BeFalse();
    }
}
