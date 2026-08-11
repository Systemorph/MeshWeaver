using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Server-side reader of a page's Open Graph metadata (<c>og:title</c> / <c>og:description</c> /
/// <c>og:image</c>) for the <see cref="OgCardLayoutArea"/> link-preview cards — the inbound
/// counterpart of the portal's own outbound SEO head.
///
/// <para><b>Reactive to the I/O boundary.</b> The HTTP leaf runs on the mesh's
/// <see cref="IoPoolNames.Http"/> pool via the eager promise-cache
/// (<see cref="IoPoolExtensions.Run{T}"/>), never a bare <c>Observable.FromAsync</c>. Each URL is
/// fetched ONCE per mesh lifetime and replayed to every later subscriber from an instance
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> — a mesh-scoped singleton (registered in
/// <c>GraphConfigurationExtensions.AddGraph</c>), never static state. A FAILED fetch surfaces as
/// <see cref="OpenGraphPreview.Unavailable"/> and evicts its cache entry, so the next
/// page view retries once — no timer, no retry loop.</para>
///
/// <para><b>Guarded against server-side request forgery.</b> Card targets are authored in
/// markdown, so this service will be pointed at arbitrary URLs. Only <c>http(s)</c> is fetched,
/// and literal loopback / private / link-local addresses (the cloud metadata endpoint,
/// cluster-internal services) are refused without a request. Hostnames are not DNS-resolved for
/// the check — the fetch only ever extracts head metadata, never returns page content.</para>
/// </summary>
public sealed class OpenGraphPreviewService
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client this service asks for; hosts
    /// may configure it (resilience, proxy). Unconfigured names resolve to a default client.</summary>
    public const string HttpClientName = "OpenGraphPreview";

    /// <summary>Per-fetch deadline. The og tags sit in the first bytes of the head; a page that
    /// cannot deliver them within this budget gets the URL-only fallback card. Bounded so a dead
    /// target cannot park an Http pool slot for the shared client's full 100 s default.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Read cap — the head is all that is parsed, a body is never downloaded.</summary>
    private const int MaxResponseBytes = 262_144;

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared (per Microsoft guidance); a per-call `new HttpClient()` leaks sockets.
    // Immutable shared resource, not a cache, so it does not fall under the no-static-state rule.
    // Auto-redirects are OFF, matching the named-client registration in AddGraph: a public target
    // 302-ing to a private address must not be followed (a redirecting page just gets the
    // URL-only fallback card — authors embed the canonical URL).
    private static readonly HttpClient SharedHttp =
        new(new HttpClientHandler { AllowAutoRedirect = false });

    private readonly Func<IIoPool> pool;
    private readonly Func<HttpClient> http;
    private readonly bool allowLoopback;

    /// <summary>The per-URL promise cache: instance state on this mesh-scoped singleton, so it
    /// dies with the mesh.</summary>
    private readonly ConcurrentDictionary<string, IObservable<OpenGraphPreview>> cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// DI constructor. Dependencies resolve lazily off the service provider so construction never
    /// races DI wiring (same idiom as <see cref="DeckSlidesCache"/>).
    /// </summary>
    /// <param name="serviceProvider">The mesh's root service provider.</param>
    public OpenGraphPreviewService(IServiceProvider serviceProvider)
        : this(
            () => serviceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                  ?? IoPool.Unbounded,
            () => serviceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName)
                  ?? SharedHttp)
    {
    }

    /// <summary>
    /// Seam constructor (unit tests via InternalsVisibleTo): inject the pool and client directly.
    /// <paramref name="allowLoopback"/> lets a test point the service at its local listener,
    /// which the production guard refuses.
    /// </summary>
    internal OpenGraphPreviewService(Func<IIoPool> pool, Func<HttpClient> http, bool allowLoopback = false)
    {
        this.pool = pool;
        this.http = http;
        this.allowLoopback = allowLoopback;
    }

    /// <summary>
    /// The Open Graph preview of <paramref name="url"/> — from the promise cache when warm, else
    /// fetched once on the Http pool and replayed to every subscriber. Never errors: an
    /// unfetchable or failed target emits <see cref="OpenGraphPreview.Unavailable"/> (and a
    /// failure evicts its entry so the next subscriber retries once).
    /// </summary>
    /// <param name="url">The absolute http(s) URL to preview.</param>
    /// <returns>A single-emission observable carrying the preview.</returns>
    public IObservable<OpenGraphPreview> Get(string url) => cache.GetOrAdd(url, Fetch);

    private IObservable<OpenGraphPreview> Fetch(string url)
    {
        if (!IsFetchable(url))
            return Observable.Return(OpenGraphPreview.Unavailable(url));

        return pool().Run(ct => FetchAsync(url, ct))
            // A failed fetch is a normal state for a card (target down, 404, DNS): surface the
            // URL-only fallback and EVICT the entry — the next page view's subscriber re-runs
            // the fetch once. TryRemove is idempotent across concurrent subscribers.
            .Catch<OpenGraphPreview, Exception>(ex =>
            {
                cache.TryRemove(url, out _);
                return Observable.Return(OpenGraphPreview.Unavailable(url));
            });
    }

    private async Task<OpenGraphPreview> FetchAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(FetchTimeout);

        // 🚨 Second SSRF gate, DNS-side: IsFetchable vets LITERAL addresses, but a hostname that
        // RESOLVES to a private/link-local address (wildcard DNS like 169.254.169.254.nip.io)
        // would pass it. Resolve here — already off-hub on the Http pool — and refuse when ANY
        // resolved address is private: the request never leaves the process. (A rebinding race
        // between this resolve and the client's own remains theoretically possible; the practical
        // wildcard-DNS bypass is closed.)
        if (!allowLoopback
            && Uri.TryCreate(url, UriKind.Absolute, out var target)
            && !IPAddress.TryParse(target.Host.Trim('[', ']'), out _))
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(target.Host, cts.Token)
                .ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocal))
                throw new InvalidOperationException(
                    $"Refusing to fetch '{target.Host}': resolves to a private or local address.");
        }

        // Per-REQUEST headers — never on the client: it can be the process-wide SharedHttp.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        request.Headers.TryAddWithoutValidation("User-Agent", "MeshWeaver-OgCard/1.0");

        using var response = await http()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        var html = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        return OpenGraphHtmlParser.Parse(url, html);
    }

    /// <summary>
    /// Whether the URL may be fetched at all: absolute http(s), and not a literal loopback /
    /// private / link-local address (the SSRF surface a markdown-authored target could otherwise
    /// reach — 169.254.169.254 metadata, cluster-internal services).
    /// </summary>
    internal bool IsFetchable(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (allowLoopback)
            return true;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            return !IsPrivateOrLocal(address);
        return true;
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,                                   // 10.0.0.0/8
            172 => bytes[1] >= 16 && bytes[1] <= 31,      // 172.16.0.0/12
            192 => bytes[1] == 168,                       // 192.168.0.0/16
            169 => bytes[1] == 254,                       // 169.254.0.0/16 (metadata endpoint)
            _ => false,
        };
    }
}
