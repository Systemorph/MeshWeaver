using Microsoft.Extensions.DependencyInjection;
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
    /// </summary>
    public static bool TryAddEmbeddingProvider(
        this IServiceCollection services, EmbeddingOptions options)
    {
        var provider = options.CreateEmbeddingProvider();
        if (provider is null)
            return false;
        services.AddSingleton(provider);
        return true;
    }
}
