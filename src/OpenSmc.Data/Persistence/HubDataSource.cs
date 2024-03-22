using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record PartitionedHubDataSource(object Id, IMessageHub Hub, IWorkspace Workspace) : HubDataSourceBase<PartitionedHubDataSource>(Id, Hub, Workspace)
{
    public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction)
        => WithType<T>(partitionFunction, x => x);

    public PartitionedHubDataSource WithType<T>(Func<T,object> partitionFunction, Func<ITypeSource, ITypeSource> config)
        => WithType<T>(partitionFunction, x => (PartitionedTypeSourceWithType<T>)config.Invoke(x));

    public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction, Func<PartitionedTypeSourceWithType<T>, PartitionedTypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new PartitionedTypeSourceWithType<T>(partitionFunction, Id, Hub.ServiceProvider)));



    protected override PartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    {
        throw new NotSupportedException("Please use method with partition");
    }

    protected override void SerializeAndPostChangeRequest(EntityStore newStore)
    {
        foreach (var partitioned in
                 TypeSources.Values
                     .OfType<IPartitionedTypeSource>()
                     .Join(
                         newStore.Instances,
                         x => x.CollectionName,
                         x => x.Key,
                         (typeSource, store) => store.Value.Instances.GroupBy(kvp => typeSource.GetPartition(kvp.Value))
                             .Select(x => new
                             {
                                 Partition = x.Key,
                                 Instances = x.ToImmutableDictionary(),
                                 Collection = typeSource.CollectionName
                             }))
                     .SelectMany(x => x)
                     .GroupBy(x => x.Partition)
                     .Select(x => new
                     {
                         Partition = x.Key,
                         Store = new EntityStore(x.ToImmutableDictionary(y => y.Collection,
                             y => new InstancesInCollection(y.Instances)))
                     })
                )
        {
            var lastSynchronizedPartition = lastSynchronized.GetValueOrDefault(partitioned.Partition);
            var serializedPartition = JsonSerializer.SerializeToNode(partitioned.Store, Options);
            var patch = lastSynchronizedPartition.CreatePatch(serializedPartition);
            if (patch.Operations.Any())
                Hub.RegisterCallback(Hub.Post(new PatchChangeRequest(patch), o => o.WithTarget(partitioned.Partition)), HandleCommitResponse);
            lastSynchronized[partitioned.Partition] = serializedPartition;
        }
    }

    private readonly Dictionary<object, JsonNode> lastSynchronized = new();

    public override Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken)
    {
        var startDataSynchronizationRequest = GetSubscribeRequest();

        var tcs = new TaskCompletionSource<WorkspaceState>(cancellationToken);
        if (InitializePartitions.Length == 0)
            tcs.SetResult(InitialWorkspaceState);

        else
        {
            var addresses = new HashSet<object>(InitializePartitions);

            foreach (var address in addresses)
                Hub.RegisterCallback
                (
                    Hub.Post(startDataSynchronizationRequest, o => o.WithTarget(address)),
                    response => Initialize(response, tcs, addresses),
                    cancellationToken
                );

        }
        return tcs.Task;
    }

    public PartitionedHubDataSource InitializingPartitions(IEnumerable<object> partitions)
        => this with { InitializePartitions = InitializePartitions.Concat(partitions).ToArray() };

    private object[] InitializePartitions { get; init; } = Array.Empty<object>();
    private EntityStore initializingStore = new([]);

    protected IMessageDelivery Initialize(IMessageDelivery<DataChangedEvent> response, TaskCompletionSource<WorkspaceState> tcs, HashSet<object> addresses)
    {
        addresses.Remove(response.Sender);
        var json = lastSynchronized[response.Sender] = JsonNode.Parse(response.Message.Change.Content);
        var store = json.Deserialize<EntityStore>(Options);
        initializingStore = new(store.Instances.Concat(initializingStore.Instances).ToImmutableDictionary());

        if (addresses.Count == 0)
        {
            tcs.SetResult(InitialWorkspaceState);
            initializingStore = null;
        }

        return response.Processed();
    }

    private WorkspaceState InitialWorkspaceState => new(Hub, initializingStore, TypeSources.Values.ToImmutableDictionary(x => x.CollectionName));
}

