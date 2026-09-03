using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// A data source that computes its data from a virtual stream rather than storing it directly.
/// This is useful for derived data, aggregations, or transformations of existing data sources.
/// </summary>
public record VirtualDataSource(object Id, IWorkspace Workspace)
    : TypeSourceBasedUnpartitionedDataSource<VirtualDataSource, VirtualTypeSource>(Id, Workspace)
{
    /// <summary>
    /// Not supported on a virtual data source — virtual types carry a stream provider and
    /// must be added with <see cref="WithVirtualType{T}"/> instead. Always throws.
    /// </summary>
    /// <typeparam name="T">The type that would be added.</typeparam>
    /// <param name="config">Ignored.</param>
    /// <returns>Never returns; always throws <see cref="NotSupportedException"/>.</returns>
    public override VirtualDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config)
    {
        throw new NotSupportedException("VirtualDataSource does not support WithType. Use WithVirtualType instead.");
    }

    /// <summary>
    /// Adds a virtual type to this data source with a stream provider function.
    /// </summary>
    /// <typeparam name="T">The type to add</typeparam>
    /// <param name="streamProvider">Function that receives the workspace and returns an observable stream of instances</param>
    /// <param name="collectionName">Optional collection name (defaults to type name)</param>
    /// <returns>Updated data source</returns>
    public VirtualDataSource WithVirtualType<T>(
        Func<IWorkspace, IObservable<IEnumerable<T>>> streamProvider,
        string? collectionName = null
    ) where T : class
    {
        var typeSource = new VirtualTypeSource<T>(
            Workspace,
            Id,
            streamProvider,
            collectionName
        );
        return WithTypeSource(typeof(T), typeSource);
    }

    /// <summary>First backoff step before a faulted provider is rebuilt; doubles per attempt.</summary>
    internal static readonly TimeSpan ProviderRetryBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ceiling the backoff saturates at. A provider whose upstream is genuinely gone is then
    /// rebuilt once a minute — cheap enough to be irrelevant, slow enough that it can never be
    /// the load.
    /// </summary>
    internal static readonly TimeSpan ProviderRetryMaxDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Backoff before rebuilding a faulted provider: 1s, 2s, 4s … saturating at
    /// <see cref="ProviderRetryMaxDelay"/>. Pure, so the policy is an assertion rather than a
    /// timing observation (issue #3155).
    /// </summary>
    /// <param name="attempt">1-based attempt number.</param>
    /// <returns>How long to wait before the next rebuild.</returns>
    internal static TimeSpan ProviderRetryDelay(int attempt)
    {
        if (attempt <= 0)
            return ProviderRetryBaseDelay;
        // Shift on a long and clamp: the loop is unbounded by design, so "the counter cannot get
        // that high" is not an argument — a naive 1 << (attempt - 1) overflows into a NEGATIVE
        // delay around attempt 63, and a negative timer delay fires immediately, turning the rate
        // ceiling into a spin exactly when the outage has lasted longest.
        var doublings = Math.Min(attempt - 1, 32);
        var ms = ProviderRetryBaseDelay.TotalMilliseconds * (1L << doublings);
        return ms >= ProviderRetryMaxDelay.TotalMilliseconds
            ? ProviderRetryMaxDelay
            : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Scheduler for the provider-rebuild backoff. An INSTANCE seam, never static state: a test
    /// pins the policy without waiting out real seconds.
    /// </summary>
    internal IScheduler ProviderRetryScheduler { get; set; } = TaskPoolScheduler.Default;

    /// <summary>
    /// Builds the backing entity-store stream and subscribes to each virtual type source's
    /// stream updates so later emissions from the provider are pushed into the local mirror.
    /// Writes are stamped with the System identity because the provider emissions land on a
    /// background scheduler where the per-request AccessContext is not available.
    /// </summary>
    /// <param name="identity">The identity (owner address plus partition) of the stream to set up.</param>
    /// <param name="config">Configuration applied to the underlying stream.</param>
    /// <returns>The configured entity-store synchronization stream.</returns>
    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(
        StreamIdentity identity,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var stream = base.SetupDataSourceStream(identity, config);

        // 🚨 The GetStreamUpdates emissions below land on the UPSTREAM provider's scheduler —
        // a SyncedQueryMeshNodes query-result hop, a derived-data CombineLatest, a timer —
        // where the per-request AsyncLocal AccessContext is WIPED (it does not flow across an
        // Rx scheduler boundary). Writing the data source's OWN computed snapshot into its OWN
        // local mirror stream is INFRASTRUCTURE: the data is already RLS-filtered at the query
        // layer (SyncedQueryMeshNodes runs per-user) or is derived framework data, and per-user
        // enforcement is re-applied at the CONSUMER (SyncedQueryDataSourceExtensions.WrapWithPerUserRls).
        // Without an explicit identity the stream.Update below posts an UpdateStreamRequest with a
        // NULL AccessContext and the PostPipeline never-null guard fails it CLOSED → a
        // DeliveryFailure storm (prod 2026-06-21: ds/Skill at ~3/sec, OnError-ing the typed
        // content stream so the bound area hangs). Stamp System on these writes — the SAME rule and
        // fix as the resubscribe in JsonSynchronizationStream and the stale-patch refresh in
        // SynchronizationStream. (System on this WRITE does not collapse per-user READS the way the
        // 88764f803 subscribe-path regression did — reads stay filtered at the consumer.)
        var accessService = Workspace.Hub.ServiceProvider.GetService<AccessService>();

        // Subscribe to each virtual type source's stream updates to propagate changes
        foreach (var typeSource in TypeSources.Values)
        {
            var isFirst = true;
            stream.RegisterForDisposal(
                // 🚨 #3155 — Defer, so every re-subscribe re-ASKS the type source for its chain
                // rather than re-attaching to the one that faulted. Paired with the eviction in the
                // fault arm below, that is what makes the rebuild real: the cached chain is
                // Replay(1).RefCount(), whose subject latches OnError, so a re-subscribe without
                // the eviction replays the same fault for ever.
                Observable.Defer(typeSource.GetStreamUpdates)
                    .RetryWhen(faults => faults
                        .Select((error, i) => (Error: error, Attempt: i + 1))
                        .SelectMany(f =>
                        {
                            var delay = ProviderRetryDelay(f.Attempt);
                            // Reported every time, never swallowed — the fault is real and the
                            // collection IS stale until the rebuild lands. What changed is the
                            // second sentence: it used to say "frozen … will receive no further
                            // updates", which was true and permanent.
                            Logger.LogError(f.Error,
                                "Virtual data source {DataSource}: the provider for collection "
                                + "'{Collection}' faulted (attempt {Attempt}) on hub {Address}. That "
                                + "collection is stale until the provider is rebuilt, which is "
                                + "scheduled in {Delay}.",
                                Id, typeSource.CollectionName, f.Attempt, Workspace.Hub.Address, delay);
                            // Drop the latched chain BEFORE the timer, so the re-subscribe above
                            // builds a fresh one instead of replaying this fault.
                            typeSource.EvictCachedStream();
                            return Observable.Timer(delay, ProviderRetryScheduler);
                        }))
                    .Subscribe(instances =>
                    {
                        // Skip the first emission since it's handled by initialization
                        if (isFirst)
                        {
                            isFirst = false;
                            return;
                        }

                        // Create an InstanceCollection from the new instances
                        var collection = new InstanceCollection(
                            instances.ToDictionary(typeSource.TypeDefinition.GetKey))
                        {
                            GetKey = typeSource.TypeDefinition.GetKey
                        };

                        // Update the stream with the new collection — under System identity so the
                        // post is never null-AccessContext on a background scheduler hop (see above).
                        using (accessService?.ImpersonateAsSystem())
                            stream.Update(store =>
                            {
                                var newStore = (store ?? new EntityStore())
                                    .WithCollection(typeSource.CollectionName, collection);
                                return (ChangeItem<EntityStore>?)
                                    new ChangeItem<EntityStore>(newStore, Id.ToString()!, stream.StreamId, ChangeType.Full, stream.Hub.Version, []);
                            }, _ => { });
                    },
                    // 🚨 THE ERROR ARM IS NOT OPTIONAL — omitting it is a PROCESS KILLER, not a
                    // missing log line. A virtual type's provider is arbitrary composed content
                    // (a mesh read, a query hop, a CombineLatest over other streams), so it CAN
                    // fault; and Rx's default onError handler for a one-argument Subscribe is
                    // Stubs.Throw, which RETHROWS the fault on whatever thread carried it. That
                    // thread is almost never one with a catch: in Systemorph/MeshWeaver#2468 the
                    // provider's `hub.GetMeshNode(...)` timed out, so the OnError originated in a
                    // CancellationTokenSource callback on a TimerQueue thread — the rethrow became
                    // an UNHANDLED exception, the host aborted (core dumped), and the Doc content
                    // gate reported "failed before it produced a verdict — no check was judged".
                    // A gate that dies before judging is worse than a gate that fails.
                    //
                    // Reported, never swallowed: the fault is real and this collection is now
                    // frozen at its last emission. The data source's own initialization observes
                    // the SAME faulted Replay(1) (VirtualTypeSource.StreamUpdates), so a fault
                    // during init still fails the hub's startup through the normal path — this arm
                    // exists so a fault can never take the process with it.
                    // With the unbounded RetryWhen above this arm is the LAST LINE OF DEFENCE
                    // rather than the policy — but it must stay, and for the reason spelled out
                    // above: Rx's default one-argument onError is Stubs.Throw, which rethrows on
                    // whatever thread carried the fault, and that thread is almost never one with a
                    // catch (#2468: a TimerQueue thread, an unhandled exception, a core-dumped
                    // host, and a gate that "failed before it produced a verdict").
                    error => Logger.LogError(error,
                        "Virtual data source {DataSource}: the provider for collection "
                        + "'{Collection}' terminated on hub {Address} — the rebuild sequence itself "
                        + "ended, so this collection is frozen at its last emission until the hub "
                        + "is recycled.",
                        Id, typeSource.CollectionName, Workspace.Hub.Address))
            );
        }

        return stream;
    }
}

