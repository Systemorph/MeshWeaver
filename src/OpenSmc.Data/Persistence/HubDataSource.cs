using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record HubDataSource(object Id, IMessageHub Hub, IWorkspace Workspace) : DataSource<HubDataSource>(Id, Hub)
{
    protected override Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<ITransaction>(new DelegateTransaction(() => Commit(), Rollback));
    }




    private readonly bool isExternalDataSource = !Id.Equals(Hub.Address);
    public DataChangeResponse Commit()
    {
        //State.Commit();
        //var newWorkspace = GetSerializedWorkspace();
        //var dataChanged = CurrentWorkspace == null
        //    ? new DataChangedEvent(Hub.Version, new(newWorkspace.ToJsonString()), ChangeType.Full)
        //    : new DataChangedEvent(Hub.Version, new(JsonSerializer.Serialize(CurrentWorkspace.CreatePatch(newWorkspace))), ChangeType.Patch);

        //if (isExternalDataSource)
        //    CommitTransactionExternally(dataChanged);

        //CurrentWorkspace = newWorkspace;
        //UpdateSubscriptions();
        //return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, dataChanged);

        throw new NotImplementedException();
    }



    private void CommitTransactionExternally(DataChangedEvent dataChanged)
    {
        var request = Hub.Post(new PatchChangeRequest(dataChanged.Change), o => o.WithTarget(Id));
        Hub.RegisterCallback(request, HandleCommitResponse);
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
    private static readonly Type[] DataTypes = [typeof(WorkspaceState), typeof(JsonPatch)];
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
        var startDataSynchronizationRequest = new SubscribeDataRequest(Main, collections);

        var subscribeRequest =
            Hub.Post(startDataSynchronizationRequest,
                o => o.WithTarget(Id));

        var tcs = new TaskCompletionSource<WorkspaceState>(cancellationToken);
        Hub.RegisterCallback(subscribeRequest, response =>
            {
                
                tcs.SetResult(new WorkspaceState(Hub, response.Message, TypeSources));
                
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }

    private const string Main = nameof(Main);

    public override void Dispose()
    {
        Hub.Post(new UnsubscribeDataRequest(Main));
        base.Dispose();
    }

    public void Rollback()
    {
    }


    internal bool SyncAll { get; init; }
    public HubDataSource SynchronizeAll(bool synchronizeAll = true)
        => this with { SyncAll = synchronizeAll };

}
