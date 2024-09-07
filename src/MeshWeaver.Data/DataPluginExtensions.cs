using System.Collections.Immutable;
using System.Reactive.Linq;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;

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
                        options.Converters.Insert(
                            0,
                            new InstancesInCollectionConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
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
            .AddPlugin<DataPlugin>(h => new DataPlugin(h));

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
        var ret = new DataContext(workspace);
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

    public static void AddUpdateOfParent<TStream, TReduced>(
        this ISynchronizationStream<TReduced> ret,
        ISynchronizationStream<TStream> stream,
        WorkspaceReference reference,
        Func<ChangeItem<TReduced>, bool> filter
    )
    {
        var backTransform = stream.ReduceManager.GetPatchFunction<TReduced>(stream, reference);

        if (backTransform != null)
        {
            ret.AddDisposable(
                ret.Where(filter).Subscribe(x => UpdateParent(stream, x, backTransform))
            );
        }
    }

    internal static void UpdateParent<TStream, TReduced>(
        ISynchronizationStream<TStream> parent,
        ChangeItem<TReduced> value,
        PatchFunction<TStream, TReduced> backTransform
    )
    {
        // if the parent is initialized, we will update the parent
        if (parent.Initialized.IsCompleted)
        {
            parent.Update(state => backTransform(state, parent, value));
        }
        // if we are in automatic mode, we will initialize the parent
        else if (parent.InitializationMode == InitializationMode.Automatic)
        {
            parent.Initialize(backTransform(Activator.CreateInstance<TStream>(), parent, value));
        }
    }
}
