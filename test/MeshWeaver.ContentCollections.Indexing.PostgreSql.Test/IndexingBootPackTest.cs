using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
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
    /// <summary>
    /// The module's registrations behind the container the portal actually runs — Autofac. This is not
    /// a detail: Autofac REFUSES a delegate registration that returns null (it throws
    /// "An exception was thrown while activating λ:T" out of <c>GetService</c>), so the
    /// "resolves to nothing when the capability is off" contract cannot be expressed as a
    /// null-returning factory and only this container proves the inert stand-in works.
    /// </summary>
    private static IServiceProvider UnconfiguredProvider()
    {
        var services = InstallModule();
        // An empty configuration — no connection string, no embeddings: the gate must hold.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services.CreateMeshWeaverServiceProvider();
    }

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

    // ── #1642: an unconfigured deployment must ANSWER "indexing is off", never throw ──

    [Fact]
    public void Unconfigured_ChunkStoreAndEmbedder_ResolveInert_NeverThrow()
    {
        var provider = UnconfiguredProvider();
        using var disposable = provider as IDisposable;

        // The regression: this resolution used to run the CONCRETE pgvector factory, whose first act is
        // GetRequiredService<IEmbeddingProvider>() — unregistered on an unconfigured deployment — so
        // every consumer (search_chunks, the Document blocks view, the settings tab) got
        // "The requested service 'IEmbeddingProvider' has not been registered" instead of an answer.
        var store = provider.GetService<IChunkedContentVectorStore>();
        var embedder = provider.GetService<IChunkEmbedder>();

        Assert.IsAssignableFrom<IInertContentIndex>(store);
        Assert.IsAssignableFrom<IInertContentIndex>(embedder);

        // …and the availability helper maps the stand-ins back to the null every consumer branches on.
        Assert.Null(provider.GetActiveChunkStore());
        Assert.Null(provider.GetActiveChunkEmbedder());
    }

    [Fact]
    public async Task Unconfigured_ChunkSearch_ReturnsEmptyEnvelopeNamingWhatToConfigure()
    {
        var provider = UnconfiguredProvider();
        using var disposable = provider as IDisposable;
        var store = provider.GetService<IChunkedContentVectorStore>();
        var embedder = provider.GetService<IChunkEmbedder>();

        // Both query shapes the tool accepts: anchored at a node path, and the namespace: grammar.
        foreach (var (query, anchor) in new[]
                 {
                     ("accrued benefit obligation", "part/Space"),
                     ("namespace:part/Space/content scope:subtree accrued benefit obligation", null),
                 })
        {
            var result = await ContentChunkSearch.Search(store, embedder, query, anchor)
                .FirstAsync().ToTask(TestContext.Current.CancellationToken);
            var json = ContentChunkSearch.ToJson(result);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
            Assert.Empty(doc.RootElement.GetProperty("results").EnumerateArray());
            var message = doc.RootElement.GetProperty("message").GetString();
            // A capability that is switched off must SAY so, and name what to configure — an opaque
            // failure sends the next reader hunting a data bug.
            Assert.Contains("not active on this deployment", message);
            Assert.Contains("Embedding:Endpoint", message);
        }
    }

    [Fact]
    public async Task Unconfigured_ChunkSearch_NeverEmbeds()
    {
        var provider = UnconfiguredProvider();
        using var disposable = provider as IDisposable;

        // The inert embedder faults if it is ever asked to embed — reaching it would mean a caller
        // skipped the availability check and ranked a meaningless zero vector. The search must settle
        // on the "off" envelope without touching it.
        var result = await ContentChunkSearch.Search(
                provider.GetService<IChunkedContentVectorStore>(),
                provider.GetService<IChunkEmbedder>(),
                "anything at all", "part/Space")
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.NotNull(result.Message);
    }
}
