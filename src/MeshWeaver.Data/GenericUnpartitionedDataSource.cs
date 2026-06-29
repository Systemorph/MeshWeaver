using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Pure-reflection replacement for the deleted <c>DelegateCache.InvokeAsFunction</c>.
/// Instantiates an open generic instance method and invokes it on <c>target</c>.
/// <para>No compiled-delegate cache: the old <c>DelegateCache</c> kept a process-wide static
/// <c>CreatableObjectStore&lt;Token, Delegate&gt;</c> whose <c>Token</c> held the generic type
/// argument — pinning a dynamically-compiled NodeType's <c>AssemblyLoadContext</c> for the
/// whole process. These two call sites run once per data-source type registration (not hot),
/// so a direct <see cref="MethodBase.Invoke(object, object[])"/> is the right trade.</para>
/// <para>The <see cref="System.Reflection.TargetInvocationException"/> is unwrapped so callers
/// observe the original exception, exactly as the old compiled delegate did.</para>
/// </summary>
internal static class GenericMethodInvoker
{
    public static object InvokeGeneric(MethodInfo openGenericMethod, Type typeArgument, object target, params object?[] args)
    {
        try
        {
            return openGenericMethod.MakeGenericMethod(typeArgument).Invoke(target, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — Throw() always throws
        }
    }
}

/// <summary>
/// Helpers for constructing the address that identifies a data-source synchronization stream.
/// </summary>
public static class DataSourceAddress
{
    /// <summary>
    /// The address-type discriminator (<c>"ds"</c>) for data-source stream addresses.
    /// </summary>
    public const string TypeName = "ds";
    /// <summary>
    /// Builds a data-source address for the given data-source id.
    /// </summary>
    /// <param name="id">The data-source identifier to embed in the address.</param>
    /// <returns>An address of type <c>"ds"</c> targeting the data source.</returns>
    public static Address Create(string id) => new(TypeName, id);
}

/// <summary>
/// A source of data for the workspace: maps CLR types to collections and exposes synchronization
/// streams over their backing <see cref="EntityStore"/>.
/// </summary>
public interface IDataSource : IDisposable
{
    /// <summary>
    /// Returns the type source registered for the given CLR type, or <c>null</c> if none.
    /// </summary>
    /// <param name="type">The mapped CLR type.</param>
    /// <returns>The matching type source, or <c>null</c> when the type is not mapped.</returns>
    ITypeSource? GetTypeSource(Type type);
    /// <summary>
    /// Returns the type source whose collection has the given name, or <c>null</c>.
    /// </summary>
    /// <param name="collectionName">The collection name to look up.</param>
    /// <returns>The matching type source, or <c>null</c> when no collection matches.</returns>
    ITypeSource? GetTypeSource(string collectionName);
    /// <summary>
    /// The CLR types this data source maps to collections.
    /// </summary>
    IReadOnlyCollection<Type> MappedTypes { get; }
    /// <summary>
    /// The identifier of this data source.
    /// </summary>
    object Id { get; }
    /// <summary>
    /// The reference describing the collections this data source exposes.
    /// </summary>
    CollectionsReference Reference { get; }

    /// <summary>
    /// Gets (creating if needed) the synchronization stream reduced to the given reference.
    /// </summary>
    /// <param name="reference">The workspace reference selecting the slice of the store to stream.</param>
    /// <returns>The synchronization stream for the referenced data.</returns>
    ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference);

    /// <summary>
    /// Gets (creating if needed) the synchronization stream for the given partition.
    /// </summary>
    /// <param name="partition">The partition key, or <c>null</c> for the unpartitioned stream.</param>
    /// <returns>The partition's synchronization stream, or <c>null</c> if it cannot be created.</returns>
    ISynchronizationStream<EntityStore>? GetStreamForPartition(object? partition);
    /// <summary>
    /// All type sources registered on this data source.
    /// </summary>
    IEnumerable<ITypeSource> TypeSources { get; }

    internal Task Initialized { get; }
    internal void Initialize();
}

