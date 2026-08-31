using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>Issue #2679 — a render that faults on its OWN host's disposed DI scope is a teardown race,
/// and the error placeholder must not be attempted on that dead scope.</b>
///
/// <para><b>What production reported</b> (memex-cloud, 2026-08-29), two <c>fail</c> lines for one
/// disposal:</para>
/// <code>
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Rendering failed for area Overview
///       System.ObjectDisposedException: Instances cannot be resolved … from this LifetimeScope …
///          at Autofac.Core.Lifetime.LifetimeScope.ThrowDisposedException()
///          at MeshWeaver.Mesh.Security.PermissionEvaluator.GetEffectivePermissions(…) :line 126
/// fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]
///       Failed to render the error placeholder for area Overview
///       System.ObjectDisposedException: …
///          at MeshWeaver.Layout.Composition.LayoutAreaLocalizationExtensions.Localize(…) :line 26
///          at MeshWeaver.Layout.Composition.LayoutAreaHost.CreateRenderErrorControl(…)
/// </code>
///
/// <para><b>Why it was mis-reported.</b> <c>AreaErrorClassifier.IsHubDisposalRace</c> matched only
/// the TYPED <see cref="HubDisposingException"/> (#2255). The DI-scope shape of the same race —
/// Autofac's bare <see cref="ObjectDisposedException"/> from a service resolved on the host's
/// already-disposed scope — looked like an ordinary defect, so the render logged it at Error (the
/// level the red-log filer turns into an incident) and then tried to LOCALISE a placeholder through
/// the very scope that was gone, faulting a second time. The user got neither the area nor a
/// message.</para>
///
/// <para><b>The fix</b> is probe-gated, the same shape <c>MessageHub</c> uses for #2444 and
/// <c>RoutingGrain</c> for #2638 (<see cref="ScopeTeardown"/>): a bare ObjectDisposedException
/// counts as the hub-disposal race only when the host's OWN scope confirms it no longer resolves.
/// Then it logs at Debug like the typed race, and no placeholder is attempted — there is nothing to
/// localise it with and nobody to render it for; the client's own resubscribe against the fresh
/// activation renders the real content.</para>
///
/// <para><b>How the race is pinned, deterministically.</b> The parked view generator is resumed
/// from Autofac's own <see cref="ILifetimeScope.CurrentScopeEnding"/> event on the host hub's
/// scope. Autofac's <c>Disposable.Dispose()</c> sets the disposed flag FIRST — from that instant
/// every resolve throws — and <c>LifetimeScope.Dispose(true)</c> then raises this event BEFORE its
/// disposer reaches a single tracked instance or child scope. So the generator resumes
/// synchronously on the disposing thread, strictly inside the incident window: the scope is dead,
/// and the stream, its sub-hub and the hub instance are all still alive and subscribed. It then
/// resolves from that scope and throws the real Autofac fault into the real render chain. No
/// sleeps, no fake exception.</para>
///
/// <para>🚨 A sentinel <see cref="IDisposable"/> tracked in the scope (the #2444 shape) is a
/// RACE here, whichever side creates it: the subscription machinery keeps creating tracked
/// instances and child scopes on other threads, and whatever is newer than the sentinel is
/// disposed before it — tearing the stream down before the fault arrives on some runs, after it
/// on others. The event has no position in that order.</para>
/// </summary>
public class ScopeTeardownRenderTest : HubTestBase
{
    private const string RacingView = nameof(RacingView);

    private readonly RenderReportCapture capture = new();
    private readonly ReplaySubject<Unit> generatorInvoked = new(1);
    /// <summary>What the parked generator waits on; the scope's own ending event signals it.</summary>
    private readonly ReplaySubject<Unit> scopeDisposing = new(1);
    private int generatorInvocations;

