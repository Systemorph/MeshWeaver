using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

/* TODO List: 
 *  a) move code DataPlugin to opensmc -- done
 *  b) create an immutable variant of the workspace -- done
 *  c) make workspace methods fully sync -- done
 *  d) offload saves & deletes to a different hub -- done
 *  e) configure Ifrs Hubs
 */

public class DataPlugin : MessageHubPlugin<DataPluginState>, 
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>
{
    private readonly IMessageHub persistenceHub;

    public DataContext Context { get; }
    private readonly TaskCompletionSource initialize = new();
    public Task Initialize => initialize.Task;
    public DataPlugin(IMessageHub hub) : base(hub)
    {
        Context = hub.GetDataConfiguration();
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
        persistenceHub = hub.GetHostedHub(new PersistenceAddress(hub.Address), conf => conf.AddPlugin(h => new DataPersistencePlugin(h, Context)));
    }


    public override async Task StartAsync()  // This loads the persisted state
    {
        await base.StartAsync();

        var response = await persistenceHub.AwaitResponse(new GetDataStateRequest());
        InitializeState(new (response.Message));
        initialize.SetResult();
    }

    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
    {
        UpdateImpl(request.Message.Elements, request.Message.Options);
        Commit();
        Hub.Post(new DataChanged(Hub.Version), o => o.ResponseFor(request));
        return request.Processed();
    }

    private void UpdateImpl(IReadOnlyCollection<object> items, UpdateOptions options)
    {
        UpdateState(s =>
            s with
            {
                Current = s.Current.Modify(items, (ws, i) => ws.Update(i)),
                UncommittedEvents = s.UncommittedEvents.Add(new UpdateDataRequest(items) { Options = options })
            }
        ); // update the state in memory (workspace)
    }

    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
    {
        DeleteImpl(request.Message.Elements);
        Commit();
        Hub.Post(new DataChanged(Hub.Version), o => o.ResponseFor(request));
        return request.Processed();

    }

    private void DeleteImpl(IReadOnlyCollection<object> items)
    {
        UpdateState(s =>
            s with
            {
                Current = s.Current.Modify(items, (ws, i) => ws.Delete(i)),
                UncommittedEvents = s.UncommittedEvents.Add(new DeleteDataRequest(items))
            }
            );

    }



    private IMessageDelivery HandleGetRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GetManyRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            return (IMessageDelivery)GetElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);
        }
        return request;
    }

    private static readonly MethodInfo GetElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));

    // ReSharper disable once UnusedMethodReturnValue.Local
    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var items = State.Current.GetItems<T>();
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

    public void Update(IReadOnlyCollection<object> instances, UpdateOptions options)
    {
        UpdateImpl(instances, options);
    }

    public void Delete(IReadOnlyCollection<object> instances)
    {
        DeleteImpl(instances);

    }


    public void Commit()
    {
        if (State.UncommittedEvents.Count == 0)
            return;
        persistenceHub.Post(new UpdateDataStateRequest(State.UncommittedEvents));
        Hub.Post(new DataChanged(Hub.Version), o => o.WithTarget(MessageTargets.Subscribers));
        UpdateState(s => s with {UncommittedEvents = ImmutableList<DataChangeRequest>.Empty});
    }

    public void Rollback()
    {
        UpdateState(s => s with
        {
            Current = s.PreviouslySaved,
            UncommittedEvents = ImmutableList<DataChangeRequest>.Empty
        });
    }

    public IReadOnlyCollection<T> GetItems<T>() where T : class
    {
        return State.Current.GetItems<T>();
    }
}

