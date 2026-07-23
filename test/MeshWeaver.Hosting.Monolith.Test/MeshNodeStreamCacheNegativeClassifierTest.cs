using System;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic classifier tests for the <see cref="MeshNodeStreamCache"/> storm-breaker's
/// TRANSIENT-vs-MISSING decision — the fix for the recurring "navigating to a just-idle page
/// intermittently crashes; a manual reload fixes it" production bug.
///
/// <para><b>The bug.</b> A per-node grain that Orleans idle-collects is deactivated. The next
/// read's <c>SubscribeRequest</c> hits the mid-<c>DeactivateOnIdle</c> activation and Orleans,
/// after forwarding <c>MaxForwardCount</c>=2 times, rejects it transiently — surfacing through
/// the silo routing grain as a <c>DeliveryFailure</c> whose message is
/// <c>"Delivery to '{path}' failed: Forwarding failed: tried to forward message … for 2 times
/// after \"DeactivateOnIdle was called.\" to invalid activation. Rejecting now."</c> (or, when
/// reactivation is slow, the 60&#160;s hub-request timeout "… target hub was not found"). The
/// cache's READ path recorded THAT into the negative cache exactly like a genuine missing node,
/// so the storm-breaker replayed the raw Orleans reject to every reader for the whole backoff
/// window AND refused to re-probe the grain that had already reactivated. A manual reload just
/// outlasted the 2&#160;s window so the re-probe landed on the fresh activation.</para>
///
/// <para><b>The invariant.</b> Only a GENUINE missing-node failure (NotFound / "No node found")
/// may be recorded as a negative (storm-breaker) entry. A transient reactivation reject / request
/// timeout is NEVER recorded — the node exists and self-heals on the next probe. Mirrors
/// <c>RoutingGrain.IsTransientFailure</c> and <c>AreaErrorClassifier.IsTransientHubFailure</c> so
/// all three layers agree.</para>
/// </summary>
public class MeshNodeStreamCacheNegativeClassifierTest
{
    // The exact string the silo routing grain (RoutingGrain.DeliverToGrainWithRetry) posts to the
    // sender once the transient Orleans forwarding-reject exhausts its retries — with the verbatim
    // Orleans reject text ("Forwarding failed … invalid activation. Rejecting now.") inlined. This
    // is the message that reached the browser as a crashed page.
    private const string ForwardingRejectMessage =
        "Delivery to 'AgenticPension/Statement' failed: Forwarding failed: tried to forward message " +
        "Request [S1]->[messagehub/AgenticPension/Statement] DeliverMessage(...) #42[ForwardCount=2] " +
        "for 2 times after \"DeactivateOnIdle was called.\" to invalid activation. Rejecting now.";

    // The 60 s hub-request timeout banner (MessageHub.BuildTimeoutMessage) — the "activation timeout"
    // symptom. It contains "target hub was not found" / "undeliverable", which the transient
    // classifier catches so a slow reactivation is never mistaken for a permanently-absent node.
    private const string RequestTimeoutMessage =
        "No response received in hub cache/mesh-node-cache within 00:01:00 for request SubscribeRequest " +
        "(id=abc) → target AgenticPension/Statement. The request may have been undeliverable or the " +
        "target hub was not found.";

    // A locally-defined stand-in with the SAME type name as the real Orleans exception, so the
    // classifier's by-name match can be exercised WITHOUT this project referencing Orleans.Core.
    private sealed class OrleansMessageRejectionException(string message) : Exception(message);

    private static DeliveryFailureException Nack(string message, ErrorType errorType)
    {
        var delivery = new MessageDelivery<object>(
            new Address("client", "1"), new Address("host", "1"), new object(), new System.Text.Json.JsonSerializerOptions());
        return new DeliveryFailureException(new DeliveryFailure(delivery, message) { ErrorType = errorType });
    }

    // ---- TRANSIENT: must be classified transient and therefore NOT recorded as a negative entry ----

    [Fact]
    public void ForwardingReject_IsTransient_NotMissingNode()
    {
        var nack = Nack(ForwardingRejectMessage, ErrorType.Failed);
        MeshNodeStreamCache.IsTransientOwnerFailure(nack).Should().BeTrue(
            "the exhausted-forward Orleans reject ('… invalid activation. Rejecting now.') is a mid-reactivation miss");
        MeshNodeStreamCache.IsMissingNodeFailure(nack).Should().BeFalse(
            "a transient reactivation reject must NEVER be recorded as a missing-node negative entry (the bug)");
    }

    [Fact]
    public void RawOrleansRejectionException_IsTransient()
    {
        var ex = new OrleansMessageRejectionException("invalid activation. Rejecting now.");
        MeshNodeStreamCache.IsTransientOwnerFailure(ex).Should().BeTrue(
            "the raw OrleansMessageRejectionException (matched by type name) is transient");
        MeshNodeStreamCache.IsMissingNodeFailure(ex).Should().BeFalse();
    }

    [Fact]
    public void RequestTimeout_TargetHubNotFound_IsTransient()
    {
        var nack = Nack(RequestTimeoutMessage, ErrorType.Exception);
        MeshNodeStreamCache.IsTransientOwnerFailure(nack).Should().BeTrue(
            "the 60 s 'target hub was not found' timeout is a slow-reactivation miss, not a permanent absence");
        MeshNodeStreamCache.IsMissingNodeFailure(nack).Should().BeFalse();
    }

    [Fact]
    public void RawTimeoutException_IsTransient()
    {
        MeshNodeStreamCache.IsTransientOwnerFailure(new TimeoutException("no response")).Should().BeTrue();
    }

