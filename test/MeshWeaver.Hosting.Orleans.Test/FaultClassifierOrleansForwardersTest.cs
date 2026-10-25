using System;
using System.Net.Sockets;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The Orleans-layer disagreements between the copies that <see cref="FaultClassifier"/> replaced.
/// Each fact fails on the copy that was wrong.
/// </summary>
public class FaultClassifierOrleansForwardersTest
{
    /// <summary>
    /// 🚨 The two routers' "is this failure transient" predicates were near-identical and differed by
    /// exactly the network faults: <c>OrleansRoutingService</c> retried a <see cref="SocketException"/>,
    /// <c>RoutingGrain</c> turned it into a terminal <c>DeliveryFailure</c>. One predicate now.
    /// </summary>
    [Fact]
    public void ASocketFault_IsTransientForBothRouters()
    {
        var fault = new SocketException((int)SocketError.ConnectionRefused);
        OrleansRoutingService.IsTransientFailure(fault).Should().BeTrue();
        RoutingGrain.IsTransientFailure(fault).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 <c>RoutingGrain.IsTransientFailure</c> walked <c>InnerException</c> — aggregate branch 0 —
    /// so a timeout at index 1 of a two-transport aggregate was terminal by arrival order.
    /// </summary>
    [Fact]
    public void ATimeoutAtAggregateIndexOne_IsTransient()
        => RoutingGrain.IsTransientFailure(
                new AggregateException(new InvalidOperationException("other transport"), new TimeoutException()))
            .Should().BeTrue();

    /// <summary>
    /// 🚨 An activation abandoned because the hub it needed answered <see cref="ErrorType.ShuttingDown"/>
    /// is a teardown race, the same verdict the rest of the platform reaches; the activation-fault
    /// classifier knew only the typed disposal and the Autofac text, so it ticketed the race at Error.
    /// </summary>
    [Fact]
    public void AShuttingDownDelivery_IsAnActivationTeardownRace()
        => MessageHubGrain.IsActivationTeardownRace(
                new DeliveryFailureException(new DeliveryFailure(null!, "Hub node/a is shutting down")
                    { ErrorType = ErrorType.ShuttingDown }))
            .Should().BeTrue();
}
