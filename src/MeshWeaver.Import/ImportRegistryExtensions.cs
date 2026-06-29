using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Import.Implementation;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import;

/// <summary>
/// Registration and data-source extensions for wiring the import plugin into a message hub
/// and sourcing data contexts from embedded resources.
/// </summary>
public static class ImportExtensions
{
    /// <summary>
    /// Adds the import plugin to the hub with default configuration.
    /// </summary>
    /// <param name="configuration">The hub configuration to extend.</param>
    /// <returns>The extended hub configuration.</returns>
    public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration) =>
        configuration.AddImport(x => x);

    /// <summary>
    /// Adds the import plugin to the hub, registering services, handlers and types on first call
    /// and appending the supplied import configuration.
    /// </summary>
    /// <param name="configuration">The hub configuration to extend.</param>
    /// <param name="importConfiguration">Configures the import builder (formats, readers, validations).</param>
    /// <returns>The extended hub configuration.</returns>
    public static MessageHubConfiguration AddImport(
        this MessageHubConfiguration configuration,
        Func<ImportBuilder, ImportBuilder> importConfiguration
    )
    {
        var lambdas = configuration.GetListOfLambdas();
        if (!lambdas.Any())
            configuration = configuration.WithInitialization(h => h.ServiceProvider.GetRequiredService<ImportManager>());
        var ret = configuration.Set(lambdas.Add(importConfiguration));
        if (lambdas.Any())
            return ret;
        return ret
            .AddData()
            .WithServices(x => x.AddScoped<ImportManager>())
            .AddHandlers()
            .WithTypes(
                typeof(ImportRequest),
                typeof(ImportResponse),
                typeof(Source),
                typeof(StringStream),
                typeof(CollectionSource),
                typeof(EmbeddedResource)
            )
            .WithInitialization(h => h.ServiceProvider.GetRequiredService<ImportManager>())
            ;

    }

    private static MessageHubConfiguration AddHandlers(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithHandler<ImportRequest>(
                (h, d) =>
                {
                    var logger = h.ServiceProvider.GetRequiredService<ILogger<ImportManager>>();
                    logger.LogDebug("ImportRequest handler called for message {MessageId} on hub {HubAddress}", d.Id, h.Address);

                    try
                    {
                        var importManager = h.ServiceProvider.GetRequiredService<ImportManager>();
                        logger.LogDebug("ImportManager resolved successfully for message {MessageId}", d.Id);
                        return importManager.HandleImportRequest(d, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to resolve or execute ImportManager for message {MessageId}", d.Id);
                        throw;
                    }
                }
            );
    }

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
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource>? configuration = null
    ) where T : class
    {
        var source = new EmbeddedResource(typeof(T).Assembly, resource);
        return dataContext.WithDataSource(_ => ConfigureDataSource(configuration ?? (x => x), dataContext.Workspace, source).WithType<T>());
    }

    /// <summary>
    /// Adds a data source that imports from a pre-built <see cref="EmbeddedResource"/> descriptor.
    /// </summary>
    /// <param name="dataContext">The data context to add the source to.</param>
    /// <param name="resource">The embedded resource to import.</param>
    /// <param name="configuration">Configures the resulting import data source.</param>
    /// <returns>The data context with the data source added.</returns>
    public static DataContext FromEmbeddedResource(
        this DataContext dataContext,
        EmbeddedResource resource,
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource> configuration
    )
    {
        return dataContext
            .WithDataSource(_ => ConfigureDataSource(configuration, dataContext.Workspace, resource));
    }

    private static ImportUnpartitionedDataSource ConfigureDataSource(
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource> configuration,
        IWorkspace workspace,
        EmbeddedResource source
    )
    {
        var ret = new ImportUnpartitionedDataSource(source, workspace);
        if (configuration != null)
            ret = configuration.Invoke(ret);

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
    internal static ImmutableList<Func<ImportBuilder, ImportBuilder>> GetListOfLambdas(
        this MessageHubConfiguration config
    ) =>
        config.Get<ImmutableList<Func<ImportBuilder, ImportBuilder>>>() ?? [];
}

/// <summary>
/// An import <see cref="Source"/> identifying a resource embedded in an assembly.
/// </summary>
/// <param name="Assembly">The assembly that contains the embedded resource.</param>
/// <param name="Resource">The resource path within the assembly (without the assembly-name prefix).</param>
public record EmbeddedResource(Assembly Assembly, string Resource) : Source;
