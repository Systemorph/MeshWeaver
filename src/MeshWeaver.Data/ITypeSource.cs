using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

public interface ITypeSource
{
    ITypeDefinition TypeDefinition { get; }
    // Reactive only — initial data loads as IObservable, never Task. A genuine I/O source
    // bridges its leaf reactively (e.g. through IIoPool at the leaf); in-memory data uses
    // Observable.Return.
    ITypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > loadInstances
    );
    ITypeSource WithInitialData(
        Func<IObservable<IEnumerable<object>>> loadInstances
    ) => WithInitialData(_ => loadInstances());

    ITypeSource WithInitialData(IEnumerable<object> instances) => WithInitialData(() => instances);
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

    InstanceCollection Update(ChangeItem<EntityStore> changeItem);

    string CollectionName { get; }

    /// <summary>
    /// Access restrictions specific to this type.
    /// Evaluated after global restrictions.
    /// </summary>
    ImmutableList<AccessRestrictionEntry> AccessRestrictions { get; }
}

public interface IPartitionedTypeSource : ITypeSource
{
    object GetPartition(object instance);
}
