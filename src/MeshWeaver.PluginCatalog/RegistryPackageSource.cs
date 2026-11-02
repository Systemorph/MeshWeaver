using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
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
public sealed class RegistryPackageSource : IPackageSource, IOriginatedPackageSource
{
    /// <summary>The registry route prefix the endpoints are mapped under.</summary>
    public const string RoutePrefix = "/api/plugins";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared (per Microsoft guidance); a per-call `new HttpClient()` leaks sockets.
    // Immutable shared resource, not a cache, so it does not fall under the no-static-state rule.
    private static readonly HttpClient SharedHttp = new();

    private readonly IMessageHub _hub;
    private readonly string _registryUrl;
    private readonly IIoPool _httpPool;
    private readonly HttpClient _http;
    private readonly IObservable<string> _credential;

    /// <summary>Creates the source. <paramref name="registryUrl"/> is the registry instance base URL
    /// (e.g. <c>https://memex.meshweaver.cloud</c>); trailing slash is trimmed.
    /// <paramref name="token"/> is the instance token issued when this installation registered with
    /// the registry (see <see cref="PluginRegistryTokens"/>), sent as <c>Authorization: Bearer</c>;
    /// empty → unauthenticated (only an open dev/e2e registry answers).
    ///
    /// <para>🚨 A FIXED credential: use it only for one that does not expire (a durable
    /// <c>mwi_</c> key, a test token). An exchanged sync token expires, and a source holding one
    /// refuses every request after that with the registry's definitive 401 (MeshWeaver#7790) —
    /// pass the credential SOURCE instead (<see cref="WithCredential"/>).</para></summary>
    public RegistryPackageSource(IMessageHub hub, string registryUrl, string? token = null)
        : this(hub, registryUrl, Observable.Return(token ?? ""))
    {
    }

    /// <summary>
    /// A source over a credential SOURCE that is read once PER REQUEST.
    ///
    /// <para>🚨 MeshWeaver#7790: the credential a registry is read with is usually a short-lived
    /// exchanged token (<see cref="RegistryTokenResolver.Credential"/>), while a source lives as long
    /// as the pass that built it — a boot reconcile, a default install over dozens of packages.
    /// Captured once, the token expired mid-pass and every later request was refused 401 ("a
    /// registered instance key is required") although the instance key itself was valid. Read per
    /// request, each request presents a token that is live when it is sent.</para>
    /// </summary>
    /// <param name="hub">The calling hub (I/O pool, HTTP client factory).</param>
    /// <param name="registryUrl">The registry instance base URL; a trailing slash is trimmed.</param>
    /// <param name="credential">COLD; subscribed once per request, and its first value is presented
    /// as <c>Authorization: Bearer</c>. Empty → unauthenticated.</param>
    /// <param name="bundles">The prebuilt-bundle client for the same registry (<see cref="Bundles"/>),
    /// or null.</param>
    /// <returns>The source.</returns>
    /// <remarks>A named factory, never a competing constructor: a second public constructor whose
    /// third parameter is a reference type would make the existing
    /// <c>new RegistryPackageSource(hub, url, null)</c> ambiguous (CS0121).</remarks>
    public static RegistryPackageSource WithCredential(
        IMessageHub hub, string registryUrl, IObservable<string> credential, PluginBundleClient? bundles = null) =>
        new(hub, registryUrl, credential ?? throw new ArgumentNullException(nameof(credential))) { Bundles = bundles };

