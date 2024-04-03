namespace OpenSmc.Data;

public interface ITypeSource : IDisposable
{
    Type ElementType { get; }
    string CollectionName { get; }
    object GetKey(object instance);
    ITypeSource WithKey(Func<object, object> key);
    ITypeSource WithInitialData(Func<CancellationToken, Task<IEnumerable<object>>> loadInstancesAsync);

    ITypeSource WithInitialData(IEnumerable<object> instances)
        => WithInitialData(() => instances);
    ITypeSource WithInitialData(Func<IEnumerable<object>> loadInstances)
        => WithInitialData(_ => Task.FromResult(loadInstances()));

    Task<InstanceCollection> InitializeAsync(CancellationToken cancellationToken);

    InstanceCollection Update(WorkspaceState ws);
}

public interface IPartitionedTypeSource : ITypeSource
{
    object GetPartition(object instance);
}
