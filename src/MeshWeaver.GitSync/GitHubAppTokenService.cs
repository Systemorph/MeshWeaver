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
///
/// <para>🚨 <b>Every fault out of <see cref="GetInstallationToken()"/> is a
/// <see cref="GitHubAppTokenMintException"/></b> (only a cancellation passes as itself). A caller
/// therefore never has to read a message to know it is looking at a credential that was NEVER
/// OBTAINED rather than one GitHub REJECTED — the two look identical once a mint failure has
/// degraded to an anonymous fetch and surfaced, one layer later, as Octokit's
/// <c>AuthorizationException: Bad credentials</c> (#4736).</para>
/// </summary>
public sealed class GitHubAppTokenService
{
    /// <summary>An installation token with its GitHub-reported expiry.</summary>
    public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

    /// <summary>A minted token together with the installation it was minted for — the one the mint
    /// actually SELECTED, which a discovery that matched no configured owner picks itself.</summary>
    private sealed record MintedInstallation(InstallationToken Token, GitHubAppInstallation Installation);

    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubAppOptions options;
    private readonly ILogger? logger;

    // Promise-cache (instance, never static): the current fetch, replayed to every caller.
    // Refreshed when the delivered token nears expiry; evicted on error so failures don't stick.
    // ONE promise per INSTALLATION: the key is the installation owner a caller
    // named, "" for the instance's own (GitHub:App:InstallationId / InstallationOwner). One App can
    // be installed on several organisations, and a token minted for one installation reads nothing
    // in another — so a single slot made a second organisation's source unreadable by construction.
    // The shared primitive owns the concurrency: one build per stored entry, a fault evicts
    // exactly the entry that faulted (so a failure never sticks and never drops a replacement).
    private readonly PromiseCache<string, MintedInstallation> cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> now;

    /// <summary>Initializes the service.</summary>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from so every HTTP leaf runs off the hub.</param>
    /// <param name="options">The bound <see cref="GitHubAppOptions"/> (client id, private key, installation).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse; a default one is created when null.</param>
    public GitHubAppTokenService(
        IoPoolRegistry ioPools,
        IOptions<GitHubAppOptions> options,
        ILogger<GitHubAppTokenService>? logger = null,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.logger = logger;
        now = clock ?? (() => DateTimeOffset.UtcNow);
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
    public IObservable<string> GetInstallationToken() => GetInstallationToken(null);

    /// <summary>
    /// The current installation token of the installation on <paramref name="installationOwner"/>
    /// (an organisation or user login) — the App installed on SEVERAL accounts reads each account's
    /// repositories with that account's own installation token. Cached per installation, refreshed
    /// exactly like <see cref="GetInstallationToken()"/>. Null/blank is the instance's own
    /// installation (<c>GitHub:App:InstallationId</c> / <c>GitHub:App:InstallationOwner</c>).
    ///
    /// <para>🚨 A NAMED owner fails closed: when the App has no installation on that account the
    /// mint faults (<see cref="GitHubAppTokenMintStage.InstallationDiscovery"/>) naming the owner.
    /// It never falls back to another installation — a token for the wrong organisation would be
    /// reported one layer later as a missing repository.</para>
    /// </summary>
    /// <param name="installationOwner">The account the installation lives on, or null/blank for
    /// the instance's own installation.</param>
    public IObservable<string> GetInstallationToken(string? installationOwner)
    {
        var owner = (installationOwner ?? "").Trim();
        if (!IsConfigured)
            return Observable.Throw<string>(new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.NotConfigured,
                "The GitHub App is not configured (set GitHub:App:ClientId + GitHub:App:PrivateKey)."));

        // Deferred: the promise is read at SUBSCRIPTION time, not when this observable was built.
        // A long-lived consumer holds ONE of these for the life of its feed and subscribes per pass
        // (the Store's git poll loop). Before, the promise of the moment was captured here, so
        // after the first refresh <see cref="Refresh"/> compared every later expiry against that
        // stale capture, never matched the current promise again, and handed the expired token
        // back on every pass — two token lifetimes after boot, every private source read as
        // "Bad credentials" until the process restarted (Systemorph/Memex#165).
        return Observable.Defer(() =>
        {
            return Current(owner).Select(minted => minted.Token.Token);
        })
        // 🚨 EVERY fault out of this method is a GitHubAppTokenMintException — translate all of it
        // or let it through, never half. A caller degrading to an anonymous fetch (the Store's
        // package feed does, so a public source keeps working) must be able to say WHICH failure it
        // is degrading over, and the only thing that survives every layer between here and its log
        // is the TYPE. Without it the degraded fetch is reported one layer later as Octokit's
        // "Bad credentials", and a mint that never happened reads as a credential GitHub rejected
        // (#4736). Cancellation is deliberately NOT translated: a cancelled mint is not a failed one.
        .Catch((Exception exception) => Observable.Throw<string>(AsMintFailure(exception)));
    }

