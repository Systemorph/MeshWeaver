using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Drains and disposes the mesh's <see cref="IoPoolRegistry"/> as part of SILO SHUTDOWN, and does
/// not let the silo proceed until the join has actually happened.
///
/// <para><b>Why the silo, when a hosted service already drains the mesh.</b>
/// <c>MeshTeardownHostedService</c> runs in <c>StoppedAsync</c> — deliberately, so the mesh
/// outlives Kestrel and every other <c>StopAsync</c>. But the Orleans silo is itself a hosted
/// service, so it stops BEFORE that: by the time the mesh drain runs, every grain has already
/// deactivated, and <c>MessageHubGrain.OnDeactivateAsync</c> calls
/// <c>loadContext.Unload()</c> on each grain's collectible ALC. A pooled I/O leaf still executing
/// that ALC's compiled types when it unloads is the native use-after-unload SIGSEGV — a crash at
/// or near process exit, after every test has passed (issue #613). The grain's own comment
/// asserted "Silo SHUTDOWN is safe (MeshTeardownHostedService drains the whole mesh …)"; the
/// ordering above is why that was not true.</para>
///
/// <para><b>Stage, and why it stops LAST.</b> Orleans starts observers in ascending stage order
/// and stops them in DESCENDING order, so subscribing LOW means stopping LATE. This subscribes at
/// <see cref="ServiceLifecycleStage.First"/> so its <c>OnStop</c> runs after the grain catalog has
/// deactivated everything — the grains get their full, ungated chance to flush final state through
/// the pools — and only then is the pool cancelled and joined. Draining EARLY would be the
/// opposite mistake: <see cref="IoPool.Drain"/> is terminal, so every grain's shutdown write would
/// be cancelled out from under it.</para>
///
/// <para>Pairs with the grain no longer unloading its ALC when the reason is silo shutdown: the
/// process is going away, so an unload buys nothing there and is precisely the window this
/// crash lives in. Belt and braces — this join makes the unload safe wherever it still happens.</para>
/// </summary>
public sealed class IoPoolSiloTeardown(
    IServiceProvider services,
    ILogger<IoPoolSiloTeardown> logger)
    : ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
{
    /// <summary>Bounded so a leaf that ignores its token cannot hang silo shutdown; it is reported
    /// instead. Matches <c>MeshTeardownHostedService</c>'s budget.</summary>
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public void Participate(ISiloLifecycle observer) =>
        // First ⇒ started first, STOPPED LAST (Orleans stops in descending stage order).
        observer.Subscribe(nameof(IoPoolSiloTeardown), ServiceLifecycleStage.First, this);

    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

    // 🚨 NOT `async`, and nothing here awaits. AGENTS.md: everything is IObservable<T> end-to-end —
    // compose and Subscribe, never await. The single Task exists only because ILifecycleObserver
    // demands one, and it is produced by ONE .ToTask() at the boundary — the sanctioned shape for a
    // framework surface whose body stays reactive.
    //
    // Orleans awaits the returned Task, which is what holds the silo back until the pools report.
    // That is the whole guarantee, and it costs no thread: nothing is parked here, the completion
    // arrives on whichever thread the last leaf unwinds on.
    Task ILifecycleObserver.OnStop(CancellationToken cancellationToken)
    {
        // Resolved lazily: the container is still alive during OnStop, and resolving in the
        // constructor would pin the registry into this participant's lifetime for no reason.
        var registry = services.GetService<IoPoolRegistry>();
        if (registry is null)
            return Task.CompletedTask;

        logger.LogInformation(
            "IoPoolSiloTeardown: draining pooled I/O before the silo releases (in-flight={InFlight})",
            registry.TotalInFlight);

        // Dispose CANCELS every leaf and RETURNS — a live change-feed leaf never completes on its
        // own, so a wait-without-cancel would burn the budget and then release over live work.
        // It does not join; Disposed is what completes once the last leaf has unwound.
        registry.Dispose();

        return registry.Disposed
            .Timeout(JoinBudget)
            // A timeout means a leaf never unwound. Report it as a residual rather than faulting:
            // shutdown must continue, and the log below is the only attribution a later SIGSEGV gets.
            .Catch<int, Exception>(_ => Observable.Return(-1))
            .Do(Report)
            .Select(_ => Unit.Default)
            .FirstAsync()
            .ToTask();
    }

    private void Report(int leaked)
    {
        if (leaked < 0)
            logger.LogError(
                "IoPoolSiloTeardown: pooled I/O did not finish within {Budget} — the silo is "
                + "releasing over live work. A leaf ignored its cancellation token; fix the leaf, "
                + "do not widen the budget.",
                JoinBudget);
        else if (leaked == 0)
            logger.LogInformation("IoPoolSiloTeardown: pooled I/O joined — no pool thread is running");
        else
            // The ONLY attribution a subsequent SIGSEGV will get. Never downgrade this: the silo is
            // about to release (and any remaining ALC to unload) over a thread still in that code.
            logger.LogError(
                "IoPoolSiloTeardown: {Leaked} pooled I/O leaf(s) survived the join — the silo is "
                + "releasing over live work. A leaf ignored its cancellation token; fix the leaf, "
                + "do not widen the budget.",
                leaked);
    }
}

/// <summary>DI wiring for <see cref="IoPoolSiloTeardown"/>.</summary>
public static class IoPoolSiloTeardownExtensions
{
    /// <summary>
    /// Registers <see cref="IoPoolSiloTeardown"/> as a silo lifecycle participant, so pooled I/O is
    /// cancelled and JOINED — bounded — before the silo releases.
    ///
    /// <para>"Joined" is the behaviour callers must plan for: <c>OnStop</c> returns a Task that
    /// completes only once every pool has reported, so Orleans genuinely holds shutdown until then.
    /// It is not fire-and-forget. The join costs no thread (the body is a reactive composition, not
    /// an await), and it is bounded by <c>JoinBudget</c>: on timeout the residual is logged as an
    /// error and shutdown proceeds rather than hanging. (Copilot review, #1903.)</para>
    /// </summary>
    /// <param name="services">The service collection to add the participant to.</param>
    /// <returns>The same service collection for further chaining.</returns>
    public static IServiceCollection AddIoPoolSiloTeardown(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IoPoolSiloTeardown)))
            return services;
        services.AddSingleton<IoPoolSiloTeardown>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            sp.GetRequiredService<IoPoolSiloTeardown>());
        return services;
    }
}
