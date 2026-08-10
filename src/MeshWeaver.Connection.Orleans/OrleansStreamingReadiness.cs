using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Deterministic "Orleans streaming is usable" signal, completed when the hosting Orleans
/// lifecycle — silo or cluster client, whichever this host runs — reaches
/// <see cref="ServiceLifecycleStage.Active"/>.
///
/// <para>Why this exists (issue #1129): Orleans' <c>PersistentStreamProvider</c> initialises
/// itself as a lifecycle participant at <c>StreamLifecycleOptions.InitStage</c>
/// (<see cref="ServiceLifecycleStage.ApplicationServices"/> by default). Touching
/// <c>GetStream</c> before that stage has run throws a <see cref="NullReferenceException"/>
/// from deep inside the Orleans stream runtime (<c>PersistentStreamProvider.get_IsRewindable</c>
/// — the provider instance exists, its state does not). The process-wide cache/mesh hubs are
/// created eagerly at silo startup and used to lose exactly that race on every pod boot,
/// which a poll-retry loop then papered over with two <c>Error</c>-level NRE logs per boot.</para>
///
/// <para>This signal replaces the poll: it observes the same lifecycle the stream provider
/// participates in, at <see cref="ServiceLifecycleStage.Active"/> — a stage strictly AFTER the
/// provider's init stage, and also after the silo has joined membership, so PubSub grain calls
/// made while subscribing can be placed. Consumers order their first provider touch on
/// <see cref="Ready"/> and the race is gone by construction — no retry, no timer.</para>
///
/// <para>Registered by <see cref="OrleansStreamingReadinessExtensions.AddOrleansStreamingReadiness"/>
/// for both lifecycles; Orleans collects lifecycle participants from DI, so whichever lifecycle
/// the host actually runs picks it up and the other registration is simply never resolved.</para>
/// </summary>
public sealed class OrleansStreamingReadiness :
    ILifecycleParticipant<ISiloLifecycle>,
    ILifecycleParticipant<IClusterClientLifecycle>,
    ILifecycleObserver
{
    private readonly AsyncSubject<Unit> ready = new();

    /// <summary>
    /// Emits a single <see cref="Unit"/> and completes once the Orleans lifecycle has reached
    /// <see cref="ServiceLifecycleStage.Active"/>. Backed by an <see cref="AsyncSubject{T}"/>,
    /// so late subscribers get the completed signal replayed immediately.
    /// </summary>
    public IObservable<Unit> Ready => ready.AsObservable();

    /// <summary>
    /// Silo-side participation: observe the silo lifecycle at
    /// <see cref="ServiceLifecycleStage.Active"/>.
    /// </summary>
    /// <param name="observer">The silo lifecycle to participate in.</param>
    public void Participate(ISiloLifecycle observer) =>
        observer.Subscribe(nameof(OrleansStreamingReadiness), ServiceLifecycleStage.Active, this);

    /// <summary>
    /// Client-side participation: observe the cluster-client lifecycle at
    /// <see cref="ServiceLifecycleStage.Active"/>.
    /// </summary>
    /// <param name="observer">The cluster-client lifecycle to participate in.</param>
    public void Participate(IClusterClientLifecycle observer) =>
        observer.Subscribe(nameof(OrleansStreamingReadiness), ServiceLifecycleStage.Active, this);

    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken)
    {
        ready.OnNext(Unit.Default);
        ready.OnCompleted();
        return Task.CompletedTask;
    }

    Task ILifecycleObserver.OnStop(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// DI wiring for <see cref="OrleansStreamingReadiness"/>.
/// </summary>
public static class OrleansStreamingReadinessExtensions
{
    /// <summary>
    /// Registers <see cref="OrleansStreamingReadiness"/> and exposes it to both the silo and the
    /// cluster-client lifecycle (Orleans collects <see cref="ILifecycleParticipant{T}"/>
    /// registrations from DI — whichever lifecycle this host runs picks the signal up).
    /// Idempotent: a second call is a no-op.
    /// </summary>
    /// <param name="services">The service collection to add the readiness signal to.</param>
    /// <returns>The same service collection for further chaining.</returns>
    public static IServiceCollection AddOrleansStreamingReadiness(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(OrleansStreamingReadiness)))
            return services;
        services.AddSingleton<OrleansStreamingReadiness>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            sp.GetRequiredService<OrleansStreamingReadiness>());
        services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>>(sp =>
            sp.GetRequiredService<OrleansStreamingReadiness>());
        return services;
    }
}
