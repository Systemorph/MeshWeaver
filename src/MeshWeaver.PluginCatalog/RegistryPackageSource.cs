using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> that reads a REMOTE MeshWeaver instance acting as the plugin
/// <b>registry</b> over HTTP (the registry's <c>/api/plugins</c> surface), so any installation
/// browses/installs the catalog WITHOUT its own git/GitHub credentials — the registry holds the
/// source access and proxies it (npm/NuGet-style). The registry itself backs those endpoints with a
/// git <see cref="IPackageSource"/> (e.g. <see cref="GitHubPackageSource"/> on the plugins repo).
///
/// <para>🚨 Reactive end-to-end — the HTTP leaves run on the mesh's Http I/O pool, never a bare
/// <c>Observable.FromAsync</c> (mirrors <c>InstanceOAuthService</c>). The <see cref="HttpClient"/>
/// comes from <see cref="IHttpClientFactory"/> when the host registered one, else a single shared
/// long-lived client — never a per-instance <c>new HttpClient()</c> (socket exhaustion).</para>
/// </summary>
public sealed class RegistryPackageSource : IPackageSource
{
    /// <summary>The registry route prefix the endpoints are mapped under.</summary>
    public const string RoutePrefix = "/api/plugins";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared (per Microsoft guidance); a per-call `new HttpClient()` leaks sockets.
    // Immutable shared resource, not a cache, so it does not fall under the no-static-state rule.
    private static readonly HttpClient SharedHttp = new();

    private readonly string _registryUrl;
    private readonly IIoPool _httpPool;
    private readonly HttpClient _http;
    private readonly string _token;