/// <summary>
/// Base class for virtual type sources
/// </summary>
public abstract record VirtualTypeSource : TypeSource<VirtualTypeSource>
{
    /// <summary>Initializes the base virtual type source for the given workspace and entity type.</summary>
    /// <param name="workspace">The workspace this type source belongs to.</param>
    /// <param name="type">The CLR entity type produced by this source.</param>
    protected VirtualTypeSource(IWorkspace workspace, Type type) : base(workspace, type)
    {
    }

    /// <summary>Returns an observable that emits the current set of instances whenever the underlying stream changes.</summary>
    /// <returns>An observable of the type's instances as untyped objects.</returns>
    public abstract IObservable<IEnumerable<object>> GetStreamUpdates();

    /// <summary>
    /// Drops the cached provider chain so the NEXT <see cref="GetStreamUpdates"/> rebuilds it from
    /// the configured provider.
    ///
    /// <para>🚨 <b>Without this a retry is inert — issue #3155.</b> The cached chain is
    /// <c>Replay(1).RefCount()</c>, and a <c>ReplaySubject</c> that has seen <c>OnError</c> LATCHES
    /// it: every later subscriber gets the same fault replayed immediately, for the lifetime of the
    /// object. So re-subscribing to a faulted provider is recovery inside the failed component —
    /// it would spin at whatever rate the retry allows and never recover. The corpse has to be
    /// dropped first.</para>
    ///
    /// <para>Virtual by design rather than abstract: a source with no cache has nothing to evict,
    /// and this is public surface on a shipped contract.</para>
    /// </summary>
    public virtual void EvictCachedStream() { }
}

