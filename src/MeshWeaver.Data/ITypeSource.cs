using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

public interface ITypeSource
{
    ITypeDefinition TypeDefinition { get; }
    ITypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<object>>
        > loadInstancesAsync
    );
    ITypeSource WithInitialData(
        Func<CancellationToken, Task<IEnumerable<object>>> loadInstancesAsync
    ) => WithInitialData((_, ct) => loadInstancesAsync(ct));

    ITypeSource WithInitialData(IEnumerable<object> instances) => WithInitialData(() => instances);
    ITypeSource WithInitialData(Func<IEnumerable<object>> loadInstances) =>
        WithInitialData((_, _) => Task.FromResult(loadInstances()));

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
