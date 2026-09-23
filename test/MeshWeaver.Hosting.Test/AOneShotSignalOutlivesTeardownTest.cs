using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#5557 — <c>"First-startup plugin provisioning failed; no retry is
/// attempted."</c> with <c>ObjectDisposedException</c> at <c>AsyncSubject.ThrowDisposed</c> ←
/// <c>AsyncSubject.Subscribe</c> ← <c>SubscribeSafe</c>, 20 times on memex-cloud.
///
/// <para><b>The mechanism.</b> The default install hops to the thread pool BEFORE it subscribes to
/// the bake barrier (<see cref="PreWarmCompletion.Settled"/>), so the install never runs on the
/// host-startup thread. On an ABORTED startup the host runs no <c>StopAsync</c> at all and disposes
/// the container (<see cref="AbortedStartupSkipsOrderedShutdownTest"/>) — and the barrier, first
/// resolved from the installer's own <c>StartAsync</c>, is created after the installer and so is
/// disposed BEFORE it. A queued subscribe that lands in between met a disposed subject. The same
/// shape sat on the two other one-shot signals the boot writers sequence on,
/// <see cref="InstanceAutoRegistrationService.Completed"/> and
/// <see cref="RegistryUpdateReconciler.BootReconciled"/>, which their owners disposed in
/// <c>StopAsync</c> while sibling hosted services could still subscribe.</para>
///
/// <para><b>What is asserted.</b> A late reader of each signal is never FAULTED by its owner's
/// teardown: it sees the value if one was published, or waits (its own owner's disposal ends that
/// wait). The negative control is the pre-fix code, on which every case below fails with the
/// production exception.</para>
/// </summary>
public class AOneShotSignalOutlivesTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The window a "no fault arrives" assertion spends in full — short, and CI-scaled.</summary>
    private static TimeSpan NoFaultWithin => TestTimeouts.Quick / 4;

    /// <summary>
    /// The thread-pool hop, placed where production's landed: it FIRES from its own disposal. It is
    /// resolved in the reader's <c>StartAsync</c> just before the barrier, so the container — which
    /// disposes in reverse creation order — disposes the barrier, then this (the queued subscribe
    /// runs), then the reader (whose own teardown would have cancelled a subscribe still queued).
    /// That is the one interleaving an aborted startup on a starved pool produces, made
    /// deterministic instead of waited for.
    /// </summary>
    private sealed class HopThatLandsDuringTeardown : IDisposable
    {
        public Subject<Unit> Fired { get; } = new();

        public void Dispose() => Fired.OnNext(Unit.Default);
    }

    /// <summary>
    /// Stands in for the default install's first leg: resolves the barrier from its
    /// <c>StartAsync</c> (as <c>InstanceAutoRegistrationService.Start</c> does), and subscribes to it
    /// only when the hop fires.
    /// </summary>
    private sealed class HoppingReader(IServiceProvider services, IObserver<Unit> attempted,
        IObserver<Notification<PreWarmSettlement>> outcome) : IHostedService, IDisposable
    {
        private IDisposable? subscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var hop = services.GetRequiredService<HopThatLandsDuringTeardown>();
            var barrier = services.GetRequiredService<PreWarmCompletion>();
            subscription = hop.Fired.Take(1)
                .SelectMany(_ => Observable.Defer(() =>
                {
                    attempted.OnNext(Unit.Default);
                    return barrier.Settled;
                }))
                .Materialize()
                .Subscribe(outcome);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => subscription?.Dispose();
    }

    /// <summary>A startup gate cancelled by a rollout — the aborted-startup shape.</summary>
    private sealed class CancelledGate : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => throw new OperationCanceledException("startup aborted by shutdown");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact(Timeout = 60_000)]
    public async Task TheBakeBarrier_IsNotFaulted_ForAReaderWhoseSubscribeLandsDuringAnAbortedStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempted = new ReplaySubject<Unit>();
        var outcome = new ReplaySubject<Notification<PreWarmSettlement>>();

        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<PreWarmCompletion>();
            services.AddSingleton<HopThatLandsDuringTeardown>();
            // Registered BEFORE the gate: it starts, then the gate aborts the startup.
            services.AddSingleton<IHostedService>(sp => new HoppingReader(sp, attempted, outcome));
            services.AddSingleton<IHostedService, CancelledGate>();
        }).Build();

        var thrown = await Record.ExceptionAsync(() => host.StartAsync(ct));
        thrown.Should().NotBeNull("precondition: the gate aborts host startup");

        // Exactly what RunAsync's finally does after a failed StartAsync: no StopAsync anywhere,
        // straight to disposing the container.
        host.Dispose();

        await attempted.Should().Emit(
            "precondition: the reader's subscribe really did land during the container's teardown — "
            + "without it the assertion below would pass having observed nothing",
            ct);
        await outcome.Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!.GetType().Name + ": " + n.Exception!.Message)
            .Should().NotEmit(NoFaultWithin,
                "the reader subscribed to a signal whose owner was being torn down; an unsettled "
                + "barrier means WAIT, never 'first-startup provisioning failed' (#5557)",
                ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASettledBakeBarrier_IsReplayed_AfterTheContainerIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<PreWarmCompletion>())
            .Build();
        var barrier = host.Services.GetRequiredService<PreWarmCompletion>();
        barrier.MarkSettled(PreWarmSettlement.Completed);
        host.Dispose();

        (await barrier.Settled.Should().Emit(
                "a one-shot replay is still a replay after the container that held it is gone",
                ct))
            .Should().Be(PreWarmSettlement.Completed);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheInstallersCompletion_IsNotFaulted_ForASiblingThatSubscribesAfterItStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        // Never started: the siblings that sequence on Completed subscribe on their own schedule,
        // and the installer's StopAsync can run first — reverse registration order.
        var installer = new InstanceAutoRegistrationService(
            Mesh, NullLogger<InstanceAutoRegistrationService>.Instance);
        await installer.StopAsync(ct);

        await installer.Completed.Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!.GetType().Name + ": " + n.Exception!.Message)
            .Should().NotEmit(NoFaultWithin,
                "a sibling that asks after the installer stopped must not be told it failed (#5557)",
                ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRegistryBootReconcile_IsNotFaulted_ForASiblingThatSubscribesAfterItStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var reconciler = new RegistryUpdateReconciler(
            Mesh, NullLogger<RegistryUpdateReconciler>.Instance);
        await reconciler.StopAsync(ct);

        await reconciler.BootReconciled.Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!.GetType().Name + ": " + n.Exception!.Message)
            .Should().NotEmit(NoFaultWithin,
                "the boot repair pass subscribes to this after the reconciler may have stopped (#5557)",
                ct);
    }
}
