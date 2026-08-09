using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the CONSEQUENCE half of the <see cref="ErrorType.ShuttingDown"/> contract — the reason the
/// classification matters at all.
///
/// <para>A routing/hub reject issued while the target is going down is TRANSIENT: the address may
/// reactivate (recycle, restart, redeploy). <see cref="SynchronizationStream{TStream}"/> therefore
/// RIDES IT OUT — its <c>DeliveryFailure</c> handler returns <c>Processed()</c> and leaves the
/// stream, its keep-alive and its change-feed resubscribe latch intact, so the fresh activation's
/// announce rehydrates it. Calling <c>OnError</c> there instead tore that machinery down with the
/// stream: nothing rehydrated and every reader of a mid-recycle NodeType waited to its timeout
/// (CI 30003419841, <c>NodeTypeCompileParkTest.RecycleRetry</c>).</para>
///
/// <para>Every OTHER failure kind stays terminal — the ride-out must be a narrow exemption for the
/// one transient classification, not a blanket swallow. Both halves are asserted in ONE observation
/// and with NO timing dependency: the two failures are delivered back-to-back to the stream hub's
/// single-threaded action block, so FIFO guarantees the shutdown reject is handled first. The
/// stream's FIRST terminal notification therefore identifies which branch ran — the terminal
/// failure's message if the shutdown reject was ridden out, the shutdown reject's message if it
/// was (wrongly) treated as terminal. No sleep, no "wait and see nothing happened".</para>
///
/// <para>The producing side of the same contract is pinned by
/// <c>MeshWeaver.Hosting.Monolith.Test.ShutdownRoutingRejectClassificationTest</c> (the router must
/// EMIT <see cref="ErrorType.ShuttingDown"/>) and <c>MeshWeaver.Layout.Test.SubscribeDuringRecycleTest</c>.
/// Neither is worth anything without this one: a correctly-labelled reject that the consumer still
/// treats as terminal reproduces the wedge exactly.</para>
/// </summary>
public class ShutdownFailureRideOutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    private const string TransientRejectMessage = "transient-shutdown-reject";
    private const string TerminalFailureMessage = "terminal-failure";

    [HubFact]
    public async Task ShuttingDownFailure_IsRiddenOut_WhileAnyOtherFailureStaysTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        // `using`: the stream owns a hosted sub-hub on the host, so it must be released
        // deterministically — including when an assertion below throws — rather than left for the
        // host's teardown cascade to collect.
        using var stream = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Empty>(host),
            null);

        // Observe the stream's FIRST notification. Nothing ever pushes data onto this stream, so the
        // first notification can only be the terminal one produced by whichever failure the handler
        // decided to fault on.
        var firstNotification = stream
            .Materialize()
            .FirstAsync()
            .Timeout(20.Seconds())
            .ToTask(ct);

        // 1. The transient shutdown reject — must be ridden out.
        DeliverFailure(host, stream, ErrorType.ShuttingDown, TransientRejectMessage);
        // 2. A terminal failure behind it on the SAME action block — must fault the stream.
        DeliverFailure(host, stream, ErrorType.Failed, TerminalFailureMessage);

        var notification = await firstNotification;

        notification.Kind.Should().Be(NotificationKind.OnError,
            "the terminal failure delivered second must still fault the stream — the ride-out is a "
            + "narrow exemption for ErrorType.ShuttingDown, never a blanket swallow of DeliveryFailure");
        notification.Exception!.Message.Should().Be(TerminalFailureMessage,
            $"the stream must have RIDDEN OUT the '{TransientRejectMessage}' "
            + $"({nameof(ErrorType)}.{nameof(ErrorType.ShuttingDown)}) reject and faulted only on the "
            + "terminal one behind it. Seeing the shutdown reject's message here means the transient "
            + "branch is gone: the stream, its keep-alive and its change-feed resubscribe latch die on "
            + "a reject whose address is about to reactivate, and every read of a mid-recycle NodeType "
            + "waits to its timeout (CI 30003419841)");
    }

    private static void DeliverFailure(
        IMessageHub host,
        SynchronizationStream<Empty> stream,
        ErrorType errorType,
        string message)
    {
        var subject = new MessageDelivery<Empty>(
            new Empty(),
            new PostOptions(host.Address).WithTarget(stream.Hub.Address),
            host.JsonSerializerOptions);
        var failure = new DeliveryFailure(subject) { ErrorType = errorType, Message = message };
        stream.Hub.DeliverMessage(new MessageDelivery<DeliveryFailure>(
            failure,
            new PostOptions(host.Address).WithTarget(stream.Hub.Address),
            host.JsonSerializerOptions));
    }
}