    /// <summary>Creates the source. <paramref name="registryUrl"/> is the registry instance base URL
    /// (e.g. <c>https://memex.meshweaver.cloud</c>); trailing slash is trimmed.
    /// <paramref name="token"/> is the instance token issued when this installation registered with
    /// the registry (see <see cref="PluginRegistryTokens"/>), sent as <c>Authorization: Bearer</c>;
    /// empty → unauthenticated (only an open dev/e2e registry answers).</summary>
    public RegistryPackageSource(IMessageHub hub, string registryUrl, string? token = null)
    {
        _registryUrl = (registryUrl ?? "").TrimEnd('/');
        _token = (token ?? "").Trim();
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.HttpClientName) ?? SharedHttp;
    }

    /// <summary>
    /// The prebuilt-bundle client for the SAME registry, same key (#1664) — set where this source
    /// is constructed, because that is the only place the URL and this installation's instance key
    /// are both known. Non-null is what lets the install orchestrator
    /// (<see cref="CatalogLayoutAreas.InstallOrUpdate"/>) land a module-declaring package's
    /// compiled module as part of the install; null (a test stub, an exotic host) simply means the
    /// module lands on the next boot's reconcile instead.
    /// </summary>
    public PluginBundleClient? Bundles { get; init; }

    private sealed record ListResponse(IReadOnlyList<PackageManifest>? Packages);
    private sealed record FilesResponse(IReadOnlyList<PackageFile>? Files);

    // Per-REQUEST auth header — never on the client: _http can be the process-wide SharedHttp, and
    // mutating its DefaultRequestHeaders would leak this registry's token to every other registry.
    private HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (_token.Length > 0)
            // Typed header, not TryAddWithoutValidation: a malformed configured token (e.g. with a
            // CRLF) must throw here rather than travel as an invalid header.
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(PluginRegistryTokens.Scheme, _token);
        return request;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        _httpPool.Invoke(async ct =>
        {
            var url = $"{_registryUrl}{RoutePrefix}?ref={Uri.EscapeDataString(gitRef ?? "")}";
            using var request = Request(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RegistryResponseException(resp.StatusCode,
                    $"Registry catalog list failed ({(int)resp.StatusCode}): {json}");
            var parsed = JsonSerializer.Deserialize<ListResponse>(json, Json);
            return (IReadOnlyList<PackageManifest>)(parsed?.Packages ?? []);
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        FetchPackageFiles(package, gitRef, null);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
        PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
        _httpPool.Invoke(async ct =>
        {
            // Only the package id is authoritative — the registry resolves the folder from its own
            // curated catalog (see PluginRegistryEndpoints), so a consumer can't reach arbitrary
            // paths; `paths` only FILTERS within that package's own files (the manifest-diff fast
            // path: unchanged files never travel). An old registry ignores the extra field and
            // returns everything — the consumer's manifest diff still upserts only what changed.
            var body = JsonSerializer.Serialize(new { id = package.Id, @ref = gitRef, paths }, Json);
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var request = Request(HttpMethod.Post, $"{_registryUrl}{RoutePrefix}/files", content);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RegistryResponseException(resp.StatusCode,
                    $"Registry file fetch for '{package.Id}' failed ({(int)resp.StatusCode}): {json}");
            var parsed = JsonSerializer.Deserialize<FilesResponse>(json, Json);
            var files = (IReadOnlyList<PackageFile>)(parsed?.Files ?? []);
            if (paths is null)
                return files;
            // Filter locally as well: an OLD registry ignores the `paths` field and returns the
            // whole package — the subset contract must hold regardless of the server's version.
            var wanted = paths as ISet<string> ?? new HashSet<string>(paths, StringComparer.Ordinal);
            return (IReadOnlyList<PackageFile>)files.Where(f => wanted.Contains(f.RelativePath)).ToList();
        });
}

/// <summary>
/// The JSON payload shapes the registry endpoints emit and <see cref="RegistryPackageSource"/> reads
/// — one place so the producer (endpoints) and consumer (source) can't drift.
/// </summary>
public static class PluginRegistryPayloads
{
    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the list-catalog response: <c>{ packages: [PackageManifest…] }</c>.</summary>
    public static string List(IReadOnlyList<PackageManifest> packages) =>
        JsonSerializer.Serialize(new { packages }, Json);

    /// <summary>Serializes the fetch-files response: <c>{ files: [PackageFile…] }</c>.</summary>
    public static string Files(IReadOnlyList<PackageFile> files) =>
        JsonSerializer.Serialize(new { files }, Json);
}

/// <summary>
/// A non-success answer from the registry, carrying the HTTP status so a caller can branch on
/// <b>refused</b> versus <b>temporarily unavailable</b> without matching on the message text —
/// the same shape, and for the same reason, as
/// <see cref="InstanceRegistrationException"/>.
///
/// <para>🚨 <b>Why the status has to survive as data.</b> Every non-2xx used to collapse into a
/// bare <see cref="InvalidOperationException"/> whose only record of the status was the text of
/// its message. <see cref="RegistryUpdateReconciler"/> reasonably reads that type as "a definite
/// HTTP answer (401/403/404) — asking again in two seconds cannot change it" and skips its retry.
/// A <c>503 Instance-key resolution is temporarily unavailable — retry shortly</c> arrived as the
/// very same type, so the one condition the boot retry exists for was the one condition it
/// declined to retry, and a transient registry blip stranded package reconciliation for the whole
/// life of the pod (Systemorph/MeshWeaver#2836). Deriving from <see cref="InvalidOperationException"/>
/// keeps every existing <c>catch</c> behaving as before; the added property is what lets the
/// policy stop conflating the two.</para>
/// </summary>
public sealed class RegistryResponseException(HttpStatusCode statusCode, string message)
    : InvalidOperationException(message)
{
    /// <summary>The HTTP status the registry answered with.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>
    /// Whether <paramref name="statusCode"/> describes a condition that may clear on its own, so
    /// re-asking is worthwhile: any 5xx (the server failed to serve a request it accepts —
    /// including the 503 of #2836), plus 408 and 429, where the server explicitly invites a
    /// retry. This is the conventional transient-HTTP set (the same one Polly's
    /// <c>HandleTransientHttpError</c> uses), chosen so the boundary is a published convention
    /// rather than a judgement call re-litigated at each call site.
    ///
    /// <para>Everything else — 401, 403, 404, 409 — is a decision the registry has already made
    /// and will make identically on the next attempt; retrying it only delays the log line that
    /// names it.</para>
    /// </summary>
    public static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode >= 500
        || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Whether this particular answer is worth re-asking — see <see cref="IsTransient"/>.</summary>
    public bool IsTransientFailure => IsTransient(StatusCode);
}
