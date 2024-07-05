using System.Security.Cryptography;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data;

public interface ITypeSource : IDisposable
{
    Type ElementType { get; }
    string DisplayName { get; }
    string CollectionName { get; }
    object GetKey(object instance);
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
    InstanceCollection Update(ChangeItem<EntityStore> workspace);
}

public interface IPartitionedTypeSource : ITypeSource
{
    object GetPartition(object instance);
}
