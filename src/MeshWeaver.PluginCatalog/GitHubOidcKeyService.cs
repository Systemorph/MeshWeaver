using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// GitHub's OIDC signing keys, fetched once and shared — the key source behind the build-principal
/// leg of <see cref="InstanceRegistryAuthenticator"/> (#2483).
///
/// <para>🚨 <b>Mesh-scoped singleton, instance field, never a static cache.</b> Process-wide static
/// state survives mesh disposal, so it bleeds across tests and across deployments sharing a host
/// (<c>Doc/Architecture/NoStaticState</c>). The cache here dies with the mesh that owns it.</para>
///
/// <para>🚨 <b>A fetch that fails FAILS CLOSED and says so.</b> The observable ERRORS; it never
/// yields an empty key set that would read as "no key matched" and never yields a stale-but-present
/// set on the strength of a failure. The authenticator turns that error into
/// <see cref="InstanceAuthResult.Unavailable"/> — a <c>503 Retry-After</c>, distinguishable from the
/// <c>401</c> a genuinely bad token gets — because "I could not find out" is a third state, not a
/// denial and certainly not an admission (core #2901).</para>
///
/// <para><b>Refresh is bounded twice.</b> The set is re-read when it is older than
/// <see cref="CacheDuration"/>; and an unknown <c>kid</c> — the signal of a GitHub key rotation — may
/// force ONE early re-read, but only if the set in hand is older than
/// <see cref="MinimumRefreshInterval"/>. Without that floor a caller presenting invented key ids
/// would turn every unauthenticated request into an outbound fetch.</para>
///
/// <para>Same promise-cache shape as <c>GitHubAppTokenService</c>: the in-flight fetch observable is
/// held on an instance field so concurrent callers share ONE round trip, and a failure nulls it so
/// the next caller starts a genuinely new attempt rather than replaying a latched
/// <c>OnError</c>.</para>
/// </summary>
public sealed class GitHubOidcKeyService
{
    /// <summary>How long a fetched key set is reused before it is re-read.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>Floor between forced re-reads. An unknown <c>kid</c> may trigger one early fetch;
    /// this is what keeps a hostile caller from turning that into a fetch per request.</summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>Largest JWKS document read, in bytes. GitHub's is a few kilobytes; the bound keeps a
    /// compromised or misrouted endpoint from streaming an unbounded body into the portal.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared; a per-call `new HttpClient()` leaks sockets. Immutable shared resource,
    // not a cache, so it does not fall under the no-static-state rule (same reasoning, same words,
    // as RegistryPackageSource.SharedHttp).
    private static readonly HttpClient SharedHttp = new();

    private readonly IIoPool pool;
    private readonly HttpClient http;
    private readonly ILogger<GitHubOidcKeyService>? logger;

    // The promise-cache: the current fetch, replayed to every caller. Instance fields, never static.
    private readonly object gate = new();
    private IObservable<GitHubSigningKeys>? cached;

