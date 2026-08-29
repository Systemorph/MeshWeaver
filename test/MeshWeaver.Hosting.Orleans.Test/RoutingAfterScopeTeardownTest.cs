using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2638 — routing work that executes after the process's Autofac root scope is
/// disposed must fail as the LIFECYCLE TRANSITION it is, not as a terminal defect.</b>
///
/// <para><b>The mechanism.</b> Orleans builds a grain proxy by resolving its codec provider out of
/// the DI container (<c>OrleansGeneratedCodeHelper.GetService</c> →
/// <c>AutofacServiceProvider.GetService</c>). Once the silo host has disposed its root
/// <c>LifetimeScope</c>, every remaining delivery — and every NACK about one — faults with Autofac's
/// <see cref="ObjectDisposedException"/> before it reaches a transport at all. Prod (memex,
/// 2026-08-29) carried that exception TWICE in one <see cref="AggregateException"/>: the directed
/// pod-hub call and the stream publish, both dying on the same dead container, reported at Error for
/// a pod that was merely exiting.</para>
///
/// <para><b>Two defects, both pinned here.</b></para>
/// <list type="number">
/// <item>The delivery this NACK was about was classified <see cref="ErrorType.Failed"/> — TERMINAL.
/// Consumers with recovery machinery of their own (<c>SynchronizationStream</c>'s resubscribe latch,
/// <c>MeshNodeStreamCache</c>'s transient-owner rule) RIDE OUT <see cref="ErrorType.ShuttingDown"/>
/// and TEAR DOWN on a terminal verdict, so a routine pod exit permanently killed live mirrors that
/// would have resumed against the surviving pod seconds later. That is the identical damage
/// #2346/#2357 removed for the directory-unstable and silo-departing shapes and left standing for
/// this one.</item>
/// <item>The second transport was attempted even though it resolves through the SAME disposed
/// scope, so the failure could only ever be the first one restated.</item>
/// </list>
///
/// <para><b>The probe is the point, and the negative control below is what proves it.</b> An
/// <see cref="ObjectDisposedException"/> from an unrelated disposed dependency is a genuine defect
/// and must stay terminal; only a probe finding the CONTAINER itself gone turns the type test into a
/// statement about teardown. Same shape as <c>MessageHub.IsTerminatedByScopeTeardown</c> (#2444).</para>
///
/// <para>Pure — the classifier and the retry primitive are <c>internal static</c>, the exception
/// text is quoted verbatim from the production log, and the container probe is a delegate. No
/// cluster, no host, no clock.</para>
/// </summary>
public class RoutingAfterScopeTeardownTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    /// <summary>
    /// Verbatim from Autofac's <c>LifetimeScope.ThrowDisposedException()</c>, as it reached
    /// <c>RoutingGrain.PostFailure</c> in the #2638 incident.
    /// </summary>
    private const string ScopeDisposedText =
        "Instances cannot be resolved and nested lifetimes cannot be created from this LifetimeScope "
        + "as it (or one of its parent scopes) has already been disposed.";

    private static Exception ScopeDisposed() => new ObjectDisposedException(ScopeDisposedText, (Exception?)null);

    /// <summary>The prod NACK shape: the same fault carried twice, once per transport.</summary>
    private static Exception BothTransportsOnTheDeadContainer() =>
        new AggregateException(
            "Neither the directed pod-hub call nor the stream publish could carry the NACK.",
            ScopeDisposed(), ScopeDisposed());

    /// <summary>
    /// 🚨 THE REGRESSION. Pre-fix this answers <see cref="ErrorType.Failed"/> — terminal — for a
    /// process that is simply exiting.
    /// </summary>
    [Fact]
    public void DisposedContainer_ClassifiesAsShuttingDown()
    {
        RoutingGrain.ClassifyDeliveryException(ScopeDisposed(), scopeDisposed: () => true)
            .Should().Be(ErrorType.ShuttingDown,
                "the container is disposed exactly once, at the end of host shutdown, and the target "
                + "hub comes back on the surviving pod — a terminal verdict tears down every consumer "
                + "that would have resumed (#2638, same damage as #2346/#2357)");
    }

    /// <summary>
    /// The graph, not the chain: the NACK leg hands the classifier an
    /// <see cref="AggregateException"/> whose ordering nobody controls.
    /// </summary>
    [Fact]
    public void DisposedContainer_IsSeenThroughAnAggregate()
    {
        RoutingGrain.ClassifyDeliveryException(BothTransportsOnTheDeadContainer(), scopeDisposed: () => true)
            .Should().Be(ErrorType.ShuttingDown,
                "classification must not depend on which fault happened to land at index 0");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — and the reason the probe exists at all. A disposed dependency while the
    /// container is ALIVE is a real defect and must stay terminal; widening the rule to the bare type
    /// test would silently reclassify those as "ride it out and resubscribe", which is the
    /// resubscribe-storm shape <c>ClassifyDeliveryException</c> is explicitly narrower than
    /// <c>IsTransientFailure</c> to avoid.
    /// </summary>
    [Fact]
    public void DisposedDependency_WhileTheContainerLives_StaysTerminal()
    {
        RoutingGrain.ClassifyDeliveryException(
                new ObjectDisposedException("SomeCache"), scopeDisposed: () => false)
            .Should().Be(ErrorType.Failed,
                "only the CONTAINER being gone is a lifecycle transition — an unrelated disposed "
                + "object is a defect and must be reported as one");

        RoutingGrain.ClassifyDeliveryException(new ObjectDisposedException("SomeCache"))
            .Should().Be(ErrorType.Failed,
                "with no probe supplied the answer must be exactly the pre-#2638 one");
    }

    /// <summary>
    /// Nothing else moved: the two shapes #2346/#2357 already classified must keep their verdict,
    /// and a genuine defect must keep its terminal one.
    /// </summary>
    [Fact]
    public void TheOtherClassificationsAreUnchanged()
    {
        RoutingGrain.ClassifyDeliveryException(
                new global::Orleans.Runtime.SiloUnavailableException("the silo went away"), () => false)
            .Should().Be(ErrorType.ShuttingDown, "#2357's silo-departing shape is untouched");

        RoutingGrain.ClassifyDeliveryException(new InvalidOperationException("a real defect"), () => true)
            .Should().Be(ErrorType.Failed,
                "the probe must not turn EVERY failure during shutdown into a ride-it-out — only the "
                + "ones that actually carry an ObjectDisposedException");
    }

    /// <summary>
    /// End to end through the delivery leg: a grain call whose PROXY cannot be built because the
    /// container is gone must NACK the sender as <see cref="ErrorType.ShuttingDown"/>, and must not
    /// burn its transient-retry budget on a container that cannot come back.
    /// </summary>
    [Fact]
    public async Task DeliveryOnADeadContainer_NacksTheSenderAsShuttingDown_WithoutRetrying()
    {
        var nacks = new List<(string Message, ErrorType Type)>();
        var attempts = 0;

        RoutingGrain.DeliverToGrainWithRetry(
            grainCall: () =>
            {
                attempts++;
                // Synchronous throw, exactly as GrainFactory.GetGrain<T> does when the codec
                // provider cannot be resolved from the disposed root scope.
                throw ScopeDisposed();
            },
            grainKey: "messagehub/Planning",
            addressPath: "Planning",
            deliveryId: "d-2638",
            postFailureToSender: (m, t) => nacks.Add((m, t)),
            logger: NullLogger.Instance,
            backoff: NoBackoff,
            scheduler: Scheduler.Immediate,
            scopeDisposed: () => true);

        await Task.Yield();

        attempts.Should().Be(1,
            "a disposed container is not transient — retrying inside a process whose DI is gone is "
            + "six guaranteed failures and six log lines for the same outcome");
        var nack = nacks.Should().ContainSingle().Subject;
        nack.Type.Should().Be(ErrorType.ShuttingDown,
            "prod NACK'd exactly this as terminal ('Delivery to Planning failed: Instances cannot be "
            + "resolved …'), tearing down the sender's recovery machinery (#2638)");
        nack.Message.Should().Contain("Planning");
    }
}
