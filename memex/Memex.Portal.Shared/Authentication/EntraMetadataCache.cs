using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The Entra discovery documents and signing keys the step-up rung validates against — ONE
/// <see cref="ConfigurationManager{T}"/> per metadata address for the life of the mesh, so the keys
/// are fetched once and refreshed on the library's own schedule instead of on every approval.
/// A mesh-scoped singleton (never a static): registered by <c>MemexConfiguration</c>, resolved from
/// the mesh hub.
///
/// <para><b>No latched failure.</b> <see cref="ConfigurationManager{T}"/> caches only a SUCCESSFUL
/// retrieval; a fetch that fails throws to that caller and the next call fetches again — so a
/// transient outage of the metadata endpoint fails the step-ups it overlaps (fail closed) and
/// never poisons the ones after it.</para>
/// </summary>
/// <param name="http">The client the managers fetch with; null ⇒ a pooled client of this cache's own.</param>
internal sealed class EntraMetadataCache(HttpClient? http = null)
{
    private readonly HttpClient client = http ?? new HttpClient();

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> managers =
        new(StringComparer.Ordinal);

    /// <summary>The manager for one metadata address.</summary>
    /// <param name="metadataAddress">The <c>.well-known/openid-configuration</c> URL.</param>
    /// <returns>The shared manager.</returns>
    public ConfigurationManager<OpenIdConnectConfiguration> For(string metadataAddress) =>
        managers.GetOrAdd(metadataAddress, address => new ConfigurationManager<OpenIdConnectConfiguration>(
            address, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(client) { RequireHttps = true }));
}
