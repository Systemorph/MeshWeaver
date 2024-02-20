using System.Collections.Immutable;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config, Func<DataContext, DataContext> dataPluginConfiguration)
    {
        var dataPluginConfig = config.GetListOfLambdas();
        return config
            .WithServices(sc => sc.AddSingleton<IWorkspace, DataPlugin>())
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

    public static HubDataSource FromEntityFramework(this DataSource dataSource, object address) =>
        new(dataSource.Id, address);

}

public record HubDataSource(object Id, object Address) : DataSourceWithStorage<HubDataSource>(Id)
{

    public override IDataStorage CreateStorage(IMessageHub hub)
    {
        return new HubDataStorage(Address);
    }


}

public record HubDataStorage(object Address) : IDataStorage
{
    public Task<IReadOnlyCollection<T>> GetData<T>(CancellationToken cancellationToken) where T : class
    {
        throw new NotImplementedException();
    }

    public Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public void Add<T>(IReadOnlyCollection<T> instances) where T : class
    {
        throw new NotImplementedException();
    }

    public void Update<T>(IReadOnlyCollection<T> instances) where T : class
    {
        throw new NotImplementedException();
    }

    public void Delete<T>(IReadOnlyCollection<T> instances) where T : class
    {
        throw new NotImplementedException();
    }
}
