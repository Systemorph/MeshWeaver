using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config, Func<DataContext, DataContext> dataPluginConfiguration)
    {
        return config
            .WithServices(sc => sc.AddSingleton<IWorkspace, DataPlugin>())
            .Set(config.GetListOfLambdas().Add(dataPluginConfiguration))
            .AddPlugin<DataPlugin>(plugin => plugin.WithFactory(() => (DataPlugin)plugin.Hub.ServiceProvider.GetRequiredService<IWorkspace>()));
    }

    public static ImmutableList<Func<DataContext, DataContext>> GetListOfLambdas(this MessageHubConfiguration config)
    {
        return config.Get<ImmutableList<Func<DataContext, DataContext>>>() ?? ImmutableList<Func<DataContext, DataContext>>.Empty;
    }

    internal static DataContext GetDataConfiguration(this IMessageHub hub)
    {
        var dataPluginConfig = hub.Configuration.GetListOfLambdas();
        var ret = new DataContext(hub, hub.ServiceProvider.GetRequiredService<IWorkspace>());
        foreach (var func in dataPluginConfig)
            ret = func.Invoke(ret);
        return ret;
    }


    internal static bool IsGetRequest(this Type type)
        => type.IsGenericType && GetRequestTypes.Contains(type.GetGenericTypeDefinition());

    private static readonly HashSet<Type> GetRequestTypes = [typeof(GetRequest<>), typeof(GetManyRequest<>)];


    public static DataContext FromHub(this DataContext dataSource, object address)
        => FromHub(dataSource, address, ds => ds.SynchronizeAll());

    public static DataContext FromHub(this DataContext dataSource, object address,
        Func<HubDataSource, HubDataSource> configuration)
        => dataSource.WithDataSourceBuilder(address, hub => configuration.Invoke(new HubDataSource(address, hub, dataSource.Workspace))
        );
    public static DataContext FromConfigurableDataSource(this DataContext dataSource, object address,
        Func<DataSource, DataSource> configuration)
        => dataSource.WithDataSourceBuilder(address, hub => configuration.Invoke(new DataSource(address, hub))
        );

}