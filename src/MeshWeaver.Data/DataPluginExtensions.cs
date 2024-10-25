using System.Collections.Immutable;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

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
                .Set(existingLambdas.Add(dataPluginConfiguration));

            if(existingLambdas.Any())
                return ret;
            return ret.AddActivities()
            .WithServices(sc => sc.AddScoped<IWorkspace, Workspace>())
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
                        options.Converters.Insert(
                            0,
                            new InstancesInCollectionConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                })
            )
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
                typeof(DataChangeRequest),
                typeof(DataChangeResponse),
                typeof(SubscribeRequest),
                typeof(UnsubscribeDataRequest)
            )
            .RegisterDataEvents()
            ;

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
        var ret = new DataContext(workspace);
        foreach (var func in dataPluginConfig)
            ret = func.Invoke(ret);
        return ret;
    }

    public static DataContext FromPartitionedHubs<TPartition>(
        this DataContext dataContext,
        object id,
        Func<PartitionedHubDataSource<TPartition>, PartitionedHubDataSource<TPartition>> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            id,
            _ => configuration.Invoke(new PartitionedHubDataSource<TPartition>(id, dataContext.Workspace))
        );

    public static DataContext FromHub(
        this DataContext dataContext,
        object address,
        Func<UnpartitionedHubDataSource, IUnpartitionedDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            address,
            _ => configuration.Invoke(new UnpartitionedHubDataSource(address, dataContext.Workspace))
        );

    public static DataContext FromConfigurableDataSource(
        this DataContext dataContext,
        object address,
        Func<GenericUnpartitionedDataSource, IUnpartitionedDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            address,
            _ => configuration.Invoke(new GenericUnpartitionedDataSource(address, dataContext.Workspace))
        );
    private static MessageHubConfiguration RegisterDataEvents(this MessageHubConfiguration configuration) =>
        configuration
            .WithHandler<DataChangeRequest>((hub, request) =>
            {
                var activity = new Activity(ActivityCategory.DataUpdate, hub);
                hub.GetWorkspace().RequestChange(request.Message with{ChangedBy = request.Sender}, activity);
                activity.Complete(log =>
                    hub.Post(new DataChangeResponse(hub.Version, log), o => o.ResponseFor(request))
                );
                return request.Processed();
            })
            .WithHandler<SubscribeRequest>((hub, request) =>
            {
                hub.GetWorkspace().SubscribeToClient(request.Message with{Subscriber = request.Sender});
                return request.Processed();
            });



}
