using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Embeddings;

/// <summary>
/// Backend-agnostic embedding-provider registration shared by the storage backends
/// (PostgreSQL, Snowflake). Each backend's own <c>AddEmbeddings</c> wraps this and
/// additionally syncs its storage options' vector dimensions.
/// </summary>
public static class EmbeddingExtensions
{
    /// <summary>
    /// Creates the embedding provider selected by <see cref="EmbeddingOptions.Provider"/>:
    /// <list type="bullet">
    /// <item>"Ollama" / "OpenAICompatible" → <see cref="OllamaEmbeddingProvider"/> (local, on-host).</item>
    /// <item>anything else (default) → the Azure Foundry provider (cloud; requires an API key) —
    /// RELOCATED to the MeshWeaver.AI.AzureFoundry module and reached by
    /// <see cref="ReflectedEmbeddingProvider"/>, so the platform compiles against nothing Azure.</item>
    /// </list>
    /// Returns null when no <see cref="EmbeddingOptions.Endpoint"/> is configured (or the
    /// cloud backend lacks an API key) — callers then skip registration and the query path
    /// falls back to ILIKE text search.
    /// </summary>
    public static IEmbeddingProvider? CreateEmbeddingProvider(
        this EmbeddingOptions options, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(options.Endpoint))
            return null;

        return options.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" or "openaicompatible" => new OllamaEmbeddingProvider(
                options.Endpoint, options.Model, options.Dimensions, options.ApiKey,
                TimeSpan.FromSeconds(options.TimeoutSeconds)),
            // Azure Foundry (default) needs a key; without one there is nothing to register.
            // The implementation lives in the MeshWeaver.AI.AzureFoundry MODULE — resolved by
            // name at first use (modules are certainly loaded by then), degrading to null
            // embeddings with a loud log when the module is not landed.
            _ => string.IsNullOrEmpty(options.ApiKey)
                ? null
                : new ReflectedEmbeddingProvider(
                    ReflectedEmbeddingProvider.AzureFoundryProviderTypeName,
                    [options.Endpoint, options.ApiKey, options.Model, options.Dimensions],
                    options.Dimensions,
                    logger),
        };
    }

    /// <summary>
    /// Registers the provider selected by <paramref name="options"/> as the singleton
    /// <see cref="IEmbeddingProvider"/>; no-op when <see cref="CreateEmbeddingProvider"/>
    /// yields null. Returns true when a provider was registered.
    ///
    /// <para>Either way it records the decision as a singleton
    /// <see cref="EmbeddingCapability"/> and registers
    /// <see cref="EmbeddingCapabilityReporter"/>, so the host says once, at startup, whether
    /// semantic search is on and — when it is not — exactly which configuration key would turn it
    /// on. The bool this returns is a composition detail only one caller reads; the log line is
    /// what an operator has.</para>
    ///
    /// <para>🚨 It deliberately does NOT fall back to registering
    /// <see cref="NullEmbeddingProvider"/> when embeddings are off. The PRESENCE of an
    /// <see cref="IEmbeddingProvider"/> registration is the capability signal every consumer reads
    /// (<c>sp.GetService&lt;IEmbeddingProvider&gt;()</c> in both storage backends, and the
    /// content-indexing module's resolve-time <c>enabledWhen</c> gate). A Null default would report
    /// the capability as PRESENT on a deployment that has none: the query path would survive it
    /// (a null vector falls through to ILIKE), but the indexing pipeline would activate against an
    /// embedder that can never embed — re-creating issue #1642 from the other direction.
    /// <see cref="NullEmbeddingProvider"/> stays what it is: an explicit stand-in a caller opts
    /// into, not a silent default.</para>
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="options">The bound <c>Embedding</c> configuration section.</param>
    /// <returns><c>true</c> when a provider was registered.</returns>
    public static bool TryAddEmbeddingProvider(
        this IServiceCollection services, EmbeddingOptions options)
    {
        var provider = options.CreateEmbeddingProvider();
        // Reported from what actually happened, never re-derived — see EmbeddingCapability.From.
        var capability = EmbeddingCapability.From(options, provider is not null);
        // Last one wins, matching the provider registration below — a host wires one backend.
        services.AddSingleton(capability);
        // TryAddEnumerable dedupes by (ServiceType, ImplementationType), so a host that wires two
        // storage backends still reports once.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, EmbeddingCapabilityReporter>());

        if (provider is null)
            return false;
        services.AddSingleton(provider);
        return true;
    }
}
