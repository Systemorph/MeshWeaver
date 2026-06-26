using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

/// <summary>
/// Describes a source of entities of a single type within a data source: its type definition,
/// collection name, initial-data loading, update handling, and access restrictions.
/// </summary>
public interface ITypeSource
{
    /// <summary>
    /// The type definition (collection name, key function, serialization metadata) for this source.
    /// </summary>
    ITypeDefinition TypeDefinition { get; }
    // Reactive only — initial data loads as IObservable, never Task. A genuine I/O source
    // bridges its leaf reactively (e.g. through IIoPool at the leaf); in-memory data uses
    // Observable.Return.
    /// <summary>
    /// Configures the initial data loader, which receives the collection reference being
    /// initialized and returns a reactive sequence of instances.
    /// </summary>
    /// <param name="loadInstances">Factory mapping the collection reference to an observable sequence of instances.</param>
    /// <returns>An updated type source.</returns>
    ITypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > loadInstances
    );
    /// <summary>
    /// Configures the initial data loader from a reactive source that ignores the collection reference.
    /// </summary>
    /// <param name="loadInstances">Factory returning an observable sequence of instances.</param>
    /// <returns>An updated type source.</returns>
    ITypeSource WithInitialData(
        Func<IObservable<IEnumerable<object>>> loadInstances
    ) => WithInitialData(_ => loadInstances());

    /// <summary>
    /// Configures the initial data from a fixed collection of instances.
    /// </summary>
    /// <param name="instances">The instances to seed the type source with.</param>
    /// <returns>An updated type source.</returns>
    ITypeSource WithInitialData(IEnumerable<object> instances) => WithInitialData(() => instances);
    /// <summary>
    /// Configures the initial data from a factory that produces the instances synchronously.
    /// </summary>
    /// <param name="loadInstances">Factory returning the instances to seed the type source with.</param>
    /// <returns>An updated type source.</returns>
    ITypeSource WithInitialData(Func<IEnumerable<object>> loadInstances) =>
        WithInitialData(_ => Observable.Return(loadInstances()));

    /// <summary>
    /// Loads the initial <see cref="InstanceCollection"/> for this type source, reactively.
    /// Implementations compose any persistence / remote / static data sources as
    /// <see cref="IObservable{T}"/> and emit exactly one <see cref="InstanceCollection"/>.
    /// <b>Never <c>await</c></b> a hub round-trip inside an implementation — that captures
    /// the calling scheduler and deadlocks the hub action block. See
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    internal IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Applies a store change to this type's collection and returns the resulting collection.
    /// </summary>
    /// <param name="changeItem">The store change to apply.</param>
    /// <returns>The updated instance collection for this type.</returns>
    InstanceCollection Update(ChangeItem<EntityStore> changeItem);

    /// <summary>
    /// The collection name under which instances of this type are stored.
    /// </summary>
    string CollectionName { get; }

    /// <summary>
    /// Access restrictions specific to this type.
    /// Evaluated after global restrictions.
    /// </summary>
    ImmutableList<AccessRestrictionEntry> AccessRestrictions { get; }
}

/// <summary>
/// A type source whose instances are assigned to partitions by a key derived from each instance.
/// </summary>
public interface IPartitionedTypeSource : ITypeSource
{
    /// <summary>
    /// Returns the partition key for the given instance.
    /// </summary>
    /// <param name="instance">The entity instance to partition.</param>
    /// <returns>The partition key.</returns>
    object GetPartition(object instance);
}
