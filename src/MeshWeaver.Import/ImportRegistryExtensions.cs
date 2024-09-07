using System.Data;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Import.Implementation;
using MeshWeaver.Messaging;

namespace MeshWeaver.Import;

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
            .AddPlugin<ImportPlugin>(hub => new(hub, importConfiguration))
        ;

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
            hub => ConfigureDataSource(configuration, dataContext.Workspace, source)
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
            hub => ConfigureDataSource(configuration, dataContext.Workspace, resource)
        );
    }

    private static ImportDataSource ConfigureDataSource(
        Func<ImportDataSource, ImportDataSource> configuration,
        IWorkspace workspace,
        EmbeddedResource source
    )
    {
        var ret = configuration.Invoke(new(source, workspace));

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