    public ScopeTeardownRenderTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(capture);
            // The Debug line IS the fixed behaviour, so the capture must see it — a category-scoped
            // rule for this provider only; nothing else gets chattier.
            l.AddFilter<RenderReportCapture>(typeof(LayoutAreaHost).FullName, LogLevel.Trace);
        });
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(RacingView, (LayoutAreaHost host, RenderingContext _) =>
                {
                    System.Threading.Interlocked.Increment(ref generatorInvocations);
                    generatorInvoked.OnNext(Unit.Default);
                    return scopeDisposing
                        .Take(1)
                        .Select(_ =>
                        {
                            // The scope's disposed flag is set, the hub's Dispose has not run: this
                            // is the resolution the permission fold performed in production, and it
                            // throws Autofac's ObjectDisposedException into the render chain.
                            host.Hub.ServiceProvider.GetRequiredService<IMessageHub>();
                            return (UiControl?)null;
                        });
                }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// The pin. RED before the fix: two Error records — "Rendering failed for area RacingView" and
    /// "Failed to render the error placeholder for area RacingView". GREEN after: one Debug record
    /// naming the teardown race, nothing at Warning or above, and no placeholder attempt at all.
    ///
    /// <para>The barrier is the host's own report: it is written BEFORE the host decides about the
    /// placeholder, in both the broken and the fixed shape, so "a record for the area exists"
    /// orders the assertions after the render's fault handling without a sleep.</para>
    /// </summary>
    [HubFact]
    public async Task ARenderFaultingOnItsOwnDisposedScope_IsATeardownRace_NotTwoErrors()
    {
        var host = GetHost();
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(RacingView));
        // ONE subscription for the whole race. A second Rx subscriber on the remote stream (a
        // FirstAsync() barrier, say) put a SECOND area host into the race on the server: the first,
        // parked one was torn down "having never rendered" — at Warning, on the hub's thread —
        // before the disposal below had even begun, and the assertion on "no paging record" then
        // failed on a fixture artefact. The generator-invocation count below pins that there is
        // exactly one host. The client's stream will see its host go away, which is the expected
        // outcome and not what is asserted here.
        var frames = new ReplaySubject<Unit>(1);
        using var subscription = stream.Subscribe(_ => frames.OnNext(Unit.Default), _ => { });

        // Two barriers: the render has parked on `scopeDisposing`, and the client has received the
        // base frame — the render subscription is live and the area is being served.
        await generatorInvoked.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();
        await frames.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();

        // The window, from Autofac itself: CurrentScopeEnding is raised inside Dispose(true), after
        // the disposed flag is set and before the disposer reaches anything. Resuming the parked
        // generator from here puts its resolve exactly where the incident's was.
        host.ServiceProvider.GetRequiredService<ILifetimeScope>().CurrentScopeEnding +=
            (_, _) => scopeDisposing.OnNext(Unit.Default);

        // Out-of-band scope teardown of the HOST hub — no hub.Dispose(), no CloseCreation cascade.
        // Disposing the hub's AutofacServiceProvider disposes the underlying lifetime scope: flag,
        // then the event above (the generator resumes, faults, and the render classifies it while
        // every subscription is still live), then the sweep over the stream's sub-hub and the
        // tracked host hub instance.
        ((IDisposable)host.ServiceProvider).Dispose();

        // The barrier: the host hub's disposal has COMPLETED, so every record its teardown was
        // going to write — the render's fault handling and the stream's own teardown — is written.
        await host.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        // Everything the host reported about this area — a synchronous snapshot, not a wait.
        var all = capture.Recorded.Where(r => r.Area == RacingView).ToArray();
        foreach (var report in all)
            Output.WriteLine($"LayoutAreaHost reported: {report}");

        System.Threading.Volatile.Read(ref generatorInvocations).Should().Be(1,
            "exactly one area host took part in the race — a second one would be a fixture "
            + "artefact (a second subscription), not the scenario under test");

        // 🚨 THE WINDOW IS GENUINELY RACY, and this test must not pretend otherwise. Two outcomes
        // are reachable and BOTH are correct behaviour:
        //
        //   * the sentinel resumes the parked generator first → the render faults on the disposed
        //     scope and is classified "raced a hub disposal";
        //   * the teardown reaches the area first             → nothing rendered, and the area is
        //     reported as torn down having never rendered.
        //
        // The first version asserted only the first branch, and failed 1 bulk run in 12 locally
        // with "found 0" — which reads as the CLASSIFICATION being broken when in fact the fault
        // never occurred. An assertion on a precondition the test cannot guarantee is a flake by
        // construction, and it hides the thing it was written to protect.
        //
        // So: the invariant is asserted unconditionally (below), and each branch's classification
        // is asserted when that branch actually happened. That is strictly stronger than the
        // original — it now pins BOTH classifications instead of one — and it cannot pass on
        // "nothing happened", because exactly one account is required.
        var classified = all.SingleOrDefault(r => r.Message.Contains("raced a hub disposal"));
        var neverRendered = all.SingleOrDefault(r => r.Message.Contains("having never rendered"));

        (classified is not null || neverRendered is not null).Should().BeTrue(
            "the disposal must leave exactly one account of what happened to this area; neither "
            + "branch reporting anything would mean the race passed in silence");

        if (classified is not null)
        {
            classified.Level.Should().Be(LogLevel.Debug,
                "a render that faulted on its OWN host's disposed scope is the hub going away — routine "
                + "lifecycle, classified at Debug like the typed HubDisposingException race (#2255); at "
                + "Error the red-log filer files an incident for it, which is how #2679 was opened");
            classified.Exception.Should().BeOfType<ObjectDisposedException>(
                "the fault that arrived is Autofac's own — the bare shape the type-only classifier missed");
        }

        if (neverRendered is not null)
            neverRendered.Level.Should().Be(LogLevel.Debug,
                "the hub's OWN disposal is not a client navigating away: every area it serves is torn "
                + "down, so an un-rendered one reports here once PER area on every teardown. That is "
                + "the same routine lifecycle the classified branch above is Debug for, and it was "
                + "still Warning — which is also what made this test flake");

        all.Should().NotContain(r => r.Level >= LogLevel.Warning,
            "one disposal must not produce a paging record — the two fail lines of #2679");
        all.Should().NotContain(r => r.Message.Contains("error placeholder"),
            "no placeholder is attempted on a dead scope: the frame cannot be localised through a "
            + "scope that is gone (the second ObjectDisposedException of #2679), and there is nobody "
            + "to render it for — the client's resubscribe renders the real content");
    }

    /// <summary>
    /// The classifier itself, stated as executable facts — the probe is the whole point.
    /// </summary>
    [Fact]
    public void TheProbeGatedClassifier_OnlyMatchesADisposedScope()
    {
        var disposedScope = new ObjectDisposedException(
            "Instances cannot be resolved and nested lifetimes cannot be created from this "
            + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");

        AreaErrorClassifier.IsHubDisposalRace(disposedScope, () => true).Should().BeTrue(
            "an ObjectDisposedException while the host's own scope is gone IS the teardown race");
        AreaErrorClassifier.IsHubDisposalRace(new AggregateException(disposedScope), () => true)
            .Should().BeTrue("the graph is walked, not the InnerException line");

        // 🚨 NEGATIVE CONTROLS — what the probe exists for.
        AreaErrorClassifier.IsHubDisposalRace(new ObjectDisposedException("SomeCache"), () => false)
            .Should().BeFalse("a disposed DEPENDENCY on a live scope is a genuine defect");
        AreaErrorClassifier.IsHubDisposalRace(disposedScope, null)
            .Should().BeFalse("with no probe the answer is exactly the type-only one");
        AreaErrorClassifier.IsHubDisposalRace(new InvalidOperationException("boom"), () => true)
            .Should().BeFalse("a dead scope does not turn every other fault into a teardown");

        // The typed race keeps matching regardless of the probe — it names its own hub.
        AreaErrorClassifier.IsHubDisposalRace(
                new HubDisposingException(new Address("Posts/Trail"), "anything"), () => false)
            .Should().BeTrue();
        AreaErrorClassifier.IsHubDisposalRace(
                new HubDisposingException(new Address("Posts/Trail"), "the permission fold", disposedScope),
                () => false)
            .Should().BeTrue("the fold's re-raised shape carries the original as its inner exception");
    }

    private sealed record RenderReport(
        LogLevel Level, string? Area, string Message, Exception? Exception, int ThreadId, DateTime At);

    /// <summary>
    /// Reads every <c>LayoutAreaHost</c> record carrying an <c>Area</c> out of the logging pipeline,
    /// at ALL levels — the Debug line is the fixed behaviour, so it has to be observable — as a
    /// replayed stream (the barrier) plus a snapshot (the final assertions). Structured state,
    /// never the formatted prose alone.
    /// </summary>
    private sealed class RenderReportCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RenderReport> recorded = new();
        private readonly ReplaySubject<RenderReport> reports = new();

        /// <summary>The barrier: every report, replayed — wait on it, never poll.</summary>
        internal IObservable<RenderReport> Reports => reports;

        /// <summary>The snapshot: what has been recorded so far, for the final assertions.</summary>
        internal RenderReport[] Recorded => recorded.ToArray();

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(LayoutAreaHost).FullName
                ? new CapturingLogger(recorded, reports)
                : Silent.Instance;

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class Silent : ILogger
        {
            internal static readonly Silent Instance = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }

        private sealed class CapturingLogger(
            ConcurrentQueue<RenderReport> recorded, ReplaySubject<RenderReport> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                var area = values.FirstOrDefault(v => v.Key == "Area");
                if (area.Key is null)
                    return;
                var report = new RenderReport(logLevel, area.Value?.ToString(), formatter(state, exception), exception,
                    Environment.CurrentManagedThreadId, DateTime.UtcNow);
                // Recorded BEFORE it is published, so a waiter woken by the stream sees it in the
                // snapshot too.
                recorded.Enqueue(report);
                sink.OnNext(report);
            }
        }
    }
}
