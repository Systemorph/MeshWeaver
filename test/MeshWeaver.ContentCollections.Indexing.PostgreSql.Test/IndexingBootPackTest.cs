using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql.Test;

/// <summary>
/// Pins the boot-pack contract of the pgvector content-indexing module: installing the assembly
/// via <see cref="MeshBuilder.InstallAssemblies"/> (the <c>Modules:Assemblies</c> path) registers
/// the pipeline with its RESOLVE-TIME activation gate — an unconfigured deployment resolves an
/// inert upload observer (uploads proceed unindexed) instead of faulting on a missing store.
/// </summary>
public class IndexingBootPackTest
{
    private static IServiceCollection InstallModule()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(typeof(PostgresContentIndexingModuleAttribute).Assembly.Location);
        return serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));
    }

    [Fact]
    public void TheAttribute_CarriesTheBuilderHook()
    {
        var attributes = typeof(PostgresContentIndexingModuleAttribute).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .ToList();
        Assert.Contains(attributes, a => a.BuilderConfigurations.Any());
    }

    [Fact]
    public void Unconfigured_ResolvesAnInertUploadObserver()
    {
        var services = InstallModule();
        // An empty configuration — no connection string, no embeddings: the gate must hold.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var observer = provider.GetRequiredService<IContentUploadObserver>();
        // The inert stand-in: uploading indexes nothing and throws nothing.
        observer.OnUploaded("part/content", "docs/note.txt");
        Assert.DoesNotContain("ContentIndexingObserver", observer.GetType().Name);
    }

    [Fact]
    public void Unconfigured_ReindexEntryPoint_FailsActionably()
    {
        var services = InstallModule();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<Graph.ContentIndexingObserver>());
        Assert.Contains("Embedding:Endpoint", ex.Message);
    }
}