/// <summary>
/// Type source for virtual data that is computed from a stream
/// </summary>
public record VirtualTypeSource<T>(
    IWorkspace Workspace,
    object DataSourceId,
    Func<IWorkspace, IObservable<IEnumerable<T>>> StreamProvider,
    string? CollectionName = null
) : VirtualTypeSource(Workspace, typeof(T)) where T : class
{
    private IObservable<IEnumerable<T>>? cachedStream;

    // Override TypeDefinition to use custom CollectionName if provided
    /// <summary>
    /// The type definition for <typeparamref name="T"/>, resolved against the optional
    /// <c>CollectionName</c> so the virtual collection can be named independently of the type.
    /// </summary>
    public new ITypeDefinition TypeDefinition { get; init; } =
        Workspace.Hub.TypeRegistry.GetTypeDefinition(typeof(T), typeName: CollectionName ?? typeof(T).Name)!;

    /// <summary>
    /// Returns the cached, replayed, distinct stream of instances produced by the configured
    /// stream provider. The first subscriber starts the provider; later subscribers share it.
    /// </summary>
    /// <returns>A hot, replay-1 observable of the typed instances.</returns>
    public IObservable<IEnumerable<T>> StreamUpdates()
    {
        return cachedStream ??= StreamProvider(Workspace)
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();
    }

    /// <summary>Returns <see cref="StreamUpdates"/> projected to untyped objects for the base contract.</summary>
    /// <returns>An observable of the type's instances as untyped objects.</returns>
    public override IObservable<IEnumerable<object>> GetStreamUpdates()
    {
        return StreamUpdates().Select(items => items.Cast<object>());
    }

    /// <inheritdoc />
    public override void EvictCachedStream() => cachedStream = null;

    /// <summary>
    /// Pure observable composition over the type's stream provider — no <c>await</c>,
    /// no <c>.ToTask</c>. The framework consumer subscribes; the gate opens on
    /// emission. See <c>Doc/Architecture/AsynchronousCalls.md</c> + the
    /// "Initialization gates" section.
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => StreamUpdates()
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(30))
        .Select(items => new InstanceCollection(items.Cast<object>(), TypeDefinition.GetKey));
}