/// <summary>
/// A data source that exposes a single, unpartitioned synchronization stream.
/// </summary>
public interface IUnpartitionedDataSource : IDataSource
{
    /// <summary>
    /// Registers a CLR type with this data source, optionally configuring its type source.
    /// </summary>
    /// <param name="type">The CLR type to map.</param>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource>? config = null);
    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/>, optionally configuring its type source.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    IUnpartitionedDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config = null) where T : class;
}
/// <summary>
/// A data source whose data is split across partitions keyed by <typeparamref name="TPartition"/>.
/// </summary>
/// <typeparam name="TPartition">The partition-key type.</typeparam>
public interface IPartitionedDataSource<in TPartition> : IDataSource
{
    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/>, deriving each instance's partition via
    /// <paramref name="partitionFunction"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="partitionFunction">Maps an instance to its partition key.</param>
    /// <param name="config">Optional configuration applied to the partitioned type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    IPartitionedDataSource<TPartition> WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config = null) where T : class;
}


/// <summary>
/// Base record for partitioned data sources, keyed by <typeparamref name="TPartition"/> and using
/// type sources of <typeparamref name="TTypeSource"/>.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TTypeSource">The partitioned type-source type used by this data source.</typeparam>
/// <typeparam name="TPartition">The partition-key type.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record PartitionedDataSource<TDataSource, TTypeSource, TPartition>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IPartitionedDataSource<TPartition>
    where TDataSource : PartitionedDataSource<TDataSource, TTypeSource, TPartition>
    where TTypeSource : IPartitionedTypeSource
{

    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/>, deriving its partition via
    /// <paramref name="partitionFunction"/> and configuring the type source with <paramref name="config"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="partitionFunction">Maps an instance to its partition key.</param>
    /// <param name="config">Configuration applied to the type source.</param>
    /// <returns>The data source with the type registered.</returns>
    public abstract TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<TTypeSource, TTypeSource> config)
        where T : class;
    IPartitionedDataSource<TPartition> IPartitionedDataSource<TPartition>.WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config) =>
        WithType(partitionFunction, ts => (TTypeSource)(config ?? (x => x)).Invoke(ts));


}

/// <summary>
/// Base record for unpartitioned data sources, using type sources of <typeparamref name="TTypeSource"/>.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TTypeSource">The type-source type used by this data source.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record UnpartitionedDataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IUnpartitionedDataSource
    where TDataSource : UnpartitionedDataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    /// <summary>
    /// Registers a CLR type, optionally configuring its type source; dispatches to the generic overload.
    /// </summary>
    /// <param name="type">The CLR type to map.</param>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public virtual IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource>? config) =>
        (TDataSource)GenericMethodInvoker.InvokeGeneric(
            WithTypeMethod, type, this, config ?? (Func<ITypeSource, ITypeSource>)(x => x));

    private static readonly MethodInfo WithTypeMethod = ReflectionHelper.GetMethodGeneric<
        UnpartitionedDataSource<TDataSource, TTypeSource>
    >(x => x.WithType<object>(default));

    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/> with default type-source configuration.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <returns>The data source with the type registered.</returns>
    public TDataSource WithType<T>()
        where T : class => WithType<T>(d => d);
    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/>, optionally configuring its type source.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config)
        where T : class;
    IUnpartitionedDataSource IUnpartitionedDataSource.WithType<T>(Func<ITypeSource, ITypeSource>? config) =>
        WithType<T>(config ?? (x => x));

    /// <summary>
    /// Registers all of the given CLR types with default type-source configuration.
    /// </summary>
    /// <param name="types">The CLR types to map.</param>
    /// <returns>The data source with all the types registered.</returns>
    public IUnpartitionedDataSource WithTypes(IEnumerable<Type> types) =>
        types.Aggregate((IUnpartitionedDataSource)This, (ds, t) => ds.WithType(t, x => x));

}


