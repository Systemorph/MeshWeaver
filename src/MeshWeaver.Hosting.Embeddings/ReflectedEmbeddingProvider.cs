using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Embeddings;

/// <summary>
/// The probe-and-delegate half of the Azure Foundry embedding provider, whose implementation
/// RELOCATED to the <c>MeshWeaver.AI.AzureFoundry</c> module ("move all the Azure stuff to
/// modules", 2026-08-20). The platform keeps the SELECTION — <see cref="EmbeddingOptions"/>
/// still says which provider a backend wants — and this shim carries the choice across the
/// module boundary: the real provider is resolved BY NAME at first use, when module assemblies
/// are certainly loaded, so registration order cannot matter and the platform compiles against
/// nothing Azure.
///
/// <para><b>Fail-soft, loudly.</b> A deployment that configures the Azure Foundry provider but
/// has not landed the AzureFoundry module gets null embeddings — the same ILIKE-text-search
/// fallback as an unconfigured endpoint — plus one error log naming the missing module. Vector
/// search degrading is an operational condition; a boot failure would be an outage.</para>
/// </summary>
public sealed class ReflectedEmbeddingProvider(
    string typeName,
    object[] constructorArguments,
    int dimensions,
    ILogger? logger = null) : IEmbeddingProvider
{
    /// <summary>The relocated Azure Foundry provider's assembly-qualified name.</summary>
    public const string AzureFoundryProviderTypeName =
        "MeshWeaver.AI.AzureFoundry.AzureFoundryEmbeddingProvider, MeshWeaver.AI.AzureFoundry";

    private readonly Lazy<IEmbeddingProvider?> inner = new(() =>
    {
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            logger?.LogError(
                "Embedding provider '{TypeName}' is not loaded — its module is not landed on this "
                + "deployment. Embeddings are OFF; queries fall back to text search.", typeName);
            return null;
        }
        try
        {
            return (IEmbeddingProvider)Activator.CreateInstance(type, constructorArguments)!;
        }
        catch (Exception e)
        {
            logger?.LogError(e,
                "Embedding provider '{TypeName}' could not be constructed — embeddings are OFF.",
                typeName);
            return null;
        }
    });

    /// <inheritdoc />
    public int Dimensions => dimensions;

    /// <inheritdoc />
    public Task<float[]?> GenerateEmbeddingAsync(string text) =>
        inner.Value?.GenerateEmbeddingAsync(text) ?? Task.FromResult<float[]?>(null);
}
