using System;
using System.Net.Sockets;
using MeshWeaver.Hosting.Persistence.Query;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The hosting-layer disagreements between the cancellation/transient copies that
/// <see cref="MeshWeaver.Messaging.FaultClassifier"/> replaced. Each fact fails on the wrong copy.
/// </summary>
public class FaultClassifierHostingForwardersTest
{
    /// <summary>
    /// 🚨 The storage walk treated ANY <see cref="OperationCanceledException"/> as mesh teardown and
    /// ended the whole walk — including a cancellation raised to express a TIMEOUT, which is one slow
    /// read: a per-path read fault its own remarks say is dropped, recorded as an incomplete snapshot,
    /// and the walk continues. <see cref="MeshWeaver.Mesh.CancellationClassifier"/> already refused
    /// that impostor; now both read the same verdict.
    /// </summary>
    [Fact]
    public void ATimeoutCancellation_IsNotTeardown_SoOneSlowReadDoesNotEndTheWalk()
        => StorageAdapterMeshQueryProvider.IsTeardownCancellation(
                new TaskCanceledException("read timed out", new TimeoutException()))
            .Should().BeFalse();

    [Fact]
    public void ACooperativeCancellation_StillEndsTheWalk()
        => StorageAdapterMeshQueryProvider.IsTeardownCancellation(new OperationCanceledException())
            .Should().BeTrue();

    /// <summary>
    /// 🚨 The stream cache matched the TEXT of a dropped connection ("connection refused") but not the
    /// typed fault, so an in-process owner read that died on a bare <see cref="SocketException"/> was
    /// cached as a permanent owner failure while the same fault flattened across a grain boundary
    /// was transient.
    /// </summary>
    [Fact]
    public void ATypedSocketFault_IsATransientOwnerFailure()
        => MeshNodeStreamCache.IsTransientOwnerFailure(new SocketException((int)SocketError.HostNotFound))
            .Should().BeTrue();

    [Fact]
    public void ACooperativeCancellation_IsNotAnOwnerFailure()
        => MeshNodeStreamCache.IsTransientOwnerFailure(new OperationCanceledException()).Should().BeFalse();

    /// <summary>
    /// 🚨 The stream cache read the connect banners on EVERY frame, a driver frame included. An
    /// unflagged driver exception saying "connection refused" with no SQLSTATE and no cause must stay
    /// a transient owner failure — otherwise an "activation failed" wrapper around it is taken for a
    /// missing node and an EXISTING node is negative-cached.
    /// </summary>
    [Fact]
    public void AnUndecidedDriverFault_ByItsText_IsNotAMissingNode()
    {
        var driver = new FakeDbException("connection refused");
        MeshNodeStreamCache.IsTransientOwnerFailure(driver).Should().BeTrue();
        MeshNodeStreamCache.IsMissingNodeFailure(new InvalidOperationException("activation failed", driver))
            .Should().BeFalse();
    }

    /// <summary>
    /// 🚨 A standing watch must not re-open on a provider-flagged driver fault whose nested aggregate
    /// carries a permanent content defect beside the socket reset: the flag speaks for the driver,
    /// not for the defect, and re-opening trades a latch for a loop.
    /// </summary>
    [Fact]
    public void AStandingWatch_DoesNotReopen_OnAFlaggedDriverFaultCarryingADefect()
    {
        StandingWatchRecovery.IsTransient(new FlaggedDbException("read failed",
                new AggregateException(new SocketException((int)SocketError.ConnectionReset),
                    new System.Text.Json.JsonException("bad content"))))
            .Should().BeFalse();
        StandingWatchRecovery.IsTransient(new FlaggedDbException("read failed",
                new AggregateException(new SocketException((int)SocketError.ConnectionReset))))
            .Should().BeTrue("a flagged driver over transport faults alone is still a store fault");
    }

