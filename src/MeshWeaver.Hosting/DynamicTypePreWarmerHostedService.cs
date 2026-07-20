using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Kicks <see cref="DynamicTypePreWarmer.WarmDynamicTypes"/> once, in the background, after
/// the host has fully started (so the Orleans silo is up and grains can activate).
///
/// <para>Registered via <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>
/// — opt-in, portal-only. It is NOT wired into the shared <see cref="MeshHostApplicationBuilder"/>
/// so tests and non-portal hosts never pay for a startup warm-up they don't want.</para>
///
/// <para><b>Never blocks host startup or readiness.</b> <see cref="StartAsync"/> returns
/// immediately; the warm-up is launched from the <c>ApplicationStarted</c> callback and runs
/// on a background Rx subscription. The subscription is torn down on shutdown.</para>
/// </summary>
public sealed class DynamicTypePreWarmerHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DynamicTypePreWarmerHostedService> logger) : IHostedService, IDisposable
{
    private IDisposable? _warmSubscription;
    private IDisposable? _startedRegistration;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStarted fires only after EVERY hosted service (incl. the Orleans silo)
        // has started — so grains can activate. Registering the kick here, rather than doing
        // work in StartAsync, guarantees the silo is up without any ordering assumptions.
        _startedRegistration = lifetime.ApplicationStarted.Register(KickWarmup);
        return Task.CompletedTask;
    }

    private void KickWarmup()
    {
        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
        {
            logger.LogDebug("DynamicTypePreWarmer: no mesh hub resolved — skipping startup warm-up");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var compiled = 0;
        var errored = 0;
        var timedOut = 0;
        var faulted = 0;

        logger.LogInformation("DynamicTypePreWarmer: starting background warm-up of dynamic NodeType hubs");
        _warmSubscription = DynamicTypePreWarmer
            .WarmDynamicTypes(mesh, logger)
            .Subscribe(
                outcome =>
                {
                    switch (outcome.Status)
                    {
                        case PreWarmStatus.Compiled: Interlocked.Increment(ref compiled); break;
                        case PreWarmStatus.CompileError: Interlocked.Increment(ref errored); break;
                        case PreWarmStatus.TimedOut: Interlocked.Increment(ref timedOut); break;
                        default: Interlocked.Increment(ref faulted); break;
                    }
                },
                ex => logger.LogWarning(ex, "DynamicTypePreWarmer: warm-up stream faulted (best-effort — lazy compile still works)"),
                () => logger.LogInformation(
                    "DynamicTypePreWarmer: warm-up complete in {Elapsed} — compiled={Compiled} compileErrors={Errored} timedOut={TimedOut} faulted={Faulted}",
                    DateTimeOffset.UtcNow - startedAt,
                    Volatile.Read(ref compiled), Volatile.Read(ref errored),
                    Volatile.Read(ref timedOut), Volatile.Read(ref faulted)));
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
        _startedRegistration?.Dispose();
        _startedRegistration = null;
        _warmSubscription?.Dispose();
        _warmSubscription = null;
    }
}

/// <summary>Opt-in registration for the dynamic-NodeType startup pre-warm (portal hosts).</summary>
public static class PreWarmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DynamicTypePreWarmerHostedService"/> so the pod front-loads its
    /// dynamic NodeType compiles at startup (Part 1 of the fresh-pod compile-race hardening).
    /// Best-effort and non-blocking — safe to call from any portal host; a no-op if no mesh
    /// hub / mesh service is present.
    /// </summary>
    public static IServiceCollection AddDynamicTypePreWarming(this IServiceCollection services)
    {
        services.AddHostedService<DynamicTypePreWarmerHostedService>();
        return services;
    }
}
