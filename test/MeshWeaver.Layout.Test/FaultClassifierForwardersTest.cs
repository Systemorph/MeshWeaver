using System;
using System.Data.Common;
using System.Net.Sockets;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Where the copies that <see cref="FaultClassifier"/> replaced DISAGREED, the answer chosen, pinned
/// through the public forwarders consumers actually call. Each fact fails on the copy that was wrong.
/// </summary>
public class FaultClassifierForwardersTest
{
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
    }

    /// <summary>
    /// 🚨 <see cref="CancellationClassifier"/> looked for the timeout cause along
    /// <c>InnerException</c> only, so a cancellation whose timeout sat at aggregate index 1 was
    /// called benign — the impostor the classifier exists to refuse.
    /// </summary>
    [Fact]
    public void ACancellationCarryingATimeoutAtAggregateIndexOne_IsNotCooperative()
        => CancellationClassifier.IsCooperativeCancellation(
                new OperationCanceledException("cancelled",
                    new AggregateException(new InvalidOperationException("sibling"), new TimeoutException())))
            .Should().BeFalse();

    [Fact]
    public void APlainCancellation_StaysCooperative()
        => CancellationClassifier.IsCooperativeCancellation(new OperationCanceledException()).Should().BeTrue();

    /// <summary>
    /// 🚨 The view's retry predicate and the stream cache's transient predicate were "kept in sync"
    /// by comment and were not: the cache knew the routed Orleans reject ("Forwarding failed … invalid
    /// activation. Rejecting now."), the view did not, so the same reactivation miss was retried by
    /// one and rendered as a permanent error by the other.
    /// </summary>
    [Fact]
    public void TheRoutedOrleansReject_IsTransientForAView()
        => AreaErrorClassifier.IsTransientHubFailure(new InvalidOperationException(
                "Delivery to 'node/a' failed: Forwarding failed: tried to forward message to invalid activation. Rejecting now."))
            .Should().BeTrue();

    /// <summary>
    /// The view counts a cooperative cancellation as worth a re-subscribe, and that is NOT the
    /// contradiction it looks like: <see cref="CancellationClassifier"/> asks "did anything fail"
    /// (no), the view asks "is asking again worthwhile" (yes — the subscription it had is gone).
    /// </summary>
    [Fact]
    public void ACancellation_IsRetryableForAView()
        => AreaErrorClassifier.IsTransientHubFailure(new OperationCanceledException()).Should().BeTrue();

    /// <summary>
    /// 🚨 <see cref="StorageFaults"/> walked <c>InnerException</c> — aggregate branch 0 — so an
    /// aggregate whose FIRST branch was a connect fault and whose second was a real defect read as
    /// "the store is unreachable": the defect was retried and then dressed up as weather. Every
    /// branch must be a connect fault, the rule <see cref="InfrastructureFault"/> already had.
    /// </summary>
    [Fact]
    public void AnAggregateCarryingADefect_IsNotAStoreOutage()
    {
        var fault = new AggregateException(
            new FakeDbException("Failed to connect", new TimeoutException()),
            new InvalidOperationException("source B is misconfigured"));
        StorageFaults.IsTransientConnectFault(fault).Should().BeFalse();
        AreaErrorClassifier.IsStorageUnavailable(fault).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 The render frame recognised a stalled provider only as the OUTERMOST exception, the
    /// standing-watch recovery anywhere in the chain; a wrapped stall was "storage unavailable" to
    /// one and an engineering defect to the other.
    /// </summary>
    [Fact]
    public void AWrappedProviderStall_IsStorageUnavailable()
        => AreaErrorClassifier.IsStorageUnavailable(new InvalidOperationException("query failed",
                new QueryProviderStalledException("pg", TimeSpan.FromSeconds(5), "q", "u")))
            .Should().BeTrue();

    [Fact]
    public void AProviderStall_IsNeverRetriedByAViewOrByTheConnectRetry()
    {
        var stall = new QueryProviderStalledException("pg", TimeSpan.FromSeconds(5), "q", "u");
        AreaErrorClassifier.IsTransientHubFailure(stall).Should().BeFalse();
        StorageFaults.IsTransientConnectFault(stall).Should().BeFalse();
    }

    [Fact]
    public void ABareSocketFault_IsNotAStoreOutage()
        => StorageFaults.IsTransientConnectFault(new SocketException()).Should().BeFalse(
            "no driver frame — the store was not the one asked");

    /// <summary>
    /// 🚨 A stall must be refused in ANY branch: beside a socket fault it combined to a layer-less
    /// transient, and beside a timeout it lost on severity, so the combined verdict retried it.
    /// </summary>
    [Fact]
    public void AProviderStall_BesideAnotherTransientBranch_IsStillNotRetriedByAView()
    {
        var stall = new QueryProviderStalledException("pg", TimeSpan.FromSeconds(5), "q", "u");
        AreaErrorClassifier.IsTransientHubFailure(new AggregateException(stall, new SocketException()))
            .Should().BeFalse("a socket sibling must not launder the stall");
        AreaErrorClassifier.IsTransientHubFailure(new AggregateException(stall, new TimeoutException()))
            .Should().BeFalse("a timeout sibling must not out-rank the stall");
    }

    /// <summary>
    /// 🚨 The connect retry and the render frame recognised a driver frame over a nested driver
    /// frame carrying <c>57P03</c> (they walked the chain to it); the refactor stopped at the outer
    /// frame and called it contention.
    /// </summary>
    [Fact]
    public void ADriverException_OverANestedConnectClassSqlState_IsAStoreOutage()
    {
        var fault = new FakeTransientDbException("read failed",
            new FakeDbException("the database system is starting up", sqlState: "57P03"));
        StorageFaults.IsTransientConnectFault(fault).Should().BeTrue();
        AreaErrorClassifier.IsStorageUnavailable(fault).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A driver frame over a SHARED aggregate graph of socket faults (nine distinct nodes, 511
    /// paths) is still a store outage — the probe's budget counts nodes, not paths.
    /// </summary>
    [Fact]
    public void ADriverException_OverASharedSocketGraph_IsAStoreOutage()
    {
        Exception current = new SocketException((int)SocketError.ConnectionRefused);
        for (var level = 0; level < 8; level++)
            current = new AggregateException(current, current);
        StorageFaults.IsTransientConnectFault(new FakeDbException("Exception while reading from stream", current))
            .Should().BeTrue();
    }

    private sealed class FakeTransientDbException(string message, Exception? inner = null)
        : DbException(message, inner)
    {
        public override bool IsTransient => true;
    }
}
