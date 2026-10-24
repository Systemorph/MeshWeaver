using System;
using System.Data.Common;
using System.Net.Sockets;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The ONE classifier for cancellation, timeout, teardown and transient faults
/// (<see cref="FaultClassifier"/>), stated as an executable truth table, plus the disagreements
/// between the copies it replaced that live in this layer — each written so it FAILS on the copy
/// that was wrong.
/// </summary>
public class FaultClassifierTest
{
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null, bool transient = false)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
        public override bool IsTransient => transient;
    }

    private static DeliveryFailureException Failure(string message, ErrorType type)
        => new(new DeliveryFailure(null!, message) { ErrorType = type });

    public static TheoryData<string, Exception?, string, string?> Corpus() => new()
    {
        { "null — nothing benign to say about an unknown", null, FaultKind.Fault, null },
        { "a cooperative cancellation", new OperationCanceledException(), FaultKind.CooperativeCancel, null },
        { "an HttpClient-style timeout: a cancellation carrying a TimeoutException",
            new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()), FaultKind.Timeout, null },
        { "a cancellation whose timeout cause sits at aggregate index 1",
            new OperationCanceledException("t", new AggregateException(new InvalidOperationException("x"), new TimeoutException())),
            FaultKind.Timeout, null },
        { "a bare timeout", new TimeoutException(), FaultKind.Timeout, null },
        { "a routed transport timeout flattened to text",
            Failure("Delivery to 'Store' failed: Response did not arrive on time in 00:00:30", ErrorType.Failed), FaultKind.Timeout, null },
        { "a hub announcing its disposal", new HubDisposingException(new Address("node", "a"), "stream"), FaultKind.HubTeardown, null },
        { "a ShuttingDown delivery failure", Failure("Hub node/a is shutting down", ErrorType.ShuttingDown), FaultKind.HubTeardown, null },
        { "the Autofac disposed-scope fault", new ObjectDisposedException("LifetimeScope", "Instances cannot be resolved and nested lifetimes cannot be created from this LifetimeScope"),
            FaultKind.ScopeTeardown, null },
        { "a bare disposed object with no context — not provably a teardown", new ObjectDisposedException("MemoryStream"), FaultKind.Fault, null },
        { "a bare socket fault", new SocketException((int)SocketError.HostNotFound), FaultKind.Transient, FaultLayer.Network },
        { "a driver wrapping a connect timeout", new FakeDbException("Failed to connect", new TimeoutException()), FaultKind.Transient, FaultLayer.Store },
        { "a connection-class SQLSTATE", new FakeDbException("cannot connect now", sqlState: "57P03"), FaultKind.Transient, FaultLayer.Store },
        { "a provider-transient conflict", new FakeDbException("deadlock", sqlState: "40P01", transient: true), FaultKind.Transient, FaultLayer.StoreContention },
        { "a schema error", new FakeDbException("undefined_table", sqlState: "42P01"), FaultKind.Fault, null },
        { "the database's own 'is shutting down' is a STORE fault, never a hub teardown",
            new FakeDbException("the database system is shutting down", sqlState: "57P03"), FaultKind.Transient, FaultLayer.Store },
        { "an Orleans forwarding reject flattened to text",
            new InvalidOperationException("Forwarding failed: tried to forward message to invalid activation. Rejecting now."), FaultKind.Transient, FaultLayer.Transport },
        { "a genuine fault", new InvalidOperationException("boom"), FaultKind.Fault, null },
        { "a mixed aggregate keeps the defect", new AggregateException(new SocketException(), new InvalidOperationException("boom")), FaultKind.Fault, null },
        { "a teardown anywhere wins", new AggregateException(new InvalidOperationException("boom"), new HubDisposingException(new Address("node", "a"), "x")),
            FaultKind.HubTeardown, null },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classifies(string shape, Exception? exception, string kind, string? layer)
    {
        var verdict = FaultClassifier.Classify(exception);
        verdict.Kind.Should().Be(kind, shape);
        verdict.Layer.Should().Be(layer, shape);
    }

    [Fact]
    public void IsCancellation_IsTheShape_WhateverTheCause()
    {
        FaultClassifier.Classify(new OperationCanceledException()).IsCancellation.Should().BeTrue();
        FaultClassifier.Classify(new OperationCanceledException("hub gone", new HubDisposingException(new Address("node", "a"), "x")))
            .IsCancellation.Should().BeTrue("a cancellation raised by a teardown is still a cancellation");
        FaultClassifier.Classify(new AggregateException(new OperationCanceledException()))
            .IsCancellation.Should().BeTrue("a bridged task's single-inner aggregate");
        FaultClassifier.Classify(new AggregateException(new OperationCanceledException(), new InvalidOperationException("boom")))
            .IsCancellation.Should().BeFalse("a defect beside the cancellation is a fault");
        FaultClassifier.Classify(new InvalidOperationException("wrapped", new OperationCanceledException()))
            .IsCancellation.Should().BeFalse("a fault wrapping a cancellation is a fault");
        FaultClassifier.Classify(new TaskCanceledException("timeout", new TimeoutException()))
            .IsCancellation.Should().BeFalse("a cancellation raised to express a timeout is the impostor");
    }

    [Fact]
    public void ScopeProbe_DecidesWhoseScope()
    {
        var autofac = new ObjectDisposedException("LifetimeScope", "nested lifetimes cannot be created from this LifetimeScope");
        FaultClassifier.Classify(autofac, new TeardownContext(() => false)).Kind.Should().NotBe(FaultKind.ScopeTeardown,
            "the caller's own scope is alive: another scope's disposal is not this caller's teardown");
        FaultClassifier.Classify(new ObjectDisposedException("x"), new TeardownContext(() => true)).Kind.Should().Be(FaultKind.ScopeTeardown);
    }

    [Fact]
    public void OwnAddress_LimitsHubTeardownToThisHub()
    {
        var other = new HubDisposingException(new Address("node", "other"), "x");
        FaultClassifier.Classify(other, new TeardownContext(OwnAddress: new Address("node", "me"))).IsTeardown.Should().BeFalse();
        FaultClassifier.Classify(other, new TeardownContext(OwnAddress: new Address("node", "other"))).Kind.Should().Be(FaultKind.HubTeardown);
    }

    [Fact]
    public void APathologicallyDeepGraph_Terminates()
    {
        Exception current = new SocketException();
        for (var i = 0; i < 400; i++)
            current = new AggregateException(current, current);
        FaultClassifier.Classify(current).Kind.Should().BeOneOf(FaultKind.Transient, FaultKind.Fault);
    }

    /// <summary>
    /// 🚨 Disagreement: <see cref="InfrastructureFault"/> trusted only the provider's
    /// <c>IsTransient</c> flag, <c>StorageFaults</c> only the SQLSTATE. A connection-class SQLSTATE
    /// the provider did not flag was "store unreachable" to the query retry and the render frame,
    /// and a permanent FAILED latch to the hub initializer. The SQLSTATE is the server's own word
    /// that it could not serve a session — transient for both.
    /// </summary>
    [Fact]
    public void AConnectClassSqlState_IsTransientInfrastructure_EvenUnflagged()
        => InfrastructureFault.IsTransient(new FakeDbException("the database system is starting up", sqlState: "57P03"))
            .Should().BeTrue();

    /// <summary>
    /// 🚨 Disagreement: two test-side copies accepted the Autofac "nested lifetimes cannot be
    /// created" text on ANY exception type; the product's copy required an
    /// <see cref="ObjectDisposedException"/>. Autofac raises it as one, so the text on anything else
    /// is a coincidence, not a disposed scope.
    /// </summary>
    [Fact]
    public void TheAutofacText_OnANonDisposedException_IsNotADisposedContainer()
    {
        FaultClassifier.IsDisposedContainer(new InvalidOperationException("nested lifetimes cannot be created"))
            .Should().BeFalse();
        FaultClassifier.IsDisposedContainer(new ObjectDisposedException("x", "nested lifetimes cannot be created"))
            .Should().BeTrue();
    }
}
