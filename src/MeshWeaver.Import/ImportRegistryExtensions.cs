﻿using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Import.Implementation;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Import;

public static class ImportExtensions
{
    public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration) =>
        configuration.AddImport(x => x);

    public static MessageHubConfiguration AddImport(
        this MessageHubConfiguration configuration,
        Func<ImportConfiguration, ImportConfiguration> importConfiguration
    )
    {
        var lambdas = configuration.GetListOfLambdas();
        var ret = configuration.Set(lambdas.Add(importConfiguration));
        if (lambdas.Any())
            return ret;
        return ret
            .AddActivities()
            .AddData()
            .WithServices(x => x.AddScoped<ImportManager>())
            .AddHandlers()
            ;

    }

    private static MessageHubConfiguration AddHandlers(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithHandler<ImportRequest>(
                (h,d) =>
                    h.ServiceProvider
                        .GetRequiredService<ImportManager>()
                        .HandleImportRequest(d)
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
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource> configuration = null
    ) where T : class
    {
        var source = new EmbeddedResource(typeof(T).Assembly, resource);
        return dataContext.WithDataSource(_ => ConfigureDataSource(configuration, dataContext.Workspace, source).WithType<T>(), source);
    }

    public static DataContext FromEmbeddedResource(
        this DataContext dataContext,
        EmbeddedResource resource,
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource> configuration
    )
    {
        return dataContext
            .WithDataSource(_ => ConfigureDataSource(configuration, dataContext.Workspace, resource), resource);
    }

    private static ImportUnpartitionedDataSource ConfigureDataSource(
        Func<ImportUnpartitionedDataSource, ImportUnpartitionedDataSource> configuration,
        IWorkspace workspace,
        EmbeddedResource source
    )
    {
        var ret = new ImportUnpartitionedDataSource(source, workspace);
        if(configuration != null)
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

    internal static ImmutableList<Func<ImportConfiguration, ImportConfiguration>> GetListOfLambdas(
        this MessageHubConfiguration config
    ) =>
        config.Get<ImmutableList<Func<ImportConfiguration, ImportConfiguration>>>() ?? [];
}

public record EmbeddedResource(Assembly Assembly, string Resource) : Source;
