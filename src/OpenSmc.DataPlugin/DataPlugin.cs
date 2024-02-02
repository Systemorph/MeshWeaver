using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.DataPlugin;

/* TODO List: 
 *  a) move code DataPlugin to opensmc -- done
 *  b) create an immutable variant of the workspace
 *  c) make workspace methods fully sync
 *  d) offload saves & deletes to a different hub
 *  e) configure Ifrs Hubs
 */

public class DataPlugin : MessageHubPlugin<DataPlugin, Workspace>,
    IMessageHandler<UpdateRequest>,
    IMessageHandler<DeleteRequest>
{
    public DataPlugin(IMessageHub hub, MessageHubConfiguration configuration) : base(hub)
    {
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
    }

    public override async Task StartAsync()  // This takes care of the Create (CRUD)
    {
        await base.StartAsync();

        var persistenceHub = Hub.GetHostedHub(new DataPersistenceAddress(Hub.Address), conf => conf.AddPlugin<DataPersistencePlugin>());
        var response = await persistenceHub.AwaitResponse(new GetDataStateRequest(State.Configuration));
        UpdateState(_ => response.Message);
    }

    IMessageDelivery IMessageHandler<UpdateRequest>.HandleMessage(IMessageDelivery<UpdateRequest> request)
    {
        var items = request.Message.Elements;
        UpdateState(s => s.Update(items)); // update the state in memory (workspace)
        Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<DeleteRequest>.HandleMessage(IMessageDelivery<DeleteRequest> request)
    {
        var items = request.Message.Elements;
        UpdateState(s => s.Delete(items));
        Hub.Post(new DataDeleted(items));
        return request.Processed();
    }

    private IMessageDelivery HandleGetRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GetManyRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            var getElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));
            return (IMessageDelivery)getElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);
        }
        return request;
    }

    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var items = State.GetItems<T>();
        var message = request.Message;
        if (message.PageSize is not null)
            items = items.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value);
        var queryResult = items.ToArray();
        Hub.Post(queryResult, o => o.ResponseFor(request));
        return request.Processed();
    }
}
