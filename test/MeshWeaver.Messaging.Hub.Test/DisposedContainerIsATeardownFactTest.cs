using System;
using System.Reflection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A closed DI scope is a teardown fact, and every layer must classify it the same way.</b>
///
/// <para>A hub whose Autofac <c>LifetimeScope</c> has been closed cannot serve anything, so a
/// delivery that faults on it must be answered as RETRYABLE — the address reactivates — never as a
/// result. <see cref="HubDisposingException.IsHubDisposal"/> does not cover it: that is the case
/// where a hub ANNOUNCED its own disposal, and a scope closed underneath a live delivery announces
/// nothing.</para>
///
/// <para>Measured 2026-08-30 (flake-repro, 40 iterations on the 4-vCPU runner): the fault behind
/// <c>SilentReadNackTest.OwnerDisposedWithReadOutstanding</c>'s bulk-only failure is exactly this
/// shape, reflection-wrapped —
/// <c>TargetInvocationException → ObjectDisposedException("… LifetimeScope …")</c>. Because it was
/// not recognised, the read path fabricated a <c>GetDataResponse{Error}</c>, claimed the once-only
/// answer slot, and reported "this node does not exist" for a node that exists (#1362/#1470's
/// shape, reached by a different cause; #2727).</para>
/// </summary>
public class DisposedContainerIsATeardownFactTest
{
    private static ObjectDisposedException DisposedScope() =>
        new("LifetimeScope",
            "Instances cannot be resolved and nested lifetimes cannot be created from this "
            + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");

    [Fact]
    public void TheReflectionWrappedDisposedScope_IsRecognised()
    {
        // The exact shape measured in CI: the reflective Reduce hands it over wrapped.
        var wrapped = new TargetInvocationException(DisposedScope());

        HubDisposingException.IsDisposedContainer(wrapped).Should().BeTrue(
            "this is the fault that reaches the read path; unrecognised, it is answered as DATA "
            + "and the caller is told a node that exists does not");
        HubDisposingException.IsHubDisposal(wrapped).Should().BeFalse(
            "a scope closed underneath a live delivery announces nothing — which is exactly why "
            + "IsHubDisposal alone left this case unclassified");
    }

    [Fact]
    public void AnUnrelatedFault_IsNotATeardown()
    {
        HubDisposingException.IsDisposedContainer(new ObjectDisposedException("SomeStream"))
            .Should().BeFalse("an ObjectDisposedException a handler caused on a LIVE hub is a real "
                + "fault; classifying it as teardown would hide it behind an endless retry");
        HubDisposingException.IsDisposedContainer(new InvalidOperationException("boom"))
            .Should().BeFalse("an ordinary fault must keep its own answer");
        HubDisposingException.IsDisposedContainer(null)
            .Should().BeFalse("no exception is not a teardown");
    }

    /// <summary>A cyclic chain must terminate — the predicate walks caller-supplied data.</summary>
    [Fact]
    public void ACyclicChain_TerminatesRatherThanHanging()
    {
        var a = new InvalidOperationException("a");
        var wrapped = new TargetInvocationException(a);
        HubDisposingException.IsDisposedContainer(wrapped).Should().BeFalse();
    }
}
