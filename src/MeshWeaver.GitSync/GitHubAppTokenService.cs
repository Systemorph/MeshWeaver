using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// Mints GitHub <b>App installation tokens</b> — the machine identity for server-side GitSync
/// (the plugin registry syncing the plugins repo, boot imports). Signs a short-lived RS256 JWT
/// with the App's private key (<see cref="GitHubAppOptions.PrivateKey"/>), exchanges it at
/// <c>POST /app/installations/{id}/access_tokens</c>, and caches the resulting token until
/// shortly before its expiry (installation tokens last one hour).
///
/// <para>🚨 Every HTTP leaf runs inside <see cref="IIoPool"/> (the <see cref="IoPoolNames.Http"/>
/// pool) and the public surface is <see cref="IObservable{T}"/> — no <c>async</c>/<c>await</c>/
/// <c>Task</c> escapes a signature (<c>Doc/Architecture/ControlledIoPooling.md</c>). The cache is
/// the sanctioned promise-cache: the in-flight fetch observable is stored on an instance field
/// (ReplaySubject-backed via <c>IIoPool.Run</c>), so concurrent callers share one HTTP
/// round-trip; a failed fetch invalidates itself so the next caller retries.</para>
/// </summary>
public sealed class GitHubAppTokenService
{
    /// <summary>An installation token with its GitHub-reported expiry.</summary>
    public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubAppOptions options;
    private readonly ILogger? logger;

    // Promise-cache (instance, never static): the current fetch, replayed to every caller.
    // Refreshed when the delivered token nears expiry; nulled on error so failures don't stick.
    private readonly object gate = new();
    private IObservable<InstallationToken>? cached;

    /// <summary>Initializes the service.</summary>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from so every HTTP leaf runs off the hub.</param>
    /// <param name="options">The bound <see cref="GitHubAppOptions"/> (client id, private key, installation).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse; a default one is created when null.</param>
    public GitHubAppTokenService(
        IoPoolRegistry ioPools,
        IOptions<GitHubAppOptions> options,
        ILogger<GitHubAppTokenService>? logger = null,
        HttpClient? httpClient = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.logger = logger;
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-GitSync");
    }

    /// <summary>True when the App identity is configured (client id + private key).</summary>
    public bool IsConfigured => options.IsConfigured;

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    /// <summary>
    /// The current installation token — cached across callers, transparently re-fetched when the
    /// cached one is within five minutes of expiry.
    /// </summary>
    public IObservable<string> GetInstallationToken()
    {
        if (!IsConfigured)
            return Observable.Throw<string>(new InvalidOperationException(
                "The GitHub App is not configured (set GitHub:App:ClientId + GitHub:App:PrivateKey)."));

        IObservable<InstallationToken> source;
        lock (gate)
            source = cached ??= CreateFetch();

        return source.SelectMany(tok =>
            tok.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)
                ? Observable.Return(tok.Token)
                : Refresh(source).Select(t => t.Token));
    }

    /// <summary>Swap the stale promise for a fresh fetch (only once — concurrent refreshers share it).</summary>
    private IObservable<InstallationToken> Refresh(IObservable<InstallationToken> stale)
    {
        lock (gate)
        {
            if (ReferenceEquals(cached, stale))
                cached = CreateFetch();
            return cached!;
        }
    }

    private IObservable<InstallationToken> CreateFetch() =>
        Http.Run(ct => FetchInstallationTokenAsync(ct))
            .Catch((Exception ex) =>
            {
                lock (gate)
                    cached = null;   // never cache a failure — the next caller retries
                return Observable.Throw<InstallationToken>(ex);
            });

    // ── HTTP leaves (run inside the I/O pool) ────────────────────────────────

    private async Task<InstallationToken> FetchInstallationTokenAsync(CancellationToken ct)
    {
        var jwt = BuildAppJwt(DateTimeOffset.UtcNow);
        var installationId = options.InstallationId
            ?? await DiscoverInstallationIdAsync(jwt, ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations/{installationId}/access_tokens");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub App installation-token request failed ({(int)resp.StatusCode}): {Truncate(json)}");

        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : throw new InvalidOperationException("GitHub App token response carried no 'token'.");
        var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var e)
                        && e.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(e.GetString(), out var dto)
            ? dto
            : DateTimeOffset.UtcNow.AddMinutes(50);   // GitHub's documented lifetime is 1h
        logger?.LogInformation("Minted GitHub App installation token (installation {Id}, expires {Exp})",
            installationId, expiresAt);
        return new InstallationToken(token, expiresAt);
    }

    private async Task<long> DiscoverInstallationIdAsync(string jwt, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub App installation discovery failed ({(int)resp.StatusCode}): {Truncate(json)}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "The GitHub App has no installations — install it on the organization (with Contents: Read " +
                "on the repos to sync) first.");

        long? first = null;
        foreach (var inst in doc.RootElement.EnumerateArray())
        {
            if (!inst.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                continue;
            first ??= id;
            if (options.InstallationOwner is { Length: > 0 } owner
                && inst.TryGetProperty("account", out var account)
                && account.TryGetProperty("login", out var login)
                && string.Equals(login.GetString(), owner, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        if (first is null)
            throw new InvalidOperationException("GitHub App installation list carried no usable id.");
        if (doc.RootElement.GetArrayLength() > 1)
            logger?.LogWarning(
                "GitHub App has {Count} installations and none matched InstallationOwner '{Owner}' — using the first. " +
                "Set GitHub:App:InstallationId or GitHub:App:InstallationOwner to pin one.",
                doc.RootElement.GetArrayLength(), options.InstallationOwner);
        return first.Value;
    }

    // ── JWT (RS256, no external dependency) ──────────────────────────────────

    /// <summary>
    /// The signed App JWT: <c>iss</c> = client id, valid from one minute ago (clock skew) to nine
    /// minutes from now (GitHub caps at ten). Internal for the offline signature test.
    /// </summary>
    internal string BuildAppJwt(DateTimeOffset now)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddMinutes(-1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = options.ClientId,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKey);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
