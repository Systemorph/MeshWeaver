using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record PartitionedTypeSourceWithType<T>(IMessageHub Hub, Func<T,object> PartitionFunction, object DataSource) : TypeSourceWithType<T>(Hub, DataSource), IPartitionedTypeSource
{
    public object GetPartition(object instance) => PartitionFunction.Invoke((T)instance);
}

public record TypeSourceWithType<T>(IMessageHub Hub, object DataSource) : TypeSourceWithType<T, TypeSourceWithType<T>>(Hub, DataSource)
{
    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
        => UpdateAction.Invoke(instances);

    protected Func<InstanceCollection, InstanceCollection> UpdateAction { get; init; } = i => i;

    public TypeSourceWithType<T> WithUpdate(Func<InstanceCollection, InstanceCollection> update) => This with { UpdateAction = update };


    public TypeSourceWithType<T> WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>> initialData)
        => WithInitialData(async  c=> (await initialData.Invoke(c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(IEnumerable<T> initialData)
        => WithInitialData(_ => Task.FromResult(initialData.Cast<object>()));

}

public abstract record TypeSourceWithType<T, TTypeSource> : TypeSource<TTypeSource>
where TTypeSource: TypeSourceWithType<T, TTypeSource>
{
    protected TypeSourceWithType(IMessageHub hub, object DataSource) : base(hub, typeof(T), DataSource, typeof(T).FullName)
    {
        hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithType(typeof(T));
    }

    public TTypeSource WithKey(Func<T, object> key)
        => This with { Key = o => key.Invoke((T)o) };

    public TTypeSource WithCollectionName(string collectionName) =>
        This with { CollectionName = collectionName };

    


    public TTypeSource WithQuery(Func<string, T> query)
        => This with { QueryFunction = query };

    protected Func<string, T> QueryFunction { get; init; }

}