/// <summary>
/// Abstract base for all data sources: owns the type-source registry, the per-partition
/// synchronization streams, and the reduce/synchronize plumbing over the backing
/// <see cref="EntityStore"/>.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TTypeSource">The type-source type used by this data source.</typeparam>
/// <param name="Id">The data-source identifier (basis of the stream address).</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record DataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace) : IDataSource
    where TDataSource : DataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    /// <summary>
    /// This data source typed as the concrete <typeparamref name="TDataSource"/>, for fluent <c>with</c> returns.
    /// </summary>
    protected virtual TDataSource This => (TDataSource)this;
    /// <summary>
    /// The message hub of the owning workspace.
    /// </summary>
    protected IMessageHub Hub => Workspace.Hub;
    /// <summary>
    /// A logger scoped to the concrete data-source type.
    /// </summary>
    protected ILogger Logger => Workspace.Hub.ServiceProvider.GetRequiredService<ILogger<TDataSource>>();

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values.Cast<ITypeSource>();

    /// <summary>
    /// The registered type sources, keyed by their mapped CLR type.
    /// </summary>
    protected ImmutableDictionary<Type, TTypeSource> TypeSources { get; init; } =
        ImmutableDictionary<Type, TTypeSource>.Empty;

    /// <summary>
    /// Returns a copy of this data source with the given type source registered for the type.
    /// </summary>
    /// <param name="type">The CLR type to map.</param>
    /// <param name="typeSource">The type source to register.</param>
    /// <returns>The data source with the type source registered.</returns>
    public TDataSource WithTypeSource(Type type, TTypeSource typeSource) =>
        This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };

    /// <summary>
    /// The CLR types mapped by this data source.
    /// </summary>
    public IReadOnlyCollection<Type> MappedTypes => TypeSources.Keys.ToArray();

    /// <summary>
    /// Returns the type source whose collection matches the given name, or <c>null</c>.
    /// </summary>
    /// <param name="collectionName">The collection name to look up.</param>
    /// <returns>The matching type source, or <c>null</c>.</returns>
    public ITypeSource? GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);

    /// <summary>
    /// Returns the type source registered for the given type, or <c>null</c> if none.
    /// </summary>
    /// <param name="type">The mapped CLR type.</param>
    /// <returns>The matching type source, or <c>null</c>.</returns>
    public ITypeSource? GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    private readonly IReadOnlyCollection<IDisposable>? changesSubscriptions;



    /// <summary>
    /// The synchronization streams created so far, keyed by partition (or the data-source id for the
    /// unpartitioned stream).
    /// </summary>
    protected readonly Dictionary<object, ISynchronizationStream<EntityStore>> Streams = new();

    /// <summary>
    /// A task that completes once every created stream's hub has started.
    /// </summary>
    public Task Initialized
    {
        get
        {
            lock (Streams)
                return Task.WhenAll(Streams.Values.Select(s => s.Hub.Started));
        }
    }
    /// <summary>
    /// The reference describing the collections exposed by this data source.
    /// </summary>
    public CollectionsReference Reference => GetReference();

    /// <summary>
    /// Builds the collections reference from the registered type sources' collection names.
    /// </summary>
    /// <returns>A reference over all mapped collections.</returns>
    protected virtual CollectionsReference GetReference() =>
        new(TypeSources.Values.Select(ts => ts.CollectionName).ToArray());

    /// <summary>
    /// Disposes all created streams and any change subscriptions.
    /// </summary>
    public virtual void Dispose()
    {
        ISynchronizationStream<EntityStore>[] streamsToDispose;
        lock (Streams)
        {
            streamsToDispose = Streams.Values.ToArray();
        }

        foreach (var stream in streamsToDispose)
            stream.Dispose();

        if (changesSubscriptions != null)
            foreach (var subscription in changesSubscriptions)
                subscription.Dispose();
    }
    /// <summary>
    /// Gets (creating if needed) the synchronization stream reduced to the given reference,
    /// selecting the partition stream when the reference is partitioned.
    /// </summary>
    /// <param name="reference">The workspace reference selecting the slice to stream.</param>
    /// <returns>The reduced synchronization stream.</returns>
    public virtual ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference)
    {
        var stream = GetStreamForPartition(reference is IPartitionedWorkspaceReference partitioned ? partitioned.Partition : null);
        return stream.Reduce(reference) ?? throw new InvalidOperationException("Unable to create stream");
    }

    /// <summary>
    /// Gets (creating and caching if needed) the synchronization stream for the given partition.
    /// </summary>
    /// <param name="partition">The partition key, or <c>null</c> for the unpartitioned stream.</param>
    /// <returns>The partition's synchronization stream.</returns>
    public ISynchronizationStream<EntityStore> GetStreamForPartition(object? partition)
    {
        var identity = new StreamIdentity(DataSourceAddress.Create(Id.ToString() ?? ""), partition);
        lock (Streams)
        {
            if (Streams.TryGetValue(partition ?? Id, out var ret))
                return ret;
            Logger.LogDebug("Creating new stream for Id {Id} and Partition {Partition}", Id, partition);
            Streams[partition ?? Id] = ret = CreateStream(identity);
            return ret;
        }
    }

    /// <summary>
    /// Creates the synchronization stream for the given stream identity.
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <returns>The newly created synchronization stream.</returns>
    protected abstract ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity);

    /// <summary>
    /// Creates the synchronization stream for the given identity, applying the supplied configuration.
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <param name="config">Additional stream configuration to apply.</param>
    /// <returns>The newly created synchronization stream.</returns>
    protected virtual ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
        => SetupDataSourceStream(identity, config);

    /// <summary>
    /// Creates and configures the data source's infrastructure <see cref="EntityStore"/> mirror stream.
    /// The stream is marked infrastructure so a context-less update stamps the System identity rather
    /// than failing closed (see the inline note).
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <param name="config">Additional stream configuration to apply.</param>
    /// <returns>The configured synchronization stream.</returns>
    protected virtual ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var reference = GetReference();

        // 🚨 The data-source EntityStore stream is a genuine INFRASTRUCTURE mirror of the data
        // source's collections (its Owner is ds/<Id>). Changes reach it AFTER being authorized at
        // the user-facing write, and it holds MANY nodes (different owners), so a deferred/cross-hub
        // propagation whose live AccessContext is gone has no single owner to attribute to. Mark it
        // infrastructure so such a context-less Update stamps System rather than posting null-context
        // and being failed closed by the never-null PostPipeline guard — which would terminally fault
        // this stream's Store and poison every future subscriber (the ds/Activity blank-side-panel
        // bug). Same rule as DataSourceWithStorage persistence + VirtualDataSource. REDUCED streams
        // built off this one (the user-facing per-node / per-collection views) are NOT marked — they
        // keep their own creation-context capture and fail closed on a genuine no-identity write.
        var stream = new SynchronizationStream<EntityStore>(
            identity,
            Hub,
            reference,
            Workspace.ReduceManager.ReduceTo<EntityStore>(),
            c => (config?.Invoke(c) ?? c).AsInfrastructure()
       );

        return stream;
    }

    /// <summary>
    /// Initializes the data source. The base implementation does nothing; overrides create the initial stream(s).
    /// </summary>
    public virtual void Initialize()
    {
    }
}

