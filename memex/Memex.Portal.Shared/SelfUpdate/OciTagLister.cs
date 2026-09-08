using System.Net;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Lists a repository's tags on an <b>OCI Distribution</b> registry — the read-through mirror
/// another installation serves at <c>{portal}/v2</c> (#3353,
/// <c>Doc/Architecture/ContainerRegistryInMemex</c>) — for a self-updater whose
/// <see cref="SelfUpdateOptions.Registry"/> is NOT an Azure Container Registry.
///
/// <para><b>The credential is the one this installation already holds.</b> The mirror authenticates
/// a caller with its plugin-registry instance key, and a consuming installation already carries
/// exactly that key for the registry it installs modules from
/// (<c>PluginCatalog:Registries:N:Token</c> / legacy <c>PluginCatalog:RegistryToken</c>, or the
/// stored auto-registration credential). So the credential is RESOLVED, never configured a second
/// time: the plugin registry whose host equals the container registry host is the one whose token
/// is presented, through the same <see cref="RegistryTokenResolver"/> the Store uses. No new
/// secret, nothing to rotate twice.</para>
///
/// <para><b>The wire conversation</b> is the standard one an OCI client has, and the one the
/// mirror's own tests walk: <c>GET /v2/{repo}/tags/list</c> → <c>401</c> with
/// <c>WWW-Authenticate: Bearer realm="…",service="…"</c> → <c>GET {realm}?service=…&amp;scope=…</c>
/// with <c>Basic base64(user:key)</c> → the bearer the realm answers, on every later request.
/// The listing is PAGINATED (<c>?n=</c>, continued by a relative <c>Link</c> header) and every
/// page is followed: ACR sorts tags lexically and pages at 100, so a lister that stopped after
/// the first page would see the OLDEST hundred builds and print "nothing newer" forever.</para>
///
/// <para><b>Failure is an error, never an empty list.</b> A refused credential, an unreachable
/// host or a malformed answer FAULT the observable and the poller records the fault — an empty
/// listing would be read as "nothing to roll to", which is the one silent state #2553 exists to
/// forbid. Compare <c>UnavailableUpdateMechanics.NoRegistry</c>, which answers empty by design
/// because it stands for "no registry configured at all".</para>
///
/// <para>Reactive at the surface, one async IO leaf inside <see cref="IIoPool"/> — the same shape
/// as <see cref="RegistryTokenResolver"/>'s exchange and the ACR lister's call site in the poller.
/// Selected by the poller from <see cref="SelfUpdateOptions.RegistryIsAzureContainerRegistry"/>;
/// the ACR path is untouched.</para>
/// </summary>
public sealed class OciTagLister(
    IMessageHub hub,
    SelfUpdateOptions options,
    ILogger<OciTagLister>? logger = null)
{
    /// <summary>The page size asked for. The registry may answer fewer; every page is followed.</summary>
    public const int PageSize = 500;

    /// <summary>The named HttpClient a host may configure; the shared fallback is used otherwise.</summary>
    public const string HttpClientName = "MeshWeaver.SelfUpdate.Oci";

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared; a per-call `new HttpClient()` leaks sockets. Immutable shared
    // resource, not a cache, so it does not fall under the no-static-state rule (the same
    // arrangement RegistryTokenResolver makes).
    private static readonly HttpClient SharedHttp = new();

    private static readonly Regex ChallengeParameter = new(
        "(?<key>[a-zA-Z]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex LinkTarget = new("<(?<url>[^>]+)>", RegexOptions.Compiled);

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

        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        var http = hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName)
                   ?? SharedHttp;

        return ResolveCredential(registry)
            .SelectMany(key => pool.Invoke(ct => ListAsync(http, registry, repository, key, ct)));
    }

    /// <summary>
    /// The plugin-registry credential for the registry whose host IS the container registry —
    /// the reuse this lister exists for. Faults, naming the host, when no configured plugin
    /// registry lives there or when the resolved credential is empty: a mirror listing without a
    /// credential could only ever be a 401, so the misconfiguration is said once here instead of
    /// on every check as a refused handshake.
    /// </summary>
    private IObservable<string> ResolveCredential(string registry)
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
    }

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// The async IO leaf — the whole conversation with the registry, run inside the pool slot.
    /// Async by the same rule that makes <c>AcrTagLister.ListTagsAsync</c> async: this is the ONE
    /// place the network is touched, and its sole caller is <see cref="IIoPool.Invoke{T}"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListAsync(
        HttpClient http, string registry, string repository, string key, CancellationToken ct)
    {
        var baseUri = new Uri($"https://{registry}/");
        Uri? next = new Uri(baseUri, $"/v2/{repository}/tags/list?n={PageSize}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var tags = new List<string>();
        string? bearer = null;
        var pages = 0;

        while (next is not null)
        {
            // 🚨 A continuation that points back at a page already read is a registry looping,
            // and following it would be an unbounded poll. Refuse rather than "cap".
            if (!visited.Add(next.AbsoluteUri))
                throw new InvalidOperationException(
                    $"{registry} named {next.AbsoluteUri} as the next tags page twice — the listing loops.");

            using var response = await SendAsync(http, next, bearer, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && bearer is null)
            {
                bearer = await ExchangeAsync(http, baseUri, response, repository, key, ct).ConfigureAwait(false);
                using var retried = await SendAsync(http, next, bearer, ct).ConfigureAwait(false);
                next = await ReadPageAsync(retried, registry, repository, baseUri, tags, ct).ConfigureAwait(false);
            }
            else
            {
                next = await ReadPageAsync(response, registry, repository, baseUri, tags, ct).ConfigureAwait(false);
            }
            pages++;
        }

        logger?.LogDebug("[SelfUpdate] {Count} tag(s) on {Registry}/{Repo} over {Pages} page(s).",
            tags.Count, registry, repository, pages);
        return tags;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Uri uri, string? bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The bearer exchange the challenge names: <c>Basic base64(user:key)</c> at the realm, with
    /// the service and a pull scope for the repository, answered as <c>token</c> or
    /// <c>access_token</c>. A realm that refuses the key is reported as exactly that.
    /// </summary>
    private async Task<string> ExchangeAsync(
        HttpClient http, Uri baseUri, HttpResponseMessage challenge, string repository, string key,
        CancellationToken ct)
    {
        var header = challenge.Headers.WwwAuthenticate
            .FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (header is null)
            throw new InvalidOperationException(
                $"{baseUri.Host} answered 401 without a Bearer challenge — not an OCI registry, or the "
                + "route is not the mirror's.");

        var parameters = ChallengeParameter.Matches(header.Parameter ?? string.Empty)
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value,
                StringComparer.OrdinalIgnoreCase);
        if (!parameters.TryGetValue("realm", out var realm) || realm.Length == 0)
            throw new InvalidOperationException(
                $"{baseUri.Host}'s Bearer challenge names no realm: {header.Parameter}");

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service) && service.Length > 0)
            query.Add("service=" + Uri.EscapeDataString(service));
        query.Add("scope=" + Uri.EscapeDataString(
            parameters.TryGetValue("scope", out var scope) && scope.Length > 0
                ? scope
                : $"repository:{repository}:pull"));
        // A relative realm resolves against the registry; an absolute one is taken as given.
        var realmBase = Uri.TryCreate(realm, UriKind.Absolute, out var absoluteRealm)
            ? absoluteRealm
            : new Uri(baseUri, realm);
        var realmUri = new Uri(
            realmBase.AbsoluteUri + (realmBase.Query.Length > 0 ? "&" : "?") + string.Join('&', query));

        using var request = new HttpRequestMessage(HttpMethod.Get, realmUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.RegistryUsername}:{key}")));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"{realmUri.Host} refused this installation's instance key at its token endpoint "
                + $"({(int)response.StatusCode}). The key presented is the plugin-registry credential "
                + "for that host; if it was rotated, the pods must restart onto the new synced Secret.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{realmUri.Host} answered {(int)response.StatusCode} at its token endpoint.",
                null, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var token = doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : doc.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
        return string.IsNullOrEmpty(token)
            ? throw new InvalidOperationException($"{realmUri.Host}'s token endpoint answered no token.")
            : token;
    }

    /// <summary>Reads one page into <paramref name="tags"/>; returns the next page's URI, or null on the last.</summary>
    private static async Task<Uri?> ReadPageAsync(
        HttpResponseMessage response, string registry, string repository, Uri baseUri,
        List<string> tags, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                $"{registry} refused the bearer it had just issued for {repository} — the credential "
                + "was revoked between the exchange and the listing.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"{registry} has no repository {repository} — or does not serve it: the mirror's "
                + "ContainerImages:Repositories allowlist must name it.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{registry} answered {(int)response.StatusCode} listing {repository}.",
                null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("tags", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"{registry} answered a tags/list body for {repository} with no \"tags\" array.");
        foreach (var t in arr.EnumerateArray())
            if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s)
                tags.Add(s);

        if (!response.Headers.TryGetValues("Link", out var links))
            return null;
        foreach (var link in links)
        {
            if (!link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = LinkTarget.Match(link);
            if (!match.Success)
                continue;
            return new Uri(baseUri, match.Groups["url"].Value);
        }
        return null;
    }
}
