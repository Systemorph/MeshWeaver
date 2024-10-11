using System.Collections.Immutable;
using MeshWeaver.Data.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public sealed record DataContext : IAsyncDisposable
{
    public ITypeRegistry TypeRegistry { get; }

    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        Workspace = workspace;
        ReduceManager = StandardWorkspaceReferenceImplementations.CreateReduceManager(Hub);

        TypeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        TypeRegistry.WithKeyFunctionProvider(type =>
            KeyFunctionBuilder.GetFromProperties(
                type,
                type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
            )
        );
    }

    private Dictionary<Type, ITypeSource> TypeSourcesByType { get; set; }

    public IEnumerable<IDataSource> DataSources => DataSourcesById.Values;

    private ImmutableDictionary<object, IDataSource> DataSourcesById { get; set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource GetDataSourceById(object id) => DataSourcesById.GetValueOrDefault(id);
    public IDataSource GetDataSourceByType(Type type) => DataSourcesByType.GetValueOrDefault(type);

    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; }

    public DataContext WithDataSourceBuilder(object id, DataSourceBuilder dataSourceBuilder) =>
        this with { DataSourceBuilders = DataSourceBuilders.Add(id, dataSourceBuilder), };

    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; }

    public ITypeSource GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);

    public ITypeSource GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type);


    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableDictionary<object, DataSourceBuilder>.Empty;

    internal ReduceManager<EntityStore> ReduceManager { get; init; }
    internal TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromHours(60);
    public IMessageHub Hub { get; }
    public IWorkspace Workspace { get; }

    public DataContext WithInitializationTimeout(TimeSpan timeout) =>
        this with { InitializationTimeout = timeout };

    public DataContext Configure(
        Func<ReduceManager<EntityStore>, ReduceManager<EntityStore>> change
    ) => this with { ReduceManager = change.Invoke(ReduceManager) };

    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    public void Initialize()
    {
        DataSourcesById = DataSourceBuilders.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Invoke(Hub)
        );


        foreach (var dataSource in DataSourcesById.Values)
            dataSource.Initialize();

        TypeSources = DataSourcesById
            .Values
            .SelectMany(ds => ds.TypeSources.Values)
            .ToDictionary(x => x.CollectionName);
        TypeSourcesByType = DataSourcesById.Values.SelectMany(ds => ds.TypeSources).ToDictionary();

    }

    public IEnumerable<Type> MappedTypes => DataSourcesByType.Keys;

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            await dataSource.DisposeAsync();
        }
    }

    public string GetCollectionName(Type type)
        => TypeSourcesByType.GetValueOrDefault(type)?.CollectionName;



    private ImmutableDictionary<object, ISynchronizationStream<EntityStore>> Streams { get; set; } =
        ImmutableDictionary<object, ISynchronizationStream<EntityStore>>.Empty;


    private IEnumerable<string> GetCollections(ChangeItem<EntityStore> changeItem)
    {
        var patch = changeItem.Patch?.Value;
        return patch != null
            ? patch.Operations.Select(p => p.Path.Segments.First().ToString()).Distinct()
            : changeItem.Value.Collections.Keys;
    }


}
