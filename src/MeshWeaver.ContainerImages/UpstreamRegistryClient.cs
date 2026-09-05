using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Talks to the UPSTREAM OCI registry on the mirror's behalf, holding the one credential the
/// fleet still needs. Everything a caller sees is authenticated against memex instead
/// (<c>InstanceRegistryAuthenticator</c>), which is the whole point: satellites drop their
/// <c>ACR_USERNAME</c>/<c>ACR_PASSWORD</c> pair and reuse the instance key they already carry for
/// the plugin registry.
///
/// <para>🚨 The token cache is an INSTANCE field on a DI singleton, never <c>static</c>. Registry
/// tokens are per-scope bearer credentials with an expiry; a process-wide cache would outlive the
/// mesh, bleed across tests and — because the value IS a credential — across tenants.</para>
///
/// <para>Bodies are never buffered. <see cref="OpenAsync"/> returns the upstream response with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so a layer flows
/// upstream → socket → client. A registry that materialises a 300 MB blob is a registry that
/// OOMs the portal under a rolling restart.</para>
/// </summary>
public sealed class UpstreamRegistryClient(
    HttpClient http,
    IOptions<ContainerImageOptions> options,
    ILogger<UpstreamRegistryClient> logger)
{
    private readonly ConcurrentDictionary<string, CachedToken> tokens = new();
    private readonly ContainerImageOptions opts = options.Value;

    /// <summary>The upstream host, e.g. <c>meshweaver.azurecr.io</c>. Empty when unconfigured —
    /// callers must treat that as "the mirror is off", never as "allow".</summary>
    public string Upstream => opts.Upstream ?? string.Empty;

    /// <summary>True when the mirror has both a host and a credential to reach it with.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(opts.Upstream)
        && !string.IsNullOrWhiteSpace(opts.Username)
        && !string.IsNullOrWhiteSpace(opts.Password);

    /// <summary>
    /// Issues <paramref name="method"/> for <paramref name="path"/> against the upstream with a
    /// pull-scoped bearer token for <paramref name="repository"/>, returning the response
    /// UNREAD so the caller can stream it. The caller owns disposal.
    /// </summary>
    public async Task<HttpResponseMessage> OpenAsync(
        HttpMethod method, string repository, string path, string? range, CancellationToken ct)
    {
        var token = await TokenFor(repository, ct);
        var request = new HttpRequestMessage(method, $"https://{opts.Upstream}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The manifest Accept set decides WHICH manifest the upstream returns: omit it and a
        // multi-arch index comes back as a v2 manifest, so an arm64 node would pull an amd64 image.
        foreach (var media in ManifestMediaTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(media));
        if (!string.IsNullOrEmpty(range))
            request.Headers.TryAddWithoutValidation("Range", range);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // A 401 here means OUR token went stale mid-flight, not that the caller is unauthorised —
        // drop it and try once, so a token expiring between issue and use is invisible rather than
        // a spurious failure handed to a `docker pull`.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            tokens.TryRemove(repository, out _);
            var fresh = await TokenFor(repository, ct);
            var retry = new HttpRequestMessage(method, $"https://{opts.Upstream}{path}");
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            foreach (var media in ManifestMediaTypes)
                retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(media));
            if (!string.IsNullOrEmpty(range))
                retry.Headers.TryAddWithoutValidation("Range", range);
            response = await http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    /// <summary>Every manifest media type a client may ask for, including the multi-arch
    /// indexes — see the note in <see cref="OpenAsync"/> on why omitting these is a correctness
    /// bug rather than a nicety.</summary>
    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private async Task<string> TokenFor(string repository, CancellationToken ct)
    {
        if (tokens.TryGetValue(repository, out var cached) && !cached.IsExpired)
            return cached.Token;

        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{opts.Username}:{opts.Password}"));
        var url = $"https://{opts.Upstream}/oauth2/token"
                  + $"?service={Uri.EscapeDataString(opts.Upstream!)}"
                  + $"&scope={Uri.EscapeDataString($"repository:{repository}:pull")}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // 🚨 Never log the credential, and never log the body — an upstream token endpoint can
            // echo request detail. The status and the repository are the whole diagnostic.
            logger.LogWarning(
                "Container registry mirror: upstream token request for {Repository} answered {Status}. "
                + "Check ContainerImages:Username/Password — the mirror cannot serve pulls without it.",
                repository, (int)response.StatusCode);
            throw new UpstreamRegistryException(
                $"upstream token request answered {(int)response.StatusCode}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var value = json.RootElement.TryGetProperty("access_token", out var at)
            ? at.GetString()
            : json.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(value))
            throw new UpstreamRegistryException("upstream token response carried no token");

        // Refresh a minute early: a token that expires while a 300 MB layer is in flight would
        // fail the pull at 99 %, and the retry above cannot help once the body has started.
        var lifetime = json.RootElement.TryGetProperty("expires_in", out var e)
                       && e.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(30, seconds - 60))
            : TimeSpan.FromMinutes(4);
        tokens[repository] = new CachedToken(value, DateTimeOffset.UtcNow + lifetime);
        return value;
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}

/// <summary>The upstream registry could not be reached or refused the mirror's own credential.
/// Distinct from a caller-facing 401, which is about the CALLER's instance key.</summary>
public sealed class UpstreamRegistryException(string message) : Exception(message);
