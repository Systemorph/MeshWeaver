using System.Collections.Immutable;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Data.Persistence;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config) =>
        config.AddData(x => x);

    public static MessageHubConfiguration AddData(
        this MessageHubConfiguration config,
        Func<DataContext, DataContext> dataPluginConfiguration
    )
    {
        var existingLambdas = config.GetListOfLambdas();
        var ret = config
            .AddActivities()
            .WithServices(sc => sc.AddScoped<IWorkspace, Workspace>().AddScoped<DataPlugin>())
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is EntityStoreConverter))
                        options.Converters.Insert(
                            0,
                            new EntityStoreConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                    if (!options.Converters.Any(c => c is InstancesInCollectionConverter))
                        options.Converters.Insert(0, new InstancesInCollectionConverter());
                })
            )
            .Set(existingLambdas.Add(dataPluginConfiguration))
            .WithTypes(
                typeof(EntityStore),
                typeof(InstanceCollection),
                typeof(EntityReference),
                typeof(CollectionReference),
                typeof(CollectionsReference),
                typeof(WorkspaceStoreReference),
                typeof(JsonPointerReference),
                typeof(JsonPatch),
                typeof(DataChangedEvent),
                typeof(UpdateDataRequest),
                typeof(DeleteDataRequest),
                typeof(DataChangeResponse),
                typeof(SubscribeRequest),
                typeof(UnsubscribeDataRequest)
            )
            .AddPlugin<DataPlugin>();

        return ret;
    }

    internal static ImmutableList<Func<DataContext, DataContext>> GetListOfLambdas(
        this MessageHubConfiguration config
    )
    {
        return config.Get<ImmutableList<Func<DataContext, DataContext>>>()
            ?? ImmutableList<Func<DataContext, DataContext>>.Empty;
    }

    internal static DataContext GetDataConfiguration(this IWorkspace workspace)
    {
        var dataPluginConfig = workspace.Hub.Configuration.GetListOfLambdas();
        var ret = new DataContext(workspace.Hub, workspace);
        foreach (var func in dataPluginConfig)
            ret = func.Invoke(ret);
        return ret;
    }

    public static DataContext FromPartitionedHubs(
        this DataContext dataContext,
        object id,
        Func<PartitionedHubDataSource, PartitionedHubDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            id,
            hub => configuration.Invoke(new PartitionedHubDataSource(id, dataContext.Workspace))
        );

    public static DataContext FromHub(
        this DataContext dataContext,
        object address,
        Func<HubDataSource, HubDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            address,
            hub => configuration.Invoke(new HubDataSource(address, dataContext.Workspace))
        );

    public static DataContext FromConfigurableDataSource(
        this DataContext dataContext,
        object address,
        Func<GenericDataSource, GenericDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            address,
            hub => configuration.Invoke(new GenericDataSource(address, dataContext.Workspace))
        );
}
