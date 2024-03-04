using System.Reflection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;
public record DataPluginState(HubDataSource Workspace, DataContext DataContext);
public class DataPlugin : MessageHubPlugin<DataPluginState>, 
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>,
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<SubscribeDataRequest>,
    IMessageHandler<PatchChangeRequest>

{
    private readonly IMessageHub persistenceHub;

    public DataPlugin(IMessageHub hub) : base(hub)
    {
        Register(HandleGetRequest); // This takes care of GetRequest and GetManyRequest
        persistenceHub = hub.GetHostedHub(new PersistenceAddress(hub.Address), conf => conf);
    }


    public IEnumerable<Type> MappedTypes => State.Workspace.MappedTypes;

    public void Update(IEnumerable<object> instances, UpdateOptions options)
        => RequestChange(null, new UpdateDataRequest(instances.ToArray()));

    public void Delete(IEnumerable<object> instances)
        => RequestChange(null, new DeleteDataRequest(instances.ToArray()));


    //private Task initializeTask;
    //public override Task Initialized => initializeTask;

    public override async Task StartAsync(CancellationToken cancellationToken)  // This loads the persisted state
    {
        await base.StartAsync(cancellationToken);

        var dataContext = Hub.GetDataConfiguration();
        await InitializeAsync(cancellationToken, dataContext); 
    }

    private async Task InitializeAsync(CancellationToken cancellationToken, DataContext dataContext)
    {
        await dataContext.InitializeAsync(cancellationToken);

        var workspace = dataContext.DataSources.Values
            .SelectMany(ds => ds.TypeSources)
            .Aggregate(
                new HubDataSource(Address, Hub), 
                (ds, ts) =>
                    ds.WithType(ts.ElementType, t => t.WithKey(ts.GetKey).WithPartition(ts.ElementType, ts.GetPartition)));


        workspace.Initialize(dataContext.GetEntities());
        InitializeState(new(workspace, dataContext));
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
        => RequestChange(request, request.Message);
    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(IMessageDelivery<PatchChangeRequest> request)
        => RequestChange(request, request.Message);


    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        var changes = State.Workspace.Change(change).ToArray();

        var dataChanged = State.Workspace.Commit();
        if(request != null)
            Hub.Post(dataChanged, o => o.ResponseFor(request));

        Task CommitToDataSource(CancellationToken cancellationToken) => State.DataContext.UpdateAsync(changes, cancellationToken);
        persistenceHub.Schedule(CommitToDataSource);
        return request?.Processed();
    }


    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
        => RequestChange(request, request.Message);


    private IMessageDelivery HandleGetRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType)
        {
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(GetManyRequest<>))
            {
                var elementType = type.GetGenericArguments().First();
                return (IMessageDelivery)GetElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);
            }

            if (genericTypeDefinition == typeof(GetRequest<>))
            {
                var elementType = type.GetGenericArguments().First();
                return (IMessageDelivery)GetElementMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);

            }
        }
        return request;
    }

    private static readonly MethodInfo GetElementMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElement<object>(null));

    // ReSharper disable once UnusedMethodReturnValue.Local
    private IMessageDelivery GetElement<T>(IMessageDelivery<GetRequest<T>> request) where T : class
    {
        var item = State.Workspace.Get<T>(request.Message.Id);
        Hub.Post(item, o => o.ResponseFor(request));
        return request.Processed();
    }

    private static readonly MethodInfo GetElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));


    // ReSharper disable once UnusedMethodReturnValue.Local
    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var items = State.Workspace.Get<T>();
        var message = request.Message;
        var queryResult = items;
        if (message.PageSize is not null)
            queryResult = queryResult.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value).ToArray();
        var response = new GetManyResponse<T>(items.Count, queryResult);
        Hub.Post(response, o => o.ResponseFor(request));
        return request.Processed();
    }

    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message.GetType().IsGetRequest())
            return true;
        if (delivery.Message is DataChangedEvent)
            return false;
        
        return base.IsDeferred(delivery);
    }


    public void Commit()
    {
        State.Workspace.Commit();
    }

    public void Rollback()
    {
        State.Workspace.Rollback();
    }


    public IReadOnlyCollection<T> GetData<T>() where T : class
    {
        return State.Workspace.Get<T>();
    }

    IMessageDelivery IMessageHandler<DataChangedEvent>.HandleMessage(IMessageDelivery<DataChangedEvent> request)
    {
        var dataSourceId = request.Sender;
        var @event = request.Message;
        State.DataContext.Synchronize(@event, dataSourceId);
        State.Workspace.UpdateWorkspace(State.DataContext.GetEntities());
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeDataRequest>.HandleMessage(IMessageDelivery<SubscribeDataRequest> request)
        => StartSynchronization(request);

    private IMessageDelivery StartSynchronization(IMessageDelivery<SubscribeDataRequest> request)
    {
        var address = request.Sender;

        var changes = State.Workspace.Subscribe(request.Message, address);
        Hub.Post(changes, o => o.ResponseFor(request));
        return request.Processed();
    }


}

