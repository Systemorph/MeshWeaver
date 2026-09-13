using System;
using System.Data.Common;
using System.Net.Sockets;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The corpus for <see cref="InfrastructureFault.IsTransient"/> — the policy gate that decides
/// whether an initialization fault RETIRES an activation (#4067/#4068) or LATCHES it FAILED. Each
/// row is a shape the two seams actually meet, or one they must NOT mistake for it; a classifier
/// change that moved any row would move which activations are retired, silently, so every row is
/// pinned here rather than only through the two end-to-end cases.
/// </summary>
public class InfrastructureFaultTest
{
    private sealed class ProviderException(bool transient, Exception? inner = null)
        : DbException("Failed to connect to 10.42.18.4:5432", inner)
    {
        public override bool IsTransient => transient;
    }

    public static TheoryData<string, Exception?, bool> Corpus() => new()
    {
        { "null", null, false },
        { "a provider fault the provider marks transient (Npgsql: connection failure)", new ProviderException(transient: true), true },
        { "a provider fault the provider marks NOT transient (a constraint violation)", new ProviderException(transient: false), false },
        { "the #4067 shape: transient provider fault wrapping a connection TimeoutException", new ProviderException(transient: true, new TimeoutException("Timeout during connection attempt")), true },
        { "the #4068 shape: transient provider fault wrapping a name-resolution SocketException", new ProviderException(transient: true, new SocketException((int)SocketError.HostNotFound)), true },
        { "a bare SocketException", new SocketException((int)SocketError.HostNotFound), true },
        { "a bare TimeoutException — the init time-box's own shape, a hang, not a dependency fault", new TimeoutException("a BuildupAction did not complete within 120s"), false },
        { "a genuine fault", new InvalidOperationException("the handler itself is broken"), false },
        { "an 'initialization failed' wrapper around a transient cause", new InvalidOperationException("Hub 'x' initialization failed", new ProviderException(transient: true)), true },
        { "a reflective wrapper around a transient cause", new System.Reflection.TargetInvocationException(new SocketException((int)SocketError.TimedOut)), true },
        { "a genuine fault that merely mentions a database in its text", new InvalidOperationException("Failed to connect to the database (misconfigured)"), false },
        { "an aggregate whose branches are ALL transient (two data sources, one outage)", new AggregateException(new ProviderException(transient: true), new SocketException((int)SocketError.HostNotFound)), true },
        { "a MIXED aggregate — one transient branch, one genuine defect: the defect must not be discarded", new AggregateException(new ProviderException(transient: true), new InvalidOperationException("source B is misconfigured")), false },
        { "a nested aggregate, all leaves transient", new AggregateException(new AggregateException(new ProviderException(transient: true)), new ProviderException(transient: true)), true },
        { "a nested aggregate with one genuine leaf", new AggregateException(new AggregateException(new ProviderException(transient: true), new InvalidOperationException("boom")), new ProviderException(transient: true)), false },
        { "an empty aggregate", new AggregateException(), false },
        { "a transient cause UNDER an aggregate branch's wrapper", new AggregateException(new InvalidOperationException("wrapped", new ProviderException(transient: true))), true },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classifies(string shape, Exception? exception, bool expected)
    {
        InfrastructureFault.IsTransient(exception).Should().Be(expected, shape);
    }

    [Fact]
    public void ACyclicChain_Terminates_AndIsNotTransientByAccident()
    {
        // An exception whose inner chain loops back on itself — expressible only by reaching into
        // the runtime's private field, which is what makes the shape worth pinning: the walk must
        // END, and a cycle that never reaches a transient leaf must not read as one.
        var outer = new InvalidOperationException("outer");
        var looped = new InvalidOperationException("loop", outer);
        var innerField = typeof(Exception).GetField("_innerException",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        innerField.Should().NotBeNull("the runtime's Exception layout is what this cycle is built on");
        innerField!.SetValue(outer, looped);
        outer.InnerException.Should().BeSameAs(looped, "precondition: the chain is a cycle");

        InfrastructureFault.IsTransient(looped).Should().BeFalse();
    }
}
