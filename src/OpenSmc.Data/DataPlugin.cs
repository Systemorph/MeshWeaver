using System.Reflection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public class DataPlugin : MessageHubPlugin<HubDataSource>, 
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>
{
    public DataPlugin(IMessageHub hub) : base(hub)
    {
        var dataContext = hub.GetDataConfiguration();
        var persistenceHub = hub
            .GetHostedHub(new PersistenceAddress(hub.Address),
                conf => conf
                    .AddPlugin(h => new DataPersistencePlugin(h, dataContext)));
        Register(HandleGetRequest); // This takes care of GetRequest and GetManyRequest
        InitializeState(dataContext.DataSources.Values
            .SelectMany(ds => ds.TypeSources)
            .Aggregate(new HubDataSource(persistenceHub.Address, persistenceHub), (ds, ts) =>
                ds.WithType(ts.ElementType, t => t.WithKey(ts.GetKey))));
    }

    public override Task Initialized => initializeStateTask;

    public IEnumerable<Type> MappedTypes => State.MappedTypes;

    public void Update(IReadOnlyCollection<object> instances, UpdateOptions options)
    {
        throw new NotImplementedException();
    }

    public void Delete(IReadOnlyCollection<object> instances)
    {
        throw new NotImplementedException();
    }

    private Task initializeStateTask;
    public override async Task StartAsync(CancellationToken cancellationToken)  // This loads the persisted state
    {
        await base.StartAsync(cancellationToken);
        await State.InitializeAsync(cancellationToken);
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request) 
        => RequestChange(request);

    private IMessageDelivery RequestChange(IMessageDelivery<DataChangeRequest> request)
    {
        State.Change(request.Message);
        var dataChanged = State.Commit();
        if(dataChanged != null)
            Hub.Post(dataChanged, o => o.ResponseFor(request));
        return request.Processed();
    }


    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
        => RequestChange(request);


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
        var item = State.Get<T>(request.Message.Id);
        Hub.Post(item, o => o.ResponseFor(request));
        return request.Processed();
    }

    private static readonly MethodInfo GetElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));

    // ReSharper disable once UnusedMethodReturnValue.Local
    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var items = State.Get<T>();
        var message = request.Message;
        var queryResult = items;
        if (message.PageSize is not null)
            queryResult = queryResult.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value).ToArray();
        var response = new GetManyResponse<T>(items.Count, queryResult);
        Hub.Post(response, o => o.ResponseFor(request));
        return request.Processed();
    }

    public override bool IsDeferred(IMessageDelivery delivery)
        => base.IsDeferred(delivery) || delivery.Message.GetType().IsGetRequest();


    public void Commit()
    {
        State.Commit();
    }

    public void Rollback()
    {
        State.Rollback();
    }


    public IReadOnlyCollection<T> GetData<T>() where T : class
    {
        return State.Get<T>();
    }
}

