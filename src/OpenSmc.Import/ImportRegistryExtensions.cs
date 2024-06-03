using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public static class ImportExtensions
{
    public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration) =>
        configuration.AddImport(x => x);

    public static MessageHubConfiguration AddImport(
        this MessageHubConfiguration configuration,
        Func<ImportConfiguration, ImportConfiguration> importConfiguration
    ) =>
        configuration
            .WithServices(services => services.AddSingleton<IActivityService, ActivityService>())
            .AddActivities()
            .AddPlugin<ImportPlugin>(plugin =>
                plugin.WithFactory(() => new(plugin.Hub, importConfiguration))
            );

    /// <summary>
    /// Describes an embedded resource.
    /// </summary>
    /// <typeparam name="T">A type contained in the assembly from which the resource is to be loaded</typeparam>
    /// <param name="dataContext"></param>
    /// <param name="resource">The path to the resource</param>
    /// <param name="configuration">Additional configuration of the data source.</param>
    /// <returns></returns>
    public static DataContext FromEmbeddedResource<T>(
        this DataContext dataContext,
        string resource,
        Func<ImportDataSource, ImportDataSource> configuration
    )
    {
        var source = new EmbeddedResource(typeof(T).Assembly, resource);
        return dataContext.WithDataSourceBuilder(
            source,
            hub => ConfigureDataSource(configuration, hub, source)
        );
    }
    public static DataContext FromEmbeddedResource<T>(
        this DataContext dataContext,
        EmbeddedResource resource,
        Func<ImportDataSource, ImportDataSource> configuration
    )
    {
        return dataContext.WithDataSourceBuilder(
            resource,
            hub => ConfigureDataSource(configuration, hub, resource)
        );
    }

    private static ImportDataSource ConfigureDataSource(
        Func<ImportDataSource, ImportDataSource> configuration,
        IMessageHub hub,
        EmbeddedResource source
    )
    {
        var ret = configuration.Invoke(new(source, hub));

        var mappedTypes = ret.MappedTypes;
        if (!mappedTypes.Any())
            throw new DataException("Data Source must contain sourced data types.");
        if (mappedTypes.Count == 1)
            ret = ret.WithRequest(r => r.WithEntityType(mappedTypes.First()));
        ret = ret.WithImportConfiguration(config =>
            config.WithFormat(ImportFormat.Default, f => f.WithAutoMappingsForTypes(mappedTypes))
        );

        return ret;
    }
}

public record EmbeddedResource(Assembly Assembly, string Resource) : Source;

public record ImportDataSource(Source Source, IMessageHub Hub)
    : GenericDataSource<ImportDataSource>(Source, Hub)
{
    private ILogger logger = Hub.ServiceProvider.GetRequiredService<ILogger<ImportDataSource>>();
    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };

    public override void Initialize()
    {
        var config = new ImportConfiguration(
            Hub,
            MappedTypes,
            Hub.ServiceProvider.GetRequiredService<ILogger<ImportDataSource>>()
        );
        config = Configurations.Aggregate(config, (c, f) => f.Invoke(c));
        ImportManager importManager = new(config);
        var ret = Workspace.GetStream(Id, GetReference());
        Hub.Schedule(async cancellationToken =>
        {
            var (state, hasError) = await importManager.ImportAsync(
                ImportRequest,
                Workspace.CreateState(Workspace.State?.Store),
                logger,
                cancellationToken
            );

            ret.Initialize(state.Store);
        });
        Streams = Streams.Add(ret);
    }

    private ImmutableList<
        Func<ImportConfiguration, ImportConfiguration>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportConfiguration, ImportConfiguration>>.Empty;

    public ImportDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configurations = Configurations.Add(config) };
}
