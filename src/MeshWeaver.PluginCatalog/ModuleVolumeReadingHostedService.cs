using System.Reactive;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Takes the FIRST reading of the module volume at host start, so that the probes which report on
/// it never have to take one (MeshWeaver#4655).
///
/// <para><b>Why a reading needs an owner at all.</b> <see cref="PendingModuleActivations"/> is a
/// pull-on-demand reader, and for every surface except a probe that is right: a card that asks is a
/// person who will wait. A probe is not — <c>/health</c> is the <c>startupProbe</c>'s instrument,
/// its per-probe budget is five seconds on the shipped chart, and a startup timeout is the one a
/// container cannot recover from (<c>Doc/Architecture/AProbeMustAnswerInsideItsOwnTimeout</c>).
/// Memoising the walk left exactly two callers still paying for it in full, and both are probes:
/// the FIRST probe of a fresh pod, and the first probe after anything lands. This service is the
/// first of those two; <see cref="PendingModuleActivations.ReadProbeInputs"/> refusing to walk is
/// the second.</para>
///
/// <para>🚨 <b>It cannot delay the listener and it gates nothing.</b> <see cref="StartAsync"/>
/// subscribes a COLD observable that runs on the reader's <c>IIoPool</c> and returns immediately.
/// If a probe arrives before the reading lands, it gets
/// <see cref="ModuleProbeInputs.NotRead"/> — which classifies
/// <see cref="RequiredModuleState.Unmeasured"/> and REFUSES, in microseconds, naming the reason.
/// That is the honest answer for that instant and it clears itself; what it must never become is a
/// quiet pass, and what it must never be is a probe that waits.</para>
///
/// <para>🚨 <b>Not <c>ApplicationStarted</c>, unlike the modules GC beside it.</b> The GC waits
/// there because reclaiming garbage must not compete with a boot that has not finished. This
/// reading is the opposite errand: the sooner it is taken, the fewer probes answer
/// <c>Unmeasured</c> — and it is a read of three directories, not a per-file delete of a whole
/// generation tree.</para>
/// </summary>
public sealed class ModuleVolumeReadingHostedService : IHostedService, IDisposable
{
    private readonly PendingModuleActivations activations;
    private readonly ILogger<ModuleVolumeReadingHostedService>? logger;
    // One-shot terminal state: completes when the first reading has been taken, or errors with
    // what stopped it. AsyncSubject so a subscriber arriving afterwards still gets the outcome.
    private readonly AsyncSubject<Unit> taken = new();
    private IDisposable? reading;

    /// <summary>Creates the service over the reader whose snapshot it warms.</summary>
    /// <param name="activations">The ONE reader every surface consults — never a second instance,
    /// which would warm a snapshot no probe reads.</param>
    /// <param name="logger">Diagnostics for a first reading that faulted.</param>
    public ModuleVolumeReadingHostedService(
        PendingModuleActivations activations,
        ILogger<ModuleVolumeReadingHostedService>? logger = null)
    {
        this.activations = activations;
        this.logger = logger;
    }

    /// <summary>
    /// Emits once the first reading has been taken, errors when it faulted, and stays silent while
    /// it has not run. Tests and diagnostics wait on this — readiness never does.
    /// </summary>
    public IObservable<Unit> Taken => taken;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        reading = activations.Refresh().Subscribe(
            _ =>
            {
                taken.OnNext(Unit.Default);
                taken.OnCompleted();
            },
            exception =>
            {
                // A faulted first reading leaves the probes answering Unmeasured, which refuses —
                // so nothing here is swallowed into a pass. Surfaced, and re-requested by the very
                // next probe.
                logger?.LogWarning(
                    exception,
                    "[ModuleActivation] the first reading of the module volume under {ModuleRoot} "
                    + "faulted — until one is taken, the required-modules probe answers "
                    + "'not measured' and refuses readiness rather than passing.",
                    activations.ModuleRootPath);
                taken.OnError(exception);
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Unsubscribing cancels the pooled leaf's linked token, so a reading still crawling a slow
        // volume stops at its next check instead of parking the drain. The AsyncSubject is
        // deliberately not disposed: a leaf mid-completion may still be delivering its terminal on
        // a pool thread, and OnNext into a disposed subject throws where nothing can observe it.
        reading?.Dispose();
        reading = null;
    }
}

/// <summary>Registration for the host-start reading of the module volume.</summary>
public static class ModuleVolumeReadingExtensions
{
    /// <summary>
    /// Registers <see cref="ModuleVolumeReadingHostedService"/> over the registered
    /// <see cref="PendingModuleActivations"/>. Two-registration idiom: the <c>IHostedService</c>
    /// forward is what STARTS it; the concrete singleton is resolvable for tests and diagnostics
    /// (<see cref="ModuleVolumeReadingHostedService.Taken"/>).
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddModuleVolumeReading(this IServiceCollection services)
        => services
            .AddSingleton(sp => new ModuleVolumeReadingHostedService(
                sp.GetRequiredService<PendingModuleActivations>(),
                sp.GetService<ILogger<ModuleVolumeReadingHostedService>>()))
            .AddSingleton<IHostedService>(
                sp => sp.GetRequiredService<ModuleVolumeReadingHostedService>());
}