    /// <summary>Creates the service against the mesh's HTTP I/O pool.</summary>
    /// <param name="hub">The hub whose service provider carries the pool registry and HTTP factory.</param>
    /// <param name="logger">Optional logger.</param>
    public GitHubOidcKeyService(IMessageHub hub, ILogger<GitHubOidcKeyService>? logger = null)
    {
        this.logger = logger;
        pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.HttpClientName) ?? SharedHttp;
    }

    /// <summary>
    /// The fetch leaf, injectable so the CACHE and REFRESH rules can be driven without reaching
    /// GitHub. Production is the HTTP read below; a test supplies a document (or a fault) directly.
    /// </summary>
    internal Func<CancellationToken, Task<string>>? FetchOverride { get; init; }

    /// <summary>
    /// GitHub's current signing keys — the cached set when it is fresh, a new fetch when it is not.
    /// Errors when the keys cannot be read: the caller must answer retryable, never "unknown key".
    /// </summary>
    /// <param name="now">The instant freshness is judged against.</param>
    /// <returns>A cold observable of the key set.</returns>
    public IObservable<GitHubSigningKeys> Keys(DateTimeOffset now)
    {
        IObservable<GitHubSigningKeys> source;
        lock (gate)
            source = cached ??= CreateFetch();

        return source.SelectMany(keys =>
            now - keys.FetchedAt < CacheDuration
                ? Observable.Return(keys)
                : Swap(source));
    }

    /// <summary>
    /// Re-reads the key set because a token named a <c>kid</c> the current set does not hold — the
    /// signal of a GitHub key rotation. Bounded by <see cref="MinimumRefreshInterval"/>: when the set
    /// in hand is younger than that, it is returned UNCHANGED and the caller refuses on it. That
    /// keeps a caller inventing key ids from becoming an outbound fetch amplifier.
    /// </summary>
    /// <param name="stale">The set that did not contain the key.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>A cold observable of the (possibly unchanged) key set.</returns>
    public IObservable<GitHubSigningKeys> Refresh(GitHubSigningKeys stale, DateTimeOffset now)
    {
        if (now - stale.FetchedAt < MinimumRefreshInterval)
            return Observable.Return(stale);

        logger?.LogInformation(
            "GitHub OIDC: a token named a signing key absent from the set read at {FetchedAt} — re-reading the JWKS",
            stale.FetchedAt);

        IObservable<GitHubSigningKeys> source;
        lock (gate)
            source = cached ?? CreateFetch();
        return Swap(source);
    }

    /// <summary>Replaces a settled promise with a fresh fetch — once; concurrent refreshers share
    /// the replacement rather than each starting a round trip of their own.</summary>
    private IObservable<GitHubSigningKeys> Swap(IObservable<GitHubSigningKeys> settled)
    {
        lock (gate)
        {
            if (ReferenceEquals(cached, settled))
                cached = CreateFetch();
            return cached!;
        }
    }

    private IObservable<GitHubSigningKeys> CreateFetch() =>
        pool.Run(FetchAsync)
            .Catch((Exception ex) =>
            {
                lock (gate)
                    cached = null;   // never cache a failure — the next caller starts a new attempt
                logger?.LogWarning(ex,
                    "GitHub OIDC: could not read the signing keys — build-principal tokens are "
                    + "UNDETERMINED (retryable), never accepted");
                return Observable.Throw<GitHubSigningKeys>(ex);
            });

    // ── the HTTP leaf (runs inside the I/O pool) ─────────────────────────────

    private async Task<GitHubSigningKeys> FetchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (FetchOverride is { } test)
            return Materialize(await test(ct).ConfigureAwait(false), now);

        // Discovery first, so a moved endpoint is not an outage — but the discovered URI is accepted
        // only when it is HTTPS on the pinned issuer's own host (JwksUriFromDiscovery). Discovery is
        // a convenience; it is never allowed to move the trust anchor.
        string? discovered = null;
        try
        {
            discovered = GitHubActionsToken.JwksUriFromDiscovery(
                await ReadAsync(GitHubActionsToken.OpenIdConfigurationUri, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A discovery blip must not fail the whole read: the pinned path below is the documented
            // endpoint and is what every federation guide uses.
            logger?.LogDebug(ex, "GitHub OIDC: discovery unavailable — using the pinned JWKS endpoint");
        }

        var uri = discovered ?? GitHubActionsToken.JwksUri;
        return Materialize(await ReadAsync(uri, ct).ConfigureAwait(false), now);
    }

    private GitHubSigningKeys Materialize(string document, DateTimeOffset now)
    {
        var keys = GitHubActionsToken.ParseJwks(document);
        if (keys.Count == 0)
            // An empty set would refuse every token — correct, but it must not be REMEMBERED for an
            // hour as if it were a valid answer. Throwing keeps it uncached and retryable.
            throw new InvalidOperationException(
                "GitHub's JWKS carried no usable RS256 signing key.");
        logger?.LogInformation("GitHub OIDC: read {Count} signing key(s)", keys.Count);
        return new GitHubSigningKeys(keys, now);
    }

    private async Task<string> ReadAsync(string uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GET {uri} failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxDocumentBytes)
            throw new InvalidOperationException(
                $"GET {uri} declared {declared} bytes, over the {MaxDocumentBytes}-byte bound.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxDocumentBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (chunk == 0)
                break;
            read += chunk;
        }
        if (read > MaxDocumentBytes)
            throw new InvalidOperationException(
                $"GET {uri} returned more than the {MaxDocumentBytes}-byte bound.");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