    /// <summary>
    /// The installation the current token of <paramref name="installationOwner"/> was minted for —
    /// its id and, where the mint learnt it, its account. Read from the SAME cached mint as
    /// <see cref="GetInstallationToken(string?)"/> (minting when none is cached), so a reader naming
    /// the installation names the one the token reads with: when <c>GitHub:App:InstallationId</c> is
    /// unset and no installation matches <c>GitHub:App:InstallationOwner</c>, the mint falls back to
    /// the first installation, and this names THAT one rather than the configured owner. Faults
    /// exactly as <see cref="GetInstallationToken(string?)"/> does.
    /// </summary>
    /// <param name="installationOwner">The account the installation lives on, or null/blank for
    /// the instance's own installation.</param>
    public IObservable<GitHubAppInstallation> GetInstallation(string? installationOwner = null)
    {
        var owner = (installationOwner ?? "").Trim();
        if (!IsConfigured)
            return Observable.Throw<GitHubAppInstallation>(new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.NotConfigured,
                "The GitHub App is not configured (set GitHub:App:ClientId + GitHub:App:PrivateKey)."));
        return Observable.Defer(() => Current(owner).Select(minted => minted.Installation))
            .Catch((Exception exception) => Observable.Throw<GitHubAppInstallation>(AsMintFailure(exception)));
    }

    /// <summary>The cached mint for <paramref name="owner"/>, refreshed when within five minutes of expiry.</summary>
    private IObservable<MintedInstallation> Current(string owner)
    {
        var source = cached.GetOrAdd(owner, CreateFetch);
        return source.SelectMany(minted =>
            minted.Token.ExpiresAt > now().AddMinutes(5)
                ? Observable.Return(minted)
                : Refresh(owner, source));
    }

    /// <summary>
    /// The one translation point: anything that is not already a mint failure — and is not a
    /// cancellation — becomes one, with the underlying fault kept as the inner exception.
    /// </summary>
    private static Exception AsMintFailure(Exception exception) => exception switch
    {
        GitHubAppTokenMintException => exception,
        OperationCanceledException => exception,
        _ => new GitHubAppTokenMintException(
            GitHubAppTokenMintStage.Transport,
            "The GitHub App installation token could not be minted: " + exception.Message,
            exception),
    };

    /// <summary>
    /// Swap the stale promise for a fresh fetch — only once: the release is pair-exact, so of
    /// several concurrent refreshers one removes the stale entry and all of them share the single
    /// replacement; a replacement already installed is never dropped.
    /// </summary>
    private IObservable<MintedInstallation> Refresh(string owner, IObservable<MintedInstallation> stale)
    {
        cached.Release(owner, stale);
        return cached.GetOrAdd(owner, CreateFetch);
    }

    private IObservable<MintedInstallation> CreateFetch(string owner) =>
        Http.Run(ct => FetchInstallationTokenAsync(owner, ct))
            .Catch((Exception ex) => Observable.Throw<MintedInstallation>(AsMintFailure(ex)));

    // ── HTTP leaves (run inside the I/O pool) ────────────────────────────────

    private async Task<MintedInstallation> FetchInstallationTokenAsync(string owner, CancellationToken ct)
    {
        string jwt;
        try
        {
            jwt = BuildAppJwt(now());
        }
        catch (Exception exception)
        {
            // Nothing reached GitHub, so this can never be a credential GitHub rejected. The key
            // itself is never named — only what could not be done with it.
            throw new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.Signing,
                "The GitHub App JWT could not be signed — GitHub:App:PrivateKey is not a usable RSA "
                + "private key. No credential was presented to GitHub.",
                exception);
        }

        // A NAMED owner is always discovered by that name: GitHub:App:InstallationId pins the
        // instance's OWN installation and says nothing about another account's.
        // A pinned id is named by its id alone: nothing was asked of GitHub about its account.
        var installation = owner.Length > 0
            ? await DiscoverInstallationIdAsync(jwt, owner, required: true, ct).ConfigureAwait(false)
            : options.InstallationId is { } pinned
                ? new GitHubAppInstallation(pinned, null)
                : await DiscoverInstallationIdAsync(jwt, options.InstallationOwner, required: false, ct)
                    .ConfigureAwait(false);
        var installationId = installation.Id;

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations/{installationId}/access_tokens");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.TokenExchange,
                $"GitHub App installation-token request failed ({(int)resp.StatusCode}): {Truncate(json)}")
            {
                StatusCode = (int)resp.StatusCode,
            };

        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : throw new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.Response,
                "GitHub App token response carried no 'token'.");
        var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var e)
                        && e.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(e.GetString(), out var dto)
            ? dto
            : now().AddMinutes(50);   // GitHub's documented lifetime is 1h
        logger?.LogInformation(
            "Minted GitHub App installation token (installation {Id}{Owner}, expires {Exp})",
            installationId, owner.Length > 0 ? $" on '{owner}'" : "", expiresAt);
        return new MintedInstallation(new InstallationToken(token, expiresAt), installation);
    }

    private async Task<GitHubAppInstallation> DiscoverInstallationIdAsync(
        string jwt, string? wantedOwner, bool required, CancellationToken ct)
    {
        // Paged to the end: an App installed on more accounts than one page holds would otherwise
        // report a real installation as missing — and, for a NAMED owner, refuse a repository the
        // App can read. The loop ends on the first short page.
        const int pageSize = 100;
        GitHubAppInstallation? first = null;
        var total = 0;
        for (var page = 1; ; page++)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations?per_page={pageSize}&page={page}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new GitHubAppTokenMintException(
                    GitHubAppTokenMintStage.InstallationDiscovery,
                    $"GitHub App installation discovery failed ({(int)resp.StatusCode}): {Truncate(json)}")
                {
                    StatusCode = (int)resp.StatusCode,
                };

            using var doc = JsonDocument.Parse(json);
            var count = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            total += count;
            foreach (var inst in count == 0 ? [] : doc.RootElement.EnumerateArray().ToArray())
            {
                if (!inst.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                    continue;
                var accountLogin = inst.TryGetProperty("account", out var account)
                                   && account.ValueKind == JsonValueKind.Object
                                   && account.TryGetProperty("login", out var login)
                                   && login.ValueKind == JsonValueKind.String
                    ? login.GetString()
                    : null;
                first ??= new GitHubAppInstallation(id, accountLogin);
                if (wantedOwner is { Length: > 0 } owner
                    && string.Equals(accountLogin, owner, StringComparison.OrdinalIgnoreCase))
                    return new GitHubAppInstallation(id, accountLogin);
            }
            if (count < pageSize)
                break;
        }

        // A NAMED owner is refused by its name first — also when the App has no installation
        // at all, which is the same answer for that owner and must name it just the same.
        if (required)
        {
            // Localizable where it is SHOWN (a sync's activity, the settings tab): the refusal
            // rides in Data, the way a legacy GitSync refusal does, and the thrown type and its
            // English message stay what every other mint stage throws.
            var refusal = GitSyncRefusal.Of("gitsync.source.noInstallation", ("owner", wantedOwner!));
            var missing = new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.InstallationDiscovery, refusal.Render(null));
            missing.Data[GitSyncRefusalException.DataKey] = refusal;
            throw missing;
        }
        if (total == 0)
            throw new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.InstallationDiscovery,
                "The GitHub App has no installations — install it on the organization (with Contents: Read " +
                "on the repos to sync) first.");
        if (first is null)
            throw new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.InstallationDiscovery,
                "GitHub App installation list carried no usable id.");
        if (total > 1)
            logger?.LogWarning(
                "GitHub App has {Count} installations and none matched InstallationOwner '{Owner}' — using the first. " +
                "Set GitHub:App:InstallationId or GitHub:App:InstallationOwner to pin one.",
                total, wantedOwner);
        return first;
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

/// <summary>A GitHub App installation as a mint selected it: its id and, when the mint read it from
/// GitHub, the account it is installed on (null for an installation pinned by id alone).</summary>
/// <param name="Id">The installation id.</param>
/// <param name="Account">The account login, or null when not learnt.</param>
public sealed record GitHubAppInstallation(long Id, string? Account);
