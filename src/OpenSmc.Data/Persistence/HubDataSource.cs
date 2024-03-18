using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record HubDataSource(object Id, IMessageHub Hub, IWorkspace Workspace) : DataSource<HubDataSource>(Id, Hub)
{

    private readonly bool isExternalDataSource = !Id.Equals(Hub.Address);



    private readonly ISerializationService serializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
    public override EntityStore Update(WorkspaceState workspace)
    {
        var newStore = base.Update(workspace);
        
        if (isExternalDataSource)
            SerializeAndPostChangeRequest(newStore);

        return LastSerialized = newStore;
    }

    private void SerializeAndPostChangeRequest(EntityStore newStore)
    {
        var oldJson = JsonNode.Parse(serializationService.SerializeToString(LastSerialized));
        var newJson = JsonNode.Parse(serializationService.SerializeToString(newStore));
        var patch = oldJson.CreatePatch(newJson);
        Hub.RegisterCallback(Hub.Post(new PatchChangeRequest(patch), o => o.WithTarget(Id)), HandleCommitResponse);
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


    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    private static readonly Type[] DataTypes = [typeof(EntityStore), typeof(JsonPatch), typeof(InstancesInCollection)];
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
                
                tcs.SetResult(new WorkspaceState(Hub, LastSerialized = (EntityStore)response.Message.Change, TypeSources));
                
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }

    private EntityStore LastSerialized { get; set; }

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
    public HubDataSource SynchronizeAll(bool synchronizeAll = true)
        => this with { SyncAll = synchronizeAll };

}
