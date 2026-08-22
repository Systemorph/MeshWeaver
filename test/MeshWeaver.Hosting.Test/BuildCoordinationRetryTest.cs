using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The pre-warm sweep's claim handshake, and what happens when the coordination node
/// (<c>Admin/Build</c>) cannot be reached (#1635).
///
/// <para>The defect: the handshake is a <c>SubscribeRequest</c> to a grain that may not have
/// activated yet at pod startup. The request died on the hub's 60 s budget, the
/// <see cref="TimeoutException"/> faulted the whole warm-up stream, and the pod REFUSED READINESS —
/// stalling a rollout on a race that clears itself in seconds.</para>
///
/// <para>🚨 The property these tests exist for is NOT the retry. It is that exhausting the attempts
/// still ERRORS. A readiness gate whose evidence is unreachable has to fail closed; the retry only
/// buys the grain time to activate. <see cref="ExhaustingTheAttempts_StillErrors_NeverSucceedsQuietly"/>
/// is the fail-closed proof, and it fails against an implementation that swallows.</para>
/// </summary>
public class BuildCoordinationRetryTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    private static Func<IObservable<string>> Handshake(int failures, Func<Exception> error, List<int> attempts)
    {
        var seen = 0;
        return () => Observable.Defer(() =>
        {
            var n = ++seen;
            attempts.Add(n);
            return n <= failures
                ? Observable.Throw<string>(error())
                : Observable.Return("granted");
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task ATransientUnreachableNode_IsRetried_AndTheSweepProceeds()
    {
        var attempts = new List<int>();
        var result = await BuildProtocolDriver.RetryUnreachableCoordination(
                Handshake(2, () => new TimeoutException(
                    "No response received in hub cache/x within 00:01:00 for request "
                    + "SubscribeRequest → target Admin/Build."), attempts),
                BuildProtocolDriver.CoordinationAttempts,
                NoBackoff,
                Scheduler.Immediate,
                logger: null)
            .FirstAsync()
            .ToTask();

        result.Should().Be("granted");
        attempts.Should().Equal(new[] { 1, 2, 3 });
        attempts.Should().HaveCount(3, "two startup-race timeouts must not end the sweep");
    }

    /// <summary>
    /// 🚨 FAIL CLOSED. Every attempt fails ⇒ the handshake still errors, so the warm-up stream still
    /// faults and readiness is still refused. A gate that cannot reach its evidence must never look
    /// like a gate that passed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ExhaustingTheAttempts_StillErrors_NeverSucceedsQuietly()
    {
        var attempts = new List<int>();
        var act = () => BuildProtocolDriver.RetryUnreachableCoordination(
                Handshake(int.MaxValue, () => new TimeoutException("→ target Admin/Build"), attempts),
                BuildProtocolDriver.CoordinationAttempts,
                NoBackoff,
                Scheduler.Immediate,
                logger: null)
            .FirstAsync()
            .ToTask();

        var thrown = await act.Should().ThrowAsync<BuildCoordinationUnreachableException>();
        // The refusal NAMES what it could not reach, and says it is a refusal — the bare
        // TimeoutException it replaces read as a compile problem for as long as anyone looked.
        thrown.Which.Message.Should().Contain("Admin/Build");
        thrown.Which.Message.Should().Contain("verified NOTHING");
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
        attempts.Should().HaveCount(BuildProtocolDriver.CoordinationAttempts);
    }

    /// <summary>
    /// A single attempt is still one attempt — a mis-configured/edge count must not turn into
    /// "never run the handshake at all", which would be an unreachable node reported as reached.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AZeroAttemptBudget_StillRunsOnce_AndStillErrors()
    {
        var attempts = new List<int>();
        var act = () => BuildProtocolDriver.RetryUnreachableCoordination(
                Handshake(int.MaxValue, () => new TimeoutException("→ target Admin/Build"), attempts),
                attempts: 0,
                NoBackoff,
                Scheduler.Immediate,
                logger: null)
            .FirstAsync()
            .ToTask();

        await act.Should().ThrowAsync<BuildCoordinationUnreachableException>();
        attempts.Should().ContainSingle();
    }

    /// <summary>
    /// Only UNREACHABLE is retried. A real defect surfaces on the first attempt, unwrapped —
    /// retrying it would delay the same verdict by minutes and bury the cause under three identical
    /// stack traces.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnErrorThatIsNotATimeout_IsNotRetried_AndIsNotRelabelled()
    {
        var attempts = new List<int>();
        var act = () => BuildProtocolDriver.RetryUnreachableCoordination(
                Handshake(int.MaxValue, () => new InvalidOperationException("malformed BuildState"), attempts),
                BuildProtocolDriver.CoordinationAttempts,
                NoBackoff,
                Scheduler.Immediate,
                logger: null)
            .FirstAsync()
            .ToTask();

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("malformed BuildState");
        attempts.Should().ContainSingle();
    }

    /// <summary>
    /// A hub timeout can arrive WRAPPED — merged inner streams surface as an
    /// <see cref="AggregateException"/>, and a matcher that only tests the outermost type would
    /// treat a startup race as a hard verdict and stall the rollout on it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AWrappedTimeout_CountsAsUnreachable()
    {
        var attempts = new List<int>();
        var result = await BuildProtocolDriver.RetryUnreachableCoordination(
                Handshake(1, () => new AggregateException(
                    new InvalidOperationException("outer"),
                    new TimeoutException("→ target Admin/Build")), attempts),
                BuildProtocolDriver.CoordinationAttempts,
                NoBackoff,
                Scheduler.Immediate,
                logger: null)
            .FirstAsync()
            .ToTask();

        result.Should().Be("granted");
        attempts.Should().Equal(new[] { 1, 2 });
    }

    /// <summary>
    /// The readiness refusal must be able to tell "no verdict" from "a bad verdict". Both refuse,
    /// but only the first is worth restarting on, and only the first is a rollout stalled on a race.
    /// </summary>
    [Fact]
    public void TheRefusalDistinguishesNoVerdictFromABadOne()
    {
        BuildProtocolDriver.DescribesUnreachableCoordination(
            new BuildCoordinationUnreachableException("x", new TimeoutException())).Should().BeTrue();
        BuildProtocolDriver.DescribesUnreachableCoordination(
            new AggregateException(new BuildCoordinationUnreachableException("x", new TimeoutException())))
            .Should().BeTrue();
        // A plain timeout raised INSIDE the sweep is a sweep fault, not an unreachable node: the
        // handshake already succeeded, so a per-type activation budget expiring says something
        // about this image and must keep its own message.
        BuildProtocolDriver.DescribesUnreachableCoordination(new TimeoutException()).Should().BeFalse();
        BuildProtocolDriver.DescribesUnreachableCoordination(null).Should().BeFalse();
    }

    /// <summary>
    /// The backoff is bounded and monotone-ish — small enough that three attempts still finish
    /// inside a startup probe budget, non-zero so a hot loop cannot re-time-out instantly.
    /// </summary>
    [Fact]
    public void TheBackoffIsBoundedAndNonZero()
    {
        BuildProtocolDriver.CoordinationBackoff(1).Should().BeGreaterThan(TimeSpan.Zero);
        BuildProtocolDriver.CoordinationBackoff(2).Should().BeGreaterThan(TimeSpan.Zero);
        BuildProtocolDriver.CoordinationBackoff(2)
            .Should().BeGreaterThanOrEqualTo(BuildProtocolDriver.CoordinationBackoff(1));
        BuildProtocolDriver.CoordinationBackoff(9).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }
}
