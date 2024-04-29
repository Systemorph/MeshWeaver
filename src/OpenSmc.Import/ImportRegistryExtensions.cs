using System.Data;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using AngleSharp.Io;
using DocumentFormat.OpenXml.Bibliography;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
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
            hub => ConfigureDatgaSource(configuration, hub, source)
        );
    }

    private static ImportDataSource ConfigureDatgaSource(
        Func<ImportDataSource, ImportDataSource> configuration,
        IMessageHub hub,
        EmbeddedResource source
    )
    {
        var ret = configuration.Invoke(
            new(source, new ImportConfiguration(hub, new Workspace(hub, source)))
        );

        var mappedTypes = ret.MappedTypes;
        if (!mappedTypes.Any())
            throw new DataException("Data Source must contain sourced data types.");
        if (mappedTypes.Count == 1)
            ret = ret.WithRequest(r => r.WithEntityType(mappedTypes.First()));
        if (ret.Configuration.GetFormat(ImportFormat.Default) == null)
            ret = ret.WithImportConfiguration(config =>
                config.WithFormat(
                    ImportFormat.Default,
                    f => f.WithAutoMappingsForTypes(mappedTypes)
                )
            );

        // ret.Configuration.Workspace.Initialize(new(mappedTypes));

        return ret;
    }
}

public record EmbeddedResource(Assembly Assembly, string Resource) : Source;

public record ImportDataSource(Source Source, ImportConfiguration Configuration)
    : GenericDataSource<ImportDataSource>(Source, Configuration.Hub)
{
    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };

    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        ImportManager importManager = new(Configuration);
        var ret = GetInitialChangeStream();
        Hub.Schedule(async cancellationToken =>
        {
            var log = await importManager.ImportAsync(ImportRequest, cancellationToken);

            foreach (var changeStream in ret)
                changeStream.Initialize(importManager.Configuration.Workspace.State.Store);
        });
        return ret;
    }

    public ImportDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configuration = config.Invoke(Configuration) };
}
