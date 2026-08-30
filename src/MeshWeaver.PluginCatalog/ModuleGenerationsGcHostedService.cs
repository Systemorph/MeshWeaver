using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Runs the <c>modules/</c> generations GC (<see cref="ModuleLandingService.CollectGarbage(string,
/// ILogger?, TimeSpan?, DateTime?, CancellationToken)"/>) OFF the readiness path — after
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/>, through the file-system
/// <see cref="IIoPool"/> (#2684).
///
/// <para>🚨 <b>Why the boot path is the wrong place.</b> The pass used to run synchronously in
/// <c>ConfigureMemexMesh</c>, before the host listened. Reclaiming orphaned generations is one SMB
/// round-trip per file on an Azure Files (CIFS) <c>/data</c> — minutes of uninterruptible IO for a
/// handful of leftover directories — and it reclaims nothing the portal needs in order to SERVE.
/// Rollout time therefore became a function of how much garbage the previous generation left on a
/// network volume: memex-cloud's roll to ci.6559 sat as PID 1 in <c>Dsl</c> at
/// <c>wchan=wait_for_response</c> deleting a <c>.trash-*</c> generation, never bound :8080, blew
/// the 300 s startup probe (whose kill cannot land on a process parked in uninterruptible IO), and
/// looped. Raising the probe budget would only move the cliff; the GC has no business in front of
/// the listener at all.</para>
///
/// <para><b>The semantics do not change — only the WHEN.</b> Same pass, same rules: delete only
/// what no activation entry references and nothing holds open (skip-on-locked), landing never
/// deletes, the atomic <c>.trash-*</c> rename (#2509), the #2303 grace window, fail-closed on an
/// unreadable reference set. The reference set is re-read from the per-module sidecar files at run
/// time, so a post-start pass sees a set at least as fresh as the boot-time pass did — and the
/// running process is immune to its own reclaim because boot loads store-landed generations from a
/// process-local pinned copy (<see cref="ModuleGenerationPin"/>), never the shared tree.</para>
///
/// <para><b>Lifecycle.</b> <see cref="StartAsync"/> only registers the
/// <c>ApplicationStarted</c> callback — it can never delay the listener, and nothing here gates
/// <c>/health</c> or <c>/alive</c>. The callback schedules the pass on the pool and returns; the
/// blocking work runs on the pool's limited-concurrency scheduler with the pool's cancellation
/// linked in, so a mesh teardown cancels a sweep still crawling a slow volume instead of waiting
/// on it (the token is observed between directories — the unit of atomic removal).</para>
/// </summary>
public sealed class ModuleGenerationsGcHostedService : IHostedService, IDisposable
{
    /// <summary>The pass itself — a test seam. Production is
    /// <see cref="ModuleLandingService.CollectGarbage(string, ILogger?, TimeSpan?, DateTime?,
    /// CancellationToken)"/>; a test substitutes a collector it can block or count, which is how
    /// "readiness never waits on a stalled sweep" is provable without a real CIFS mount.</summary>
    public delegate int Collector(string moduleRoot, ILogger? logger, CancellationToken cancellationToken);

    private readonly string moduleRoot;
    private readonly IHostApplicationLifetime lifetime;
    private readonly IIoPool pool;
    private readonly ILogger<ModuleGenerationsGcHostedService>? logger;
    private readonly Collector collect;
    // One-shot terminal state: the count once the pass finished, or its error. AsyncSubject so a
    // subscriber arriving after the sweep (a test, a diagnostic) still gets the outcome.
    private readonly AsyncSubject<int> completed = new();
    private IDisposable? startedRegistration;
    private IDisposable? sweep;

    /// <summary>Creates the service.</summary>
    /// <param name="moduleRoot">The deployment root the <c>modules/</c> tree lives under — the
    /// SAME resolved root the boot path computes effective modules from (<see cref="ModuleRoot"/>),
    /// never <c>AppContext.BaseDirectory</c> directly.</param>
    /// <param name="lifetime">The host lifetime whose <c>ApplicationStarted</c> gates the pass —
    /// it fires only once every hosted service (Kestrel's listener included) has started.</param>
    /// <param name="pool">The bounded IO pool the blocking filesystem pass runs through.</param>
    /// <param name="logger">Diagnostics — the removal count is reported exactly as the boot-path
    /// call reported it.</param>
    /// <param name="collect">Test seam for the pass; see <see cref="Collector"/>.</param>
    public ModuleGenerationsGcHostedService(
        string moduleRoot,
        IHostApplicationLifetime lifetime,
        IIoPool pool,
        ILogger<ModuleGenerationsGcHostedService>? logger = null,
        Collector? collect = null)
    {
        this.moduleRoot = moduleRoot;
        this.lifetime = lifetime;
        this.pool = pool;
        this.logger = logger;
        this.collect = collect ?? ((root, log, ct) =>
            ModuleLandingService.CollectGarbage(root, log, cancellationToken: ct));
    }

    /// <summary>
    /// The pass's terminal outcome: emits the removed-directory count once the sweep finished,
    /// errors when it faulted, and stays silent while it has not run. Tests and diagnostics wait
    /// on this — readiness never does.
    /// </summary>
    public IObservable<int> Completed => completed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 🚨 Register only — no filesystem IO before the host listens (#2684). ApplicationStarted
        // fires after EVERY hosted service has started, i.e. strictly after the listener is bound.
        startedRegistration = lifetime.ApplicationStarted.Register(KickSweep);
        return Task.CompletedTask;
    }

    private void KickSweep()
    {
        // InvokeBlocking: the pass is synchronous, blocking filesystem IO — it runs on the pool's
        // limited-concurrency scheduler, with the pool's own cancellation linked in, so it can
        // neither starve the ThreadPool nor outlive a teardown drain (the collector observes the
        // token between directories).
        sweep = pool.InvokeBlocking(ct => collect(moduleRoot, logger, ct))
            .Subscribe(
                removedCount =>
                {
                    if (removedCount > 0)
                        logger?.LogInformation(
                            "[ModuleActivation] modules GC removed {Count} unreferenced generation(s)",
                            removedCount);
                    completed.OnNext(removedCount);
                    completed.OnCompleted();
                },
                ex =>
                {
                    // A faulted pass reclaims nothing and breaks nothing: every state it can leave
                    // behind (an intact orphan, a .trash-* remainder) is one a later pass — the
                    // next pod start — already plans for. Surfaced, never swallowed.
                    logger?.LogWarning(ex,
                        "[ModuleActivation] modules GC pass over {ModuleRoot} faulted — nothing "
                        + "was collected; the next pod start plans it again.", moduleRoot);
                    completed.OnError(ex);
                });
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
        startedRegistration?.Dispose();
        startedRegistration = null;
        // Unsubscribing cancels the pooled leaf's linked token — a sweep still crawling a slow
        // volume stops at its next between-directories check instead of parking the drain. The
        // AsyncSubject is deliberately NOT disposed: a leaf mid-completion may still be delivering
        // its terminal notification on a pool thread, and OnNext into a disposed subject throws
        // where nothing can observe it. The subject holds no resources; it dies with the service.
        sweep?.Dispose();
        sweep = null;
    }
}

/// <summary>Registration for the post-start <c>modules/</c> generations GC.</summary>
public static class ModuleGenerationsGcExtensions
{
    /// <summary>
    /// Registers <see cref="ModuleGenerationsGcHostedService"/> over <paramref name="moduleRoot"/>.
    /// Two-registration idiom: the <c>IHostedService</c> forward is what STARTS it; the concrete
    /// singleton is resolvable for tests and diagnostics (<see
    /// cref="ModuleGenerationsGcHostedService.Completed"/>).
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="moduleRoot">The resolved module root — the same one the boot path computed its
    /// effective module set from.</param>
    public static IServiceCollection AddModuleGenerationsGc(
        this IServiceCollection services, string moduleRoot)
        => services
            .AddSingleton(sp => new ModuleGenerationsGcHostedService(
                moduleRoot,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded,
                sp.GetService<ILogger<ModuleGenerationsGcHostedService>>()))
            .AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ModuleGenerationsGcHostedService>());
}