    private RegistryPackageSource(IMessageHub hub, string registryUrl, IObservable<string> credential)
    {
        _hub = hub;
        _registryUrl = (registryUrl ?? "").TrimEnd('/');
        _credential = credential
            .Take(1)
            .Select(token => (token ?? "").Trim());
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.HttpClientName) ?? SharedHttp;
    }

    /// <summary>The registry this source reads, as an install ORIGIN (MeshWeaver#6526): a package
    /// installed through it records this, so an update watcher on the same instance never binds it
    /// to a repository that happens to list the same id.</summary>
    string IOriginatedPackageSource.Origin => InstallOrigin.ForRegistry(_registryUrl);

    /// <summary>
    /// The prebuilt-bundle client for the SAME registry, same key (#1664) — set where this source
    /// is constructed, because that is the only place the URL and this installation's instance key
    /// are both known. Non-null is what lets the install orchestrator
    /// (<see cref="CatalogLayoutAreas.InstallOrUpdate"/>) land a module-declaring package's
    /// compiled module as part of the install; null (a test stub, an exotic host) simply means the
    /// module lands on the next boot's reconcile instead.
    /// </summary>
    public PluginBundleClient? Bundles { get; init; }

    // Per-REQUEST auth header — never on the client: _http can be the process-wide SharedHttp, and
    // mutating its DefaultRequestHeaders would leak this registry's token to every other registry.
    private static HttpRequestMessage Request(string token, HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (token.Length > 0)
            // Typed header, not TryAddWithoutValidation: a malformed configured token (e.g. with a
            // CRLF) must throw here rather than travel as an invalid header.
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(PluginRegistryTokens.Scheme, token);
        return request;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        ListCatalog(gitRef).Select(listing => listing.Packages);

    /// <summary>
    /// The registry's whole answer for this instance (#4097): the packages it is granted AND the
    /// pre-installed packages the registry declares in its default set that its PLAN refuses, as
    /// typed <see cref="PlanTierRefusal"/>s. <see cref="ListPackages"/> is the first half and is
    /// what every existing consumer keeps reading — a refused package is never mistaken for an
    /// installable one by the reconciler, the update watcher or the Store's count. The two surfaces
    /// that must SAY the refusal (the unattended default install, which records it, and the package
    /// card, which renders it) read this form.
    /// </summary>
    public IObservable<RegistryListing> ListCatalog(string gitRef) =>
        _credential.SelectMany(token => _httpPool.Invoke(async ct =>
        {
            var url = $"{_registryUrl}{RoutePrefix}?ref={Uri.EscapeDataString(gitRef ?? "")}";
            using var request = Request(token, HttpMethod.Get, url);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RegistryResponseException(resp.StatusCode,
                    $"Registry catalog list failed ({(int)resp.StatusCode}): {json}");
            return PluginRegistryPayloads.ParseList(json);
        }))
        // 🚨 A successful read is the EVENT a deferred boot reconcile waits for (#2888). Every
        // registry contact this installation makes — a catalog open, an install, the Store's
        // count, the boot reconcile itself — comes through here, so reporting the read from this
        // one place is what lets the reconciler drain a registry that was down at boot without a
        // timer and without every caller having to remember to. A no-op unless that registry is
        // pending; the reconciler runs off this thread and never delays the read's subscriber.
        // Gated on the hub's run level, never wrapped in a catch: once the hub has begun quiescing its
        // service scope is being torn down and a resolve would throw ObjectDisposedException INTO the
        // caller's successful read. A read during teardown has no boot reconcile left to drain, so it
        // simply does not report.
        .Do(listing =>
        {
            if (_hub.RunLevel > MessageHubRunLevel.Started)
                return;
            _hub.ServiceProvider.GetService<RegistryUpdateReconciler>()
                ?.OnFeedRead(_registryUrl, gitRef, listing.Packages);
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        FetchPackageFiles(package, gitRef, null);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
        PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
        FetchPackage(package, gitRef, paths).Select(fetched => fetched.Files);

    /// <inheritdoc />
    /// <remarks>The commit is the one the REGISTRY reports it served (<c>commit</c> on the
    /// <c>/api/plugins/files</c> answer, MeshWeaver#3848). A registry that predates it answers
    /// none, and the install then records the requested ref, as before.</remarks>
    public IObservable<PackageFetch> FetchPackage(
        PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
        _credential.SelectMany(token => _httpPool.Invoke(async ct =>
        {
            // Only the package id is authoritative — the registry resolves the folder from its own
            // curated catalog (see PluginRegistryEndpoints), so a consumer can't reach arbitrary
            // paths; `paths` only FILTERS within that package's own files (the manifest-diff fast
            // path: unchanged files never travel). An old registry ignores the extra field and
            // returns everything — the consumer's manifest diff still upserts only what changed.
            var body = JsonSerializer.Serialize(new { id = package.Id, @ref = gitRef, paths }, Json);
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var request = Request(token, HttpMethod.Post, $"{_registryUrl}{RoutePrefix}/files", content);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RegistryResponseException(resp.StatusCode,
                    $"Registry file fetch for '{package.Id}' failed ({(int)resp.StatusCode}): {json}");
            var parsed = PluginRegistryPayloads.ParseFiles(json);
            if (paths is null)
                return parsed;
            // Filter locally as well: an OLD registry ignores the `paths` field and returns the
            // whole package — the subset contract must hold regardless of the server's version.
            var wanted = paths as ISet<string> ?? new HashSet<string>(paths, StringComparer.Ordinal);
            return parsed with
            {
                Files = parsed.Files.Where(f => wanted.Contains(f.RelativePath)).ToImmutableList(),
            };
        }));
}

/// <summary>
/// The JSON payload shapes the registry endpoints emit and <see cref="RegistryPackageSource"/> reads
/// — one place so the producer (endpoints) and consumer (source) can't drift.
/// </summary>
public static class PluginRegistryPayloads
{
    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the list-catalog response: <c>{ packages: [PackageManifest…] }</c>, plus
    /// <c>refused: [PlanTierRefusal…]</c> when the registry has plan-tier refusals to report
    /// (#4097). An old consumer deserializes only <c>packages</c> and ignores the sibling; an old
    /// registry never writes it and the consumer reads an empty list — additive both ways.</summary>
    public static string List(IReadOnlyList<PackageManifest> packages, IReadOnlyList<PlanTierRefusal>? refused = null) =>
        refused is { Count: > 0 }
            ? JsonSerializer.Serialize(new { packages, refused }, Json)
            // Nothing refused ⇒ the member is not written at all — the pre-#4097 bytes exactly.
            : JsonSerializer.Serialize(new { packages }, Json);

    /// <summary>Parses the list-catalog response <see cref="List"/> wrote.</summary>
    public static RegistryListing ParseList(string json)
    {
        var parsed = JsonSerializer.Deserialize<ListResponse>(json, Json);
        return new RegistryListing
        {
            Packages = parsed?.Packages ?? [],
            Refused = parsed?.Refused ?? [],
        };
    }

    private sealed record ListResponse(
        IReadOnlyList<PackageManifest>? Packages, IReadOnlyList<PlanTierRefusal>? Refused);

    /// <summary>Serializes the fetch-files response: <c>{ files: [PackageFile…] }</c>.</summary>
    public static string Files(IReadOnlyList<PackageFile> files) =>
        JsonSerializer.Serialize(new { files }, Json);

    /// <summary>
    /// Serializes the fetch-files response with the commit the registry served the files at:
    /// <c>{ files: [PackageFile…], commit: "&lt;sha&gt;" }</c> (MeshWeaver#3848 — the install
    /// record stores it, and the combo verifier checks the installed content against it). A
    /// commit that is not a full sha is not written, so the answer is the pre-#3848 bytes exactly;
    /// an old consumer ignores the sibling — additive both ways.
    /// </summary>
    /// <param name="fetched">The files and the commit they were read at.</param>
    /// <returns>The response body.</returns>
    public static string Files(PackageFetch fetched) =>
        PackageFetch.IsCommitSha(fetched.Commit)
            ? JsonSerializer.Serialize(new { files = fetched.Files, commit = fetched.Commit }, Json)
            : Files(fetched.Files);

    /// <summary>Parses the fetch-files response <see cref="Files(PackageFetch)"/> wrote. A commit
    /// that is absent (an old registry) or not a full sha parses as <c>null</c>.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>The files and the served commit, when named.</returns>
    public static PackageFetch ParseFiles(string json)
    {
        var parsed = JsonSerializer.Deserialize<FilesResponse>(json, Json);
        return new PackageFetch(
            parsed?.Files ?? [],
            PackageFetch.IsCommitSha(parsed?.Commit) ? parsed!.Commit : null);
    }

    private sealed record FilesResponse(ImmutableList<PackageFile>? Files, string? Commit);
}

/// <summary>
/// One registry listing as the consumer sees it (#4097): what it may pull, and what the registry
/// declares in its default set but its plan refuses. Init properties rather than positional
/// parameters so a later field is not a binary break for a host compiled against this shape.
/// </summary>
public sealed record RegistryListing
{
    /// <summary>The packages this instance is granted and its plan covers — the catalog.</summary>
    public IReadOnlyList<PackageManifest> Packages { get; init; } = [];

    /// <summary>The pre-installed packages the registry declares in this instance's default set
    /// whose tier the instance's plan does not cover. Empty from a registry that predates #4097.</summary>
    public IReadOnlyList<PlanTierRefusal> Refused { get; init; } = [];
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
        TransientRegistryFailure.IsTransient(statusCode);

    /// <summary>Whether this particular answer is worth re-asking — see <see cref="IsTransient"/>.</summary>
    public bool IsTransientFailure => IsTransient(StatusCode);
}