/// <summary>
/// A concrete, non-generic unpartitioned data source over arbitrary type sources.
/// </summary>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public record GenericUnpartitionedDataSource(object Id, IWorkspace Workspace)
    : GenericUnpartitionedDataSource<GenericUnpartitionedDataSource>(Id, Workspace)
{
    /// <summary>
    /// Gets (creating if needed) this data source's single unpartitioned synchronization stream.
    /// </summary>
    /// <returns>The unpartitioned synchronization stream.</returns>
    public ISynchronizationStream<EntityStore> GetStream()
        => GetStreamForPartition(null);
}

/// <summary>
/// Generic base for unpartitioned data sources backed by <see cref="ITypeSource"/> type sources,
/// adding reflection-based <c>WithType</c> overloads.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record GenericUnpartitionedDataSource<TDataSource>(object Id, IWorkspace Workspace)
    : TypeSourceBasedUnpartitionedDataSource<TDataSource, ITypeSource>(Id, Workspace)
    where TDataSource : GenericUnpartitionedDataSource<TDataSource>
{

    private static readonly MethodInfo WithTypeGeneric = ReflectionHelper.GetMethodGeneric<
        GenericUnpartitionedDataSource<TDataSource>>(x => x.WithType<object>((Func<ITypeSource, ITypeSource>?)null));
    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/> with an optional type-source configuration.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public override TDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config) =>
        WithType<T>(x => (TypeSourceWithType<T>)(config ?? (y => y))(x));
    /// <summary>
    /// Registers a CLR type with an optional type-source configuration; dispatches to the generic overload.
    /// </summary>
    /// <param name="type">The CLR type to map.</param>
    /// <param name="config">Optional configuration applied to the type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public override TDataSource WithType(Type type, Func<ITypeSource, ITypeSource>? config = null) =>
        (TDataSource)GenericMethodInvoker.InvokeGeneric(
            WithTypeGeneric, type, this, config ?? (Func<ITypeSource, ITypeSource>)(x => x));

    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/> using a strongly-typed type-source configurator.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="configurator">Optional configurator for the typed type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public TDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>>? configurator)
        where T : class => WithTypeSource(typeof(T), (configurator ?? (x => x)).Invoke(new(Workspace, Id)));
}
/// <summary>
/// A partitioned data source keyed by <typeparamref name="TPartition"/> over partitioned type sources.
/// </summary>
/// <typeparam name="TPartition">The partition-key type.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record GenericPartitionedDataSource<TPartition>(object Id, IWorkspace Workspace)
    : GenericPartitionedDataSource<GenericPartitionedDataSource<TPartition>, TPartition>(Id, Workspace)
{
    /// <summary>
    /// Gets (creating if needed) the synchronization stream for the default (null) partition.
    /// </summary>
    /// <returns>The synchronization stream for the default partition.</returns>
    public ISynchronizationStream<EntityStore> GetStream()
        => GetStreamForPartition(null);

}

