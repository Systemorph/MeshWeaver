using System.Collections.Immutable;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public sealed record DataContext : IDisposable
{
    public const string InitializationGateName = "DataContextInit";
    private const string DataContextGateName = InitializationGateName;

    public ITypeRegistry TypeRegistry { get; }

    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<DataContext>>();
        Workspace = workspace;
        ReduceManager = Hub.CreateReduceManager();

        TypeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        TypeRegistry.WithKeyFunctionProvider(type =>
            KeyFunctionBuilder.GetFromProperties(
                type,
                type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
            ) ?? null
        );
    }

    private readonly ILogger<DataContext> logger;

    private Dictionary<Type, ITypeSource> TypeSourcesByType { get; set; } = new();

    public IEnumerable<IDataSource> DataSources => DataSourcesById.Values;

    private ImmutableDictionary<object, IDataSource> DataSourcesById { get; set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource? GetDataSourceForId(object id) => DataSourcesById.GetValueOrDefault(id);

    public IDataSource? GetDataSourceForType(Type type) => DataSourcesByType.GetValueOrDefault(type)
          ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetDataSourceForType(type.BaseType));

    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; } = new Dictionary<Type, IDataSource>();
    public IReadOnlyDictionary<string, IDataSource> DataSourcesByCollection { get; private set; } = new Dictionary<string, IDataSource>();

    public DataContext WithDataSource(DataSourceBuilder dataSourceBuilder) =>
        this with { DataSourceBuilders = DataSourceBuilders.Add(dataSourceBuilder), };

    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; } = new Dictionary<string, ITypeSource>();

    public ITypeSource? GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);

    public ITypeSource? GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type)
        ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetTypeSource(type.BaseType));


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
        logger.LogDebug("Starting initialization of DataContext for {Address}", Hub.Address);
        DataSourcesById = DataSourceBuilders.Select(x => x.Invoke(Hub)).ToImmutableDictionary(x => x.Id);

        DataSourcesByType = DataSourcesById.Values
            .SelectMany(ds => ds.MappedTypes.Select(t => new KeyValuePair<Type, IDataSource>(t, ds))).ToDictionary();
        DataSourcesByCollection = DataSourcesByType.Select(kvp => new KeyValuePair<string, IDataSource>(TypeRegistry.GetCollectionName(kvp.Key)!, kvp.Value)).ToDictionary();
        TypeSources = DataSourcesById
            .Values
            .SelectMany(ds => ds.TypeSources)
            .ToDictionary(x => x.CollectionName);
        TypeSourcesByType = DataSourcesById.Values.SelectMany(ds => ds.TypeSources).ToDictionary(ts => ts.TypeDefinition.Type);

        foreach (var typeSource in TypeSources.Values)
            TypeRegistry.WithType(typeSource.TypeDefinition.Type, typeSource.TypeDefinition.CollectionName);

        // Initialize each data source
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Initialize();
            tasks.Add(dataSource.Initialized);
        }

        Task.WhenAll(tasks)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    logger.LogError(task.Exception, "DataContext initialization failed for {Address}", Hub.Address);
                }
                else if (task.IsCanceled)
                {
                    logger.LogWarning("DataContext initialization was canceled for {Address}", Hub.Address);
                }
                else
                {
                    logger.LogDebug("Finished initialization of DataContext for {Address}", Hub.Address);
                }
            }, TaskScheduler.Default);
    }

    public IEnumerable<Type> MappedTypes => DataSourcesByType.Keys;
    private readonly List<Task> tasks = new();
    public void Dispose()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Dispose();
        }
    }

    public string? GetCollectionName(Type type)
        => TypeSourcesByType.GetValueOrDefault(type)?.CollectionName;




}
