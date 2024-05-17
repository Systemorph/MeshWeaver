using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record PartitionedHubDataSource(object Id, IMessageHub Hub)
    : HubDataSourceBase<PartitionedHubDataSource>(Id, Hub)
{
    public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction) =>
        WithType(partitionFunction, x => x);

    public PartitionedHubDataSource WithType<T>(
        Func<T, object> partitionFunction,
        Func<ITypeSource, ITypeSource> config
    ) => WithType(partitionFunction, x => (PartitionedTypeSourceWithType<T>)config.Invoke(x));

    public PartitionedHubDataSource WithType<T>(
        Func<T, object> partitionFunction,
        Func<PartitionedTypeSourceWithType<T>, PartitionedTypeSourceWithType<T>> typeSource
    ) =>
        WithTypeSource(
            typeof(T),
            typeSource.Invoke(new PartitionedTypeSourceWithType<T>(Hub, partitionFunction, Id))
        );

    protected override PartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    {
        throw new NotSupportedException("Please use method with partition");
    }

    protected WorkspaceReference<EntityStore> GetReference(object partition)
    {
        var ret = new PartitionedCollectionsReference(GetReference(), partition);
        return ret;
    }

    public PartitionedHubDataSource InitializingPartitions(IEnumerable<object> partitions) =>
        this with
        {
            InitializePartitions = InitializePartitions.Concat(partitions).ToArray()
        };

    private object[] InitializePartitions { get; init; } = Array.Empty<object>();

    public override void Initialize()
    {
        foreach (var partition in InitializePartitions)
        {
            var reference = GetReference(partition);
            Streams = Streams.Add(Workspace.Subscribe(partition, reference));
        }
    }
}

public abstract record HubDataSourceBase<TDataSource> : DataSource<TDataSource>
    where TDataSource : HubDataSourceBase<TDataSource>
{
    private readonly ITypeRegistry typeRegistry;
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;

    protected HubDataSourceBase(object Id, IMessageHub Hub)
        : base(Id, Hub)
    {
        typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    }
}

public record HubDataSource : HubDataSourceBase<HubDataSource>
{
    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Hub, Id)));

    public HubDataSource(object Id, IMessageHub Hub)
        : base(Id, Hub) { }

    public override void Initialize()
    {
        var reference = new CollectionsReference(
            TypeSources.Values.Select(ts => ts.CollectionName).ToArray()
        );
        Streams = Streams.Add(Workspace.Subscribe(Id, reference));
    }
}
