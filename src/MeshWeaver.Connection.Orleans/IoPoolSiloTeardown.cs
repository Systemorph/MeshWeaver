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
///
/// <para>🚨 <b>Everything the stop needs is captured on <c>OnStart</c>, because the container is
/// NOT guaranteed to outlive the stop.</b> The registry used to be resolved lazily inside
/// <c>OnStop</c> on the reasoning that "the container is still alive during OnStop". That holds only
/// on the ORDERLY shutdown path. When host STARTUP is aborted — a rollout replacing a pod that is
/// still starting — <c>Host.StartAsync</c> throws, and <c>RunAsync</c>'s <c>finally</c> goes
/// straight to <c>host.DisposeAsync()</c>: no <c>IHostedService.StopAsync</c> runs, no
/// <c>IHostedLifecycleService.StoppedAsync</c> runs, and the root provider is disposed while the
/// already-started silo is still stopping. This observer then resolved from a dead Autofac
/// <c>LifetimeScope</c> and threw <see cref="ObjectDisposedException"/> from the one method whose
/// whole job is to make the release safe — issues #1898 and #1899, both on the same pod and second
/// as the aborted startup that caused them (#1897).</para>
///
/// <para>The repair is the ordering, not a <c>catch</c>: a swallowed
/// <see cref="ObjectDisposedException"/> would make the drain silently not happen, which is the
/// use-after-unload SIGSEGV with its only attribution removed. Capturing at
/// <see cref="ServiceLifecycleStage.First"/> — the stage that starts FIRST, when the container is
/// provably alive — and using the capture at the stop that runs LAST means the join needs nothing
/// from DI at all, on either shutdown path. Same rule
/// <c>MeshTeardownExtensions.TeardownAsync</c> already states: <i>capture mesh-scoped teardown
/// services while the scope is still ALIVE — never resolve DI once disposal has begun.</i></para>
///
/// <para>Note this deliberately does NOT assume the drain faces a settled silo (#1868: a hub build
/// constructs other hubs, so a disposal races a TREE of constructions). It cancels and joins
/// whatever the pools hold, and reports what did not unwind.</para>
/// </summary>
public sealed class IoPoolSiloTeardown(
    IServiceProvider services,
    ILogger<IoPoolSiloTeardown> logger)
    : ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
{
    /// <summary>Bounded so a leaf that ignores its token cannot hang silo shutdown; it is reported
    /// instead. Matches <c>MeshTeardownHostedService</c>'s budget.</summary>
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

    // Captured on OnStart while the container is provably alive, read on OnStop which may run
    // AFTER the container is gone (see the type remarks). Volatile because start and stop are
    // different lifecycle turns on different threads with no lock between them.
    private volatile IoPoolRegistry? _registry;
    private volatile bool _captured;

    /// <inheritdoc />
    public void Participate(ISiloLifecycle observer) =>
        // First ⇒ started first, STOPPED LAST (Orleans stops in descending stage order).
        observer.Subscribe(nameof(IoPoolSiloTeardown), ServiceLifecycleStage.First, this);

    // 🚨 The ONE DI resolution in this type, and it happens at the FIRST silo stage — the earliest
    // moment the container is provably alive. OnStop must never resolve: see the type remarks for
    // the aborted-startup path that disposes the container without ever running an ordered
    // shutdown. Not `async` either — there is nothing to await; the capture is synchronous.
    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken)
    {
        _registry = services.GetService<IoPoolRegistry>();
        _captured = true;
        return Task.CompletedTask;
    }

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
        if (!_captured)
        {
            // Orleans only stops observers whose start completed, so this is not expected — but a
            // teardown that did not run must SAY so rather than return like a clean join. Silence
            // here would leave a later use-after-unload SIGSEGV with no attribution at all.
            logger.LogError(
                "IoPoolSiloTeardown: OnStop ran without OnStart, so no IoPoolRegistry was ever "
                + "captured — pooled I/O has NOT been drained and the silo is releasing over "
                + "whatever is still in flight. Do not resolve it here: on an aborted startup the "
                + "container is already disposed (#1898).");
            return Task.CompletedTask;
        }

        // Read the capture — never a resolve. The container may already be disposed by now.
        var registry = _registry;
        if (registry is null)
            // Nothing registered pooled I/O in this container (a silo without the mesh services),
            // so there is genuinely nothing to drain. Distinct from the case above: this one is a
            // real no-op, not an unrun teardown.
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
