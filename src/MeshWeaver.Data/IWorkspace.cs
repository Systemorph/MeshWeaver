using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// The in-memory data workspace for a message hub: it owns the configured data sources,
/// exposes their content as reactive synchronization streams, and serves as the single
/// surface for reading and mutating entities mapped to the hub's address.
/// </summary>
public interface IWorkspace : IAsyncDisposable
{
    /// <summary>The message hub that owns and serializes access to this workspace.</summary>
    IMessageHub Hub { get; }
    /// <summary>The data context holding the configured data sources, type sources and reduce manager.</summary>
    DataContext DataContext { get; }
    /// <summary>The CLR types mapped to a data source in this workspace.</summary>
    IReadOnlyCollection<Type> MappedTypes { get; }
    /// <summary>Creates or updates the given instances using default update options.</summary>
    /// <param name="instances">The entities to upsert.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">The originating message delivery, used for response correlation.</param>
    void Update(IReadOnlyCollection<object> instances, Activity? activity, IMessageDelivery request) => Update(instances, new(), activity, request);
    /// <summary>Creates or updates the given instances using the supplied update options.</summary>
    /// <param name="instances">The entities to upsert.</param>
    /// <param name="updateOptions">Options controlling merge vs. snapshot semantics.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">The originating message delivery, used for response correlation.</param>
    void Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions, Activity? activity, IMessageDelivery request);
    /// <summary>Creates or updates a single instance using default update options.</summary>
    /// <param name="instance">The entity to upsert.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">The originating message delivery, used for response correlation.</param>
    void Update(object instance, Activity? activity, IMessageDelivery request) => Update([instance], activity, request);

    /// <summary>Deletes the given instances from their mapped collections.</summary>
    /// <param name="instances">The entities to delete.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">The originating message delivery, used for response correlation.</param>
    void Delete(IReadOnlyCollection<object> instances, Activity? activity, IMessageDelivery request);
    /// <summary>Deletes a single instance from its mapped collection.</summary>
    /// <param name="instance">The entity to delete.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">The originating message delivery, used for response correlation.</param>
    void Delete(object instance, Activity? activity, IMessageDelivery request) => Delete([instance], activity, request);

    /// <summary>Validates and applies a data change request (creations, updates and deletions) to the workspace streams.</summary>
    /// <param name="change">The change request describing the creations, updates and deletions.</param>
    /// <param name="activity">Optional activity to log the change against; may be null.</param>
    /// <param name="request">Optional originating message delivery, used for response correlation.</param>
    public void RequestChange(DataChangeRequest change, Activity? activity, IMessageDelivery? request);
    /// <summary>Gets a synchronization stream over the collections backing the given CLR types.</summary>
    /// <param name="types">The CLR types whose collections should be combined into the stream.</param>
    /// <returns>A synchronization stream of the combined entity store.</returns>
    ISynchronizationStream<EntityStore> GetStream(params Type[] types);
    /// <summary>The reduce manager that projects the entity store into reduced views and reference streams.</summary>
    ReduceManager<EntityStore> ReduceManager { get; }

    /// <summary>Subscribes to a reduced stream owned by a remote hub.</summary>
    /// <typeparam name="TReduced">The reduced value type produced by the reference.</typeparam>
    /// <param name="owner">The address of the hub that owns the source data.</param>
    /// <param name="reference">The reference describing the reduced view to subscribe to.</param>
    /// <returns>A synchronization stream of the reduced value.</returns>
    ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        Address owner,
        WorkspaceReference<TReduced> reference
    );
    /// <summary>Gets a local reduced stream for the given workspace reference.</summary>
    /// <typeparam name="TReduced">The reduced value type produced by the reference.</typeparam>
    /// <param name="reference">The reference describing the reduced view.</param>
    /// <param name="configuration">Optional configuration of the stream (e.g. null-when-not-present behaviour).</param>
    /// <returns>The reduced synchronization stream, or null if the reference cannot be resolved.</returns>
    ISynchronizationStream<TReduced>? GetStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration = null);

    /// <summary>Subscribes to the collection of a given type owned by a remote hub.</summary>
    /// <typeparam name="TType">The entity type whose remote collection is observed.</typeparam>
    /// <param name="address">The address of the hub that owns the data.</param>
    /// <returns>An observable of the remote collection, or null if it cannot be resolved.</returns>
    IObservable<IEnumerable<TType>>? GetRemoteStream<TType>(Address address);

    /// <summary>Observes the local collection of the given type as an array, or null if the type is not mapped.</summary>
    /// <typeparam name="T">The entity type whose local collection is observed.</typeparam>
    /// <returns>An observable of the collection array, or null if the type is unknown to this workspace.</returns>
    IObservable<T[]?>? GetStream<T>();

    /// <summary>Subscribes to a remote reduced stream using an explicit reference instance.</summary>
    /// <typeparam name="TReduced">The reduced value type produced by the reference.</typeparam>
    /// <typeparam name="TReference">The concrete reference type.</typeparam>
    /// <param name="address">The address of the hub that owns the source data.</param>
    /// <param name="reference">The reference describing the reduced view.</param>
    /// <returns>A synchronization stream of the reduced value.</returns>
    ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        Address address,
        TReference reference
    )
        where TReference : WorkspaceReference;

    /// <summary>
    /// Gets a remote stream with hub impersonation. Used by HubDataSource and
    /// PartitionedHubDataSource for hub-to-hub subscriptions where the subscribing
    /// hub's address should be the identity (not any ambient user context).
    /// </summary>
    ISynchronizationStream<EntityStore> GetRemoteStreamAsHub(
        Address owner,
        WorkspaceReference<EntityStore> reference
    );



    internal void SubscribeToClient(
        IMessageDelivery<SubscribeRequest> request
    );

    /// <summary>Registers a disposable to be disposed when the workspace is disposed.</summary>
    /// <param name="disposable">The disposable whose lifetime is tied to the workspace.</param>
    void AddDisposable(IDisposable disposable);
    /// <summary>Registers an async disposable to be disposed when the workspace is disposed.</summary>
    /// <param name="disposable">The async disposable whose lifetime is tied to the workspace.</param>
    void AddDisposable(IAsyncDisposable disposable);
    /// <summary>Gets the entity-store stream for a specific data-source partition identified by stream identity.</summary>
    /// <param name="kvpKey">The stream identity (owner address plus partition) to resolve.</param>
    /// <returns>The synchronization stream for the partition, or null if no data source owns it.</returns>
    ISynchronizationStream<EntityStore>? GetStream(StreamIdentity kvpKey);
}
