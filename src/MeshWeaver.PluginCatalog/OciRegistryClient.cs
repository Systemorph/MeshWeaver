using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The fleet's one <b>OCI Distribution</b> client — the read side of the registry an installation
/// pulls its images and its plugin bundles from (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>,
/// <c>Doc/Architecture/ContainerRegistryInMemex</c>): the bearer handshake, a manifest by tag or
/// digest, a blob streamed to a destination, and a repository's tags.
///
/// <para><b>One credential, presented one way.</b> The registry edge authenticates a MeshWeaver
/// instance by exchanging the key it presents at memex's <c>/api/instances/token</c>. So the
/// credential a caller hands this client is the plugin-registry token
/// <see cref="RegistryTokenResolver.ResolveToken"/> yields — the same one the Store presents — and
/// it travels as <c>Basic base64(<see cref="DefaultUsername"/>:token)</c> to the realm the
/// registry's <c>401</c> challenge names. Nothing new to configure, nothing to rotate twice.</para>
///
/// <para><b>The wire conversation</b> is the standard one: <c>GET /v2/…</c> → <c>401</c> with
/// <c>WWW-Authenticate: Bearer realm="…",service="…"</c> → <c>GET {realm}?service=…&amp;scope=…</c>
/// with the Basic credential → the bearer the realm answers, cached per pull scope for the life of
/// this client and sent on every later request. A bearer the registry refuses is dropped and
/// exchanged once more; a second refusal is reported as the revocation it is.</para>
///
/// <para><b>Every byte that comes back is verified.</b> A manifest asked for by digest must hash
/// to that digest; one asked for by tag must hash to the <c>Docker-Content-Digest</c> the registry
/// states; a blob must hash to the digest it was asked for. A mismatch FAULTS with
/// <see cref="OciDigestMismatchException"/> — the caller never sees bytes the registry could have
/// swapped, which is what makes "fetch by digest" a proof rather than a name.</para>
///
/// <para><b>Failure is an error, never an empty answer.</b> A refused credential, an unreachable
/// host, a missing manifest or a malformed body FAULT the observable. Reactive at the surface, one
/// async IO leaf per operation inside <see cref="IIoPool"/>: the handshake, manifests and tag
/// listings run on the <see cref="IoPoolNames.Http"/> pool, a blob transfer on
/// <see cref="IoPoolNames.Blob"/> — it is a bulk object read, budgeted like one.</para>
/// </summary>
public sealed class OciRegistryClient
{
    /// <summary>The user name every MeshWeaver instance presents at the registry's realm — the
    /// password is the instance credential, and the registry edge ignores the name.</summary>
    public const string DefaultUsername = "instance";

    /// <summary>The media type of a bundle manifest's <c>&lt;package&gt;.zip</c> layer — the
    /// archive <see cref="PluginBundleClient"/> lands.</summary>
    public const string BundleLayerMediaType = "application/vnd.meshweaver.bundle.v1.zip";

    /// <summary>The media type of a bundle manifest's <c>&lt;package&gt;.module.nupkg</c> layer —
    /// absent for a content-only package.</summary>
    public const string ModuleLayerMediaType = "application/vnd.meshweaver.module.v1.nupkg";

    /// <summary>The media type of a publication's config blob (the sidecars).</summary>
    public const string PublicationConfigMediaType = "application/vnd.meshweaver.publication.v1+json";

    /// <summary>OCI image manifest.</summary>
    public const string ImageManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

    /// <summary>OCI image index.</summary>
    public const string ImageIndexMediaType = "application/vnd.oci.image.index.v1+json";

    /// <summary>Docker schema-2 manifest — what an ACR-hosted image answers with.</summary>
    public const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>The page size a tag listing asks for. The registry may answer fewer; every page is followed.</summary>
    public const int TagPageSize = 500;

    /// <summary>The named <see cref="HttpClient"/> used when the caller names none.</summary>
    public const string DefaultHttpClientName = "MeshWeaver.Oci";

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared; a per-call `new HttpClient()` leaks sockets. Immutable shared
    // resource, not a cache, so it does not fall under the no-static-state rule (the same
    // arrangement RegistryTokenResolver makes).
    private static readonly HttpClient SharedHttp = new();

