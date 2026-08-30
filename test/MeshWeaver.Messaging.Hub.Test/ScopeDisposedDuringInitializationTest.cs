using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the recognized-shutdown outcome for a hub whose initialization is terminated by an
/// OUT-OF-BAND teardown of its DI scope (issue #2444) — the case
/// <see cref="DisposeDuringInitializationTest"/> does NOT cover, because there the ancestor's
/// <c>Dispose()</c> cascades <c>CloseCreation</c> first, so <see cref="IMessageHub.IsShuttingDown"/>
/// is already true when the BuildupAction faults.
///
/// <para><b>The production sequence.</b> The host's root Autofac container (or an ancestor scope)
/// is disposed directly — a <c>Host.StartAsync</c> abort, pod shutdown — without any HUB
/// <c>Dispose()</c> having run. Autofac's <c>Disposable.Dispose()</c> flips the scope's disposed
/// flag FIRST and only then runs the disposer over the tracked instances, so from that first
/// instant every resolve throws <see cref="ObjectDisposedException"/> while the hub instance —
/// itself a tracked component of the same scope, disposed later in the same sweep — still has
/// <c>IsDisposing == false</c> and an unfrozen subtree. A BuildupAction resolving in that window
/// (prod: <c>MeshDataSourceExtensions.SubscribeToOwnDeletion</c> resolving the workspace on a
/// <c>mesh/…</c> data-source hub, 2026-08-23/26) faulted the init, and <c>HandleInitialize</c>'s
/// <c>.Catch</c> — seeing <c>IsShuttingDown == false</c> — logged a fail-level "initialization
/// failed … Hub is now in FAILED state." for a hub whose own disposal was already queued in the
/// very disposer that killed the scope: routine teardown reported as an error, with FAILED
/// residue.</para>
///
/// <para><b>The fix.</b> The catch now recognizes the window positively: when the fault chain
/// carries an <see cref="ObjectDisposedException"/> AND the hub's own scope no longer resolves
/// (probed against the hub's already-materialized <see cref="IMessageHub"/> registration), the
/// init terminates as a recognized shutdown — Debug log, no
/// <see cref="MessageHub.InitializationError"/>, gate opened so the imminent disposal traffic
/// flows.</para>
///
/// <para><b>RED before the fix:</b> <c>InitializationError</c> records the
/// <see cref="ObjectDisposedException"/> (FAILED state). <b>GREEN after:</b> it stays null.
/// <see cref="InitializationErrorSurfacedTest"/> still pins the other side — a genuine fault on a
/// hub whose scope is alive (including an <see cref="ObjectDisposedException"/> from an unrelated
/// disposed dependency) must still enter the FAILED state.</para>
/// </summary>
public class ScopeDisposedDuringInitializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Registered in the hub's OWN scope and resolved by the BuildupAction — i.e. created AFTER
    /// the hub instance, so Autofac's reverse-creation-order disposer disposes it BEFORE the hub.
    /// Its <see cref="Dispose"/> therefore runs exactly inside the incident window: the scope's
    /// disposed flag is already set (every resolve throws), while the hub's <c>Dispose()</c> —
    /// later in the same sweep — has not run (<c>IsShuttingDown</c> still false). The
    /// <see cref="ReplaySubject{T}"/> emission is delivered synchronously on the disposing thread,
    /// so the parked BuildupAction resumes and resolves strictly inside that window — the race is
    /// pinned deterministically, no sleeps.
    /// </summary>
    private sealed class ScopeTeardownSentinel : IDisposable
    {
        public ReplaySubject<Unit> ScopeDisposing { get; } = new(1);

        public void Dispose() => ScopeDisposing.OnNext(Unit.Default);
    }

    [Fact(Timeout = 30000)]
    public async Task InitTerminatedByOutOfBandScopeDisposal_IsRecognizedShutdown_NoFailedState()
    {
        var host = GetHost();
        var buildupParked = new ReplaySubject<Unit>(1);

        var hub = host.GetHostedHub(
            new Address("scope-teardown", Guid.NewGuid().ToString("N")),
            c => c
                // Plumbing fixture, no logged-in user — post as infrastructure, like the
                // fixture's own hubs (see HubTestBase.ConfigureMesh).
                .WithPostingIdentity(PostingIdentity.System)
                .WithServices(s => s.AddSingleton<ScopeTeardownSentinel>())
                .WithInitialization(h =>
                {
                    // Created here → tracked AFTER the hub instance → disposed BEFORE it.
                    var sentinel = h.ServiceProvider.GetRequiredService<ScopeTeardownSentinel>();
                    buildupParked.OnNext(Unit.Default);
                    return sentinel.ScopeDisposing
                        .Take(1)
                        .Select(_ =>
                        {
                            // The scope's disposed flag is set, the hub's Dispose has not run:
                            // this is the resolve the incident's BuildupAction performed, and it
                            // throws ObjectDisposedException into HandleInitialize's .Catch with
                            // IsShuttingDown == false.
                            h.ServiceProvider.GetRequiredService<IMessageHub>();
                            return Unit.Default;
                        });
                }));

        hub.Should().NotBeNull();
        await buildupParked.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();

        // Out-of-band scope teardown — no hub Dispose(), no CloseCreation cascade: exactly the
        // host-root-container disposal of a Host.StartAsync abort / pod shutdown. Disposing the
        // hub's AutofacServiceProvider disposes the underlying lifetime scope, which flips the
        // disposed flag, disposes the sentinel (waking the parked BuildupAction inside the
        // window), and only then disposes the tracked hub instance.
        ((IDisposable)hub!.ServiceProvider).Dispose();

        await hub.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        ((MessageHub)hub).InitializationError.Should().BeNull(
            "an init BuildupAction faulting on a DISPOSED scope is teardown racing initialization "
            + "— the hub's own disposal is already queued in the same disposer, so it must "
            + "terminate as a recognized shutdown outcome (no FAILED-state residue, no fail-level "
            + "log), not as 'initialization failed'");
    }
}
