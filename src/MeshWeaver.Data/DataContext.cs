using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public sealed record DataContext : IDisposable
{
    public ITypeRegistry TypeRegistry { get; }

    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        Workspace = workspace;
        ReduceManager = Hub.CreateReduceManager();

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

    public IDataSource GetDataSourceForId(object id) => DataSourcesById.GetValueOrDefault(id);

    public IDataSource GetDataSourceForType(Type type) => DataSourcesByType.GetValueOrDefault(type);

    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; }
    public IReadOnlyDictionary<string, IDataSource> DataSourcesByCollection { get; private set; }

    public DataContext WithDataSource(DataSourceBuilder dataSourceBuilder, object id = null) =>
        this with { DataSourceBuilders = DataSourceBuilders.Add(dataSourceBuilder), };

    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; }

    public ITypeSource GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);

    public ITypeSource GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type);


    public ImmutableList<DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableList<DataSourceBuilder>.Empty;

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
        DataSourcesById = DataSourceBuilders.Select(x => x.Invoke(Hub)).ToImmutableDictionary(x => x.Id);


        foreach (var dataSource in DataSourcesById.Values)
            dataSource.Initialize();
        DataSourcesByType = DataSourcesById.Values
            .SelectMany(ds => ds.MappedTypes.Select(t => new KeyValuePair<Type, IDataSource>(t, ds))).ToDictionary();
        DataSourcesByCollection = DataSourcesByType.Select(kvp => new KeyValuePair<string, IDataSource>(TypeRegistry.GetCollectionName(kvp.Key), kvp.Value)).ToDictionary();
        TypeSources = DataSourcesById
            .Values
            .SelectMany(ds => ds.TypeSources)
            .ToDictionary(x => x.CollectionName);
        TypeSourcesByType = DataSourcesById.Values.SelectMany(ds => ds.TypeSources).ToDictionary(ts => ts.TypeDefinition.Type);

        foreach (var typeSource in TypeSources.Values)
            TypeRegistry.WithType(typeSource.TypeDefinition.Type, typeSource.TypeDefinition.CollectionName);

    }

    public IEnumerable<Type> MappedTypes => DataSourcesByType.Keys;

    public void Dispose()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Dispose();
        }
    }

    public string GetCollectionName(Type type)
        => TypeSourcesByType.GetValueOrDefault(type)?.CollectionName;




}
