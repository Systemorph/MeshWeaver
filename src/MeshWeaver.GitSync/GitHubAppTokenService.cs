using System.Collections.Immutable;
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
/// degraded to an anonymous fetch and surfaced, one layer later, as a
/// <see cref="GitHubAuthorizationException"/>: <c>Bad credentials</c> (#4736).</para>
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
    // The instance's OWN App (Doc/Architecture/InstanceRepositoryAccess), read LIVE on every mint:
    // a connect, a rotation or a disconnect on any replica takes effect at the next mint, with no
    // restart. Null when the host wires none (a bare test mesh, a CLI).
    private readonly Func<GitHubAppOptions?>? instanceApp;
    // Emits once the stores the instance's App is read from have LISTED (InstanceGitHubApp.WhenReadable):
    // a cold replica must not decide "no App" from catalogs that have read nothing yet.
    private readonly Func<IObservable<System.Reactive.Unit>>? instanceAppReady;

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
        : this(ioPools, options, instanceApp: null, instanceAppReady: null, logger, httpClient, clock)
    {
    }

    /// <summary>Initializes the service with the instance's own App beside the deployment's.</summary>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from so every HTTP leaf runs off the hub.</param>
    /// <param name="options">The DEPLOYMENT's <see cref="GitHubAppOptions"/> (<c>GitHub:App</c>).</param>
    /// <param name="instanceApp">Reads the instance's OWN App identity live (<see cref="InstanceGitHubApp.Options(System.IServiceProvider)"/>);
    /// null when the host wires none. Used whenever the deployment configures no App.</param>
    /// <param name="instanceAppReady">Emits once the instance App's stores have listed; null treats
    /// them as read.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse; a default one is created when null.</param>
    /// <param name="clock">Optional clock.</param>
    public GitHubAppTokenService(
        IoPoolRegistry ioPools,
        IOptions<GitHubAppOptions> options,
        Func<GitHubAppOptions?>? instanceApp,
        Func<IObservable<System.Reactive.Unit>>? instanceAppReady = null,
        ILogger<GitHubAppTokenService>? logger = null,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.instanceApp = instanceApp;
        this.instanceAppReady = instanceAppReady;
        this.logger = logger;
        now = clock ?? (() => DateTimeOffset.UtcNow);
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-GitSync");
    }

    /// <summary>
    /// True when an App identity is usable: the deployment's (client id + private key under
    /// <c>GitHub:App</c>), or else the instance's own (<see cref="InstanceGitHubApp"/>). Read live.
    /// </summary>
    public bool IsConfigured => Effective() is { IsConfigured: true };

    /// <summary>True when the DEPLOYMENT configures an App (<c>GitHub:App</c>) — the platform identity
    /// that is assumed to reach the platform's own repositories. The instance's own App does not
    /// count here: it reaches only what its administrator installed it on.</summary>
    public bool IsDeploymentConfigured => options.IsConfigured;

    /// <summary>True when the instance's OWN App is connected and readable. Read live.</summary>
    public bool HasInstanceApp => InstanceOptions() is not null;

    /// <summary>
    /// Emits once the identity can be DECIDED: at once when the deployment configures an App, else
    /// once the instance App's stores have listed — bounded, so an unreadable store answers "no App"
    /// rather than holding its caller forever (logged). Ask <see cref="IsConfigured"/> after it.
    /// </summary>
    public IObservable<System.Reactive.Unit> WhenReady =>
        Observable.Defer(() => options.IsConfigured
            ? Observable.Return(System.Reactive.Unit.Default)
            : WhenInstanceAppReady);

    /// <summary>
    /// Emits once the INSTANCE App's stores have listed — whatever the deployment configures. What a
    /// caller that asks for the instance App explicitly (an instance-local source, a configured
    /// <c>Identity = instance</c>) waits on before asking <see cref="HasInstanceApp"/>. Bounded: an
    /// unreadable store answers "not connected" rather than holding its caller forever (logged).
    /// </summary>
    public IObservable<System.Reactive.Unit> WhenInstanceAppReady =>
        Observable.Defer(() => instanceAppReady is null
            ? Observable.Return(System.Reactive.Unit.Default)
            : instanceAppReady().Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "The instance's own GitHub App could not be read within 30 s; deciding without it.");
                    return Observable.Return(System.Reactive.Unit.Default);
                }));

    /// <summary>
    /// The installation token when an App identity is available once the identity can be decided
    /// (<see cref="WhenReady"/>), else an empty string (anonymous). The choice is made on every
    /// subscription, so a long-lived consumer follows a connect or a disconnect.
    /// </summary>
    public IObservable<string> GetInstallationTokenOrAnonymous() =>
        WhenReady.SelectMany(_ => IsConfigured ? GetInstallationToken() : Observable.Return(string.Empty));

    private GitHubAppOptions? InstanceOptions() =>
        instanceApp?.Invoke() is { IsConfigured: true } app ? app with { ApiBaseUrl = options.ApiBaseUrl } : null;

    /// <summary>The identity a mint uses now: the deployment's App, else the instance's own, else the
    /// (unconfigured) deployment options.</summary>
    private GitHubAppOptions Effective() =>
        options.IsConfigured ? options : InstanceOptions() ?? options;

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
    public IObservable<string> GetInstallationToken(string? installationOwner) =>
        Mint(Effective, () => WhenReady, installationOwner, GitSyncRefusal.Of("gitsync.app.notConfigured"));

    /// <summary>
    /// An installation token of the instance's OWN App (<see cref="InstanceGitHubApp"/>), whatever the
    /// deployment configures — the identity an instance-local package source, or a configured source
    /// that asks for it (<c>PluginCatalog:Sources:N:Identity = instance</c>), is read with. Faults
    /// <see cref="GitHubAppTokenMintStage.NotConfigured"/> when no instance App is connected; it never
    /// falls back to the deployment's App.
    /// </summary>
    /// <param name="installationOwner">The account the installation lives on, or null/blank for the
    /// App's only installation (a private App installs only on its owning account).</param>
    public IObservable<string> GetInstanceAppInstallationToken(string? installationOwner = null) =>
        Mint(() => InstanceOptions() ?? new GitHubAppOptions(), () => WhenInstanceAppReady, installationOwner,
            GitSyncRefusal.Of("gitsync.instanceApp.notConnected"));

    private IObservable<string> Mint(
        Func<GitHubAppOptions> identity, Func<IObservable<System.Reactive.Unit>> ready, string? installationOwner,
        GitSyncRefusal notConfigured)
    {
        var owner = (installationOwner ?? "").Trim();

        // Deferred: the promise is read at SUBSCRIPTION time, not when this observable was built.
        // A long-lived consumer holds ONE of these for the life of its feed and subscribes per pass
        // (the Store's git poll loop). Before, the promise of the moment was captured here, so
        // after the first refresh <see cref="Refresh"/> compared every later expiry against that
        // stale capture, never matched the current promise again, and handed the expired token
        // back on every pass — two token lifetimes after boot, every private source read as
        // "Bad credentials" until the process restarted (Systemorph/Memex#165).
        return ready().SelectMany(_ => Observable.Defer(() =>
        {
            return Resolve(identity, owner, notConfigured).Select(minted => minted.Token.Token);
        }))
        // 🚨 EVERY fault out of this method is a GitHubAppTokenMintException — translate all of it
        // or let it through, never half. A caller degrading to an anonymous fetch (the Store's
        // package feed does, so a public source keeps working) must be able to say WHICH failure it
        // is degrading over, and the only thing that survives every layer between here and its log
        // is the TYPE. Without it the degraded fetch is reported one layer later as GitHub's
        // "Bad credentials", and a mint that never happened reads as a credential GitHub rejected
        // (#4736). Cancellation is deliberately NOT translated: a cancelled mint is not a failed one.
        .Catch((Exception exception) => Observable.Throw<string>(AsMintFailure(exception)));
    }

    /// <summary>
    /// A token NARROWED to <paramref name="repositories"/> and <paramref name="permissions"/>
    /// (<c>POST /app/installations/{id}/access_tokens</c> with <c>repositories</c> + <c>permissions</c>)
    /// — the credential handed to something that must reach ONE repository read-only and nothing else,
    /// such as a standing build host's checkout (Doc/Architecture/BuildHosts: <c>{"contents":"read"}</c>
    /// for the claimed run's repository).
    ///
    /// <para>🚨 NEVER CACHED. Every call mints its own token: a scoped token is not the installation's
    /// token, and sharing one across scopes would hand a caller a repository or a permission it did not
    /// ask for. The installation is resolved exactly as <see cref="GetInstallationToken(string?)"/>
    /// resolves it, and every fault is a <see cref="GitHubAppTokenMintException"/>. GitHub refuses a
    /// scope wider than the installation's grant (422), which surfaces as
    /// <see cref="GitHubAppTokenMintStage.TokenExchange"/> — never as a wider token.</para>
    /// </summary>
    /// <param name="installationOwner">The account the installation lives on, or null/blank for the
    /// instance's own installation.</param>
    /// <param name="repositories">Bare repository NAMES on that account (no owner, no slash); at least one.</param>
    /// <param name="permissions">GitHub App permission → <c>read</c> | <c>write</c>; at least one.</param>
    public IObservable<string> GetScopedInstallationToken(
        string? installationOwner, IReadOnlyList<string> repositories, IReadOnlyDictionary<string, string> permissions)
    {
        var owner = (installationOwner ?? "").Trim();
        TokenScope scope;
        try
        {
            scope = TokenScope.Of(repositories, permissions);
        }
        catch (ArgumentException exception)
        {
            return Observable.Throw<string>(exception);
        }
        var notConfigured = GitSyncRefusal.Of("gitsync.app.notConfigured");
        return WhenReady.SelectMany(_ => Observable.Defer(() =>
            {
                var app = Effective();
                if (!app.IsConfigured)
                {
                    var missing = new GitHubAppTokenMintException(
                        GitHubAppTokenMintStage.NotConfigured, notConfigured.Render(null));
                    missing.Data[GitSyncRefusalException.DataKey] = notConfigured;
                    return Observable.Throw<string>(missing);
                }
                return Http.Run(ct => FetchInstallationTokenAsync(app, owner, scope, ct))
                    .Select(minted => minted.Token.Token);
            }))
            .Catch((Exception exception) => Observable.Throw<string>(AsMintFailure(exception)));
    }

    /// <summary>The validated scope of a narrowed token: bare repository names and read/write permissions.</summary>
    private sealed record TokenScope(ImmutableArray<string> Repositories, ImmutableSortedDictionary<string, string> Permissions)
    {
        public static TokenScope Of(IReadOnlyList<string> repositories, IReadOnlyDictionary<string, string> permissions)
        {
            ArgumentNullException.ThrowIfNull(repositories);
            ArgumentNullException.ThrowIfNull(permissions);
            var repos = repositories.Select(r => (r ?? "").Trim()).ToImmutableArray();
            if (repos.Length == 0)
                throw new ArgumentException("A scoped token names at least one repository.", nameof(repositories));
            foreach (var r in repos)
                if (r.Length == 0 || r.Contains('/') || r.Any(char.IsWhiteSpace))
                    throw new ArgumentException($"'{r}' is not a bare repository name (no owner, no slash).", nameof(repositories));
            if (permissions.Count == 0)
                throw new ArgumentException("A scoped token names at least one permission.", nameof(permissions));
            var perms = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (name, level) in permissions)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A permission name is blank.", nameof(permissions));
                if (level is not ("read" or "write"))
                    throw new ArgumentException($"Permission '{name}' must be 'read' or 'write' (got '{level}').", nameof(permissions));
                perms[name.Trim()] = level;
            }
            return new TokenScope(repos.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), perms.ToImmutable());
        }

        public string Body() => JsonSerializer.Serialize(new { repositories = Repositories, permissions = Permissions });
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
        var notConfigured = GitSyncRefusal.Of("gitsync.app.notConfigured");
        return WhenReady.SelectMany(_ => Observable.Defer(() =>
                Resolve(Effective, owner, notConfigured).Select(minted => minted.Installation)))
            .Catch((Exception exception) => Observable.Throw<GitHubAppInstallation>(AsMintFailure(exception)));
    }

    /// <summary>
    /// The cached mint of <paramref name="identity"/>'s installation on <paramref name="owner"/>,
    /// refreshed when within five minutes of expiry. The identity is resolved HERE, at subscription:
    /// a held observable follows a connect, a rotation or a disconnect of the instance's own App.
    /// </summary>
    private IObservable<MintedInstallation> Resolve(Func<GitHubAppOptions> identity, string owner, GitSyncRefusal notConfigured)
    {
        var app = identity();
        if (!app.IsConfigured)
        {
            // The typed mint failure, carrying its keyed refusal so every surface that shows it
            // renders it in the viewer's language (the shape of the named-owner refusal below).
            var missing = new GitHubAppTokenMintException(
                GitHubAppTokenMintStage.NotConfigured, notConfigured.Render(null));
            missing.Data[GitSyncRefusalException.DataKey] = notConfigured;
            return Observable.Throw<MintedInstallation>(missing);
        }
        // ONE promise per (identity, installation): two Apps never share a token, and a rotated
        // key or a different App re-mints instead of serving the old identity's token.
        var key = $"{app.ClientId}|{KeyFingerprint(app.PrivateKey!)}|{owner}";
        var source = cached.GetOrAdd(key, _ => CreateFetch(app, owner));
        return source.SelectMany(minted =>
            minted.Token.ExpiresAt > now().AddMinutes(5)
                ? Observable.Return(minted)
                : Refresh(key, app, owner, source));
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
    private IObservable<MintedInstallation> Refresh(
        string key, GitHubAppOptions app, string owner, IObservable<MintedInstallation> stale)
    {
        cached.Release(key, stale);
        return cached.GetOrAdd(key, _ => CreateFetch(app, owner));
    }

    private IObservable<MintedInstallation> CreateFetch(GitHubAppOptions app, string owner) =>
        Http.Run(ct => FetchInstallationTokenAsync(app, owner, scope: null, ct))
            .Catch((Exception ex) => Observable.Throw<MintedInstallation>(AsMintFailure(ex)));

    // A short, non-reversible tag of the key, so the cache key never holds the PEM itself.
    private static string KeyFingerprint(string privateKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(privateKey)))[..16];

    // ── HTTP leaves (run inside the I/O pool) ────────────────────────────────

    private async Task<MintedInstallation> FetchInstallationTokenAsync(
        GitHubAppOptions options, string owner, TokenScope? scope, CancellationToken ct)
    {
        string jwt;
        try
        {
            jwt = BuildAppJwt(options, now());
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
            ? await DiscoverInstallationIdAsync(options, jwt, owner, required: true, ct).ConfigureAwait(false)
            : options.InstallationId is { } pinned
                ? new GitHubAppInstallation(pinned, null)
                : await DiscoverInstallationIdAsync(options, jwt, options.InstallationOwner, required: false, ct)
                    .ConfigureAwait(false);
        var installationId = installation.Id;

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.ApiBaseUrl.TrimEnd('/')}/app/installations/{installationId}/access_tokens");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        if (scope is not null)
            req.Content = new StringContent(scope.Body(), Encoding.UTF8, "application/json");
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
            "Minted GitHub App installation token (installation {Id}{Owner}{Scope}, expires {Exp})",
            installationId, owner.Length > 0 ? $" on '{owner}'" : "",
            scope is null ? "" : $", scoped to {string.Join(",", scope.Repositories)} [{string.Join(",", scope.Permissions.Select(p => p.Key + ":" + p.Value))}]",
            expiresAt);
        return new MintedInstallation(new InstallationToken(token, expiresAt), installation);
    }

    private async Task<GitHubAppInstallation> DiscoverInstallationIdAsync(
        GitHubAppOptions options, string jwt, string? wantedOwner, bool required, CancellationToken ct)
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
    internal string BuildAppJwt(DateTimeOffset now) => BuildAppJwt(Effective(), now);

    private static string BuildAppJwt(GitHubAppOptions options, DateTimeOffset now)
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
