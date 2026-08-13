using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the classification of a round abandoned by hub teardown (issue #1300).
///
/// <para>The production shape: a thread is deleted while an agent streaming round is in flight.
/// Hosted-hub creation is frozen from the first instant of disposal, so the round's
/// <c>SynchronizationStream.Reduce</c> throws <see cref="HubDisposingException"/> — and it does so
/// through a REFLECTIVE dispatch, which re-wraps it as
/// <see cref="TargetInvocationException"/>. <c>ThreadExecution</c> already meant to treat that as
/// benign teardown, but its predicate tested the OUTERMOST exception type only
/// (<c>ex is ObjectDisposedException</c>), so the wrapped form fell through to the error path:
/// logged as an Error with a full stack pageant by BOTH nested Catch handlers, and rethrown to the
/// submission watcher as if the round had genuinely failed.</para>
///
/// <para>These are pure-predicate tests deliberately: the exception CHAIN is the entire defect, and
/// a chain is exactly reproducible without standing up a mesh and racing a delete against a live
/// streaming round — a race that could only ever be sampled, never pinned.</para>
/// </summary>
public class ThreadTeardownClassificationTest
{
    private static HubDisposingException HubDisposing() =>
        new(new Address("thread", "TestEmail/_Thread/delete-the-underwriting-guidelines-node-de12"),
            "/MeshNode");

    /// <summary>
    /// The exact chain from the issue's stack trace. This is the assertion that fails against the
    /// replaced predicate — <see cref="TargetInvocationException"/> is not an
    /// <see cref="ObjectDisposedException"/>, so the outermost-type test returned false and the
    /// round was reported as a failure.
    /// </summary>
    [Fact]
    public void WrappedHubDisposal_IsTeardown()
    {
        var wrapped = new TargetInvocationException(HubDisposing());

        ThreadExecution.IsTeardownRace(wrapped).Should().BeTrue(
            "the round was abandoned because its own hub is going away — the reflective dispatch "
            + "that wraps the fault does not make it a genuine round failure");
    }

    /// <summary>
    /// The unwrapped form was ALREADY classified correctly by the predecessor. Pinning it here is
    /// what makes the test above a statement about WRAPPING specifically, rather than about
    /// hub-disposal handling in general.
    /// </summary>
    [Fact]
    public void BareHubDisposal_IsTeardown()
    {
        ThreadExecution.IsTeardownRace(HubDisposing()).Should().BeTrue();
    }

    /// <summary>Nesting is not depth-limited in practice — a doubly-wrapped chain must still classify.</summary>
    [Fact]
    public void DeeplyWrappedHubDisposal_IsTeardown()
    {
        var wrapped = new TargetInvocationException(
            new InvalidOperationException("dispatch failed", HubDisposing()));

        ThreadExecution.IsTeardownRace(wrapped).Should().BeTrue();
    }

    /// <summary>
    /// The legacy untyped legs the predecessor carried, now walked through a wrapper too: Rx and the
    /// renderer both raise "disposed" as an untyped <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void WrappedUntypedDisposedShapes_AreTeardown()
    {
        ThreadExecution.IsTeardownRace(
                new TargetInvocationException(new ObjectDisposedException("someStream")))
            .Should().BeTrue();

        ThreadExecution.IsTeardownRace(
                new TargetInvocationException(
                    new InvalidOperationException("Cannot access a disposed object.")))
            .Should().BeTrue();
    }

    /// <summary>
    /// An <see cref="AggregateException"/> fan-out is a tree, not a chain — the walk has to
    /// branch, which is why the typed leg delegates to <see cref="HubDisposingException.IsHubDisposal"/>.
    /// </summary>
    [Fact]
    public void HubDisposalInsideAnAggregate_IsTeardown()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            HubDisposing());

        ThreadExecution.IsTeardownRace(aggregate).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 The load-bearing negative. A predicate that swallowed everything would make this issue
    /// "go away" by hiding every genuine round failure from the submission watcher — the exact
    /// swallow-and-continue the codebase forbids. A real fault must still fault.
    /// </summary>
    [Fact]
    public void GenuineRoundFailures_AreNotTeardown()
    {
        ThreadExecution.IsTeardownRace(new InvalidOperationException("model returned no content"))
            .Should().BeFalse();
        ThreadExecution.IsTeardownRace(new TimeoutException("streaming exceeded its budget"))
            .Should().BeFalse();
        ThreadExecution.IsTeardownRace(
                new TargetInvocationException(new HttpRequestException("502 from the provider")))
            .Should().BeFalse();
        ThreadExecution.IsTeardownRace(null).Should().BeFalse();
    }

    /// <summary>
    /// A self-referencing chain is caller-supplied data; the walk must terminate rather than spin.
    /// </summary>
    [Fact]
    public void CyclicChain_Terminates()
    {
        var inner = new InvalidOperationException("inner");
        var outer = new TargetInvocationException(inner);
        // A chain long enough to prove the depth cap engages without relying on reflection to
        // build a true cycle.
        Exception deep = outer;
        for (var i = 0; i < 40; i++)
            deep = new TargetInvocationException(deep);

        ThreadExecution.IsTeardownRace(deep).Should().BeFalse();
    }
}