    [Fact]
    public void TransientCause_WrappedAsInnerException_IsStillTransient()
    {
        var wrapped = new InvalidOperationException("outer", new TimeoutException("target hub was not found"));
        MeshNodeStreamCache.IsTransientOwnerFailure(wrapped).Should().BeTrue(
            "the transient classifier walks the inner-exception chain, like the sibling classifiers");
    }

    // ---- MISSING NODE: a genuine absence IS recorded (the storm-breaker's legitimate job) ----

    [Fact]
    public void NoNodeFound_IsMissingNode_NotTransient()
    {
        var nack = Nack(
            "No node found at 'rbuergi/_Activity/markdown-abc'. Closest ancestor is 'rbuergi' (remainder='…').",
            ErrorType.NotFound);
        MeshNodeStreamCache.IsTransientOwnerFailure(nack).Should().BeFalse(
            "a genuine 'No node found' is a permanent absence, not a transient reactivation miss");
        MeshNodeStreamCache.IsMissingNodeFailure(nack).Should().BeTrue(
            "a genuinely-missing node IS recorded — that is the storm-breaker's legitimate purpose");
    }

    [Fact]
    public void UnrelatedError_IsNeitherTransientNorMissing()
    {
        // An RLS denial or a random processing fault: not transient (don't retry-forever) and not
        // missing (don't suppress the node) — recorded by NEITHER predicate, matching prior behaviour.
        var nack = Nack("Access denied: user 'x' lacks Read permission on 'y'.", ErrorType.Unauthorized);
        MeshNodeStreamCache.IsTransientOwnerFailure(nack).Should().BeFalse();
        MeshNodeStreamCache.IsMissingNodeFailure(nack).Should().BeFalse();
    }

    [Fact]
    public void Null_IsNeither()
    {
        MeshNodeStreamCache.IsTransientOwnerFailure(null).Should().BeFalse();
    }

    // ---- TRANSIENT DATABASE-CONNECTIVITY: the 2026-07-23 memex outage ----
    //
    // An Azure emergency host repair rebooted the silo's node; for ~2 min the silo could not reach
    // Postgres. The owner READ for the AgenticEngineering NodeType node faulted with an Npgsql
    // "Failed to connect …", which crossed the Orleans grain boundary FLATTENED to its message
    // string (the inner TimeoutException object lost in serialization) and reached the cache as a
    // DeliveryFailureException. Before the fix it matched NEITHER predicate → the entry was marked
    // faulted with NO breaker window → every re-activation replayed the stale error FOREVER, until
    // a manual recycle. It MUST classify transient so the ≤60s breaker self-heals it.

    // The verbatim shape the silo routing grain posts when the owner read hits a dead PG endpoint.
    private const string PgConnectFailureMessage =
        "Delivery to 'AgenticEngineering' failed: Failed to connect to 10.42.18.4:5432";

    [Fact]
    public void PostgresConnectFailure_AsNack_IsTransient_NotMissingNode()
    {
        var nack = Nack(PgConnectFailureMessage, ErrorType.Failed);
        MeshNodeStreamCache.IsTransientOwnerFailure(nack).Should().BeTrue(
            "a lost Postgres connection is a node-EXISTS-but-DB-unreachable miss that self-heals — " +
            "the exact 2026-07-23 memex wedge");
        MeshNodeStreamCache.IsMissingNodeFailure(nack).Should().BeFalse(
            "a transient DB fault must NEVER be recorded as a missing-node negative (it would suppress a live node)");
    }

    [Theory]
    [InlineData("Failed to connect to 10.42.18.4:5432")]
    [InlineData("Timeout during connection attempt")]
    [InlineData("Exception while connecting: connection reset by peer")]
    [InlineData("server closed the connection unexpectedly")]
    [InlineData("The connection pool has been exhausted, either raise MaxPoolSize or Timeout")]
    [InlineData("57P01: terminating connection due to administrator command")]
    [InlineData("the database system is starting up")]
    [InlineData("the database system is in recovery mode")]
    [InlineData("No connection could be made because the target machine actively refused it")]
    public void TransientDatabaseFaultMessages_AreTransient(string message)
    {
        MeshNodeStreamCache.IsTransientDatabaseFailure(message).Should().BeTrue();
        MeshNodeStreamCache.IsTransientOwnerFailure(Nack(message, ErrorType.Failed)).Should().BeTrue();
    }

    [Fact]
    public void TransientDatabaseFault_WrappedAsInner_IsStillTransient()
    {
        var wrapped = new InvalidOperationException("owner read failed",
            new Exception("Failed to connect to 10.42.18.4:5432"));
        MeshNodeStreamCache.IsTransientOwnerFailure(wrapped).Should().BeTrue(
            "the classifier walks the inner-exception chain");
    }

    [Theory]
    [InlineData("relation \"mesh_nodes\" does not exist")]            // 42P01 — a real missing table, NOT transient
    [InlineData("duplicate key value violates unique constraint")]    // 23505 — a real write conflict
    [InlineData("syntax error at or near \"SELCT\"")]                 // a real SQL bug
    [InlineData("Access denied: user lacks Read permission")]         // an RLS denial
    [InlineData("")]
    public void NonTransientMessages_AreNotTransientDatabaseFaults(string message)
    {
        MeshNodeStreamCache.IsTransientDatabaseFailure(message).Should().BeFalse(
            "only connectivity faults are transient — a real query/schema/permission error must propagate");
    }
}
