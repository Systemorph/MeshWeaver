using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data.Persistence;

public record PartitionedHubDataSource(object Id, IWorkspace Workspace)
    : HubDataSourceBase<PartitionedHubDataSource>(Id, Workspace)
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
            typeSource.Invoke(
                new PartitionedTypeSourceWithType<T>(Workspace, partitionFunction, Id)
            )
        );

    protected override PartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    {
        throw new NotSupportedException("Please use method with partition");
    }

    protected PartitionedCollectionsReference GetReference(object partition)
    {
        if (TypeSources.Count != 1)
            throw new NotSupportedException("Only one type is supported");

        return new PartitionedCollectionsReference(GetReference(), partition);
    }

    private string GetCollectionName()
    {
        if (TypeSources.Count != 1)
            throw new NotSupportedException("Only one type is supported");

        return TypeSources.Values.First().CollectionName;
    }

    public PartitionedHubDataSource InitializingPartitions(IEnumerable<object> partitions) =>
        this with
        {
            InitializePartitions = InitializePartitions.Concat(partitions).ToArray()
        };

    private object[] InitializePartitions { get; init; } = Array.Empty<object>();

    public override void Initialize(WorkspaceState state)
    {
        foreach (var partition in InitializePartitions)
        {
            var reference = GetReference(partition);
            Streams = Streams.Add(Workspace.GetStream(partition, reference));
        }
    }
}

public abstract record HubDataSourceBase<TDataSource> : DataSource<TDataSource>
    where TDataSource : HubDataSourceBase<TDataSource>
{
    private readonly ITypeRegistry typeRegistry;
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;

    protected HubDataSourceBase(object Id, IWorkspace Workspace)
        : base(Id, Workspace)
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
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    public HubDataSource(object Id, IWorkspace Workspace)
        : base(Id, Workspace) { }

    public override void Initialize(WorkspaceState state)
    {
        var reference = new CollectionsReference(
            TypeSources.Values.Select(ts => ts.CollectionName).ToArray()
        );
        Streams = Streams.Add(Workspace.GetStream(Id, reference));
        base.Initialize(state);
    }
}