public abstract record HubDataSourceBase<TDataSource> : DataSource<TDataSource> where TDataSource : HubDataSourceBase<TDataSource>
{


    private readonly bool isExternalDataSource;


    public override EntityStore Update(WorkspaceState workspace)
    {
        var newStore = base.Update(workspace);
        
        if (isExternalDataSource)
            SerializeAndPostChangeRequest(newStore);

        return newStore;
    }

    protected abstract void SerializeAndPostChangeRequest(EntityStore newStore);


    private readonly ConcurrentBag<long> versions = new();
    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        versions.Add(response.Message.Version);
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }



    private readonly ITypeRegistry typeRegistry;
    protected JsonSerializerOptions Options;
    private readonly ISerializationService serializationService;

    protected HubDataSourceBase(object Id, IMessageHub Hub, IWorkspace Workspace) : base(Id, Hub)
    {
        this.Workspace = Workspace;
        isExternalDataSource = !Id.Equals(Hub.Address);
        serializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
        typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    }


    protected SubscribeRequest GetSubscribeRequest()
    {
        Options = serializationService.Options(TypeSources.Values.ToDictionary(x => x.CollectionName));

        typeRegistry.WithTypes(TypeSources.Values.Select(t => t.ElementType));
        typeRegistry.WithType<JsonPatch>();
        WorkspaceReference collections =
            SyncAll
                ? new EntireWorkspace()
                : new CollectionsReference
                (
                    TypeSources
                        .Values
                        .Select(ts => ts.CollectionName).ToArray()
                );
        var startDataSynchronizationRequest = new SubscribeRequest(Main, collections);
        return startDataSynchronizationRequest;
    }



    private const string Main = nameof(Main);

    public override ValueTask DisposeAsync()
    {
        // TODO V10: Cannot post from dispose ==> where to put? (12.03.2024, Roland Bürgi)
        //Hub.Post(new UnsubscribeDataRequest(Main));
        return base.DisposeAsync();
    }

    public void Rollback()
    {
    }


    internal bool SyncAll { get; init; }
    public IWorkspace Workspace { get; init; }

    public TDataSource SynchronizeAll(bool synchronizeAll = true)
        => This with { SyncAll = synchronizeAll };

}

public record HubDataSource : HubDataSourceBase<HubDataSource>
{
    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource)
        => WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Id, Hub.ServiceProvider)));

    public HubDataSource(object Id, IMessageHub Hub, IWorkspace Workspace) : base(Id, Hub, Workspace)
    {
    }
    protected JsonNode LastSerialized { get; set; }

    protected override void SerializeAndPostChangeRequest(EntityStore newStore)
    {
        var newJson = JsonSerializer.SerializeToNode(newStore, Options);
        var patch = LastSerialized.CreatePatch(newJson);
        if (patch.Operations.Any())
            Hub.RegisterCallback(Hub.Post(new PatchChangeRequest(patch), o => o.WithTarget(Id)), HandleCommitResponse);
        LastSerialized = newJson;
    }

    public override EntityStore Update(WorkspaceState workspace)
    {
        var newStore = base.Update(workspace);
        LastSerialized = JsonSerializer.SerializeToNode(newStore, Options);
        return newStore;
    }
    public override Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken)
    {
        var startDataSynchronizationRequest = GetSubscribeRequest();

        var tcs = new TaskCompletionSource<WorkspaceState>(cancellationToken);

        Hub.RegisterCallback
        (
            Hub.Post(startDataSynchronizationRequest, o => o.WithTarget(Id)),
            response => Initialize(response, tcs),
            cancellationToken);
        return tcs.Task;
    }

    protected IMessageDelivery Initialize(IMessageDelivery<DataChangedEvent> response, TaskCompletionSource<WorkspaceState> tcs)
    {
        var workspaceState = new WorkspaceState(Hub, JsonNode.Parse(response.Message.Change.Content), TypeSources.Values.ToDictionary(x => x.CollectionName));
        tcs.SetResult(workspaceState);

        return response.Processed();
    }
}

