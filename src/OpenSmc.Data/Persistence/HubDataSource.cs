using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record HubDataSource : DataSource<HubDataSource>
{

    private readonly bool isExternalDataSource;

    

    private readonly ISerializationService serializationService;
    public override EntityStore Update(WorkspaceState workspace)
    {
        var newStore = base.Update(workspace);
        
        if (isExternalDataSource)
            SerializeAndPostChangeRequest(newStore);

        LastSerialized = JsonSerializer.SerializeToNode(newStore, options);
        return newStore;
    }

    private void SerializeAndPostChangeRequest(EntityStore newStore)
    {
        var newJson = JsonSerializer.SerializeToNode(newStore, options);
        var patch = LastSerialized.CreatePatch(newJson);
        Hub.RegisterCallback(Hub.Post(new PatchChangeRequest(patch), o => o.WithTarget(Id)), HandleCommitResponse);
        LastSerialized = newJson;
    }

    private IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();

        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource)
        => WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T> (Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Id, Hub.ServiceProvider)));


    private readonly ITypeRegistry typeRegistry;
    private static readonly Type[] DataTypes = [typeof(EntityStore), typeof(JsonPatch), typeof(InstancesInCollection)];
    private readonly JsonSerializerOptions options;

    public HubDataSource(object Id, IMessageHub Hub, IWorkspace Workspace) : base(Id, Hub)
    {
        this.Workspace = Workspace;
        isExternalDataSource = !Id.Equals(Hub.Address);
        serializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
        typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        options = serializationService.Options(TypeSources.Values.ToDictionary(x => x.CollectionName));
    }

    public override Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken)
    {
        typeRegistry.WithTypes(TypeSources.Values.Select(t => t.ElementType));
        typeRegistry.WithTypes(DataTypes);
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

        var subscribeRequest =
            Hub.Post(startDataSynchronizationRequest,
                o => o.WithTarget(Id));

        var tcs = new TaskCompletionSource<WorkspaceState>(cancellationToken);
        Hub.RegisterCallback(subscribeRequest, response =>
            {
                tcs.SetResult(new WorkspaceState(Hub, JsonNode.Parse(response.Message.Change.Content), TypeSources.Values.ToDictionary(x => x.CollectionName)));
                
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }

    private JsonNode LastSerialized { get; set; }

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

    public HubDataSource SynchronizeAll(bool synchronizeAll = true)
        => this with { SyncAll = synchronizeAll };

}
