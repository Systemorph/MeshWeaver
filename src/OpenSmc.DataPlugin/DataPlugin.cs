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

public class DataPlugin : MessageHubPlugin<Workspace>,
    IMessageHandler<UpdateRequest>,
    IMessageHandler<DeleteRequest>
{
    private readonly Func<DataConfiguration, DataConfiguration> configure;
    public record SatelliteAddress(object Host) : IHostedAddress;
    private SatelliteAddress satelliteAddress;

    public DataPlugin(IMessageHub hub, Func<DataConfiguration, DataConfiguration> configure) : base(hub)
    {
        this.configure = configure;
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
    }

    public override async Task StartAsync()  // This takes care of the Create (CRUD)
    {
        await base.StartAsync();

        var dataConfiguration = configure(new DataConfiguration());
        if (dataConfiguration.CreateSatellitePlugin != null)
        {
            satelliteAddress = new SatelliteAddress(Hub.Address);
            var persistenceHub = Hub.GetHostedHub(satelliteAddress, conf => conf.AddPlugin(persistenceHub => dataConfiguration.CreateSatellitePlugin(persistenceHub)));
            var workspaceConfiguration = dataConfiguration.Workspace;
            var response = await persistenceHub.AwaitResponse(new GetDataStateRequest(workspaceConfiguration));
            UpdateState(_ => response.Message);
        }
        else
        {
            // TODO V10: how to initialize state without satellite plugin? (05.02.2024, Alexander Yolokhov)
            var workspaceConfiguration = dataConfiguration.Workspace;
            UpdateState(_ => new Workspace(workspaceConfiguration));
        }
    }

    IMessageDelivery IMessageHandler<UpdateRequest>.HandleMessage(IMessageDelivery<UpdateRequest> request)
    {
        var items = request.Message.Elements;
        UpdateState(s => s.Update(items)); // update the state in memory (workspace)
        Hub.Post(new DataChanged(items), o => o.ResponseFor(request).WithTarget(MessageTargets.Subscribers));      // notify all subscribers that the data has changed
        if (satelliteAddress != null)
            Hub.Post(request, o => o.WithTarget(satelliteAddress));
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<DeleteRequest>.HandleMessage(IMessageDelivery<DeleteRequest> request)
    {
        var items = request.Message.Elements;
        UpdateState(s => s.Delete(items));
        Hub.Post(new DataDeleted(items), o => o.ResponseFor(request).WithTarget(MessageTargets.Subscribers));
        if (satelliteAddress != null)
            Hub.Post(request, o => o.WithTarget(satelliteAddress));
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
