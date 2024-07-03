using Microsoft.Extensions.DependencyInjection;
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
    private readonly ITypeRegistry typeRegistry;

    protected TypeSourceWithType(IWorkspace workspace, object DataSource)
        : base(workspace, typeof(T), DataSource)
    {
        typeRegistry = workspace.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        typeRegistry.WithType(typeof(T));
    }

    public TTypeSource WithKey<TKey>(Func<T, TKey> key)
    {
        var ret = new KeyFunction(o => key.Invoke((T)o), typeof(TKey));
        typeRegistry.WithKeyFunctionProvider(type =>ret);
        return This with { Key = ret };
    }

    public TTypeSource WithQuery(Func<string, T> query) => This with { QueryFunction = query };

    protected Func<string, T> QueryFunction { get; init; }
}
