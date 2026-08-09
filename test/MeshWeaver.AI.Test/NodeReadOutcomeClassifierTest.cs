using System;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins <see cref="NodeReadOutcome.FromReadFailure"/> — the point where a failed node read decides
/// between "there is nothing here" and "we could not find out" (issue #974, split out of #637).
///
/// <para><b>The bug.</b> <c>MeshOperations.FetchNode</c> mapped every failure — and its own 10s
/// budget — onto the same <c>null</c>, and every caller rendered that as <c>"Not found: {path}"</c>.
/// Agents act on that sentence: they re-create the node (duplicating it) or delete and rebuild the
/// "broken" path. Against a node that exists and is merely unreachable for a moment, that is
/// destructive, and it is invited by a message we had no basis to print.</para>
///
/// <para><b>Typed, not textual.</b> The classification reads the failure's TYPE
/// (<see cref="ErrorType"/> / <see cref="UnauthorizedAccessException"/>), never its wording — a
/// message-string sniff drifts the moment someone rewords a banner. Both routers stamp
/// <see cref="ErrorType.NotFound"/> on a genuinely-missing node
/// (<c>RoutingServiceBase.PostNotFound</c>, <c>RoutingGrain</c>), which is what makes the typed
/// check sufficient.</para>
/// </summary>
public class NodeReadOutcomeClassifierTest
{
    private const string Path = "AgenticPension/Statement";

    private static DeliveryFailureException Nack(string message, ErrorType errorType)
    {
        var delivery = new MessageDelivery<object>(
            new Address("client", "1"), new Address("host", "1"), new object(), new JsonSerializerOptions());
        return new DeliveryFailureException(new DeliveryFailure(delivery, message) { ErrorType = errorType });
    }

    // ---- DEFINITIVE: the read reached an answer ----

    [Fact]
    public void RoutingNotFound_IsADefinitiveAbsence()
    {
        // The authoritative "this node does not exist" — the router resolved the address and found
        // nothing. This is the ONLY failure that may be reported as "Not found", and it must keep
        // working: a fix that made everything unavailable would be just as useless.
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, Nack($"No node found at '{Path}'.", ErrorType.NotFound));

        Assert.False(outcome.IsUnavailable);
        Assert.Null(outcome.Node);
    }

    [Fact]
    public void ReadDenied_StaysAbsent_SoAGatedNodeIsNotDisclosed()
    {
        // A per-user read denial is a COMPLETED evaluation with a definitive answer ("nothing
        // readable here for you"), so it is not an availability failure. Reporting it as one would
        // also disclose that a gated node exists at this exact path — the non-disclosure property
        // this leg has always had, and which the fix must not break.
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, new UnauthorizedAccessException("lacks Read"));

        Assert.False(outcome.IsUnavailable);
        Assert.Null(outcome.Node);
    }

    [Fact]
    public void ADenialNestedUnderAnotherException_IsStillAbsent()
    {
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, new InvalidOperationException("wrapped", new UnauthorizedAccessException("lacks Read")));

        Assert.False(outcome.IsUnavailable);
    }

    // ---- THE DEFECT: availability failures must stop claiming absence ----

    [Fact]
    public void RequestTimeout_IsUnavailable_NotNotFound()
    {
        // 🚨 The regression pin. A read that timed out says nothing about whether the node exists.
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, new TimeoutException($"No response received in hub … target {Path}"));

        Assert.True(outcome.IsUnavailable);
        Assert.Contains("faulted", outcome.UnavailableReason);
    }

    [Fact]
    public void OrleansReactivationReject_IsUnavailable_TheGrainIsComingBack()
    {
        // A grain that idle-collected rejects the next delivery while it reactivates. The node
        // exists; the very next probe lands on the fresh activation. Calling it "not found" is how
        // a transient miss turns into a delete-and-recreate.
        var outcome = NodeReadOutcome.FromReadFailure(
            Path,
            Nack("Forwarding failed: tried to forward message … to invalid activation. Rejecting now.",
                ErrorType.Failed));

        Assert.True(outcome.IsUnavailable);
    }

    [Fact]
    public void HubShuttingDown_IsUnavailable()
    {
        // A delivery that raced the target hub's disposal — the address may reactivate on the very
        // next probe (ErrorType.ShuttingDown is documented as retry-worthy, never terminal).
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, Nack($"Hub '{Path}' is shutting down", ErrorType.ShuttingDown));

        Assert.True(outcome.IsUnavailable);
    }

    [Fact]
    public void DatabaseBlackout_IsUnavailable()
    {
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, new InvalidOperationException("Failed to connect to 10.0.0.4:5432"));

        Assert.True(outcome.IsUnavailable);
    }

    [Fact]
    public void AnUnrecognisedFault_DefaultsToUnavailable_NotAbsent()
    {
        // 🚨 The direction of the default is itself load-bearing. An unrecognised fault is by
        // definition one nobody has reasoned about; admitting we do not know costs a retry, while
        // guessing "not found" invites deleting a node that exists.
        var outcome = NodeReadOutcome.FromReadFailure(Path, new Exception("something new"));

        Assert.True(outcome.IsUnavailable);
    }

    [Fact]
    public void ADeliveryFailureThatIsNotNotFound_IsUnavailable()
    {
        // Same rule at the typed layer: only ErrorType.NotFound is a definitive absence. A generic
        // Failed NACK is an infrastructure outcome, whatever its wording happens to be.
        var outcome = NodeReadOutcome.FromReadFailure(
            Path, Nack("Delivery to 'x' failed: something went wrong", ErrorType.Failed));

        Assert.True(outcome.IsUnavailable);
    }

    // ---- The value legs ----

    [Fact]
    public void Found_CarriesTheNode_AndAbsentCarriesNothing()
    {
        var node = new MeshNode("Statement", "AgenticPension");
        Assert.Same(node, NodeReadOutcome.Found(node).Node);
        Assert.False(NodeReadOutcome.Found(node).IsUnavailable);

        Assert.Null(NodeReadOutcome.Absent.Node);
        Assert.False(NodeReadOutcome.Absent.IsUnavailable);

        Assert.True(NodeReadOutcome.Unavailable("stalled").IsUnavailable);
        Assert.Null(NodeReadOutcome.Unavailable("stalled").Node);
    }
}
