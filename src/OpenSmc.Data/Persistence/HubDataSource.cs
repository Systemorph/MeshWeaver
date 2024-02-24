using OpenSmc.Messaging;

namespace OpenSmc.Data.Persistence;

public record HubDataSource(object Id, IMessageHub Hub) : DataSource<HubDataSource, IHubTypeSource>(Id, Hub)
{
    protected override Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<ITransaction>(new DelegateTransaction(() => Commit(), Revert));
    }

    private void Revert()
    {
        throw new NotImplementedException();
    }


    public DataChangedEvent Commit()
    {
        var deltas = 
            TypeSources
                .Values
                .Select(x => x.GetChanges(CollectionChangeType.Patch))
                .Where(x => x != null)
                .ToArray();

        if (!deltas.Any()) return null;

        var dataChanged = new DataChangedEvent(Hub.Version, deltas);
        Hub.Post(dataChanged, o => o.WithTarget(Id));
        return dataChanged;
    }


    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource)
        => WithType<T>(x => (HubTypeSource<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T> (Func<HubTypeSource<T>, HubTypeSource<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new HubTypeSource<T>(Hub)));


    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        var collections = TypeSources
            .Values
            .Select(ts => (ts.CollectionName, Path: $"$['{ts.CollectionName}']")).ToDictionary(x => x.CollectionName, x => x.Path);
        var startDataSynchronizationRequest = new StartDataSynchronizationRequest(collections);



        var subscribeRequest =
            Hub.Post(startDataSynchronizationRequest,
                o => o.WithTarget(Id));

        var tcs = new TaskCompletionSource(cancellationToken);
        Hub.RegisterCallback(subscribeRequest, response =>
            {
                Synchronize(response.Message);
                tcs.SetResult();
                
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }


    public IReadOnlyCollection<T> Get<T>() where T : class
        => GetTypeSource(typeof(T))?.GetData()?.Values.Cast<T>().ToArray() ?? Array.Empty<T>();
    public T Get<T>(object id) where T : class
        => (T)GetTypeSource(typeof(T))?.GetData()?.GetValueOrDefault(id);

    public void Rollback()
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Rollback();
    }

}
