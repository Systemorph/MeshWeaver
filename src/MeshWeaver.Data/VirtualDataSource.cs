using System.Collections.Immutable;
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
        // DeliveryFailure storm (atioz 2026-06-21: ds/Skill at ~3/sec, OnError-ing the typed
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
                typeSource.GetStreamUpdates()
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
                    })
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
