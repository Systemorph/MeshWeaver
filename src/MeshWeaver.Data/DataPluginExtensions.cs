using System.Collections.Immutable;
using System.Reactive.Linq;
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
            .AddPlugin(h => new DataPlugin(h));

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

    internal static void AddUpdateOfParent<TStream, TReference, TReduced>(
        this ISynchronizationStream<TReduced> ret,
        ISynchronizationStream<TStream> stream,
        TReference reference,
        Func<ChangeItem<TReduced>, bool> filter,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    ) where TReference : WorkspaceReference
    {
        //var backTransform = stream.ReduceManager.GetPatchFunction<TReduced>(stream, reference);

        if (backTransform != null)
        {
            ret.AddDisposable(
                ret.Where(filter).Subscribe(x => UpdateParent(stream, reference, x, backTransform))
            );
        }
    }


    internal static void UpdateParent<TStream, TReference, TReduced>(
        ISynchronizationStream<TStream> parent,
        TReference reference,
        ChangeItem<TReduced> change,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    ) where TReference : WorkspaceReference
    {
        // if the parent is initialized, we will update the parent
        if (parent.Initialized.IsCompleted)
        {
            parent.Update(state => change.SetValue(backTransform(state, change, reference)));
        }
        // if we are in automatic mode, we will initialize the parent
        else if (parent.InitializationMode == InitializationMode.Automatic)
        {
            parent.Initialize(change.SetValue(backTransform(Activator.CreateInstance<TStream>(),  change, reference)));
        }
    }
}
