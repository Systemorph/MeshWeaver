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

    private sealed class FakeDbException(string message, Exception? inner = null)
        : System.Data.Common.DbException(message, inner);
}
