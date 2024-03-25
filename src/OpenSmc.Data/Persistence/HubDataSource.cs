using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record PartitionedHubDataSource(object Id, IMessageHub Hub) : HubDataSourceBase<PartitionedHubDataSource>(Id, Hub)
{
    public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction)
        => WithType(partitionFunction, x => x);

    public PartitionedHubDataSource WithType<T>(Func<T,object> partitionFunction, Func<ITypeSource, ITypeSource> config)
        => WithType(partitionFunction, x => (PartitionedTypeSourceWithType<T>)config.Invoke(x));

    public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction, Func<PartitionedTypeSourceWithType<T>, PartitionedTypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new PartitionedTypeSourceWithType<T>(Hub, partitionFunction, Id)));



    protected override PartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    {
        throw new NotSupportedException("Please use method with partition");
    }


    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        streams = InitializePartitions.Select(a => GetStream(a, GetReference(a))).ToArray();
        return streams;
    }



    protected  WorkspaceReference<EntityStore> GetReference(object partition)
    {
        var ret = new PartitionedCollectionsReference(GetReference(), partition);
        return ret;
    }

    public PartitionedHubDataSource InitializingPartitions(IEnumerable<object> partitions)
        => this with { InitializePartitions = InitializePartitions.Concat(partitions).ToArray() };

    private object[] InitializePartitions { get; init; } = Array.Empty<object>();
    private ChangeStream<EntityStore>[] streams;

}



public abstract record HubDataSourceBase<TDataSource> : DataSource<TDataSource> where TDataSource : HubDataSourceBase<TDataSource>
{
    private readonly ITypeRegistry typeRegistry;
    protected JsonSerializerOptions Options;
    private readonly ISerializationService serializationService;

    protected HubDataSourceBase(object Id, IMessageHub Hub) : base(Id, Hub)
    {
        serializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
        typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    }



    protected override WorkspaceReference<EntityStore> GetReference()
    {
        Options = serializationService.Options(TypeSources.Values.ToDictionary(x => x.CollectionName));
        typeRegistry.WithTypes(TypeSources.Values.Select(t => t.ElementType));
        typeRegistry.WithType<JsonPatch>();
        return SyncAll
            ? new EntireWorkspace()
            : base.GetReference();
    }

    protected ChangeStream<EntityStore> GetStream(object address, WorkspaceReference<EntityStore> reference)
    {
        return Workspace.GetChangeStream(address, reference);
    }


    internal bool SyncAll { get; init; }

    public TDataSource SynchronizeAll(bool synchronizeAll = true)
        => This with { SyncAll = synchronizeAll };

}

public record HubDataSource : HubDataSourceBase<HubDataSource>
{
    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource)
        => WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Hub, Id)));

    public HubDataSource(object Id, IMessageHub Hub) : base(Id, Hub)
    {
    }


    
    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        return [GetStream(Id, GetReference())];
    }

    protected IMessageDelivery Initialize(IMessageDelivery<DataChangedEvent> response, TaskCompletionSource<EntityStore> tcs)
    {
        tcs.SetResult(JsonNode.Parse(response.Message.Change.Content).Deserialize<EntityStore>(Options));

        return response.Processed();
    }
}

