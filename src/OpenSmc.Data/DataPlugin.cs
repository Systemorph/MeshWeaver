using System.Collections.Immutable;
using AngleSharp.Io;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config, Func<DataConfiguration, DataConfiguration> dataPluginConfiguration)
    {
        var dataPluginConfig = config.Get<DataConfiguration>() ?? new();
        return config
            .WithServices(sc => sc.AddSingleton<IWorkspace, DataPlugin>())
            .Set(dataPluginConfiguration(dataPluginConfig))
            .AddPlugin(hub => (DataPlugin)hub.ServiceProvider.GetRequiredService<IWorkspace>());
    }

    internal static MessageHubConfiguration WithPersistencePlugin(this MessageHubConfiguration config, DataConfiguration dataConfiguration) => config.AddPlugin(hub => new DataPersistencePlugin(hub, dataConfiguration));
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
    IMessageHandler<DeleteDataRequest>,
    IMessageHandler<DeleteByIdRequest>
{
    private readonly DataConfiguration dataConfiguration;
    public record DataPersistenceAddress(object Host) : IHostedAddress;
    private readonly IMessageHub persistenceHub;

    public DataPlugin(IMessageHub hub) : base(hub)
    {
        dataConfiguration = hub.Configuration.Get<DataConfiguration>() ?? new();
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
        persistenceHub = hub.GetHostedHub(new DataPersistenceAddress(hub.Address), conf => conf.WithPersistencePlugin(dataConfiguration));
    }

    public override async Task StartAsync()  // This takes care of the Create (CRUD)
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
        s with {
            Current = s.Current.Update(items, dataConfiguration),
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
                Current = s.Current.Delete(items, dataConfiguration),
                UncommittedEvents = s.UncommittedEvents.Add(new DeleteDataRequest(items))
            }
            );

    }

    IMessageDelivery IMessageHandler<DeleteByIdRequest>.HandleMessage(IMessageDelivery<DeleteByIdRequest> request)
    {
        var ids = request.Message.Ids;
        DeleteByIdsImpl(ids);
        Commit();
        Hub.Post(new DataChanged(Hub.Version), o => o.ResponseFor(request));
        return request.Processed();

    }

    private object[] DeleteByIdsImpl(IDictionary<Type, IReadOnlyCollection<object>> ids)
    {
        var items = ids
            .Select(id =>
                State.Current.Data.TryGetValue(id.Key, out var inner)
                    ? id.Value.Select(ii => inner.Remove(ii))
                    : Enumerable.Empty<object>()).Aggregate((x, y) => x.Concat(y)).ToArray();
        DeleteImpl(items);
        Hub.Post(new DataChanged(Hub.Version), o => o.WithTarget(MessageTargets.Subscribers));

        return items;
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
        var (items, count) = State.Current.GetItems<T>();
        var message = request.Message;
        if (message.PageSize is not null)
            items = items.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value);
        var queryResult = items.ToArray();
        var response = new GetManyResponse<T>(count, queryResult);
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

    public void DeleteByIds(IDictionary<Type, IReadOnlyCollection<object>> instances)
    {
        var items = DeleteByIdsImpl(instances);
        Delete(items);
    }

    public void Commit()
    {
        if (State.UncommittedEvents.Count == 0)
            return;
        persistenceHub.Post(new UpdateDataStateRequest(State.UncommittedEvents));
        Hub.Post(new DataChanged(Hub.Version), o => o.WithTarget(MessageTargets.Subscribers));
        UpdateState(s => s with {LastSaved = s.Current, UncommittedEvents = ImmutableList<DataChangeRequest>.Empty});
    }

    public IQueryable<T> Query<T>()
    {
        return
            (State.Current.Data.TryGetValue(typeof(T), out var inner)
                ? inner
                : ImmutableDictionary<object, object>.Empty)
            .Values
            .Cast<T>()
            .AsQueryable();
    }
}

public record UpdateDataStateRequest(IReadOnlyCollection<DataChangeRequest> Events);
