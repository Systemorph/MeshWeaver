using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config, Func<DataContext, DataContext> dataPluginConfiguration)
    {
        var dataPluginConfig = config.GetListOfLambdas();
        return config
            .WithServices(sc => sc.AddSingleton<IWorkspace, DataPlugin>())
            .WithRoutes(routes => routes
                .RouteMessage<StartDataSynchronizationRequest>(d => new PersistenceAddress(routes.Hub.Address))
                .RouteMessage<StopDataSynchronizationRequest>(d => new PersistenceAddress(routes.Hub.Address))
            )
            .Set(dataPluginConfig.Add(dataPluginConfiguration))
            .AddPlugin(hub => (DataPlugin)hub.ServiceProvider.GetRequiredService<IWorkspace>());
    }

    private static ImmutableList<Func<DataContext, DataContext>> GetListOfLambdas(this MessageHubConfiguration config)
    {
        return config.Get<ImmutableList<Func<DataContext, DataContext>>>() ?? ImmutableList<Func<DataContext, DataContext>>.Empty;
    }

    internal static DataContext GetDataConfiguration(this IMessageHub hub)
    {
        var dataPluginConfig = hub.Configuration.GetListOfLambdas();
        var ret = new DataContext(hub);
        foreach (var func in dataPluginConfig)
            ret = func.Invoke(ret);
        return ret.Build(hub);
    }

    public static async Task<IReadOnlyCollection<T>> GetAll<T>(this IMessageHub hub, object dataSourceId, CancellationToken cancellationToken) where T : class
    {
        // this is usually not to be written ==> just test code.
        var persistenceHub = hub.GetHostedHub(new PersistenceAddress(hub.Address), null);
        return (await persistenceHub.AwaitResponse(new GetManyRequest<T>(), cancellationToken)).Message.Items;
    }

    internal static bool IsGetRequest(this Type type)
        => type.IsGenericType && GetRequestTypes.Contains(type.GetGenericTypeDefinition());

    private static readonly HashSet<Type> GetRequestTypes = [typeof(GetRequest<>), typeof(GetManyRequest<>)];

    public static HubDataSource FromHub(this DataSource dataSource, object address) =>
        new(address);

}

public record HubDataSource(object Id) : DataSource<HubDataSource>(Id)
{
    private static readonly JsonSerializer Serializer = JsonSerializer.CreateDefault();
    protected override async Task<WorkspaceState> InitializeAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        //var getRequests = TypeSources.Keys.Select(t => typeof(GetRequest<>).MakeGenericType(t)).ToHashSet();
        //var deferral = hub.ServiceProvider.GetRequiredService<IMessageService>().Defer(d => getRequests.Contains(d.Message.GetType()));
        var collections = TypeSources.Values.Select(ts => (ts.CollectionName, $"$.{ts.CollectionName}")).ToArray();
        var subscribeRequest = new StartDataSynchronizationRequest(collections);
        var response = await hub.AwaitResponse(subscribeRequest, 
            o => o.WithTarget(Id), cancellationToken);
        return new WorkspaceState(this)
        {
            Data = response.Message.Data
                .ToImmutableDictionary(
                    x => x.Key, 
                    x => x.Value.Select(item => ParseInstanceAndKey(item, GetTypeSource(x.Key))).ToImmutableDictionary())
            
                
                
        };
    }


    private KeyValuePair<object,object> ParseInstanceAndKey(JToken item, TypeSource typeSource)
    {
        if (item is not JObject obj)
            return default;

        var id = obj.GetValue("$id");
        if (id == null)
            return default;

        return new KeyValuePair<object, object>(Serializer.Deserialize(new JTokenReader(id)),
            Serializer.Deserialize(new JTokenReader(item), typeSource.ElementType));
    }
}

