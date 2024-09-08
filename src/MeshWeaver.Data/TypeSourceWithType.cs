using MeshWeaver.Domain;

namespace MeshWeaver.Data;

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

public abstract record TypeSourceWithType<T, TTypeSource>(IWorkspace Workspace, object DataSource)
    : TypeSource<TTypeSource>(Workspace, DataSource, typeof(T))
    where TTypeSource : TypeSourceWithType<T, TTypeSource>
{
    public TTypeSource WithQuery(Func<string, T> query) => This with { QueryFunction = query };

    protected Func<string, T> QueryFunction { get; init; }

    public TTypeSource WithKey<TProp>(Func<T, TProp> keyFunc)
        => WithKey(new KeyFunction(o => keyFunc.Invoke((T)o), typeof(TProp)));

}
