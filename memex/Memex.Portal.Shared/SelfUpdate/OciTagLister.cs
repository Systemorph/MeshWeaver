using System.Reactive.Linq;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Lists a repository's tags on an <b>OCI Distribution</b> registry — the fleet's own registry, or
/// the read-through mirror another installation serves at <c>{portal}/v2</c> (#3353,
/// <c>Doc/Architecture/ContainerRegistryInMemex</c>) — for a self-updater whose
/// <see cref="SelfUpdateOptions.Registry"/> is NOT an Azure Container Registry.
///
/// <para><b>The credential is the one this installation already holds.</b> The registry authenticates
/// a caller with its plugin-registry instance key, and a consuming installation already carries
/// exactly that key for the registry it installs modules from
/// (<c>PluginCatalog:Registries:N:Token</c> / legacy <c>PluginCatalog:RegistryToken</c>, or the
/// stored auto-registration credential). So the credential is RESOLVED, never configured a second
/// time: the plugin registry whose host equals the container registry host is the one whose token
/// is presented, through the same <see cref="RegistryTokenResolver"/> the Store uses. No new
/// secret, nothing to rotate twice.</para>
///
/// <para><b>The wire conversation</b> — the bearer handshake, the paginated listing, every page
/// followed — is <see cref="OciRegistryClient.ListTags"/>, the same client
/// <see cref="PluginBundleClient"/> pulls bundles with; this class contributes the credential
/// resolution and the self-update-specific configuration errors.</para>
///
/// <para><b>Failure is an error, never an empty list.</b> A refused credential, an unreachable
/// host or a malformed answer FAULT the observable and the poller records the fault — an empty
/// listing would be read as "nothing to roll to", which is the one silent state #2553 exists to
/// forbid. Compare <c>UnavailableUpdateMechanics.NoRegistry</c>, which answers empty by design
/// because it stands for "no registry configured at all".</para>
///
/// <para>Selected by the poller from <see cref="SelfUpdateOptions.RegistryIsAzureContainerRegistry"/>;
/// the ACR path is untouched.</para>
/// </summary>
public sealed class OciTagLister(
    IMessageHub hub,
    SelfUpdateOptions options,
    ILogger<OciTagLister>? logger = null)
{
    /// <summary>The page size asked for. The registry may answer fewer; every page is followed.</summary>
    public const int PageSize = OciRegistryClient.TagPageSize;

    /// <summary>The named HttpClient a host may configure; the shared fallback is used otherwise.</summary>
    public const string HttpClientName = "MeshWeaver.SelfUpdate.Oci";

    /// <summary>
    /// Every tag on <paramref name="repository"/> at <see cref="SelfUpdateOptions.Registry"/>.
    /// Cold; emits once; FAULTS on any answer that is not a complete listing.
    /// </summary>
    public IObservable<IReadOnlyList<string>> ListTags(string repository)
    {
        var registry = (options.Registry ?? string.Empty).Trim().TrimEnd('/');
        if (registry.Length == 0)
            return Observable.Throw<IReadOnlyList<string>>(new InvalidOperationException(
                "SelfUpdate:Registry is empty — there is no registry to list tags on."));

        return new OciRegistryClient(
                hub, registry, ResolveCredential(registry), options.RegistryUsername, HttpClientName, logger)
            .ListTags(repository);
    }

    /// <summary>
    /// The plugin-registry credential for the registry whose host IS the container registry —
    /// the reuse this lister exists for. Faults, naming the host, when no configured plugin
    /// registry lives there or when the resolved credential is empty: a listing without a
    /// credential could only ever be a 401, so the misconfiguration is said once here instead of
    /// on every check as a refused handshake. Cold: the fault is raised at subscription, before
    /// the client sends a byte.
    /// </summary>
    private IObservable<string> ResolveCredential(string registry) =>
        Observable.Defer(() =>
        {
            var catalog = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
            var registries = RegistryTokenResolver.WithLegacyTokens(catalog, catalog.EffectiveRegistries);
            var match = registries.FirstOrDefault(r => HostOf(r.Url) is { } host
                && string.Equals(host, registry, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Observable.Throw<string>(new InvalidOperationException(
                    $"SelfUpdate:Registry is '{registry}', an OCI registry the self-updater authenticates "
                    + "with this installation's plugin-registry instance key — but no plugin registry "
                    + "(PluginCatalog:Registries / PluginCatalog:RegistryUrl) is configured on that host, "
                    + "so there is no key to present. The mirror an installation pulls images from is the "
                    + "installation it installs modules from; configure both on the same host, or set "
                    + "SelfUpdate:Registry back to the upstream Azure Container Registry."));

            var resolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
            var credential = resolver is null
                ? Observable.Return(match.Token?.Trim() ?? string.Empty)
                : resolver.ResolveToken(match);

            return credential.Select(token => token.Length > 0
                ? token
                : throw new InvalidOperationException(
                    $"No instance key could be resolved for the plugin registry at {match.Url}, so the "
                    + $"container registry {registry} cannot be listed. The key arrives as "
                    + "PluginCatalog:Registries:N:Token (or the legacy PluginCatalog:RegistryToken), or "
                    + "through auto-registration with PluginCatalog:BootstrapKey."));
        });

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}
