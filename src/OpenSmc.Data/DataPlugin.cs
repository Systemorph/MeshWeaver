using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config, Func<DataConfiguration, DataConfiguration> dataPluginConfiguration)
    {
        var dataPluginConfig = config.GetListOfLambdas();
        return config
            .WithServices(sc => sc.AddSingleton<IWorkspace, DataPlugin>())
            .Set(dataPluginConfig.Add(dataPluginConfiguration))
            .AddPlugin(hub => (DataPlugin)hub.ServiceProvider.GetRequiredService<IWorkspace>());
    }

    private static ImmutableList<Func<DataConfiguration, DataConfiguration>> GetListOfLambdas(this MessageHubConfiguration config)
    {
        return config.Get<ImmutableList<Func<DataConfiguration, DataConfiguration>>>() ?? ImmutableList<Func<DataConfiguration, DataConfiguration>>.Empty;
    }

    internal static DataConfiguration GetDataConfiguration(this IMessageHub hub)
    {
        var dataPluginConfig = hub.Configuration.GetListOfLambdas();
        var ret = new DataConfiguration(hub);
        foreach (var func in dataPluginConfig)
            ret = func.Invoke(ret);
        return ret;
    }

    internal static MessageHubConfiguration WithPersistencePlugin(this MessageHubConfiguration config, DataConfiguration dataConfiguration) => 
        config.AddPlugin(hub => new DataPersistencePlugin(hub, dataConfiguration));
}

/* TODO List: 
 *  a) move code DataPlugin to opensmc -- done
 *  b) create an immutable variant of the workspace
 *  c) make workspace methods fully sync
 *  d) offload saves & deletes to a different hub
 *  e) configure Ifrs Hubs
 */

public class DataPlugin : MessageHubPlugin<DataPluginState>, 
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>
{
    public record DataPersistenceAddress(object Host) : IHostedAddress;
    private readonly IMessageHub persistenceHub;

    public DataPlugin(IMessageHub hub) : base(hub)
    {
        var dataConfiguration = hub.GetDataConfiguration();
        dataConfiguration = dataConfiguration with { Hub = hub };
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
        persistenceHub = hub.GetHostedHub(new DataPersistenceAddress(hub.Address), conf => conf.WithPersistencePlugin(dataConfiguration));
    }

    public override async Task StartAsync()  // This loads the persisted state
    {
        await base.StartAsync();

        var response = await persistenceHub.AwaitResponse(new GetDataStateRequest());
        InitializeState(new (response.Message, response.Message));
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
                Current = s.Current.Modify(items, (ws, i) => ws.Update(i)),
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
            var getElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));
            return (IMessageDelivery)getElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);
        }
        return request;
    }

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
        => delivery.Message.GetType().Namespace == typeof(GetManyRequest<>).Namespace;

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
        UpdateState(s => s with {LastSaved = s.Current, UncommittedEvents = ImmutableList<DataChangeRequest>.Empty});
    }

    public IReadOnlyCollection<T> GetItems<T>() where T : class
    {
        return State.Current.GetItems<T>();
    }
}

