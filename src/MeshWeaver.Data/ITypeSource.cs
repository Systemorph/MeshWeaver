using System.Security.Cryptography;
using MeshWeaver.Data.Serialization;
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

    internal Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    );
    InstanceCollection Update(ChangeItem<EntityStore> changeItem);

    string CollectionName { get; }
}

public interface IPartitionedTypeSource : ITypeSource
{
    object GetPartition(object instance);
}