/// <summary>
/// Generic base for partitioned data sources backed by <see cref="IPartitionedTypeSource"/> type sources.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TPartition">The partition-key type.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record GenericPartitionedDataSource<TDataSource, TPartition>(object Id, IWorkspace Workspace)
    : TypeSourceBasedPartitionedDataSource<TDataSource, IPartitionedTypeSource, TPartition>(Id, Workspace)
    where TDataSource : GenericPartitionedDataSource<TDataSource, TPartition>
{
    /// <summary>
    /// Registers the CLR type <typeparamref name="T"/>, deriving each instance's partition via
    /// <paramref name="partitionFunction"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="partitionFunction">Maps an instance to its partition key.</param>
    /// <param name="config">Optional configuration applied to the partitioned type source; identity if <c>null</c>.</param>
    /// <returns>The data source with the type registered.</returns>
    public override TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config)
        => WithTypeSource(typeof(T), (config ?? (x => x)).Invoke(new PartitionedTypeSourceWithType<T, TPartition>(Workspace, partitionFunction, Id)));
}

/// <summary>
/// Unpartitioned data source whose initial store and synchronization are driven by its type sources:
/// it builds the initial <see cref="EntityStore"/> from each type source and pushes incoming changes back into them.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TTypeSource">The type-source type used by this data source.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record TypeSourceBasedUnpartitionedDataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace)
    : UnpartitionedDataSource<TDataSource, TTypeSource>(Id, Workspace)
    where TDataSource : TypeSourceBasedUnpartitionedDataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    /// <summary>
    /// Initializes the data source and eagerly creates the stream for its full reference.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        GetStream(GetReference());
    }


    /// <summary>
    /// Pushes a change item into every registered type source.
    /// </summary>
    /// <param name="item">The change to apply to the type sources.</param>
    protected virtual void Synchronize(ChangeItem<EntityStore> item)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Update(item);
    }

    /// <summary>
    /// Builds the initial <see cref="EntityStore"/> by composing each type source's
    /// reactive <see cref="ITypeSource.Initialize"/> emission via <c>.Aggregate</c>.
    /// <para>
    /// Reactive end-to-end: no <c>await</c> on a per-type-source step (that bridge
    /// is what deadlocks the hub action block when a type source touches a hub —
    /// see <c>Doc/Architecture/InitializationGates.md</c>). The single
    /// <c>.FirstAsync().ToTask(ct)</c> at the bottom is the framework-edge bridge
    /// because <see cref="StreamConfiguration{T}.WithInitialization(System.Func{ISynchronizationStream{T}, System.Threading.CancellationToken, System.Threading.Tasks.Task{T}})"/> consumes a
    /// <c>Func&lt;…, Task&lt;TStream&gt;&gt;</c>; that bridge is sanctioned per
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    protected virtual Task<EntityStore> GetInitialValueAsync(ISynchronizationStream<EntityStore> stream, CancellationToken cancellationToken)
    {
        var emptyStore = new EntityStore()
        {
            GetCollectionName = valueType => Workspace.DataContext.TypeRegistry.GetOrAddType(valueType, valueType.Name)
        };

        return TypeSources.Values
            .ToObservable()
            .SelectMany(ts =>
            {
                WorkspaceReference<InstanceCollection> reference =
                    stream.StreamIdentity.Partition == null
                        ? new CollectionReference(ts.CollectionName)
                        : new PartitionedWorkspaceReference<InstanceCollection>(
                            stream.StreamIdentity.Partition,
                            new CollectionReference(ts.CollectionName)
                        );
                return ts.Initialize(reference, cancellationToken)
                    .Take(1)
                    .Select(instances => (Reference: reference, Instances: instances));
            })
            .Aggregate(emptyStore, (acc, item) => acc.Update(item.Reference, item.Instances))
            .FirstAsync()
            .ToTask(cancellationToken);
    }


    /// <summary>
    /// Creates the stream wired with type-source-driven initialization and exception logging.
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <returns>The configured synchronization stream.</returns>
    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
    {
        return CreateStream(identity,
            config => config.WithInitialization(GetInitialValueAsync).WithExceptionCallback(LogException));
    }

    private void LogException(Exception exception)
    {
        Logger.LogError("An exception occurred synchronizing Data Source {Identity}: {Exception}", this.Id, exception);
    }

    /// <summary>
    /// Configures the data-source stream and subscribes a resilient handler that feeds external changes
    /// back into the type sources (skipping the initial emission and the source's own writes).
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <param name="config">Additional stream configuration to apply.</param>
    /// <returns>The configured synchronization stream.</returns>
    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var stream = base.SetupDataSourceStream(identity, config);

        var isFirst = true;
        stream.RegisterForDisposal(
            stream
                .Synchronize()
                .Where(x => isFirst || (x.ChangedBy is not null && !x.ChangedBy.Equals(Id)))
                .Subscribe(change =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        return; // Skip processing on first emission (initialization)
                    }
                    // A single bad change must NOT kill the data-source sync
                    // subscription — if it does, every query/catalog fed by this
                    // source goes stale and query-based work parks forever (the
                    // data-layer "observer dies" deadlock behind CI query flakes).
                    // Log and survive; the next change still flows.
                    try { Synchronize(change); }
                    catch (Exception syncEx)
                    {
                        Logger.LogWarning(syncEx,
                            "Data source stream {Id}: Synchronize threw for a change — skipped; stream stays alive", Id);
                    }
                },
                ex => Logger.LogWarning(ex, "Data source stream {Id} errored", Id))
        );
        // Always use async initialization to call GetInitialValueAsync properly

        return stream;
    }
}
/// <summary>
/// Partitioned counterpart of the type-source-driven data source: builds the initial
/// <see cref="EntityStore"/> from its partitioned type sources and feeds changes back into them.
/// </summary>
/// <typeparam name="TDataSource">The concrete data-source record type (self type).</typeparam>
/// <typeparam name="TTypeSource">The partitioned type-source type used by this data source.</typeparam>
/// <typeparam name="TPartition">The partition-key type.</typeparam>
/// <param name="Id">The data-source identifier.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public abstract record TypeSourceBasedPartitionedDataSource<TDataSource, TTypeSource, TPartition>(object Id, IWorkspace Workspace)
    : PartitionedDataSource<TDataSource, TTypeSource, TPartition>(Id, Workspace)
    where TDataSource : TypeSourceBasedPartitionedDataSource<TDataSource, TTypeSource, TPartition>
    where TTypeSource : IPartitionedTypeSource
{
    /// <summary>
    /// Initializes the data source and eagerly creates the stream for its full reference.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        GetStream(GetReference());
    }


    /// <summary>
    /// Pushes a change item into every registered partitioned type source.
    /// </summary>
    /// <param name="item">The change to apply to the type sources.</param>
    protected virtual void Synchronize(ChangeItem<EntityStore> item)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Update(item);
    }

    /// <summary>
    /// Reactive equivalent of the unpartitioned <c>GetInitialValueAsync</c> for the
    /// partitioned data source. Same pattern: per-type-source observable composition
    /// via <c>SelectMany</c> + <c>Aggregate</c>, single bridge to Task at the
    /// framework edge for <c>WithInitialization</c>.
    /// </summary>
    protected virtual Task<EntityStore>
        GetInitialValue(ISynchronizationStream<EntityStore> stream,
            CancellationToken cancellationToken)
    {
        var emptyStore = new EntityStore()
        {
            GetCollectionName = valueType => Workspace.DataContext.TypeRegistry.GetOrAddType(valueType, valueType.Name)
        };

        return TypeSources.Values
            .ToObservable()
            .SelectMany(ts =>
            {
                WorkspaceReference<InstanceCollection> reference =
                    stream.StreamIdentity.Partition == null
                        ? new CollectionReference(ts.CollectionName)
                        : new PartitionedWorkspaceReference<InstanceCollection>(
                            stream.StreamIdentity.Partition,
                            new CollectionReference(ts.CollectionName)
                        );
                return ts.Initialize(reference, cancellationToken)
                    .Take(1)
                    .Select(instances => (Reference: reference, Instances: instances));
            })
            .Aggregate(emptyStore, (acc, item) => acc.Update(item.Reference, item.Instances))
            .FirstAsync()
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Creates the partitioned data-source stream with the supplied configuration.
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <param name="config">Additional stream configuration to apply.</param>
    /// <returns>The configured synchronization stream.</returns>
    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        return SetupDataSourceStream(identity, config);
    }

    /// <summary>
    /// Configures the partitioned data-source stream and subscribes a resilient handler that feeds external
    /// changes back into the type sources (skipping the initial emission and the source's own writes).
    /// </summary>
    /// <param name="identity">The identity (address + partition) of the stream to create.</param>
    /// <param name="config">Additional stream configuration to apply.</param>
    /// <returns>The configured synchronization stream.</returns>
    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var stream = base.SetupDataSourceStream(identity, config);

        // Always use async initialization to call GetInitialValueAsync properly

        var isFirst = true;
        stream.RegisterForDisposal(
            stream
                .Synchronize()
                .Where(x => isFirst || (x.ChangedBy is not null && !x.ChangedBy.Equals(Id)))
                .Subscribe(change =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        return; // Skip processing on first emission (initialization)
                    }
                    // A single bad change must NOT kill the data-source sync
                    // subscription — if it does, every query/catalog fed by this
                    // source goes stale and query-based work parks forever (the
                    // data-layer "observer dies" deadlock behind CI query flakes).
                    // Log and survive; the next change still flows.
                    try { Synchronize(change); }
                    catch (Exception syncEx)
                    {
                        Logger.LogWarning(syncEx,
                            "Data source stream {Id}: Synchronize threw for a change — skipped; stream stays alive", Id);
                    }
                },
                ex => Logger.LogWarning(ex, "Data source stream {Id} errored", Id))
        );
        return stream;
    }
}