    private static readonly Regex ChallengeParameter = new(
        "(?<key>[a-zA-Z]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex LinkTarget = new("<(?<url>[^>]+)>", RegexOptions.Compiled);

    private readonly string registry;
    private readonly Uri baseUri;
    private readonly IObservable<string> credential;
    private readonly string username;
    private readonly ILogger? logger;
    private readonly IIoPool httpPool;
    private readonly IIoPool blobPool;
    private readonly HttpClient http;

    // The bearer per pull scope, for the life of THIS client — an instance field on an object a
    // caller constructs per registry, never process-wide state.
    private readonly ConcurrentDictionary<string, string> bearers = new(StringComparer.Ordinal);

    /// <summary>Creates a client for one registry.</summary>
    /// <param name="hub">The hub whose I/O pools and <see cref="IHttpClientFactory"/> the client uses.</param>
    /// <param name="registry">The registry host, with a port when not the default
    /// (<c>cr.meshweaver.cloud</c>, <c>localhost:5000</c>); <c>https</c> unless a scheme is given.</param>
    /// <param name="credential">The credential to present at the realm — cold, resolved once per
    /// operation, so a caller can hand over <see cref="RegistryTokenResolver.ResolveToken"/> and a
    /// refreshed token is what a later operation presents. Faulting it faults the operation before
    /// any request is sent.</param>
    /// <param name="username">The Basic user name; <see cref="DefaultUsername"/> unless the
    /// deployment configured another.</param>
    /// <param name="httpClientName">The named client to take from the factory — a caller whose
    /// transfers are measured in megabytes names one with a budget in minutes.</param>
    /// <param name="logger">Optional.</param>
    public OciRegistryClient(
        IMessageHub hub,
        string registry,
        IObservable<string> credential,
        string username = DefaultUsername,
        string? httpClientName = null,
        ILogger? logger = null)
    {
        this.registry = (registry ?? string.Empty).Trim().TrimEnd('/');
        if (this.registry.Length == 0)
            throw new ArgumentException("An OCI registry host is required.", nameof(registry));
        baseUri = this.registry.Contains("://", StringComparison.Ordinal)
            ? new Uri(this.registry + "/")
            : new Uri($"https://{this.registry}/");
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        this.username = string.IsNullOrWhiteSpace(username) ? DefaultUsername : username.Trim();
        this.logger = logger;

        var pools = hub.ServiceProvider.GetService<IoPoolRegistry>();
        httpPool = pools?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        blobPool = pools?.Get(IoPoolNames.Blob) ?? IoPool.Unbounded;
        http = hub.ServiceProvider.GetService<IHttpClientFactory>()
                   ?.CreateClient(httpClientName ?? DefaultHttpClientName)
               ?? SharedHttp;
    }

    /// <summary>The host this client talks to, as given.</summary>
    public string Registry => registry;

    // ══════════════════════════════════════════════════════════════════════════
    //  Surfaces
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every tag on <paramref name="repository"/>. Cold; emits once; FAULTS on any answer that is
    /// not a complete listing. Paginated (<c>?n=</c>, continued by a relative <c>Link</c> header)
    /// and every page is followed: ACR sorts tags lexically and pages at 100, so a lister that
    /// stopped after the first page would see the OLDEST hundred builds and report "nothing
    /// newer" forever.
    /// </summary>
    public IObservable<IReadOnlyList<string>> ListTags(string repository) =>
        credential.Take(1)
            .SelectMany(key => httpPool.Invoke(ct => ListTagsAsync(repository, key, ct)));

    /// <summary>
    /// The manifest at <paramref name="reference"/> — a tag or a <c>sha256:…</c> digest — with its
    /// media type and its VERIFIED digest. Cold; emits once; faults with
    /// <see cref="OciDigestMismatchException"/> when the bytes do not hash to the digest asked for
    /// (or, for a tag, to the digest the registry states).
    /// </summary>
    public IObservable<OciManifest> GetManifest(string repository, string reference) =>
        credential.Take(1)
            .SelectMany(key => httpPool.Invoke(ct => GetManifestAsync(repository, reference, key, ct)));

    /// <summary>
    /// Streams the blob <paramref name="digest"/> into the destination <paramref name="openDestination"/>
    /// opens, hashing as it goes, and disposes the destination when the transfer ends. Cold; emits
    /// once; runs on the <see cref="IoPoolNames.Blob"/> pool.
    ///
    /// <para>🚨 The receipt is the proof. On a mismatch the observable FAULTS with
    /// <see cref="OciDigestMismatchException"/> and the destination holds bytes that are NOT the
    /// blob asked for — the caller discards it (a <see cref="MemoryStream"/> it drops, a file it
    /// deletes) and lands nothing.</para>
    /// </summary>
    public IObservable<OciBlobReceipt> GetBlob(string repository, string digest, Func<Stream> openDestination) =>
        credential.Take(1)
            .SelectMany(key => blobPool.Invoke(ct => GetBlobAsync(repository, digest, key, openDestination, ct)));

    // ══════════════════════════════════════════════════════════════════════════
    //  The IO leaves — async by the one rule that allows it: each is the sole body of an
    //  IIoPool.Invoke, the one place the network is touched.
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<IReadOnlyList<string>> ListTagsAsync(string repository, string key, CancellationToken ct)
    {
        Uri? next = new Uri(baseUri, $"/v2/{repository}/tags/list?n={TagPageSize}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var tags = new List<string>();
        var pages = 0;

        while (next is not null)
        {
            // 🚨 A continuation that points back at a page already read is a registry looping,
            // and following it would be an unbounded poll. Refuse rather than "cap".
            if (!visited.Add(next.AbsoluteUri))
                throw new InvalidOperationException(
                    $"{registry} named {next.AbsoluteUri} as the next tags page twice — the listing loops.");

            using var response = await SendAuthenticatedAsync(next, repository, key, null, ct).ConfigureAwait(false);
            next = await ReadTagPageAsync(response, repository, tags, ct).ConfigureAwait(false);
            pages++;
        }

        logger?.LogDebug("[Oci] {Count} tag(s) on {Registry}/{Repo} over {Pages} page(s).",
            tags.Count, registry, repository, pages);
        return tags;
    }

    private async Task<OciManifest> GetManifestAsync(
        string repository, string reference, string key, CancellationToken ct)
    {
        var uri = new Uri(baseUri, $"/v2/{repository}/manifests/{reference}");
        using var response = await SendAuthenticatedAsync(uri, repository, key, request =>
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ImageManifestMediaType));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ImageIndexMediaType));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(DockerManifestMediaType));
            }, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"{registry} has no manifest {reference} in {repository} (MANIFEST_UNKNOWN) — the "
                + "publication is unsealed, the reference is stale, or the repository is not served.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{registry} answered {(int)response.StatusCode} for manifest {reference} in {repository}.",
                null, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var digest = OciDigest.Of(bytes);

        // 🚨 Verified against what was ASKED for first, and against what the registry STATES
        // second — a tag has no digest of its own, so the header is the only claim to hold it to.
        if (OciDigest.IsWellFormed(reference) && !OciDigest.Matches(reference, digest))
            throw new OciDigestMismatchException(registry, repository, "manifest", reference, digest);
        if (response.Headers.TryGetValues("Docker-Content-Digest", out var stated)
            && stated.FirstOrDefault() is { Length: > 0 } advertised
            && !OciDigest.Matches(advertised, digest))
            throw new OciDigestMismatchException(registry, repository, "manifest", advertised, digest);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return OciManifest.Parse(bytes, mediaType, digest);
    }

    private async Task<OciBlobReceipt> GetBlobAsync(
        string repository, string digest, string key, Func<Stream> openDestination, CancellationToken ct)
    {
        if (!OciDigest.IsWellFormed(digest))
            throw new ArgumentException(
                $"'{digest}' is not a sha256 digest — a blob is fetched by digest only.", nameof(digest));

        var uri = new Uri(baseUri, $"/v2/{repository}/blobs/{digest}");
        using var response = await SendAuthenticatedAsync(uri, repository, key, null, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"{registry} has no blob {digest} in {repository} (BLOB_UNKNOWN) — the manifest names "
                + "a layer the registry does not hold, which a sealed publication cannot do.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{registry} answered {(int)response.StatusCode} for blob {digest} in {repository}.",
                null, response.StatusCode);

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var destination = openDestination();
        try
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await destination.DisposeAsync().ConfigureAwait(false);
        }

        var actual = OciDigest.FromHash(hash.GetHashAndReset());
        if (!OciDigest.Matches(digest, actual))
            throw new OciDigestMismatchException(registry, repository, "blob", digest, actual);

        logger?.LogDebug("[Oci] blob {Digest} ({Bytes} bytes) from {Registry}/{Repo} verified.",
            digest, total, registry, repository);
        return new OciBlobReceipt(digest, total);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The handshake
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One authenticated request: the cached bearer for the repository's pull scope when there is
    /// one; on a <c>401</c>, the exchange the challenge names and ONE retry. Headers-only completion,
    /// so a blob body streams rather than buffers.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Uri uri, string repository, string key, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        var scope = $"repository:{repository}:pull";
        bearers.TryGetValue(scope, out var bearer);

        var response = await SendAsync(uri, bearer, configure, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // A bearer we held and the registry refused is gone — exchanged once more below; a second
        // refusal is the revocation ReadTagPage/the callers report.
        if (bearer is not null)
            bearers.TryRemove(scope, out _);

        string minted;
        using (response)
            minted = await ExchangeAsync(response, repository, key, ct).ConfigureAwait(false);
        bearers[scope] = minted;

        var retried = await SendAsync(uri, minted, configure, ct).ConfigureAwait(false);
        if (retried.StatusCode == HttpStatusCode.Unauthorized)
        {
            retried.Dispose();
            throw new InvalidOperationException(
                $"{registry} refused the bearer it had just issued for {repository} — the credential "
                + "was revoked between the exchange and the request.");
        }
        return retried;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri, string? bearer, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        configure?.Invoke(request);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The bearer exchange the challenge names: <c>Basic base64(user:key)</c> at the realm, with
    /// the service and a pull scope for the repository, answered as <c>token</c> or
    /// <c>access_token</c>. A realm that refuses the key is reported as exactly that.
    /// </summary>
    private async Task<string> ExchangeAsync(
        HttpResponseMessage challenge, string repository, string key, CancellationToken ct)
    {
        var header = challenge.Headers.WwwAuthenticate
            .FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (header is null)
            throw new InvalidOperationException(
                $"{baseUri.Host} answered 401 without a Bearer challenge — not an OCI registry, or the "
                + "route is not the registry's.");

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
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{key}")));
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

    /// <summary>Reads one tags page into <paramref name="tags"/>; returns the next page's URI, or null on the last.</summary>
    private async Task<Uri?> ReadTagPageAsync(
        HttpResponseMessage response, string repository, List<string> tags, CancellationToken ct)
    {
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

/// <summary>One content descriptor of an OCI manifest — a layer, a config, or an index entry.</summary>
/// <param name="MediaType">The descriptor's media type.</param>
/// <param name="Digest">The <c>sha256:…</c> digest of the blob it names.</param>
/// <param name="Size">The blob's size in bytes, as the manifest states it.</param>
public sealed record OciDescriptor(string MediaType, string Digest, long Size);

/// <summary>
/// A manifest as fetched: its bytes, the media type the registry served it as, its VERIFIED
/// digest, and the descriptors it carries. An image manifest fills <see cref="Layers"/> (and
/// <see cref="Config"/>); an image index fills <see cref="Manifests"/>.
/// </summary>
public sealed record OciManifest(
    byte[] Bytes,
    string MediaType,
    string Digest,
    OciDescriptor? Config,
    IReadOnlyList<OciDescriptor> Layers,
    IReadOnlyList<OciDescriptor> Manifests)
{
    /// <summary>Parses manifest bytes. <paramref name="servedMediaType"/> is the response's
    /// <c>Content-Type</c>; the body's own <c>mediaType</c> is the fallback, then the OCI image
    /// manifest type.</summary>
    public static OciManifest Parse(byte[] bytes, string? servedMediaType, string digest)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var mediaType = servedMediaType
            ?? (root.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String
                ? mt.GetString()
                : null)
            ?? OciRegistryClient.ImageManifestMediaType;
        return new OciManifest(
            bytes,
            mediaType,
            digest,
            root.TryGetProperty("config", out var config) ? Descriptor(config) : null,
            Descriptors(root, "layers"),
            Descriptors(root, "manifests"));
    }

    private static IReadOnlyList<OciDescriptor> Descriptors(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray().Select(Descriptor).Where(d => d is not null).Select(d => d!).ToArray();
    }

    private static OciDescriptor? Descriptor(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var mediaType = element.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String
            ? mt.GetString() ?? string.Empty
            : string.Empty;
        var digest = element.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? string.Empty
            : string.Empty;
        var size = element.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt64()
            : 0L;
        return digest.Length == 0 ? null : new OciDescriptor(mediaType, digest, size);
    }
}

/// <summary>What a verified blob transfer delivered: the digest the bytes hashed to (equal to the
/// one asked for, by construction) and how many bytes reached the destination.</summary>
public sealed record OciBlobReceipt(string Digest, long Size);

/// <summary>
/// The bytes a registry served do not hash to the digest they were fetched under. A caller that
/// sees this has bytes it must DISCARD — never land, never cache, never retry into.
/// </summary>
public sealed class OciDigestMismatchException(
    string registry, string repository, string what, string expected, string actual)
    : InvalidOperationException(
        $"{registry} served a {what} in {repository} that hashes to {actual}, not to the {expected} "
        + "it was fetched under — REFUSED; the bytes are not the artifact the publication sealed.")
{
    /// <summary>The digest the bytes were asked for under.</summary>
    public string Expected { get; } = expected;

    /// <summary>What the served bytes actually hash to.</summary>
    public string Actual { get; } = actual;
}

/// <summary>The <c>sha256:&lt;hex&gt;</c> digest form OCI uses, computed and compared in one place.</summary>
public static class OciDigest
{
    private static readonly Regex WellFormed = new("^sha256:[a-f0-9]{64}$", RegexOptions.Compiled);

    /// <summary>The digest of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => FromHash(SHA256.HashData(bytes));

    /// <summary>The digest form of a raw SHA-256 hash.</summary>
    public static string FromHash(byte[] hash) => "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Whether <paramref name="value"/> is a lowercase <c>sha256:</c> digest.</summary>
    public static bool IsWellFormed(string? value) => value is not null && WellFormed.IsMatch(value);

    /// <summary>Digest equality — hex is case-insensitive.</summary>
    public static bool Matches(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A parsed OCI artifact reference — <c>&lt;registry&gt;/&lt;repository&gt;@sha256:…</c> (the
/// only form a publication advertises: fetching by digest is the proof) or
/// <c>&lt;registry&gt;/&lt;repository&gt;:&lt;tag&gt;</c>.
/// </summary>
/// <param name="Registry">The host, with its port when not the default.</param>
/// <param name="Repository">The repository path under <c>/v2/</c>.</param>
/// <param name="Tag">The tag, when the reference names one.</param>
/// <param name="Digest">The digest, when the reference names one.</param>
public sealed record OciReference(string Registry, string Repository, string? Tag, string? Digest)
{
    /// <summary>The reference as it is written.</summary>
    public override string ToString() =>
        Digest is not null ? $"{Registry}/{Repository}@{Digest}"
        : Tag is not null ? $"{Registry}/{Repository}:{Tag}"
        : $"{Registry}/{Repository}";

    /// <summary>Parses a reference; false when it has no registry segment or no repository.</summary>
    public static bool TryParse(string? reference, out OciReference? parsed)
    {
        parsed = null;
        var text = (reference ?? string.Empty).Trim();
        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
            return false;
        var registry = text[..slash];
        var rest = text[(slash + 1)..];

        string? digest = null;
        string? tag = null;
        var at = rest.IndexOf('@');
        if (at >= 0)
        {
            digest = rest[(at + 1)..];
            rest = rest[..at];
            if (!OciDigest.IsWellFormed(digest))
                return false;
        }
        else
        {
            var lastSlash = rest.LastIndexOf('/');
            var colon = rest.IndexOf(':', lastSlash + 1);
            if (colon >= 0)
            {
                tag = rest[(colon + 1)..];
                rest = rest[..colon];
                if (tag.Length == 0)
                    return false;
            }
        }

        if (rest.Length == 0 || rest.EndsWith('/'))
            return false;
        parsed = new OciReference(registry, rest, tag, digest);
        return true;
    }
}