    /// <summary>
    /// 🚨 A standing watch re-opens on a timeout over a socket fault — the outage is recoverable —
    /// and on two flagged drivers over all-transient branches, but never on two flagged drivers over
    /// a content defect.
    /// </summary>
    [Fact]
    public void AStandingWatch_ReadsTheEvidenceBeneathTimeoutsAndNestedDrivers()
    {
        StandingWatchRecovery.IsTransient(new TimeoutException("read failed", new SocketException((int)SocketError.ConnectionReset)))
            .Should().BeTrue("the socket fault beneath the time-box is recoverable");
        StandingWatchRecovery.IsTransient(new TimeoutException("hung")).Should().BeFalse("a cause-free timeout is not");
        StandingWatchRecovery.IsTransient(new FlaggedDbException("outer", new FlaggedDbException("inner",
                new AggregateException(new SocketException((int)SocketError.ConnectionReset),
                    new System.Text.Json.JsonException("bad content")))))
            .Should().BeFalse("the inner driver's flag does not stand in for the defect it encloses");
    }

    /// <summary>
    /// 🚨 A time-box over a fault the OWNER classified as transient (<see cref="MeshWeaver.Mesh.MeshNodeError.Transient"/>
    /// on a <see cref="MeshWeaver.Mesh.MeshNodeStreamException"/>) is that store outage, not a plain timeout —
    /// the standing watch re-opens on it, as the walk it replaced did. Unclassified, the same wrapper is not.
    /// </summary>
    [Fact]
    public void AStandingWatch_ReopensOnATimeoutOverAnOwnerClassifiedStoreFault()
    {
        MeshWeaver.Mesh.MeshNodeStreamException Owner(bool transient) => new(new MeshWeaver.Mesh.MeshNodeError(
            MeshWeaver.Mesh.MeshNodeErrorCode.Unknown, "Admin/Build", "NpgsqlException: Failed to connect") { Transient = transient });

        StandingWatchRecovery.IsTransient(new TimeoutException("read failed", Owner(transient: true)))
            .Should().BeTrue("the owner classified the store fault beneath the time-box as transient");
        StandingWatchRecovery.IsTransient(new TimeoutException("read failed", Owner(transient: false)))
            .Should().BeFalse("the control: an owner fault it did NOT classify transient stays a plain timeout");
    }

    /// <summary>
    /// 🚨 A timeout or IO wrapper INSIDE a driver exception is not the evidence when it has a cause — its cause
    /// is. A driver over Timeout(Aggregate(socket, JsonException)) carries a permanent content defect, flagged
    /// or not, and a standing watch must not retry it; a driver over a CAUSE-FREE timeout, or over IO(socket),
    /// stays a transient store fault.
    /// </summary>
    [Fact]
    public void ADriverProbe_ReadsTheBranchesBeneathATimeoutOrIoWrapper()
    {
        Exception Defect() => new TimeoutException("read timed out",
            new AggregateException(new SocketException((int)SocketError.ConnectionReset), new System.Text.Json.JsonException("bad content")));

        StandingWatchRecovery.IsTransient(new FakeDbException("read failed", Defect()))
            .Should().BeFalse("the timeout wraps a content defect beside the socket reset");
        StandingWatchRecovery.IsTransient(new FlaggedDbException("read failed", Defect()))
            .Should().BeFalse("the provider's flag does not stand in for the defect beneath the timeout");
        StandingWatchRecovery.IsTransient(new FakeDbException("read failed", new TimeoutException("read timed out")))
            .Should().BeTrue("a cause-free driver timeout is still an unreachable store");
        StandingWatchRecovery.IsTransient(new FlaggedDbException("read failed", new TimeoutException("read timed out")))
            .Should().BeTrue("and so is a flagged one");
        StandingWatchRecovery.IsTransient(new FakeDbException("read failed",
                new System.IO.IOException("broken pipe", new SocketException((int)SocketError.ConnectionReset))))
            .Should().BeTrue("an IO wrapper over a socket fault reaches the socket fault");
    }

    private sealed class FakeDbException(string message, Exception? inner = null)
        : System.Data.Common.DbException(message, inner);

    private sealed class FlaggedDbException(string message, Exception? inner = null)
        : System.Data.Common.DbException(message, inner)
    {
        public override bool IsTransient => true;
    }
}
