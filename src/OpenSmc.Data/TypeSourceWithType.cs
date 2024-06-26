using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data;

public record PartitionedTypeSourceWithType<T>(
    IWorkspace Workspace,
    Func<T, object> PartitionFunction,
    object DataSource
) : TypeSourceWithType<T>(Workspace, DataSource), IPartitionedTypeSource
{
    public object GetPartition(object instance) => PartitionFunction.Invoke((T)instance);
}

public record TypeSourceWithType<T>(IWorkspace Workspace, object DataSource)
    : TypeSourceWithType<T, TypeSourceWithType<T>>(Workspace, DataSource)
{
    protected override InstanceCollection UpdateImpl(InstanceCollection instances) =>
        UpdateAction.Invoke(instances);

    protected Func<InstanceCollection, InstanceCollection> UpdateAction { get; init; } = i => i;

    public TypeSourceWithType<T> WithUpdate(Func<InstanceCollection, InstanceCollection> update) =>
        This with
        {
            UpdateAction = update
        };

    public TypeSourceWithType<T> WithInitialData(
        Func<CancellationToken, Task<IEnumerable<T>>> initialData
    ) => WithInitialData(async (_, c) => (await initialData.Invoke(c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<T>>
        > initialData
    ) => WithInitialData(async (r, c) => (await initialData.Invoke(r, c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(IEnumerable<T> initialData) =>
        WithInitialData((_, _) => Task.FromResult(initialData.Cast<object>()));
}

public abstract record TypeSourceWithType<T, TTypeSource> : TypeSource<TTypeSource>
    where TTypeSource : TypeSourceWithType<T, TTypeSource>
{
    protected TypeSourceWithType(IWorkspace workspace, object DataSource)
        : base(workspace, typeof(T), DataSource)
    {
        workspace.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithType(typeof(T));
    }

    public TTypeSource WithKey(Func<T, object> key) => This with { Key = o => key.Invoke((T)o) };

    public TTypeSource WithCollectionName(string collectionName) =>
        This with
        {
            CollectionName = collectionName
        };

    public TTypeSource WithQuery(Func<string, T> query) => This with { QueryFunction = query };

    protected Func<string, T> QueryFunction { get; init; }
}
