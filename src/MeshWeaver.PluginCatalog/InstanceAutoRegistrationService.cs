using MeshWeaver.AI;   // IProviderKeyProtector & co keep their ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using System.Collections.Immutable;
using System.Net;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The instance key a consumer received when it auto-registered at a registry, persisted so the
/// installation survives restarts without anyone re-copying a token. Lives in the <b>Admin</b>
/// partition (<c>Admin/PluginRegistryCredential/{host-slug}</c>) — platform admins and System only —
/// with the key encrypted at rest via <see cref="IProviderKeyProtector"/> when a master key is
/// configured (the same envelope provider keys use).
/// </summary>
public record PluginRegistryCredential
{
    /// <summary>Base URL of the registry this credential authenticates against.</summary>
    public string RegistryUrl { get; init; } = "";

    /// <summary>The instance id this installation registered under.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>The issued instance key (<c>mwi_…</c>), <c>enc:</c>-protected at rest when a
    /// master key is configured; plaintext passthrough otherwise (same policy as provider keys).</summary>
    public string ProtectedKey { get; init; } = "";

    /// <summary>When the registration happened.</summary>
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>The plan the registry put this instance on (#2804), as echoed by the registration
    /// response — what this installation is entitled to pull, shown in Settings. Null when the
    /// registry predates plans; the registry is the authority either way.</summary>
    public string? Plan { get; init; }
}

/// <summary>Where auto-registration credentials live and how a registry URL maps to a node id.</summary>
public static class PluginRegistryCredentials
{
    /// <summary>The credential node type.</summary>
    public const string NodeType = "PluginRegistryCredential";

    /// <summary>Namespace (under the Admin partition) holding the credentials.</summary>
    public const string Namespace = "Admin/PluginRegistryCredential";

    /// <summary>The credential node path for a registry base URL — keyed by HOST so the same
    /// registry reached via http/https or with a trailing slash resolves to one credential.</summary>
    public static string Path(string registryUrl)
    {
        var host = Uri.TryCreate((registryUrl ?? "").Trim(), UriKind.Absolute, out var uri)
            ? uri.Host
            : (registryUrl ?? "").Trim();
        var slug = new string(host.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"{Namespace}/{(slug.Length == 0 ? "registry" : slug)}";
    }
}

/// <summary>
/// Resolves the credential a consumer presents to a registry: an explicitly configured token always
/// wins; otherwise the stored <see cref="PluginRegistryCredential"/> from a first-startup
/// auto-registration, decrypted — and, since #2804, EXCHANGED for a short-lived JWT
/// (<see cref="SyncAccessToken"/>) at the registry's token route, so the durable <c>mwi_</c> key
/// leaves this process once per token lifetime instead of on every request; otherwise empty
/// (unauthenticated — only an open dev/e2e registry answers). Reads run as System: the credential
/// is deployment infrastructure in the Admin partition, not something the browsing user could (or
/// should) read.
///
/// <para>The exchange degrades, it never blocks: a registry that predates the token route, or one
/// that is momentarily unreachable, gets the durable key presented directly (the legacy window the
/// registry still honours), with one warning per attempt so the fallback is visible in the log.
/// Tokens are cached per registry AND credential (<see cref="TokenCacheKey"/>) until
/// <see cref="RefreshMargin"/> before their expiry — an instance field on this mesh-scoped singleton,
/// never static state.</para>
/// </summary>
public sealed class RegistryTokenResolver(IMessageHub hub, ILogger<RegistryTokenResolver> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long before a cached token's expiry it stops being reused — wide enough that a
    /// request in flight when the margin is reached still arrives with a live token.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    /// <summary>The clock a cached token's remaining life is judged by. The system clock in
    /// production; a test moves it past a token's expiry instead of waiting it out.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    // Shared fallback when no IHttpClientFactory is registered — HttpClient is designed to be
    // long-lived and shared; a per-call `new HttpClient()` leaks sockets. Immutable shared
    // resource, not a cache, so it does not fall under the no-static-state rule.
    private static readonly HttpClient SharedHttp = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> tokens =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The cache key of an exchanged token: the registry URL plus a fingerprint of the key that was
    /// presented. Keyed by URL alone, a second key for the same registry (a configured
    /// <c>Token</c> beside a stored registration, or a rotated key) would be answered with the FIRST
    /// key's token and authenticate as the wrong instance until expiry.
    /// </summary>
    internal static string TokenCacheKey(string registryUrl, string rawKey) =>
        registryUrl + "#" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)))[..16].ToLowerInvariant();

    /// <summary>
    /// The effective credential for <paramref name="registry"/>: the configured <c>Token</c> when
    /// there is one, else the stored auto-registration key — and in BOTH cases an instance key
    /// (<c>mwi_</c>) is EXCHANGED for a short-lived JWT when the registry issues one (#7312: so an
    /// installation that pulls on a configured key leaves its heartbeat too), falling back to the key
    /// itself when the registry answers no token. A configured token of any other shape is presented
    /// as configured. Cold; emits once. 🚨 Never for a caller whose counterpart IS the exchange
    /// endpoint — that is <see cref="ResolveDurableKey"/>.
    /// </summary>
    public IObservable<string> ResolveToken(PluginRegistryReference registry) =>
        ConfiguredToken(registry) is { } configured
            // A CONFIGURED instance key is exchanged like a stored one, so an installation that
            // pulls on PluginCatalog:Registries:N:Token still leaves its heartbeat
            // (Doc/Architecture/InstanceTelemetry); Exchange hands any other token shape back as is.
            ? Exchange(registry.Url, configured)
            : StoredCredential(registry)
                .SelectMany(raw => raw.Length == 0 ? Observable.Return("") : Exchange(registry.Url, raw));

    /// <summary>
    /// The credential for <paramref name="registry"/> as a SOURCE: COLD, and each subscription
    /// resolves it afresh (<see cref="ResolveToken"/>) — the cached exchanged token while it has more
    /// than <see cref="RefreshMargin"/> left, a newly exchanged one after that. Hand THIS, never a
    /// resolved string, to a client that outlives one request
    /// (<see cref="RegistryPackageSource.WithCredential"/>,
    /// <see cref="PluginBundleClient.WithCredential"/>).
    ///
    /// <para>🚨 MeshWeaver#7790: since a configured instance key is exchanged too (#7312), every
    /// registry client of a pass held a token that expires — at most
    /// <see cref="SyncAccessToken.MaximumLifetime"/>, and as little as <see cref="RefreshMargin"/>
    /// when it came from the cache. A boot reconcile or default install that outlived it was refused
    /// 401 for every remaining fetch, with the instance key valid throughout.</para>
    /// </summary>
    /// <param name="registry">The registry to authenticate against.</param>
    /// <returns>A cold source that emits one credential per subscription.</returns>
    public IObservable<string> Credential(PluginRegistryReference registry) =>
        Observable.Defer(() => ResolveToken(registry)).Take(1);

    /// <summary>
    /// The DURABLE credential this installation holds for <paramref name="registry"/> — the
    /// configured <c>Token</c>, else the stored auto-registration key decrypted — with NO exchange.
    /// Empty when it holds neither. Cold; emits once.
    ///
    /// <para>🚨 For the ONE caller whose counterpart IS the exchange endpoint. A container registry
    /// that decides a pull by forwarding the presented secret to the portal's key→token exchange
    /// (<c>cr.meshweaver.cloud</c> → <c>/api/instances/token</c>, <c>SelfUpdate:RegistryValidationUrl</c>)
    /// must be handed the <c>mwi_</c> key itself: that endpoint refuses a token by design — a token
    /// may never mint its successor — so the <c>mwa_</c> JWT <see cref="ResolveToken"/> yields for an
    /// auto-registered installation is a 401 there on every attempt (#4093, review of #4094). The
    /// two methods share ONE rule for "which durable credential" (<see cref="ConfiguredToken"/>,
    /// then the store) and differ only in whether the STORED key is exchanged.</para>
    /// </summary>
    public IObservable<string> ResolveDurableKey(PluginRegistryReference registry) =>
        ConfiguredToken(registry) is { } configured
            ? Observable.Return(configured)
            : StoredCredential(registry);

    /// <summary>An explicitly configured token wins over the store, on every path, as configured —
    /// it is presented as-is whether or not it is an instance key. Null when none is configured.</summary>
    private static string? ConfiguredToken(PluginRegistryReference registry) =>
        string.IsNullOrWhiteSpace(registry.Token) ? null : registry.Token.Trim();

    /// <summary>
    /// The durable <c>mwi_</c> key exchanged for a token at <c>{registry}/api/instances/token</c>,
    /// cached until <see cref="RefreshMargin"/> before it expires. Anything other than a token in
    /// the answer — a 404 from an older registry, a 5xx, a network fault — yields the key itself.
    /// </summary>
    private IObservable<string> Exchange(string registryUrl, string rawKey)
    {
        if (!rawKey.StartsWith(InstanceKeys.KeyPrefix, StringComparison.Ordinal))
            return Observable.Return(rawKey);           // not an instance key: nothing to exchange

        var url = (registryUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
            return Observable.Return(rawKey);

        var cacheKey = TokenCacheKey(url, rawKey);
        if (tokens.TryGetValue(cacheKey, out var hit) && hit.ExpiresAt - Clock.GetUtcNow() > RefreshMargin)
            return Observable.Return(hit.Token);

        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        var http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.BundleHttpClientName) ?? SharedHttp;

        // The exchange carries what this instance says about itself (Doc/Architecture/InstanceTelemetry):
        // additive, optional, bounded — the registry stamps it beside LastSeenAt and reads nothing of
        // it for authorization. The reader never errors and never waits longer than its budget, so
        // the token is never held hostage by the inventory.
        var telemetry = hub.ServiceProvider.GetService<InstanceTelemetryReader>();
        var telemetryRead = telemetry is null
            ? Observable.Return<InstanceTelemetry?>(null)
            : telemetry.Read().Select(read => (InstanceTelemetry?)read);

        return telemetryRead.Take(1).SelectMany(report => pool.Invoke<ExchangeOutcome>(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url + SyncTokenPayloads.Route);
                request.Headers.TryAddWithoutValidation("Authorization", $"{SyncAccessToken.Scheme} {rawKey}");
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new SyncTokenPayloads.Request { Telemetry = report }, SyncTokenPayloads.Json),
                    System.Text.Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                    return new ExchangeOutcome(null, status, 0);
                var body = JsonSerializer.Deserialize<SyncTokenPayloads.Response>(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), SyncTokenPayloads.Json);
                return new ExchangeOutcome(body?.AccessToken, status, body?.ExpiresIn ?? 0);
            }))
            .Select(result =>
            {
                if (string.IsNullOrWhiteSpace(result.Token) || result.ExpiresIn <= 0)
                {
                    logger.LogWarning(
                        "Token exchange at {Url} answered {Status} without a token — presenting the "
                        + "instance key directly (legacy)", url, result.Status);
                    return rawKey;
                }
                tokens[cacheKey] = (result.Token, Clock.GetUtcNow().AddSeconds(result.ExpiresIn));
                return result.Token;
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "Token exchange at {Url} failed — presenting the instance key directly (legacy)", url);
                return Observable.Return(rawKey);
            });
    }

    /// <summary>What the token route answered: the token and its lifetime, or the status that
    /// carried none.</summary>
    private sealed record ExchangeOutcome(string? Token, int Status, int ExpiresIn);

    /// <summary>The stored, decrypted registration credential for <paramref name="registry"/>, or
    /// empty. Reads run as System (the credential lives in the Admin partition).</summary>
    private IObservable<string> StoredCredential(PluginRegistryReference registry)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(PluginRegistryCredentials.Path(registry.Url), ReadTimeout))
            .Take(1)
            .Select(node =>
            {
                var credential = ContentAs(node);
                if (credential is null || string.IsNullOrWhiteSpace(credential.ProtectedKey))
                    return "";
                var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
                var raw = protector is null ? credential.ProtectedKey : protector.Unprotect(credential.ProtectedKey);
                if (string.IsNullOrWhiteSpace(raw))
                    logger.LogWarning(
                        "Stored registry credential for {Url} could not be decrypted — was the master key changed?",
                        registry.Url);
                return raw ?? "";
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Reading the stored registry credential for {Url} failed", registry.Url);
                return Observable.Return("");
            });
    }

    /// <summary>
    /// The legacy-token fallback decision, PURE: a named registry with no token of its own may
    /// use the legacy single-registry <see cref="PluginCatalogOptions.RegistryToken"/> ONLY when
    /// the attribution is unambiguous — the registry IS the legacy URL, or it is the sole one
    /// configured. Without the fallback, upgrading a consumer from RegistryUrl+RegistryToken to
    /// the named Registries shape silently drops auth and every catalog read 401s (systemorph,
    /// 2026-08-20); without the ambiguity guard, a token could be sent to a host it was not
    /// issued for. Null → no fallback, resolve the stored credential.
    /// </summary>
    /// <summary>
    /// Applies <see cref="LegacyTokenFallback"/> to every registry reference: each one that
    /// qualifies gets a COPY carrying the legacy token, so the resolver's ordinary
    /// registry-token fast path serves it — no service lookup inside the resolver (the
    /// in-resolver lookup hung the install lane silently on ci.4679). Pure.
    /// </summary>
    public static IReadOnlyList<PluginRegistryReference> WithLegacyTokens(
        PluginCatalogOptions options, IReadOnlyList<PluginRegistryReference> registries) =>
        registries
            .Select(r => LegacyTokenFallback(options, r) is { } legacy
                ? new PluginRegistryReference
                {
                    Name = r.Name, Url = r.Url, Ref = r.Ref, Token = legacy,
                }
                : r)
            .ToList();

    public static string? LegacyTokenFallback(
        PluginCatalogOptions options, PluginRegistryReference registry)
    {
        if (string.IsNullOrWhiteSpace(options.RegistryToken)
            || !string.IsNullOrWhiteSpace(registry.Token))
            return null;
        var matchesLegacyUrl = !string.IsNullOrWhiteSpace(options.RegistryUrl)
            && string.Equals(registry.Url.TrimEnd('/'), options.RegistryUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        var sole = options.Registries.Count(r => !string.IsNullOrWhiteSpace(r.Url)) == 1;
        return matchesLegacyUrl || sole ? options.RegistryToken.Trim() : null;
    }

    private PluginRegistryCredential? ContentAs(MeshNode? node)
    {
        if (node?.Content is null) return null;
        if (node.Content is PluginRegistryCredential typed) return typed;
        if (node.Content is not JsonElement json) return null;
        try { return JsonSerializer.Deserialize<PluginRegistryCredential>(json.GetRawText(), hub.JsonSerializerOptions); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// First-startup auto-registration: when <c>PluginCatalog:BootstrapKey</c> is configured, this
/// installation registers itself at the configured registry on startup — presenting the bootstrap
/// key, receiving its own <c>mwi_</c> instance key — and persists that key as a
/// <see cref="PluginRegistryCredential"/>. With the registry's <c>PluginCatalog:DefaultGrants</c>
/// seeding <c>Plugins/*</c>, a brand-new deployment reaches a filled plugin catalog with nobody
/// copying a token.
///
/// <para>Idempotent and deliberately single-shot per boot: a stored credential (or an explicitly
/// configured token) short-circuits, and a FAILED attempt logs an error and stops — no retry
/// timer. The failure modes are all operator-fixable config (bad/revoked bootstrap key → 401,
/// taken id → 409, wrong URL), and the Plugin Catalog tab makes the unauthenticated state visible;
/// a retry loop would only hide the misconfiguration (AGENTS.md → no watchdogs).</para>
/// </summary>
public sealed class InstanceAutoRegistrationService(
    IMessageHub hub, ILogger<InstanceAutoRegistrationService> logger)
    : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>The boot pass's outcome, replayed to whoever asks after the fact.</summary>
    private readonly AsyncSubject<DefaultInstallSummary> completed = new();

    /// <summary>
    /// The default install's outcome — emits once, when the boot pass has finished, and replays
    /// that emission to late subscribers (<c>AsyncSubject</c>). This is the signal to wait on when
    /// something must happen AFTER the platform's defaults are in place; nothing polls. Never emits
    /// on an instance whose hosted service was not started.
    /// </summary>
    public IObservable<DefaultInstallSummary> Completed => completed;

    /// <summary>What the LAST default-install pass of this process left undone, in words — what
    /// <see cref="RetryFailedDefaults"/> repairs. Null when it left nothing undone, and before the
    /// boot pass has ended.</summary>
    private volatile string? repairOwed;

    /// <summary>What a pass left undone that a LATER pass of this process can change (pure): the
    /// packages it could not install, and a source that did not answer its listing — packages a
    /// listing never named are neither failed nor installed. Null when the pass delivered
    /// everything it selected.
    ///
    /// <para>🚨 A source that is HELD is not on this list. A hold releases at the next process start
    /// and nowhere else (<see cref="ListAtProvenRef(ConfiguredPackageSource)"/>), so running the
    /// pass again cannot list it; counting it as owed would re-run the default install on every
    /// reconcile for the life of the process, each time against a source this lane must not revisit.
    /// It still makes the listing incomplete (<see cref="DefaultInstallSummary.ListingIncomplete"/>,
    /// #4097) — that is a statement about what the pass SAW, not about what a retry can
    /// repair.</para></summary>
    /// <param name="summary">The pass's outcome.</param>
    internal static string? LeftUndone(DefaultInstallSummary summary) =>
        summary.Failed > 0 && summary.SourceUnreachable
            ? $"{summary.Failed} package(s) failed ([{string.Join(", ", summary.Failures)}]) and a source could not be listed"
            : summary.Failed > 0
                ? $"{summary.Failed} package(s) failed ([{string.Join(", ", summary.Failures)}])"
                : summary.SourceUnreachable
                    ? "a source could not be listed"
                    : null;

    /// <summary>The sources HELD in this process, by name. A hold releases at the next process
    /// start (<see cref="ListAtProvenRef(ConfiguredPackageSource)"/>), so every later pass of this
    /// process — the repair pass above all — answers for such a source from here: its seal is not
    /// read again and it is not listed. Instance state of this mesh-scoped singleton; swapped
    /// atomically, never mutated.</summary>
    private ImmutableHashSet<string> heldSources = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 Disposes this service's OWN subscriptions and nothing else — never the <c>completed</c>
    /// signal (#5557). The registry reconciler, module discovery, the deployment report and the
    /// boot repair pass sequence on <see cref="Completed"/>, and a sibling hosted service can
    /// subscribe after this one has stopped (hosted services stop in reverse registration order; an aborted
    /// startup skips <c>StopAsync</c> and disposes the container). A disposed
    /// <see cref="System.Reactive.Subjects.AsyncSubject{T}"/> answers that late subscribe with an
    /// <see cref="ObjectDisposedException"/> the reader then reports as its OWN failure. The one-shot
    /// replay holds no resource, so leaving it readable costs nothing: a late reader sees the value
    /// or waits, and its own owner's teardown ends the wait — the same rule
    /// <c>PreWarmCompletion</c> follows.
    /// </remarks>
    public void Dispose() => subscriptions.Dispose();

    private void Start()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        var registry = RegistryTokenResolver
            .WithLegacyTokens(options, options.EffectiveRegistries).FirstOrDefault();

        // Two independent phases, sequenced: first make sure this installation HAS an instance key
        // (auto-registering when a bootstrap key is configured), then install the packages this
        // deployment should come up with. Phase 2 runs for a MANUALLY tokened install too — "a new
        // instance ships with the platform plugins" is not conditional on how it got its key.
        //
        // 🚨 Phase 2 runs even with NO registry configured. A REGISTRY instance (the one holding the
        // git credential and serving /api/plugins) is not a consumer of its own HTTP surface, so it
        // has no EffectiveRegistries at all — and it is exactly the instance that must come up with
        // the platform baseline it serves. Bailing out here on `registry is null` is what left the
        // production portal with no mechanism to restore its agent catalog (#902).
        var registered = registry is null
            ? Observable.Return(Unit.Default)
            // 🚨 A failed registration must NOT sink the install phase. The phases are independent:
            // what matters to phase 2 is whether a usable key can be RESOLVED, not how phase 1
            // went. The case that forced this: two replicas start together, both see no stored
            // credential, one registers and the other gets 409 — the loser's convergence read can
            // still miss the sibling's just-committed write, and it would then skip installing
            // although the deployment holds a perfectly good key moments later. Phase 2 resolves
            // the token itself and fails closed (a warning + a 401 from the registry) when there
            // genuinely is none.
            : EnsureRegistered(options, registry).Catch((Exception ex) =>
            {
                // 🚨 Say what actually failed, not a menu of guesses. The old text asserted
                // "registration failed (401/409)" for ANY exception here — including a LOCAL
                // failure after the registry had already accepted the request, which is how #2585
                // read as a bad key while the key had been issued and lost. The exception carries
                // the truth; the guidance stays as guidance.
                logger.LogError(ex,
                    "First-startup instance registration against {Url} did not complete: {Reason}. "
                    + "If this is an HTTP refusal: 401 = invalid or revoked bootstrap key, 409 = "
                    + "instance id already taken. A failure AFTER the request (a store or "
                    + "encryption error) can mean the registry already holds the id — check it "
                    + "before re-registering under a new one. Continuing to the install phase, "
                    + "which uses whatever key this installation already holds.",
                    registry.Url, ex.Message);
                return Observable.Return(Unit.Default);
            });

        // 🚨 Sequenced AFTER the host's boot bake, when one is registered (#1114). Both phases
        // write through per-node hubs (phase 2 upserts every package's partition ROOT via the
        // owning hub's stream), and on a pre-warming host a framework roll leaves every dynamic
        // NodeType ABI-stale: activating such a root mid-bake parks its enrichment on the type's
        // rebuild, which queues BEHIND the sweep's ~240 sequential compiles — far past the
        // cross-hub Update's 30 s initial-state bound. The observed shape was every default
        // package aborting with "no initial state arrived for '{package}' within 30s" on every
        // pod boot, leaving the instance with no default plugins until the next boot re-ran the
        // identical race. Waiting on the bake's one-shot completion signal is ordering on the
        // actual precondition — no timer, no retry, replayed to late subscribers; a host without
        // the pre-warm has no PreWarmCompletion registered and proceeds exactly as before.
        //
        // 🚨 The signal now carries HOW the bake settled, and this consumer deliberately does NOT
        // branch its BEHAVIOUR on it — it proceeds on all three outcomes, and says which one it
        // proceeded on. The reasoning, outcome by outcome:
        //
        //   Completed      — the precondition this ordering exists for is met; install.
        //   NotApplicable  — there is no bake on this host, so there is nothing to be behind; the
        //                    per-node hubs are not parked and the install runs as it always did.
        //   Faulted        — the sweep errored, so it never saturated the compile queue either;
        //                    there is nothing left to wait FOR, and the types compile lazily. More
        //                    importantly, withholding the install here would be backwards: installs
        //                    REPAIR content, and a boot where the bake could not run is exactly the
        //                    boot where the instance is most likely to need its defaults. That is
        //                    the same call the pre-warmer already documents for a Regressed+armed
        //                    pod ("the default install proceeding is deliberate — installs repair
        //                    content"). Readiness is refused by the bake gate, which is the surface
        //                    that owns that decision; the installer is not a second gate.
        //
        // What the outcome DOES buy is that a boot whose bake errored is no longer indistinguishable
        // from one that verified ~240 types in every downstream log — previously this waited on an
        // IObservable<Unit> that fired identically in all three cases.
        var bakeSettled = hub.ServiceProvider.GetService<PreWarmCompletion>()?.Settled
            ?? Observable.Return(PreWarmSettlement.NotApplicable);

        subscriptions.Add(bakeSettled
            .Take(1)
            // Hop off whatever thread settled the bake (the sweep's completion callback, or the
            // ApplicationStarted callback on a no-bake host) before the install chain runs.
            .ObserveOn(TaskPoolScheduler.Default)
            .Do(settlement =>
            {
                if (settlement is PreWarmSettlement.Faulted)
                    logger.LogWarning(
                        "[DefaultInstall] proceeding after a bake that FAULTED — this host verified "
                        + "nothing about its NodeTypes, so package roots may compile lazily as they "
                        + "are touched. Installing anyway: installs repair content, and readiness "
                        + "is the bake gate's call, not this service's.");
                else
                    logger.LogInformation(
                        "[DefaultInstall] bake settled as {Settlement} — starting provisioning",
                        settlement);
            })
            .SelectMany(_ => registered)
            .SelectMany(_ => InstallDefaults(options))
            // 🚨 SubscribeOn the thread pool, NOT the host-startup thread. The chain is synchronous
            // right up to its first genuinely-async leaf (an in-memory or already-cached source
            // lists its packages inline), so subscribing here would run the whole install ON the
            // startup thread — re-entering the hub schedulers mid-init, which deadlocks. Same fix,
            // same reason, as StaticRepoImportHostedService.
            .SubscribeOn(TaskPoolScheduler.Default)
            // 🚨 A pass that dropped a package must not report at the same level as a clean one.
            // The boot line was Information either way, so an instance coming up short of a
            // package the deployment DECLARES read as a healthy boot (#2254).
            .Do(summary =>
            {
                repairOwed = LeftUndone(summary);
                if (summary.Failed > 0)
                    logger.LogError(
                        "[DefaultInstall] reconciled with FAILURES: {Summary}. This installation is "
                        + "missing {Count} declared package(s) — they are recorded on {Ledger} and "
                        + "re-attempted when a registry next answers a feed read, and on the next boot.",
                        summary, summary.Failed, SeedLedgerPath);
                else
                    logger.LogInformation("[DefaultInstall] reconciled: {Summary}", summary);
            })
            // A failed pass must not leave the completion signal hanging forever — report it as a
            // pass that installed nothing, having already logged the cause at Error.
            .Catch((Exception ex) =>
            {
                repairOwed = "the pass did not complete";
                logger.LogError(ex,
                    "First-startup plugin provisioning failed; it is re-attempted when a registry next "
                    + "answers a feed read, and on the next boot.");
                return Observable.Return(DefaultInstallSummary.Empty);
            })
            .Subscribe(completed));
    }

    /// <summary>
    /// The registration pre-flight, as a PURE decision — every branch that decides whether this
    /// installation may present its bootstrap key is here, unit-testable with no mesh.
    ///
    /// <para>🚨 The master-key probe is the #2585 fix, and its ORDER is the point: the issued
    /// <c>mwi_</c> key is returned exactly once and stored encrypted-or-not-at-all, so an
    /// installation that CANNOT encrypt must refuse to register — probing after
    /// <c>Register</c> succeeded is how a valid registration's key was thrown away and the
    /// instance id burned (the registry held the id, the pod held nothing, and no retry could
    /// ever succeed).</para>
    /// </summary>
    /// <param name="bootstrapKey">The configured bootstrap key, if any.</param>
    /// <param name="registryToken">An explicitly configured registry token, if any.</param>
    /// <param name="instanceId">The configured instance id, if any.</param>
    /// <param name="masterKeyPresent">Whether the platform key protector can actually encrypt.</param>
    /// <param name="consentGiven">Whether a platform admin of this installation has accepted the
    /// privacy statement and the platform terms (<see cref="InstanceConsent"/>). Gates the OPEN
    /// lane only: an instance provisioned with a registration key was set up by an operator who
    /// accepted on the fleet's side, and asking an unattended pod would leave it unregistered
    /// forever.</param>
    internal static (RegistrationPreflight Action, string? Reason) DecideRegistration(
        string? bootstrapKey, string? registryToken, string? instanceId, bool masterKeyPresent,
        bool consentGiven)
    {
        // OPEN registration: an instance id and no key. The registry decides whether it accepts
        // un-keyed callers and which plan they enrol into (the free tier on the cloud registry);
        // a refusal is logged and skipped by the caller, never an abort — that id was harmless
        // before the open lane existed and must stay so.
        var open = string.IsNullOrWhiteSpace(bootstrapKey);
        if (open && string.IsNullOrWhiteSpace(instanceId))
            return (RegistrationPreflight.Skip, null);  // manual token (or none) — nothing to register
        if (!string.IsNullOrWhiteSpace(registryToken))
            return (RegistrationPreflight.Skip, open
                ? "PluginCatalog:InstanceId is set but a registry token is already configured — the "
                  + "explicit token wins; skipping (open) auto-registration."
                : "PluginCatalog:BootstrapKey is set but a registry token is already configured — the "
                  + "explicit token wins; skipping auto-registration.");
        if (string.IsNullOrWhiteSpace(instanceId))
            return (RegistrationPreflight.Abort,
                "PluginCatalog:BootstrapKey is set but PluginCatalog:InstanceId is not. The instance "
                + "id is a stable global identity and is never derived from a machine or pod name — "
                + "set it explicitly.");
        if (!masterKeyPresent)
            return open
                ? (RegistrationPreflight.Skip,
                    "PluginCatalog:InstanceId is set (open registration) but no encryption master key "
                    + "is configured (Ai:KeyProtection:MasterKey), so the instance key the registry "
                    + "would issue could not be stored — skipping registration; configure the master "
                    + "key and restart to register.")
                : (RegistrationPreflight.Abort,
                    "PluginCatalog:BootstrapKey is set but no encryption master key is configured "
                    + "(Ai:KeyProtection:MasterKey), so the instance key the registry would issue could "
                    + "not be stored — it is stored encrypted or not at all. REFUSING to register before "
                    + "anything is consumed: the bootstrap key stays valid and the instance id stays "
                    + "free, so configuring the master key and restarting completes the registration. "
                    + "(Registering first and failing to store would burn both — #2585.)");
        // The instance starts, and ASKS. An open registration is a deployment presenting itself to
        // a registry on someone's behalf; until a platform admin here has read and accepted the
        // privacy statement and the platform terms, nothing is registered, no token is obtained
        // and no catalogue is pulled. The caller waits for the consent record and then registers —
        // no restart, no timer.
        if (open && !consentGiven)
            return (RegistrationPreflight.AwaitConsent,
                "PluginCatalog:InstanceId is set (open registration) but no platform admin has accepted "
                + "the privacy statement and platform terms yet — waiting. Open Settings ▸ Instance "
                + "registration, review both, and register; the registration then runs at once.");
        return (RegistrationPreflight.Register, null);
    }

    /// <summary>What the registration pre-flight decided.</summary>
    internal enum RegistrationPreflight
    {
        /// <summary>Nothing to register — proceed to the install phase.</summary>
        Skip = 0,

        /// <summary>Misconfigured — log the reason and do not go on to install.</summary>
        Abort = 1,

        /// <summary>Present the bootstrap key.</summary>
        Register = 2,

        /// <summary>Open registration, and the deployment's consent has not been given yet — wait
        /// for the <see cref="InstanceConsent"/> record, then register.</summary>
        AwaitConsent = 3,
    }

    /// <summary>
    /// Phase 1 — make sure this installation holds an instance key: auto-register when a bootstrap
    /// key is configured and nothing is stored yet. Emits once when a credential is in place (or
    /// when none is needed because a token is configured explicitly).
    /// </summary>
    private IObservable<Unit> EnsureRegistered(PluginCatalogOptions options, PluginRegistryReference registry)
    {
        var bootstrapKey = options.BootstrapKey?.Trim() ?? "";
        // The probe asks the key PROVIDER, not Protect: the provider answers "can anything be
        // encrypted right now" without a throw, which is exactly what a pre-flight needs
        // (IProviderKeyProtector's own doc names this as the sanctioned order).
        var masterKeyPresent =
            hub.ServiceProvider.GetService<IMasterKeyProvider>()?.GetMasterKey() is not null;

        // Consent is read only where it can gate: the OPEN lane with an instance id. A keyed or
        // unconfigured deployment never touches the consent record.
        var open = bootstrapKey.Length == 0 && !string.IsNullOrWhiteSpace(options.InstanceId);
        var consent = open ? ConsentGiven(wait: false) : Observable.Return(true);

        return consent.SelectMany(consentGiven =>
        {
            var (action, reason) = DecideRegistration(
                bootstrapKey, registry.Token, options.InstanceId, masterKeyPresent, consentGiven);
            switch (action)
            {
                case RegistrationPreflight.Skip:
                    if (reason is not null)
                        logger.LogInformation("{Reason}", reason);
                    return Observable.Return(Unit.Default);
                case RegistrationPreflight.Abort:
                    logger.LogError("{Reason}", reason);
                    return Observable.Empty<Unit>();       // misconfigured → do not go on to install
                case RegistrationPreflight.AwaitConsent:
                    logger.LogInformation("{Reason}", reason);
                    // The instance has asked; now it waits for the answer — the consent record a
                    // platform admin writes from Settings — and registers the moment it lands.
                    // Ordering on the actual precondition: no timer, no restart, no poll.
                    return ConsentGiven(wait: true)
                        .Where(given => given)
                        .Take(1)
                        .Do(_ => logger.LogInformation(
                            "Consent recorded — registering this installation as '{InstanceId}' at {Url}.",
                            options.InstanceId!.Trim(), registry.Url))
                        .SelectMany(_ => Register(options, registry, bootstrapKey));
            }
            return Register(options, registry, bootstrapKey);
        });
    }

    /// <summary>
    /// Whether the deployment's <see cref="InstanceConsent"/> is on record and complete. Existence
    /// through a query (empty-on-absent, storm-safe — a point read of a maybe-absent node opens the
    /// breaker on the path, #2229), the content off the live node stream. With
    /// <paramref name="wait"/> the query is LEFT OPEN until a record appears, which is how the
    /// registration that was waiting for consent wakes up without a restart.
    /// </summary>
    internal IObservable<bool> ConsentGiven(bool wait)
    {
        var workspace = hub.GetWorkspace();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var path = MeshWeaverInstanceNodeType.ConsentPath;
        IObservable<IEnumerable<MeshNode>> Rows() => ConsentRowsOverride?.Invoke()
            ?? accessService.RunAsSystem(() => workspace.GetQuery(
                $"consent|{path}", $"path:{path} nodeType:{MeshWeaverInstanceNodeType.ConsentNodeType}"));
        IObservable<MeshNode> Content() => (ConsentContentOverride?.Invoke()
                ?? accessService.RunAsSystem(() => workspace.GetMeshNodeStream(path)))
            .Where(node => node is not null)
            .Select(node => node!);
        bool IsComplete(MeshNode node) =>
            node.ContentAs<InstanceConsent>(hub.JsonSerializerOptions)?.IsComplete ?? false;
        // One-shot: the record as it stands now.
        IObservable<bool> Verdict(IEnumerable<MeshNode> r) => r.Any()
            ? Content().Take(1).Timeout(TimeSpan.FromSeconds(10)).Select(IsComplete)
            : Observable.Return(false);
        // 🚨 Waiting: the record's node stream stays LIVE. The query and the node stream are two
        // independent feeds; a query emission can arrive before the node stream has the completed
        // record, and a one-shot read then consumes the stale, incomplete one — with no further
        // query emission guaranteed, registration would never run. Only the FIRST value is bounded.
        IObservable<bool> LiveVerdict(IEnumerable<MeshNode> r) => r.Any()
            ? Content()
                .Timeout(Observable.Timer(TimeSpan.FromSeconds(10)), _ => Observable.Never<long>())
                .Select(IsComplete)
            : Observable.Return(false);

        if (!wait)
            return Observable.Defer(Rows).Take(1).Timeout(TimeSpan.FromSeconds(10))
                .SelectMany(Verdict)
                .Catch((Exception ex) =>
                {
                    // A read that reached no verdict is not consent. Say so, and stay unregistered.
                    logger.LogWarning(ex, "Could not read the instance consent record at {Path} — treating as not given", path);
                    return Observable.Return(false);
                });

        // 🚨 The WAIT is a standing watch, and it used to die on its first fault: the Catch above
        // answered `false`, the waiter's `Where(given)` filtered it out, the chain completed, and the
        // registration never ran until a restart — however soon after the admin consented. It now
        // stays live (a record written incomplete and completed later is re-read on each emission)
        // and RE-OPENS after any fault on the standing-watch backoff, at Warning, for as long as
        // the waiter is subscribed.
        return StandingWatchRecovery.ReopenOnTransientFault(
            () => Observable.Defer(Rows).Select(LiveVerdict).Switch(),
            maxConsecutive: int.MaxValue,
            backoff: StandingWatchRecovery.DefaultBackoff,
            scheduler: ConsentReopenScheduler ?? Scheduler.Default,
            onReopen: (ex, attempt, delay) => logger.LogWarning(ex,
                "Waiting for the instance consent record at {Path}: the read faulted (attempt {Attempt}) — "
                + "re-opening in {Delay}", path, attempt, delay),
            isTransient: _ => true);
    }

    /// <summary>Test seam: the consent existence query. Null in production.</summary>
    internal Func<IObservable<IEnumerable<MeshNode>>>? ConsentRowsOverride { get; set; }

    /// <summary>Test seam: the consent record's live node. Null in production.</summary>
    internal Func<IObservable<MeshNode?>>? ConsentContentOverride { get; set; }

    /// <summary>Test seam: the clock the consent wait's re-open runs on. Null in production.</summary>
    internal IScheduler? ConsentReopenScheduler { get; set; }

    /// <summary>The registration proper — present the key (or none, openly), store the issued
    /// credential. Split out of <see cref="EnsureRegistered"/> so the consent-deferred path and the
    /// immediate path run the same code.</summary>
    private IObservable<Unit> Register(PluginCatalogOptions options, PluginRegistryReference registry, string bootstrapKey)
    {
        var instanceId = options.InstanceId!.Trim();

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var client = hub.ServiceProvider.GetRequiredService<InstanceRegistrationClient>();
        var credentialPath = PluginRegistryCredentials.Path(registry.Url);

        return (Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(credentialPath, TimeSpan.FromSeconds(10)))
            .Take(1)
            .SelectMany(existing =>
            {
                if (existing is not null)
                {
                    logger.LogDebug("Registry credential already stored at {Path} — no registration needed.",
                        credentialPath);
                    return Observable.Return(Unit.Default);
                }

                return client.Register(registry.Url, new InstanceRegistrationPayloads.Request(
                        bootstrapKey, instanceId,
                        DisplayName: options.InstanceName?.Trim() is { Length: > 0 } name ? name : instanceId,
                        HomeUrl: options.HomeUrl?.Trim() ?? ""))
                    .SelectMany(result =>
                    {
                        // 🚨 Required, and no `?? result.InstanceKey` fallback: the issued key is
                        // this installation's credential at the registry. Storing it in the clear
                        // is the same defect as a plaintext provider key, so a protector that
                        // cannot encrypt it means we do not store it at all.
                        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
                        var node = new MeshNode(
                            credentialPath.Split('/').Last(), PluginRegistryCredentials.Namespace)
                        {
                            Name = $"Registry credential: {registry.Url}",
                            NodeType = PluginRegistryCredentials.NodeType,
                            State = MeshNodeState.Active,
                            Content = new PluginRegistryCredential
                            {
                                RegistryUrl = registry.Url.TrimEnd('/'),
                                InstanceId = result.InstanceId,
                                // `!`: Protect returns null only for a null input, and the registry
                                // never issues a null key — it either refuses or returns ciphertext.
                                ProtectedKey = protector.Protect(result.InstanceKey)!,
                                RegisteredAt = DateTimeOffset.UtcNow,
                                Plan = result.Plan,
                            },
                        };
                        return Observable.Defer(() =>
                        {
                            var write = accessService.ImpersonateAsSystem();
                            return meshService.CreateNode(node)
                                .Select(_ =>
                                {
                                    logger.LogInformation(
                                        "Auto-registered this installation as instance '{InstanceId}' at {Url}; "
                                        + "the issued key is stored at {Path}.",
                                        result.InstanceId, registry.Url, credentialPath);
                                    return Unit.Default;
                                })
                                .Finally(() => write.Dispose());
                        });
                    })
                    // An OPEN registration the registry refuses (401: it configures no open key) is
                    // not this deployment's fault and not a reason to withhold the install phase —
                    // the id was set, no key was, and that combination was harmless before the open
                    // lane existed. Say what would make it work, and go on unregistered.
                    .Catch((InstanceRegistrationException ex) =>
                    {
                        if (bootstrapKey.Length > 0 || ex.StatusCode != HttpStatusCode.Unauthorized)
                            return Observable.Throw<Unit>(ex);
                        logger.LogWarning(
                            "Open registration as '{InstanceId}' at {Url} was refused (401): that registry "
                            + "accepts no un-keyed registrations. Ask its platform admin for a registration "
                            + "key (PluginCatalog:BootstrapKey) — or for the free-tier open key to be "
                            + "configured there. Continuing without a registry credential.",
                            instanceId, registry.Url);
                        return Observable.Return(Unit.Default);
                    })
                    // Concurrent replicas race this whole block: both read "no credential", one
                    // registers first, and the loser sees 409 (the id is taken) — or, narrower, the
                    // credential write collides. In BOTH cases the deployment as a whole is fine if
                    // a credential now exists; converge on it instead of erroring a healthy pod.
                    // A 409 with NO stored credential is the real misconfiguration (the id belongs
                    // to someone else) and still propagates.
                    .Catch((Exception ex) => Observable.Using(
                            () => accessService.ImpersonateAsSystem(),
                            _ => hub.GetMeshNode(credentialPath, TimeSpan.FromSeconds(10)))
                        .Take(1)
                        .SelectMany(stored =>
                        {
                            if (stored is null)
                                return Observable.Throw<Unit>(ex);
                            logger.LogInformation(
                                "Another replica completed the auto-registration first; using the "
                                + "credential stored at {Path}.", credentialPath);
                            return Observable.Return(Unit.Default);
                        }));
            }));
    }

    /// <summary>
    /// Phase 2 — THE DEFAULT INSTALL. The single code path that decides what this installation
    /// comes up with and in what order. Everything installs through
    /// <c>CatalogLayoutAreas.InstallOrUpdate</c> — the very method the catalog tab's Install button
    /// and the green-build watcher use — so the ModuleVersion "nothing to sync" gate, the
    /// manifest-diff fast path and the per-node <c>SyncBehavior</c> claim all apply unchanged.
    /// There is no parallel installer.
    ///
    /// <para><b>Three selection signals, one decision.</b> They answer different questions and all
    /// feed the same ordered install:</para>
    /// <list type="bullet">
    ///   <item><b>The package's own <c>preInstalled</c> declaration</b> — the PLATFORM's baseline
    ///     (the Agents and Skills libraries, Essentials, …). Reconciled on EVERY boot, because it
    ///     is what the platform requires to function and what must survive a self-update; it is
    ///     also the only thing that can heal an instance whose baseline partition was lost (#902).
    ///     Suppressible with <see cref="PluginCatalogOptions.InstallPreInstalledPackages"/>.
    ///     <b>The default-apps lane</b> (<c>defaultApp: true</c> — Threads, Examples) rides the same
    ///     reconcile but is NOT dropped by an explicit declared set (<see cref="DefaultAppsApply"/>).</item>
    ///   <item><b>The ENVIRONMENT's feature flags</b> (<c>Features:Flags:{name}:Packages</c>) —
    ///     what THIS deployment always has. Reconciled on EVERY boot, because that is the whole
    ///     difference between a policy and a seed: an environment declaring "I have the Store"
    ///     must still have it after a self-update, a lost partition, or a boot whose install
    ///     failed. An enabled flag includes its packages; a declared-but-DISABLED flag EXCLUDES
    ///     them, and the exclusion wins over every other signal here — that is how
    ///     "all of Plugins, without the games" is one line per environment.</item>
    ///   <item><b>The operator's <see cref="PluginCatalogOptions.InstallByDefault"/> patterns</b> —
    ///     the extras a FRESH deployment seeds itself with, source-scoped so an instance granted
    ///     paid course content never auto-installs it. Gated on the ledger: this seeds a new
    ///     deployment rather than asserting a policy, so an admin who later uninstalls a package is
    ///     not fought by the next restart. 🚨 Deliberately UNCHANGED by the flag lane — the two
    ///     coexist, and an already-populated installation is exactly the case the seed cannot
    ///     express and the flags can. A COMMERCIAL package an entry names EXACTLY
    ///     (<c>Source/Package</c>) is installed on the deployment configuration's authority
    ///     (<see cref="DeploymentConfigAuthority"/>, principal <c>deployment-config:{instance}</c>),
    ///     with its requirements; one a wildcard merely covers is skipped.</item>
    ///   <item><b>The operator's patterns over a LOCAL CHECKOUT</b>
    ///     (<see cref="ConfiguredPackageSource.LocalCheckout"/>) — the same patterns, but the
    ///     source is a working tree mounted into this host, which is what a self-registry
    ///     <c>memex-local</c> serves. Reconciled on EVERY boot: a mounted tree is standing operator
    ///     intent, the portal exists to MIRROR it, and nothing else refreshes it — the update
    ///     watcher needs GitHub webhooks a local install never receives and the registry
    ///     reconciler does nothing without a registry. Seeded once, the portal silently served a
    ///     week-old course while every boot logged "0 written, 0 unchanged" (MeshWeaver#3359).
    ///     Costs nothing when nothing changed (the content-hash gate), and a disabled feature flag
    ///     still excludes — the exclusion outranks this lane like every other.</item>
    /// </list>
    ///
    /// <para>Installs run SEQUENTIALLY (<c>Concat</c>) — each one writes a partition's worth of
    /// nodes and may compile node types; a parallel fan-out on a cold starting pod is how you
    /// saturate it.</para>
    /// </summary>
    private IObservable<DefaultInstallSummary> InstallDefaults(PluginCatalogOptions options) =>
        InstanceSourcesRead().SelectMany(read => InstallDefaultsWith(options, read.Sources)
            // An unread instance store is a source that did not ANSWER: the pass still installs the
            // deployment's packages, but it is not complete, so the repair lane (LeftUndone /
            // RetryFailedDefaults) knows work remains once the store answers.
            .Select(summary => read.Failed
                ? summary with { ListingIncomplete = true, SourceUnreachable = true }
                : summary));

    /// <summary>
    /// The instance's OWN package sources (<c>Admin/PackageSource-*</c>,
    /// Doc/Architecture/InstanceRepositoryAccess) once their catalog has read the store — so a boot
    /// pass never mistakes "not read yet" for "none". A catalog that does not answer within its
    /// bound is said at Error and the pass proceeds with the deployment's own sources: the instance's
    /// additions are then installed by the next pass, and the deployment's never wait on them.
    /// </summary>
    private IObservable<(IReadOnlyList<InstancePackageSourceContent> Sources, bool Failed)> InstanceSourcesRead()
    {
        var catalog = hub.ServiceProvider.GetService<InstancePackageSourceCatalog>();
        if (catalog is null)
            return Observable.Return<(IReadOnlyList<InstancePackageSourceContent>, bool)>(([], false));
        // Loaded is not enough: after a fault the catalog keeps its last listing, and WhenLoaded
        // still answers at once. A pass that read that stale listing would report itself complete
        // and clear the repair debt although a source added during the outage was never read — so
        // it waits for the re-opened query's complete listing, inside the same bound, and a
        // continuing outage reaches the failure path below.
        return WhenHealthy(catalog.WhenLoaded, catalog.Changes.Select(_ => Unit.Default), () => catalog.Faulted)
            .Select(_ => ((IReadOnlyList<InstancePackageSourceContent>)catalog.Sources, false))
            .Timeout(TimeSpan.FromSeconds(30))
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "[DefaultInstall] the instance's own package sources (Admin/PackageSource-*) could not "
                    + "be read; this pass installs from the deployment's sources only, and what the "
                    + "instance's administrators chose to install by default waits for the next pass.");
                return Observable.Return<(IReadOnlyList<InstancePackageSourceContent>, bool)>(([], true));
            });
    }

    /// <summary>
    /// Emits once the catalog has loaded AND is not faulted — immediately when both already hold,
    /// otherwise on the first change after which it is healthy again. Pure over its inputs.
    /// </summary>
    /// <param name="whenLoaded">Emits once the first complete listing has arrived.</param>
    /// <param name="changes">Emits on subscribe and after every snapshot change, the fault flag included.</param>
    /// <param name="faulted">Whether the catalog's live query is down right now.</param>
    internal static IObservable<Unit> WhenHealthy(IObservable<Unit> whenLoaded, IObservable<Unit> changes, Func<bool> faulted)
        => whenLoaded.Take(1).SelectMany(_ => changes.Where(_ => !faulted()).Take(1));

    /// <summary>
    /// Each instance source whose repository a configured source also names, renamed to that
    /// configured source's name — the one the listing carries, because the configured source wins
    /// the duplicate. Every other source is returned unchanged. Pure.
    /// </summary>
    /// <param name="instanceSources">The instance's own sources.</param>
    /// <param name="configured">The deployment's configured sources: name and repository.</param>
    internal static IReadOnlyList<InstancePackageSourceContent> UnderWinningNames(
        IReadOnlyList<InstancePackageSourceContent> instanceSources,
        IReadOnlyList<(string Name, string RepoPath)> configured)
    {
        var byRepo = configured
            .Select(c => (Repo: GitSourceCredentials.NormalizeRepository(c.RepoPath), c.Name))
            .Where(c => c.Repo is not null && !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Repo!, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        return [.. instanceSources.Select(s =>
            GitSourceCredentials.NormalizeRepository(s.Repo) is { } repo && byRepo.TryGetValue(repo, out var name)
                ? s with { Name = name }
                : s)];
    }

    private IObservable<DefaultInstallSummary> InstallDefaultsWith(
        PluginCatalogOptions options, IReadOnlyList<InstancePackageSourceContent> instanceSources)
    {
        // The instance's own install-by-default choices (Admin/PackageSource-*.installByDefault) join
        // the deployment's patterns as exact Source/Package entries FOR SELECTION ONLY. They never
        // change what the RECORD's set means — the baseline decision below reads the record alone —
        // and they carry no deployment-configuration authority: that authority, and the dependency
        // closure it admits, is derived from the record's own entries (`authorizing`) alone, so a
        // locally chosen package can never pull a commercial requirement in as deployment-config.
        var authorizing = options.InstallByDefault
            .Select(PluginGrantEntry.TryParse)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        // A local source naming a repository the deployment's configuration already names is
        // dropped from the source list (PackageSources.FromInstance — the record's source wins), so
        // its selections are spelled under the WINNING source's name; otherwise "reporting/Reporting"
        // could never match the listing under "Acme" and the package would never be installed. This
        // renames only — the entries still join `wanted`, never `authorizing`.
        var configured = hub.ServiceProvider.GetService<IConfiguration>() is { } configuration
            ? PackageSources.FromConfiguration(hub, configuration, logger)
                .Select(c => (c.Name, c.RepoPath))
                .ToList()
            : [];
        var wanted = authorizing
            .Concat(InstancePackageSources.InstallEntries(UnderWinningNames(instanceSources, configured))
                .Select(PluginGrantEntry.TryParse)
                .Where(e => e is not null)
                .Select(e => e!))
            .ToList();
        var malformed = options.InstallByDefault
            .Where(p => !string.IsNullOrWhiteSpace(p) && PluginGrantEntry.TryParse(p) is null)
            .ToList();
        if (malformed.Count > 0)
            logger.LogWarning(
                "[DefaultInstall] {Count} PluginCatalog:InstallByDefault entr(y/ies) are not a Source/Package or "
                + "Source/* pattern and match nothing: [{Malformed}]. The list is therefore not an explicit "
                + "package set, and the preInstalled baseline is kept.",
                malformed.Count, string.Join(", ", malformed));
        var baseline = BaselineApplies(options.EffectiveInstallPreInstalledPackages, options.InstallByDefault);
        var defaultApps = DefaultAppsApply(options.EffectiveInstallPreInstalledPackages);
        if (options.EffectiveInstallPreInstalledPackages is null && !baseline)
            logger.LogInformation(
                "[DefaultInstall] PluginCatalog:InstallByDefault declares an EXPLICIT package set [{Wanted}] — "
                + "exactly those packages and what they declare in `requires` are selected; the "
                + "registry's preInstalled baseline is not (the packages declaring `defaultApp` still are: "
                + "Threads, Examples — the narrow default-apps lane). Set "
                + "PluginCatalog:InstallPreInstalledPackages=true to keep the whole baseline beside the "
                + "set, or =false to suppress the default apps as well.",
                string.Join(", ", wanted));
        // The environment's own composition, read ONCE per boot pass off the live flag surface. The
        // reader is reactive (configuration reloads push a new value), but a boot pass is a single
        // decision taken at a point in time — re-deciding mid-install would mean two passes writing
        // the same partitions.
        var composition = hub.ServiceProvider.GetService<IFeatureFlags>() is { } flags
            ? flags.Composition.Take(1).Timeout(TimeSpan.FromSeconds(5))
                .Catch((Exception ex) =>
                {
                    // Loud, not silent: a composition that could not be read means this
                    // environment's declared packages are NOT asserted this boot. The pass still
                    // proceeds — the platform baseline is what keeps the portal usable, and
                    // withholding it would turn one unreadable setting into a dead deployment — but
                    // "the flags installed nothing" must never look like "the flags declared
                    // nothing".
                    logger.LogError(ex,
                        "[DefaultInstall] the environment's feature flags could not be read; NOTHING "
                        + "this environment declares is asserted this boot. The platform baseline "
                        + "and the seed still apply.");
                    return Observable.Return(FeatureComposition.Empty);
                })
            : Observable.Return(FeatureComposition.Empty);

        return composition.SelectMany(composed => InstallSelection(options, wanted, baseline, defaultApps, composed, authorizing));
    }

    /// <summary>
    /// Whether the packages declaring <c>preInstalled</c> are selected — the platform baseline lane.
    /// Pure.
    ///
    /// <para>🚨 <b>An instance that DECLARES its set gets that set</b> (policy
    /// <c>declared-package-set-is-exact</c>). <see cref="PluginCatalogOptions.InstallByDefault"/> naming packages EXACTLY
    /// (<c>Source/Package</c>, no whole-source <c>Source/*</c>) is a deployment record saying
    /// "these" — and the baseline lane used to add every <c>preInstalled</c> package of every source
    /// on top of it, unasked. Measured on a fresh control instance that named five packages: the
    /// pass selected 48 (47 <c>preInstalled</c> packages plus <c>Store</c> by closure), and on a
    /// closed type set every one of their in-mesh NodeTypes parks. The declared set's own
    /// <c>requires</c> are still installed (the dependency closure runs over the full catalog), so
    /// "exactly what was named, and what that needs" is what lands.</para>
    ///
    /// <para>An EXPLICIT <see cref="PluginCatalogOptions.InstallPreInstalledPackages"/> wins either
    /// way: <c>true</c> keeps the baseline beside a declared set, <c>false</c> suppresses it
    /// everywhere. Unset, the baseline applies wherever nothing is named exactly — no patterns, or
    /// any whole-source pattern (the chart default <c>Plugins/*</c>) — exactly as before.</para>
    /// </summary>
    /// <param name="configured">The configured switch; null when not set.</param>
    /// <param name="patterns">The CONFIGURED <see cref="PluginCatalogOptions.InstallByDefault"/>
    /// entries, unparsed — a malformed one must be seen, not silently dropped.</param>
    /// <returns>Whether the <c>preInstalled</c> baseline is selected this pass.</returns>
    internal static bool BaselineApplies(bool? configured, IReadOnlyList<string> patterns) =>
        configured ?? !DeclaresExplicitSet(patterns);

    /// <summary>
    /// Whether the packages declaring <c>defaultApp</c> (<see cref="PackageManifest.DefaultApp"/>) are
    /// selected — the narrow DEFAULT-APPS lane. Pure.
    ///
    /// <para>🚨 <b>The one named exception to <c>declared-package-set-is-exact</c>.</b> A package that
    /// says <c>defaultApp: true</c> on its root (Threads, Examples, …) is an app EVERY instance carries:
    /// the launcher's own entries (Inbox, Settings) are platform built-ins, and these are the two
    /// installable apps a person expects to find beside them. An instance that names an explicit
    /// package set must not lose them — that is the very case this lane exists for: the baseline
    /// lane (<see cref="BaselineApplies"/>) rightly drops the 47 other <c>preInstalled</c> packages
    /// there, and with them the apps. The lane is deliberately SMALL (a flag only an app author sets,
    /// free by contract) so the exception cannot grow into the baseline it was carved out of.</para>
    ///
    /// <para>Suppressed by exactly one thing: an explicit
    /// <c>PluginCatalog:InstallPreInstalledPackages=false</c> — the existing "this instance manages
    /// its own content" switch (an air-gapped or hand-curated instance). No second switch: an
    /// operator who turned the platform's content off would otherwise have to know to turn it off
    /// twice. Unset, or <c>true</c>, selects them.</para>
    /// </summary>
    /// <param name="configured">The configured <see cref="PluginCatalogOptions.EffectiveInstallPreInstalledPackages"/>; null when not set.</param>
    /// <returns>Whether the <c>defaultApp</c> packages are selected this pass.</returns>
    internal static bool DefaultAppsApply(bool? configured) => configured != false;

    /// <summary>Whether the operator's patterns name an explicit package set: at least one entry,
    /// and EVERY configured entry is a well-formed <c>Source/Package</c> (never a whole source, never
    /// an entry that does not parse — a malformed entry keeps the baseline rather than letting the
    /// valid rest stand for a set the operator did not state). Blank entries are not entries. Pure.</summary>
    /// <param name="patterns">The configured <see cref="PluginCatalogOptions.InstallByDefault"/> entries.</param>
    /// <returns>True for an explicit set.</returns>
    internal static bool DeclaresExplicitSet(IReadOnlyList<string> patterns)
    {
        var entries = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return entries.Count > 0
               && entries.All(p => PluginGrantEntry.TryParse(p) is { } entry
                                   && entry.PackageId != PluginGrantEntry.AllPackages);
    }

    /// <summary>The decision, once the three selection signals are known: list the sources, select,
    /// close over dependencies, drop what the seed already delivered, install in order.</summary>
    private IObservable<DefaultInstallSummary> InstallSelection(
        PluginCatalogOptions options,
        IReadOnlyList<PluginGrantEntry> wanted,
        bool baseline,
        bool defaultApps,
        FeatureComposition composition,
        IReadOnlyList<PluginGrantEntry>? authorizing = null)
    {
        var included = Parse(composition.Included);
        var excluded = Parse(composition.Excluded);
        if (!baseline && !defaultApps && wanted.Count == 0 && included.Count == 0)
            return Observable.Return(DefaultInstallSummary.Empty);

        return Sources(options).SelectMany(sources => sources.Count == 0
            // 🚨 Asked to install, with nowhere to install FROM. Silence here would recreate the
            // original "healthy boot, zero plugins" failure for the no-sources misconfiguration —
            // the same shape, one layer up. Say it, and say what to set.
            ? Observable.Defer(() =>
            {
                if (wanted.Count + included.Count > 0)
                    logger.LogError(
                        "[DefaultInstall] {Count} composition pattern(s) are configured "
                        + "(InstallByDefault [{Wanted}]; feature flags [{Included}]) but this "
                        + "installation has NO package sources — nothing can be installed. "
                        + "Configure PluginCatalog:Sources (a registry serving its own repos) or "
                        + "PluginCatalog:RegistryUrl (a consumer).",
                        wanted.Count + included.Count,
                        string.Join(", ", wanted),
                        string.Join(", ", included.Select(c => $"{c.Flag}:{c.Entry}")));
                return Observable.Return(DefaultInstallSummary.Empty);
            })
            : SeedLedger().SelectMany(ledger =>
            {
                var seeded = ledger.Seeded.ToImmutableHashSet(StringComparer.Ordinal);
                // What the governed-provision hold treats as already known: seeded, FAILED (the
                // retry #2254 exists for) and SKIPPED (a standing decision re-derived each pass).
                var known = KnownToTheLedger(ledger);
                // 🚨 PER-PACKAGE, not once-per-installation. The old gate was "install the seed only
                // while the instance has ZERO packages", which made a misconfigured first boot
                // unrecoverable: the boot installed the pre-installed baseline, the instance was no
                // longer "fresh", and correcting the config could never take effect again — with no
                // UI path back, because the catalog UI ships inside a plugin that failed to install.
                //
                // The ledger records what the SEED has already delivered, so the two cases the old
                // flag conflated are now distinguished:
                //   • never seeded  → install it (repairs a bad config, a failed install, a package
                //                     newly added to the repo)
                //   • seeded before → leave it alone forever, even if it is gone now, because the
                //                     only way it can be gone is that someone REMOVED it, and the
                //                     seed must not fight an operator.
                // Content the operator edited inside an installed package is protected separately
                // and already: the installer honours per-node SyncBehavior claims on upsert+prune.
                if (wanted.Count > 0 && seeded.Count > 0)
                    logger.LogDebug(
                        "Default-install ledger holds {Count} package(s) already seeded; they are "
                        + "not re-installed even if absent (an operator removed them).", seeded.Count);

                return Candidates(sources, baseline, defaultApps, wanted, included, excluded, authorizing)
                    // Drop anything the seed has already delivered once. Done AFTER listing because
                    // the decision is per PACKAGE, and only the listing knows which packages a
                    // pattern covers.
                    //
                    // 🚨 A RECONCILED candidate is exempt: the ledger records what the SEED
                    // delivered, and the seed's whole point is that it does not re-assert. A
                    // package this environment's flags declare must re-assert on every boot — that
                    // is the difference between the two lanes — so it is never dropped here, the
                    // same exemption the platform's own preInstalled baseline already has.
                    .SelectMany(selection => InstallAll(selection.Candidates
                            .Where(c => c.Reconciled || !seeded.Contains(c.Package.Id))
                            .Where(c => !HeldForGovernedProvision(c, known, freshInstance: seeded.Count == 0))
                            .ToList())
                        .Select(summary => summary with
                        {
                            ListingIncomplete = selection.ListingIncomplete,
                            SourceUnreachable = selection.SourceUnreachable,
                        }))
                    .SelectMany(summary => RecordSeeded(ledger, summary).Select(_ => summary));
            }));
    }

    /// <summary>
    /// 🚨 A package NEWLY LISTED in a source this instance already seeded from is NOT installed just
    /// because a whole-source pattern (<c>Plugins/*</c>) covers it. It is AVAILABLE; it lands on this
    /// instance through a governed <c>package.provision</c> activity, or by naming it in
    /// <see cref="PluginCatalogOptions.InstallByDefault"/> (a reviewed deployment change).
    ///
    /// <para><b>Why.</b> Measured 2026-09-29: a package merged into MeshWeaver.Plugins <c>main</c>
    /// (<c>Parties</c>) was on memex.systemorph.com 20 minutes later and on memex.meshweaver.cloud
    /// 33 minutes later, because both seed <c>Plugins/*</c> and "never seeded" read as "install
    /// it". A merge was a release to every instance with nobody deciding (maintainer, 2026-09-30:
    /// "an instance installs a new package only through its own signed provision").</para>
    ///
    /// <para><b>What still installs</b>: everything on a FRESH instance (an empty ledger — the seed
    /// exists to populate a new deployment), a package an entry names exactly, the platform
    /// baseline and this environment's flags (reconciled lanes), and a package pulled in as a
    /// selected package's requirement. What is already installed is untouched.</para>
    /// </summary>
    private bool HeldForGovernedProvision(InstallCandidate candidate, IReadOnlySet<string> known, bool freshInstance)
    {
        if (!HoldsForGovernedProvision(candidate.WildcardOnly, candidate.Reconciled,
                known.Contains(candidate.Package.Id), freshInstance))
            return false;
        logger.LogWarning(
            "[DefaultInstall] HELD {Id} from {Source}: newly listed and covered only by a whole-source "
            + "pattern. It is available but NOT installed — provision it through a governed "
            + "package.provision activity, or name it in PluginCatalog:InstallByDefault.",
            candidate.Package.Id, candidate.Package.Source ?? candidate.Source.Name);
        return true;
    }

    /// <summary>
    /// Every package id this instance's default-install ledger has RECORDED: delivered (seeded),
    /// FAILED (kept off the seeded list so the next pass retries it — #2254), or SKIPPED with a
    /// standing reason that is re-derived each pass. The governed-provision hold is for a package
    /// the instance has never seen; one on this set is not "newly listed", and holding it would
    /// swallow the ledger's retry. A persisted list that reads back <c>null</c> (an explicit
    /// JSON <c>null</c>) is empty, for every list alike — this runs inside the boot install. Pure.
    /// </summary>
    internal static ImmutableHashSet<string> KnownToTheLedger(DefaultInstallLedger ledger) =>
        (ledger.Seeded ?? ImmutableList<string>.Empty)
            .Concat(ledger.Failed ?? ImmutableList<string>.Empty)
            .Concat((ledger.Skipped ?? ImmutableList<DefaultInstallSkip>.Empty).Select(s => s.Package))
            .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>The hold's decision (<see cref="HeldForGovernedProvision"/>), over its four facts. Pure.</summary>
    internal static bool HoldsForGovernedProvision(
        bool wildcardOnly, bool reconciled, bool recordedInLedger, bool freshInstance) =>
        wildcardOnly && !reconciled && !recordedInLedger && !freshInstance;

    /// <summary>
    /// The ledger's authorization trail after this pass: <paramref name="previous"/> with each of
    /// <paramref name="fresh"/> added — replacing a previous entry for the same package only when
    /// the principal or the reason differs, so an unchanged re-assert keeps the original entry
    /// (and its time). Ordered by package. Pure.
    /// </summary>
    /// <param name="previous">The ledger's entries (null = none).</param>
    /// <param name="fresh">This pass's entries (null = none).</param>
    /// <returns>The merged trail.</returns>
    internal static ImmutableList<DefaultInstallAuthorization> MergeAuthorizations(
        ImmutableList<DefaultInstallAuthorization>? previous,
        ImmutableList<DefaultInstallAuthorization>? fresh)
    {
        var byPackage = (previous ?? ImmutableList<DefaultInstallAuthorization>.Empty)
            .GroupBy(a => a.Package, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var entry in fresh ?? ImmutableList<DefaultInstallAuthorization>.Empty)
            if (!byPackage.TryGetValue(entry.Package, out var existing)
                || !string.Equals(existing.InstalledBy, entry.InstalledBy, StringComparison.Ordinal)
                || !string.Equals(existing.Reason, entry.Reason, StringComparison.Ordinal))
                byPackage = byPackage.SetItem(entry.Package, entry);
        return byPackage.Values.OrderBy(a => a.Package, StringComparer.Ordinal).ToImmutableList();
    }

    /// <summary>Node holding the default-install ledger — what the SEED has delivered, ever.</summary>
    private const string SeedLedgerPath = PackageInstaller.InstalledPartition + "/_DefaultInstallLedger";

    /// <summary>The ledger's own node type — deliberately distinct from <c>Package</c> so it never
    /// appears in installed-package enumerations.</summary>
    public const string LedgerNodeType = "DefaultInstallLedger";

    /// <summary>
    /// The default-install ledger as stored — what the seed has delivered, and what the LAST pass
    /// could not deliver. Empty on a fresh instance and whenever the ledger cannot be read —
    /// failing OPEN here is deliberate: the cost of a missing ledger is re-installing a package
    /// (idempotent, content-identity gated), whereas failing closed would silently skip the repair
    /// this whole mechanism exists to perform.
    /// </summary>
    private IObservable<DefaultInstallLedger> SeedLedger()
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(SeedLedgerPath, TimeSpan.FromSeconds(10)))
            .Take(1)
            .Select(node => node?.ContentAs<DefaultInstallLedger>(hub.JsonSerializerOptions)
                            ?? new DefaultInstallLedger())
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "Could not read the default-install ledger at {Path} — treating as empty.",
                    SeedLedgerPath);
                return Observable.Return(new DefaultInstallLedger());
            });
    }

    /// <summary>
    /// Records what this pass DELIVERED (installed or already current) on the ledger, so the seed
    /// never re-asserts it, and records what it FAILED so a dropped package is detectable without
    /// grepping a boot log. Written as System; the Plugins partition is System-owned.
    ///
    /// <para>🚨 <b>A FAILED package must never reach the seeded list.</b> The ledger is what makes
    /// the seed lane skip a package forever ("the only way it can be gone is that someone removed
    /// it"), so ledgering a failure converts one transient install error into a package this
    /// installation will never install again. That is exactly what happened on memex-cloud
    /// (#2254): the per-instance NodeOps hub did not answer inside the 60 s request budget, the
    /// package was stepped over — and then written to the ledger anyway, because the summary's
    /// <c>Packages</c> list carried every package the pass TOUCHED, failures included. The class
    /// doc already promised the opposite ("a package that FAILED stays off the ledger and is
    /// retried next boot — that retry is the repair"); the code did not honour it, so the retry
    /// that IS the repair never ran.</para>
    ///
    /// <para>🚨 <b>The failure list is REPLACED by this pass, never appended to</b> — it is a
    /// snapshot of what is missing NOW, which is what its name and its docstring promise. Carrying
    /// an untouched id forward would have made it lie in the one case that matters: a package the
    /// operator STOPS declaring by default is never selected again, so a retained entry would
    /// advertise it as missing for the life of the installation. That is the same
    /// stale-state-reported-as-current defect this whole change is about, one level up.</para>
    ///
    /// <para>Replacement is safe because a failed package is never seeded, so every later pass
    /// re-selects it while it is still declared: still-declared ⇒ re-attempted and re-recorded;
    /// no longer declared ⇒ correctly dropped. The one thing replacement must NOT do is read a
    /// pass that attempted nothing as "nothing is missing any more" — see the guard below.</para>
    /// </summary>
    private IObservable<Unit> RecordSeeded(
        DefaultInstallLedger ledger, DefaultInstallSummary summary)
    {
        // 🚨 A pass that attempted NOTHING and classified nothing knows nothing. A source listing
        // that failed yields an empty summary, and treating that as "no package is missing" would
        // erase the record of a genuinely missing one — the ledger would then be at its most
        // optimistic exactly when the instance is least healthy. A skip-only pass, by contrast,
        // DID read a listing (skips are derived from listed manifests), so it may write.
        // A HELD-only pass counts with the skips: a hold is derived from a listed candidate and a
        // partition this pass actually asked about, so it KNOWS something — and a hold that lifted
        // has to be able to disappear from the ledger.
        if (summary.Packages.Count == 0 && summary.Skipped.Count == 0 && summary.Held.Count == 0)
            return Observable.Return(Unit.Default);

        var already = ledger.Seeded.ToImmutableHashSet(StringComparer.Ordinal);
        // 🚨 Delivered, NOT covered. summary.Packages includes the failures.
        var delivered = summary.Delivered.Where(id => !already.Contains(id)).ToList();

        var failures = summary.Failures
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableList();
        // The cause of each, same snapshot (#5826): exactly one per failed package — the first one
        // named, or an entry that SAYS none was recorded. See DefaultInstallSummary.CausePerFailure.
        var failureCauses = summary.CausePerFailure();

        // Snapshot semantics, exactly like Failed (#2536): what THIS pass classified as outside
        // the unattended lane's authority, deduplicated by package (records compare by value).
        var skipped = summary.Skipped
            .GroupBy(s => s.Package, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.Package, StringComparer.Ordinal)
            .ToImmutableList();

        // Same snapshot semantics, one list over (MeshWeaver#4588): what THIS pass held. It is
        // deliberately NOT merged with the ledger's previous holds — a hold that has lifted must
        // vanish, and the next pass re-derives every one of them from scratch.
        var held = summary.Held
            .GroupBy(h => h.Package, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(h => h.Package, StringComparer.Ordinal)
            .ToImmutableList();

        // 🚨 #4097 — a pass in which SOME source did not answer knows only what it saw. The
        // per-source catch in Candidates substitutes an empty list for a failed listing, so
        // another source can still populate this summary while the failed source's refusals are
        // simply missing from it — not lifted. Treating that as authoritative would clear the
        // markers and send /health and the self-updater back to the generic "not installed"
        // sentence for the length of a registry outage. So an incomplete pass keeps the ledger's
        // previous refusals and leaves the markers standing; only a pass every source answered
        // may move them — in either direction.
        var tierRefused = summary.ListingIncomplete
            ? ledger.TierRefused
            : summary.TierRefused
                .DistinctBy(r => r.PackageId, StringComparer.Ordinal)
                .OrderBy(r => r.PackageId, StringComparer.Ordinal)
                .ToImmutableList();

        // The activation record's plan-tier markers are made to MATCH this pass's answer, before
        // the ledger and regardless of whether the ledger changes: /health's required_modules
        // line, the activation report and the self-updater read the markers, and a marker left
        // standing after a plan upgrade would name a refusal that no longer exists. Guarded by
        // the "knows nothing" rule above and the completeness rule here — a failed listing must
        // not clear a standing refusal. A volume that cannot be written is reported, never
        // fatal: the ledger below still records the verdict.
        if (!summary.ListingIncomplete)
        {
            try
            {
                ModuleActivationSidecar.SyncTierRefusals(
                    ModuleRoot.Resolve(hub.ServiceProvider.GetService<IConfiguration>()), tierRefused);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "[DefaultInstall] could not write the plan-tier refusal markers under the module root; "
                    + "/health and the self-updater will not name the plan until a pass can.");
            }
        }

        // CUMULATIVE, unlike the snapshots above: the audit trail of which commercial package the
        // deployment configuration authorized, and why. An entry is replaced only when the same
        // package is authorized again by a different principal or for a different reason, so a
        // reconciled lane re-asserting it every boot does not rewrite the ledger every boot.
        var authorized = MergeAuthorizations(ledger.Authorized, summary.Authorized);

        if (delivered.Count == 0
            && authorized.SequenceEqual(ledger.Authorized ?? ImmutableList<DefaultInstallAuthorization>.Empty)
            && failures.SequenceEqual(ledger.Failed ?? ImmutableList<string>.Empty, StringComparer.Ordinal)
            && failureCauses.SequenceEqual(ledger.FailureCauses ?? ImmutableList<DefaultInstallFailure>.Empty)
            && skipped.SequenceEqual(ledger.Skipped)
            && held.SequenceEqual(ledger.Held)
            && tierRefused.SequenceEqual(ledger.TierRefused))
            return Observable.Return(Unit.Default);

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        // Satellite(): MainNode = the Plugins partition root, not the ledger's own path (#2383).
        // Bookkeeping that points at itself IS a main node by the catalog's definition: `is:main`
        // KEEPS exactly the rows where MainNode == Path (SQL `n.main_node = n.path`), which is also
        // what search_across_schemas hard-filters every union branch on. So the self-default listed
        // the ledger as partition CONTENT and put it in mesh-wide search.
        var node = MeshNode.Satellite("_DefaultInstallLedger", PackageInstaller.InstalledPartition) with
        {
            Name = "Default install ledger",
            // 🚨 NOT PackageNodeType. The ledger lives in the Plugins partition but is bookkeeping,
            // not an install record — typing it "Package" puts it in every query that enumerates
            // installed packages by nodeType (ModuleDiscoveryService.ReadInstanceState, the
            // freshness probe, any inventory UI). The tell was undeniable: every verification query
            // written against this feature had to say `id <> '_DefaultInstallLedger'`. A filter you
            // must repeat at each call site is the schema telling you the type is wrong.
            NodeType = LedgerNodeType,
            State = MeshNodeState.Active,
            Content = new DefaultInstallLedger
            {
                Seeded = already.Union(delivered).OrderBy(x => x, StringComparer.Ordinal).ToImmutableList(),
                Failed = failures,
                FailureCauses = failureCauses,
                Skipped = skipped,
                Held = held,
                TierRefused = tierRefused,
                Authorized = authorized,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        return Observable.Defer(() =>
        {
            var write = accessService.ImpersonateAsSystem();
            return meshService.CreateOrUpdateNode(node)
                .Select(_ => Unit.Default)
                .Finally(() => write.Dispose());
        }).Catch((Exception ex) =>
        {
            // A ledger write failure must not fail the boot — the packages ARE installed. The only
            // consequence is that the next boot reconsiders them, which is idempotent.
            logger.LogWarning(ex, "Could not update the default-install ledger at {Path}", SeedLedgerPath);
            return Observable.Return(Unit.Default);
        });
    }

    /// <summary>
    /// The sources to read the default install out of, in precedence order. Cold; emits once.
    /// Registry token resolution is reactive (it reads the stored auto-registration credential), so
    /// this is an observable rather than a plain list.
    ///
    /// <para>Three source kinds: sources registered in DI (the extension point, and the seam a test
    /// hands a repo in on), this instance's OWN configured git sources (<c>PluginCatalog:Sources</c>
    /// — a REGISTRY instance serves these over <c>/api/plugins</c> and is not a consumer of its own
    /// HTTP surface, so it must read the same config directly, through the shared reader, so
    /// serving and installing agree), and the registries this instance consumes.</para>
    /// </summary>
    private IObservable<IReadOnlyList<ConfiguredPackageSource>> Sources(PluginCatalogOptions options)
    {
        var services = hub.ServiceProvider;

        var registered = services.GetServices<IPackageSource>()
            .Select((s, i) => new ConfiguredPackageSource(s, "HEAD", $"registered-{i}"))
            .ToList();

        var fromConfig = services.GetService<IConfiguration>() is { } config
            ? PackageSources.FromConfiguration(hub, config, logger)
            : [];
        IReadOnlyList<ConfiguredPackageSource> configured = fromConfig;
        // The sources the instance's administrators added ON the instance
        // (Doc/Architecture/InstanceRepositoryAccess) come LAST — after the registries too — so they
        // can never shadow a package the deployment's own sources list (the first listing of an id
        // wins); a repository the record names stays the record's.
        var instanceLocal = PackageSources.FromInstance(hub, fromConfig, logger);

        var tokenResolver = services.GetService<RegistryTokenResolver>();
        var registries = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries);
        var remote = registries.Count == 0 || tokenResolver is null
            ? Observable.Return<IReadOnlyList<ConfiguredPackageSource>>([])
            : registries
                .Select(registry => tokenResolver.ResolveToken(registry)
                    .Take(1)
                    .Do(token =>
                    {
                        if (token.Length == 0)
                            logger.LogWarning(
                                "Installing defaults from {Url} with NO instance key — only an open "
                                + "dev/e2e registry will answer.", registry.Url);
                    })
                    .Select(token =>
                    {
                        // Same URL and same key as the catalog read: a registry entitled to serve
                        // this instance its package files is exactly the one entitled to serve the
                        // assemblies compiled from them. ONE client, carried on BOTH handles: the
                        // ConfiguredPackageSource's (the NodeType adopt after a default install)
                        // and the RegistryPackageSource's own (the module landing inside
                        // InstallOrUpdate, #1664) — so every lane reads the same promise-cached
                        // bundle index.
                        var bundles = PluginBundleClient.WithCredential(hub, registry.Url, tokenResolver.Credential(registry));
                        return new ConfiguredPackageSource(
                            RegistryPackageSource.WithCredential(hub, registry.Url, tokenResolver.Credential(registry), bundles),
                            string.IsNullOrWhiteSpace(registry.Ref) ? "HEAD" : registry.Ref,
                            string.IsNullOrWhiteSpace(registry.Name) ? registry.Url : registry.Name)
                        {
                            Bundles = bundles,
                        };
                    }))
                .ToObservable()
                .Concat()
                .ToList()
                .Select(list => (IReadOnlyList<ConfiguredPackageSource>)list);

        return remote.Select(remoteSources => (IReadOnlyList<ConfiguredPackageSource>)registered
            .Concat(configured)
            .Concat(remoteSources)
            .Concat(instanceLocal)
            .ToList());
    }

    /// <summary>
    /// Every package the default install should carry, from every source, deduplicated by id
    /// (first source wins — the same precedence the registry's merged catalog uses) and ordered by
    /// DEPENDENCY. A source that cannot be listed is logged and skipped: one unreachable repo must
    /// not withhold the packages the others carry.
    ///
    /// <para>🚨 The operator's patterns are matched SOURCE-SCOPED (through
    /// <see cref="PluginGrantEntry"/> against <see cref="PackageManifest.Source"/>), so a registry
    /// too old to stamp the source matches nothing and installs nothing rather than guessing. The
    /// package's own <c>preInstalled</c> declaration needs no such scoping — it is the package
    /// author declaring platform baseline, not an entitlement being swept in.</para>
    /// </summary>
    /// <summary>
    /// The <c>InstallByDefault</c> patterns that can NEVER match, because they name a source this
    /// installation does not have. Pure — no I/O, so it is checked before any listing and is
    /// directly unit-testable.
    ///
    /// <para>🚨 This exists because the failure is otherwise SILENT. Matching is source-scoped and
    /// fails closed by design (an instance entitled to paid content must not auto-install it), so a
    /// pattern naming a non-existent source installs nothing and reports nothing. Observed for
    /// real: a local registry served its checkout under the source name <c>MWP-main</c> (taken from
    /// the directory) while <c>InstallByDefault</c> stayed <c>Plugins/*</c> — the deployment came up
    /// healthy, every probe green, and not one plugin installed. The post-listing warning did not
    /// fire either, because the pre-installed baseline had matched 8 packages, so "matched
    /// something" was true overall while the operator's patterns matched nothing.</para>
    /// </summary>
    /// <param name="wanted">The operator's source-scoped patterns.</param>
    /// <param name="sourceNames">The names of the sources actually configured.</param>
    internal static IReadOnlyList<PluginGrantEntry> UnmatchablePatterns(
        IReadOnlyList<PluginGrantEntry> wanted, IReadOnlyCollection<string> sourceNames) =>
        wanted
            .Where(w => !sourceNames.Any(
                n => string.Equals(n, w.Source, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>
    /// One package pattern a feature flag contributes, and the flag that declared it — so an error
    /// message can name the flag an operator has to fix, not just the pattern.
    /// </summary>
    /// <param name="Flag">The declaring flag's name.</param>
    /// <param name="Entry">The parsed <c>Source/Package</c> pattern.</param>
    internal readonly record struct FlagPattern(string Flag, PluginGrantEntry Entry);

    /// <summary>Parses a flag's raw <c>Source/Package</c> strings, dropping the malformed ones (a
    /// bad entry must not take the whole boot down — <see cref="PluginGrantEntry.TryParse"/>).</summary>
    /// <param name="packages">The declared package patterns with their flags.</param>
    /// <returns>The parseable patterns.</returns>
    internal static IReadOnlyList<FlagPattern> Parse(IEnumerable<FeaturePackage> packages) =>
        packages
            .Select(p => (p.Flag, Entry: PluginGrantEntry.TryParse(p.Package)))
            .Where(p => p.Entry is not null)
            .Select(p => new FlagPattern(p.Flag, p.Entry!))
            .ToList();

    /// <summary>
    /// Whether the environment's flags EXCLUDE this package — the declared-but-disabled side of the
    /// composition. Pure, and deliberately checked against every other signal (the platform
    /// baseline included): "this environment does not have that" is an explicit statement and the
    /// only reading under which "all of Plugins, WITHOUT the games" is expressible at all.
    /// </summary>
    /// <param name="excluded">The disabled flags' patterns.</param>
    /// <param name="package">The candidate package.</param>
    /// <returns>The excluding flag's name, or null when nothing excludes it.</returns>
    internal static string? ExcludedBy(
        IReadOnlyList<FlagPattern> excluded, PackageManifest package) =>
        excluded.FirstOrDefault(e => e.Entry.Matches(package.Source ?? "", package.Id)) is
            { Flag.Length: > 0 } hit
            ? hit.Flag
            : null;

    /// <summary>One pass's selection, and whether every source ANSWERED (#4097): a source whose
    /// listing failed contributes nothing, and a pass with such a source must not read its own
    /// silence as "that source refuses nothing".</summary>
    /// <param name="Candidates">What the pass selected.</param>
    /// <param name="ListingIncomplete">A source contributed nothing — held, or it did not answer.</param>
    /// <param name="SourceUnreachable">A source did not ANSWER — the half of
    /// <paramref name="ListingIncomplete"/> a later pass of this process can change. A held source
    /// is not counted (<see cref="LeftUndone"/>).</param>
    private sealed record CandidateSelection(
        IReadOnlyList<InstallCandidate> Candidates, bool ListingIncomplete, bool SourceUnreachable);

    /// <summary>One source's listing: what it listed, whether it contributed nothing
    /// (<paramref name="Failed"/>), and — when it contributed nothing — whether that is a HOLD
    /// (<paramref name="Held"/>: released at the next process start only) rather than a source that
    /// did not answer (retryable).</summary>
    /// <param name="Candidates">The packages listed; empty for a failed listing.</param>
    /// <param name="Failed">True when the source contributed nothing this pass.</param>
    /// <param name="Held">True when <paramref name="Failed"/> is a hold, not an unanswered listing.</param>
    private readonly record struct SourceListing(List<InstallCandidate> Candidates, bool Failed, bool Held)
    {
        /// <summary>A source that listed.</summary>
        public static SourceListing Of(List<InstallCandidate> candidates) => new(candidates, false, false);

        /// <summary>A source that did not answer its listing — retryable.</summary>
        public static SourceListing Unreachable { get; } = new([], true, false);

        /// <summary>A source held until the next process start.</summary>
        public static SourceListing OnHold { get; } = new([], true, true);
    }

    private IObservable<CandidateSelection> Candidates(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        bool defaultApps,
        IReadOnlyList<PluginGrantEntry> wanted,
        IReadOnlyList<FlagPattern> included,
        IReadOnlyList<FlagPattern> excluded,
        IReadOnlyList<PluginGrantEntry>? authorizing = null) =>
        Observable.Defer(() =>
        {
            var sourceNames = sources.Select(s => s.Name).ToList();
            // Config error, reported BEFORE any listing: a pattern naming a source that does not
            // exist here can never install anything, and silence is the worst possible answer.
            if (UnmatchablePatterns(wanted, sourceNames) is { Count: > 0 } bad)
                logger.LogError(
                    "[DefaultInstall] {Count} InstallByDefault pattern(s) name a source this "
                    + "installation does not have: [{Bad}]. Configured sources: [{Sources}]. Those "
                    + "patterns will install NOTHING — fix the names so they agree (a source's name "
                    + "is what grants and install-defaults are written against).",
                    bad.Count, string.Join(", ", bad), string.Join(", ", sourceNames));
            // The same check for the flag lane, and for BOTH directions. A misspelled source in an
            // EXCLUSION is the more dangerous of the two: it fails open — the packages the operator
            // meant to keep out are installed, and nothing says so.
            foreach (var (label, patterns) in new[] { ("includes", included), ("excludes", excluded) })
                if (patterns
                        .Where(p => !sourceNames.Any(
                            n => string.Equals(n, p.Entry.Source, StringComparison.OrdinalIgnoreCase)))
                        .ToList() is { Count: > 0 } unmatchable)
                    logger.LogError(
                        "[DefaultInstall] {Count} feature-flag {Direction} name a source this "
                        + "installation does not have: [{Bad}]. Configured sources: [{Sources}]. "
                        + "They will match NOTHING — an unmatchable exclusion in particular fails "
                        + "OPEN, so the packages it names WILL be installed.",
                        unmatchable.Count, label,
                        string.Join(", ", unmatchable.Select(p => $"{p.Flag}:{p.Entry}")),
                        string.Join(", ", sourceNames));
            return Observable.Return(Unit.Default);
        }).SelectMany(_ => sources
            .Select(ListAtProvenRef)
            .ToObservable()
            .Concat()
            .ToList()
            .Select(answers =>
            {
                var listingIncomplete = answers.Any(a => a.Failed);
                var sourceUnreachable = answers.Any(a => a.Failed && !a.Held);
                var perSource = answers.Select(a => a.Candidates).ToList();
                var catalog = perSource
                    .SelectMany(list => list)
                    .GroupBy(c => c.Package.Id, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(c => c.Package.Id, StringComparer.Ordinal)
                    .ToList();
                // The platform's own lanes: the preInstalled baseline, and the narrow default-apps lane
                // (defaultApp, which survives an explicit declared set — see DefaultAppsApply).
                bool IsPlatformDefault(InstallCandidate c) =>
                    (baseline && c.Package.PreInstalled) || (defaultApps && c.Package.DefaultApp);
                bool IsIncluded(InstallCandidate c) =>
                    included.Any(i => i.Entry.Matches(c.Package.Source ?? "", c.Package.Id));
                bool IsWanted(InstallCandidate c) =>
                    wanted.Any(w => w.Matches(c.Package.Source ?? "", c.Package.Id));
                // Named EXACTLY by an operator entry (Source/Package), not merely covered by Source/*.
                // The entries that carry the deployment configuration's AUTHORITY: the record's own
                // (PluginCatalog:InstallByDefault). Selection reads `wanted`, which may also hold the
                // instance's own choices; authority never does. Null keeps today's single list.
                var named = authorizing ?? wanted;
                bool IsSelectedExactly(InstallCandidate c) =>
                    wanted.Any(w => w.PackageId != PluginGrantEntry.AllPackages
                                    && w.Matches(c.Package.Source ?? "", c.Package.Id));
                bool IsNamed(InstallCandidate c) =>
                    named.Any(w => w.PackageId != PluginGrantEntry.AllPackages
                                    && w.Matches(c.Package.Source ?? "", c.Package.Id));
                var selected = catalog
                    .Where(c => IsPlatformDefault(c) || IsIncluded(c) || IsWanted(c))
                    .ToList();
                // Judge the FLAG lane on its own too, for the same reason the operator's patterns
                // are judged on their own below: with a baseline selected, "matched something" is
                // true overall while every flag pattern matched nothing.
                if (included.Count > 0 && !selected.Any(IsIncluded))
                    logger.LogWarning(
                        "[DefaultInstall] the environment's feature flags matched no packages "
                        + "([{Included}]). If the registry predates source-stamped catalog entries, "
                        + "a Source/* pattern cannot match — it fails closed rather than guessing.",
                        string.Join(", ", included.Select(i => $"{i.Flag}:{i.Entry}")));
                // 🚨 Judge the OPERATOR'S patterns on their own, not on whether the pass matched
                // anything overall. `selected.Count == 0` alone is masked by the pre-installed
                // baseline: with 8 baseline packages selected, "matched something" is true while
                // every InstallByDefault pattern matched nothing — which is exactly how a local
                // registry came up with no plugins and no warning.
                if (wanted.Count > 0 && !selected.Any(IsWanted))
                    logger.LogWarning(
                        "The default install matched no packages for the operator's patterns "
                        + "(wanted [{Wanted}]; {Baseline} pre-installed package(s) were still "
                        + "selected). If the registry predates source-stamped catalog entries, a "
                        + "Source/* pattern cannot match — it fails closed rather than guessing.",
                        string.Join(", ", wanted), selected.Count);

                // 🚨 THE DEPLOYMENT CONFIGURATION AUTHORIZES WHAT IT NAMES — policy
                // `deployment-config-authorizes-named-commercial`. A package named EXACTLY in
                // PluginCatalog:InstallByDefault (Source/Package — never a whole-source Source/*) is
                // named in the instance's deployment record, which changes only through a reviewed,
                // App-merged pull request; that record is the authorizing principal
                // (deployment-config:{instance}, see DeploymentConfigAuthority) for it AND for what it
                // requires, commercial requirements included. A wildcard names nothing and still never
                // pulls a commercial package in. Installing grants nobody access — the plan and the
                // subscriptions decide who reads it.
                var catalogPackages = catalog.Select(c => c.Package).ToImmutableList();
                var trustedPackages = catalog.Where(ProvenanceIsTrusted).Select(c => c.Package).ToImmutableList();
                // Only a candidate whose source is PROVEN may carry the configuration's authority
                // (ProvenanceIsTrusted): a manifest a direct repository lists can AUTHOR any
                // `source`, and must not borrow another configured source's Source/Package naming.
                var namedPackages = selected.Where(c => IsNamed(c) && ProvenanceIsTrusted(c)).Select(c => c.Package).ToImmutableList();
                var namedRequirements = ConfigAuthorizedRequirements(trustedPackages, namedPackages);
                // Admitted keyed by SOURCE and id (the catalog stamps every listed manifest's
                // source), so the gate never authorizes the same id listed by another source.
                var bySourceId = trustedPackages.ToImmutableDictionary(p => p.Id, p => p.Source ?? "", StringComparer.Ordinal);
                hub.ServiceProvider.GetService<DeploymentConfigAuthority>()?.Admit(namedRequirements
                    .Where(kv => bySourceId.TryGetValue(kv.Key, out var src) && src.Length > 0)
                    .ToImmutableDictionary(
                        kv => DeploymentConfigAuthority.RequirementKey(bySourceId[kv.Key], kv.Key),
                        kv => kv.Value,
                        StringComparer.Ordinal));
                var configAuthorized = namedPackages.Select(p => p.Id)
                    .Concat(namedRequirements.Keys)
                    .ToImmutableHashSet(StringComparer.Ordinal);
                string? ConfigAuthority(InstallCandidate c) =>
                    !ProvenanceIsTrusted(c) ? null
                    : IsNamed(c)
                        ? $"named in PluginCatalog:InstallByDefault as '{named.First(w => w.PackageId != PluginGrantEntry.AllPackages && w.Matches(c.Package.Source ?? "", c.Package.Id))}'"
                        : namedRequirements.TryGetValue(c.Package.Id, out var root)
                            ? $"required by '{root}', which PluginCatalog:InstallByDefault names"
                            : null;

                // 🚨 A selected package's REQUIREMENTS are selected too, even when nothing selects
                // them directly. Ordering the selection was never enough: every pre-installed
                // package declares `requires: ["Store@^1.0.0"]` while `Store` is itself
                // `preInstalled: false`, so an unattended boot installed 8 packages that all need
                // the Store and never installed the Store — no install record, therefore no
                // declared-access pass, therefore a partition only `system-security` could read,
                // therefore no catalog page to install it from by hand. Exactly the state a portal
                // was found in on 2026-08-10. The closure is taken over the FULL catalog, so a
                // dependency outside the selection is pulled in rather than silently ignored.
                var withDependencies = DependencyClosure(
                    catalogPackages,
                    selected.Select(c => c.Package).ToImmutableList(),
                    logger,
                    configAuthorized);

                // The TOLERANT sort: a cycle warns and still yields every package exactly once
                // (the requirement closing the loop is ignored; order within the cycle is
                // arbitrary — see InDependencyOrder's remarks) rather than refusing, because
                // nobody is present at boot to fix a malformed repo and one bad package must not
                // strand the whole instance. The Install CLICK uses the same graph with the strict
                // policy (PackageDependencyGraph.InstallClosure).
                var ordered = PackageDependencyGraph.InDependencyOrder(withDependencies, logger);
                var bySource = catalog.ToDictionary(c => c.Package.Id, StringComparer.Ordinal);
                return new CandidateSelection(ordered
                    .Select(p => bySource[p.Id])
                    // 🚨 The EXCLUSION is applied LAST — after the dependency closure, so it also
                    // removes a package the closure pulled back in as somebody's requirement. That
                    // is the honest reading of "this environment does not have that": the operator's
                    // explicit statement outranks an inferred edge. It is not silent — a package
                    // removed after being pulled in is named at Warning, because whatever required
                    // it will now fail at use and the operator has to un-exclude it or drop the
                    // dependent.
                    .Where(c =>
                    {
                        if (ExcludedBy(excluded, c.Package) is not { } flag)
                            return true;
                        if (selected.Any(s =>
                                string.Equals(s.Package.Id, c.Package.Id, StringComparison.Ordinal)))
                            logger.LogInformation(
                                "[DefaultInstall] {Id} is excluded here by the disabled feature flag "
                                + "'{Flag}'", c.Package.Id, flag);
                        else
                            logger.LogWarning(
                                "[DefaultInstall] {Id} is excluded here by the disabled feature flag "
                                + "'{Flag}', but another selected package REQUIRES it — whatever "
                                + "needs it will install and then fail at use. Enable that flag, or "
                                + "stop selecting the dependent.", c.Package.Id, flag);
                        return false;
                    })
                    // Whether this candidate belongs to a RECONCILED lane (the platform baseline,
                    // this environment's flags, or the operator's patterns over a LOCAL CHECKOUT)
                    // rather than the seed-once one. Read by the ledger filter: a reconciled
                    // package re-asserts on every boot, a seeded one never does.
                    .Select(c => c with
                    {
                        Reconciled = IsPlatformDefault(c)
                                     || IsIncluded(c)
                                     || (c.Source.LocalCheckout && IsWanted(c)),
                        // Selected ONLY because a whole-source pattern (`Source/*`) covers it — no
                        // entry names it, no flag declares it, it is not the platform baseline.
                        // Read by the governed-provision hold in InstallSelection.
                        // Exact-versus-wildcard reads EVERY exact selection (the record's and the
                        // instance's own); only the configuration AUTHORITY is the record's alone.
                        WildcardOnly = IsWanted(c)
                                       && !IsSelectedExactly(c)
                                       && !IsIncluded(c)
                                       && !IsPlatformDefault(c),
                        // Why the deployment configuration authorizes it — named exactly, or
                        // required by a package that is. Null when it does not (a wildcard, the
                        // baseline, a flag): such a commercial package is still skipped.
                        ConfigAuthority = ConfigAuthority(c),
                    })
                    .ToList(), listingIncomplete, sourceUnreachable);
            }));

    /// <summary>
    /// One source's listing, at the ref this UNATTENDED lane is allowed to read — the commit the
    /// seal names for the source's repository when this instance runs a publication of it, the
    /// configured ref otherwise — and a HOLD when the seal cannot yet say which
    /// (Systemorph/MeshWeaver#4259).
    ///
    /// <para>🚨 <b>The boot default install was the last unattended branch-tip import, and it was
    /// writing partitions a sync entry already held at the sealed commit.</b> The Sync-Ref Contract
    /// (<c>Doc/Architecture/SyncRefContract</c>) says an unattended import reads a commit a build
    /// PROVED; only a human clicking "Update to latest" reads a branch. Every GitSync path honours
    /// it, and <c>ModuleDiscoveryService.FirstImport</c> was pinned to the seal for exactly that
    /// reason (MeshWeaver#3845). This lane still listed <c>PluginCatalog:Sources:N:Ref</c> —
    /// <c>main</c> on every deployment — resolved at fetch time on every boot, and stamped
    /// <c>installedFromRef: main</c> on the record.</para>
    ///
    /// <para>Measured on memex.meshweaver.cloud, 2026-09-13. <c>Hosting/_GitSync</c> (the same
    /// repository, <c>subdirectory: Hosting</c>) is held by <see cref="SealedSyncGate"/> to
    /// <c>627fb3cd</c>, the Plugins commit sealed for the running framework identity — a 2026-09-12
    /// tree. The boot install put <c>Plugins/Hosting</c> 1.19.0 (a 2026-09-13 tree) into the same
    /// partition at 08:12Z. On the next boot the bake declined the Hosting bundles on their source
    /// fingerprint, <c>SealedSyncReconcile</c> read that as "the live sources have drifted from the
    /// commit they claim" — which they had — and re-imported the partition at <c>627fb3cd</c> at
    /// 21:56:05Z, pruning the eleven <c>Triage*</c> nodes that exist only in the newer tree. The
    /// boot install then re-applied <c>main</c> (1.19.16) as a diff against its own record, which
    /// declared those files unchanged, so they stayed absent and eight NodeTypes sat at
    /// <c>compilationStatus: Error</c>. Two unattended writers of one partition with two commit
    /// policies: each boot the seal-bound one removes what the tip-bound one added.</para>
    ///
    /// <para>So this lane now asks the SAME question the first import asks —
    /// <see cref="SealedSyncGate.DecideFirstImport"/> over the seal on this pod's disk — and lands
    /// on the same answer, so the two writers of a partition agree on its tree. Three outcomes, kept
    /// apart on purpose: a repository this instance runs no publication of keeps the configured ref
    /// (an Education or course repo on a registry, a local checkout, a registered
    /// <see cref="IPackageSource"/> with no repository — the residue the contract already names);
    /// a sealed commit is listed AND installed at, so the record's <c>installedFromRef</c> carries
    /// the sha; a seal that is torn, at an unknown commit, or self-contradictory HOLDS the source
    /// this boot — its packages are neither installed at the branch (the very shape being removed)
    /// nor reported as failed (a retry cannot change a seal; the seal landing can), and the pass
    /// says so at Warning and marks the listing incomplete so its silence is not read as "that
    /// source refuses nothing" (#4097).</para>
    ///
    /// <para>🚨 <b>A hold releases at the NEXT PROCESS START, everywhere.</b> This lane runs once
    /// per boot, after the bake settles, and subscribes to neither
    /// <c>PublicationSealArrivalService</c> nor <c>SealedPublicationSyncReconciler</c> —
    /// deliberately (no timer, no retry, no watchdog). The sync side of the same partition DOES
    /// follow a seal that arrives mid-process (MeshWeaver#4209); the two do not fight in between,
    /// because this lane writes nothing again until it runs again, and then both are on the new
    /// seal.</para>
    /// </summary>
    /// <param name="configured">The source as configured.</param>
    /// <returns>The candidates listed at the proven ref, or an empty, FAILED listing for a held
    /// source; the tuple shape every other listing outcome uses.</returns>
    private IObservable<SourceListing> ListAtProvenRef(
        ConfiguredPackageSource configured)
        => Observable.Defer(() => heldSources.Contains(configured.Name)
            ? StillHeld(configured)
            : ListAtFreshlyProvenRef(configured));

    /// <summary>A source already held in this process: answered from the hold, with no read of its
    /// seal and no listing. The first hold said why at Warning; this is the same fact, repeated by a
    /// later pass.</summary>
    private IObservable<SourceListing> StillHeld(ConfiguredPackageSource configured)
    {
        logger.LogInformation(
            "[DefaultInstall] {Name} was held earlier in this process and stays held until the next "
            + "process start — its seal is not read again and it is not listed "
            + "(Doc/Architecture/SyncRefContract).", configured.Name);
        return Observable.Return(SourceListing.OnHold);
    }

    private IObservable<SourceListing> ListAtFreshlyProvenRef(ConfiguredPackageSource configured)
        => ProvenRef(configured)
            .Catch((Exception exception) =>
            {
                // An unobserved seal is not a clean one: a read that faulted holds the source,
                // exactly as a torn seal does — never a fall-through to the branch.
                logger.LogWarning(exception,
                    "[DefaultInstall] the seal for {Name} could not be read — its packages are "
                    + "held this boot and re-attempted at the next process start; an unattended "
                    + "install never resolves the branch instead (Doc/Architecture/SyncRefContract).",
                    configured.Name);
                return Observable.Return(new ProvenSource(configured, exception.Message, exception.Message));
            })
            .SelectMany(proven => ListAtProvenRef(configured, proven));

    /// <summary>One seal answer's listing as the pass reads it — whether the source contributed
    /// nothing, whether that is a hold, and how many packages it listed. The pin for
    /// <c>AFailedDefaultInstallIsRepairedWhenTheFeedAnswersTest</c> (InternalsVisibleTo): it goes
    /// through the same hold path a seal answer takes in production.</summary>
    /// <param name="configured">The source as configured.</param>
    /// <param name="proven">The seal's answer for it.</param>
    /// <returns>Cold; emits once.</returns>
    internal IObservable<(bool Failed, bool Held, int Listed)> ListingFor(
        ConfiguredPackageSource configured, ProvenSource proven)
        => ListAtProvenRef(configured, proven).Select(l => (l.Failed, l.Held, l.Candidates.Count));

    /// <summary>The listing for one seal answer — the ONE place a hold is declared: it is recorded
    /// for the rest of this process (<see cref="heldSources"/>) and answered as
    /// <see cref="SourceListing.OnHold"/>, typed apart from a source that did not answer.</summary>
    /// <param name="configured">The source as configured.</param>
    /// <param name="proven">The seal's answer for it.</param>
    private IObservable<SourceListing> ListAtProvenRef(
        ConfiguredPackageSource configured, ProvenSource proven)
    {
        if (proven.HoldReason is { } hold)
        {
            ImmutableInterlocked.Update(ref heldSources, static (held, name) => held.Add(name), configured.Name);
            logger.LogWarning(
                "[DefaultInstall] {Name} is HELD this boot — {Reason}. Its packages are not "
                + "asserted this boot; this lane runs once per boot, so they are re-attempted at "
                + "the next process start after the seal lands — an unattended install never "
                + "resolves the branch instead (Doc/Architecture/SyncRefContract).",
                configured.Name, hold);
            return Observable.Return(SourceListing.OnHold);
        }
        var source = proven.Source;
        if (!ReferenceEquals(source, configured))
            logger.LogInformation(
                "[DefaultInstall] {Name} is listed and installed at {Ref}, not '{Configured}' — {Reason}",
                source.Name, source.GitRef, configured.GitRef, proven.Reason);
        return ListWithRefusals(source)
            .Take(1)
            // 🚨 Stamp the source name HERE, not only in the registry's HTTP merge. Source-
            // scoped matching reads PackageManifest.Source, and until now only
            // PluginRegistryEndpoints set it — so a REGISTRY instance, which reads its own
            // configured sources directly with no HTTP hop, saw Source == null on every
            // package and matched nothing. The result was a healthy deploy that installed
            // zero plugins while reporting a green boot. The lister always knows which source
            // it read from, so that is where the stamp belongs; an already-stamped value
            // (arriving over the wire) is left alone.
            // The WHOLE listing is carried forward, not just the selected packages: a selected
            // package's requirements are resolved against the full catalog below, and a
            // dependency that is neither pre-installed nor pattern-matched exists only here.
            .Select(packages => SourceListing.Of(packages
                .Select(p => string.IsNullOrEmpty(p.Source) ? p with { Source = source.Name } : p)
                .Select(p => new InstallCandidate(source, p)
                {
                    // PROVEN means the seal named the commit — ApplyPlan re-refs the source, so it
                    // is a different instance, which is exactly how "pinned" is told from "as
                    // configured" without comparing refs — or the operator's own mirrored working
                    // tree. Anything else is the branch this boot happened to resolve, and
                    // UnprovenRefHold decides what that may be written into.
                    RefIsProven = !ReferenceEquals(source, configured) || configured.LocalCheckout,
                })
                .ToList()))
            .Catch((Exception exception) =>
            {
                logger.LogWarning(exception,
                    "[DefaultInstall] listing {Name} @ {Ref} failed — its packages are skipped "
                    + "this boot", source.Name, source.GitRef);
                return Observable.Return(SourceListing.Unreachable);
            });
    }

    /// <summary>
    /// The seal's answer for one configured source, as the source to list — pinned to the sealed
    /// commit, left as configured, or held. Cold; the decision is pure once the seal is read.
    ///
    /// <para>🚨 The seal is read on the FileSystem <see cref="IIoPool"/>, never inline. The
    /// published-bundle root is a mounted share, and a read of it that is not on the pool is
    /// invisible to <see cref="IoPoolRegistry"/>'s teardown drain — it can still be running after
    /// the mesh scope is gone, the straggler the pool exists to prevent — and would block whatever
    /// thread the install chain happens to be on while the share is slow. Same shape, same pool as
    /// <c>PublicationSealArrivalService</c>. A mesh with a published root but no pool registry is
    /// not composed the way this needs; it keeps the configured ref and says so, rather than
    /// running untracked I/O.</para>
    /// </summary>
    /// <param name="source">The source as configured.</param>
    /// <returns>A cold observable emitting exactly once.</returns>
    internal IObservable<ProvenSource> ProvenRef(ConfiguredPackageSource source)
    {
        // A local checkout is a working tree this portal MIRRORS — there is no commit to pin and
        // the operator is the authority (MeshWeaver#3359). A source with no repository path (a
        // registered IPackageSource, a remote registry) names no repository a seal could be
        // attributed to. Both keep today's behaviour, by construction rather than by fall-through.
        if (source.LocalCheckout || string.IsNullOrWhiteSpace(source.RepoPath))
            return Observable.Return(new ProvenSource(source, null, "no repository to attribute a seal to"));
        var (owner, name) = ModuleDiscovery.SplitRepo(source.RepoPath);
        if (owner.Length == 0 || name.Length == 0)
            return Observable.Return(new ProvenSource(source, null,
                $"'{source.RepoPath}' is not an owner/name repository, so no seal is attributable"));
        var publishedRoot = hub.ServiceProvider.GetService<IConfiguration>()?[ShippedPrebuiltBundles.PublishedRootConfigKey];
        if (string.IsNullOrWhiteSpace(publishedRoot))
            return Observable.Return(new ProvenSource(source, null,
                "this installation has no published bundle root, so no publication is sealed here"));
        if (hub.ServiceProvider.GetService<IoPoolRegistry>() is not { } pools)
        {
            logger.LogWarning(
                "[DefaultInstall] no IoPoolRegistry is registered, so the seal under {Root} cannot be "
                + "read on a drained pool — not reading it; {Name} is listed at its configured ref "
                + "'{Ref}'.", publishedRoot, source.Name, source.GitRef);
            return Observable.Return(new ProvenSource(source, null, "the seal was not read: no IoPoolRegistry"));
        }
        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        var repo = new RepoIdentity(owner, name);
        // Policy module-sync-per-manifest-hash: the seal no longer pins (or holds) this lane's ref —
        // the same answer the GitSync first import takes, so the two writers of a partition agree.
        // The configured ref is therefore NOT proven, and where a sync source keeps the target
        // partition current the install defers to it (UnprovenRefHold, MeshWeaver#4588) — one
        // partition, one bookkeeping. An unreadable index is said, never read as "nothing sealed".
        return pools.Get(IoPoolNames.FileSystem)
            .InvokeBlocking(ct => SealedPublicationIndex.ReadingFor(publishedRoot, identity, ct, logger))
            .Select(reading =>
            {
                if (SealedSyncGate.RefusedForUnreadableIndex(reading.Outcome, identity) is { HoldReason: { } unreadable })
                    logger.LogWarning(
                        "[DefaultInstall] {Name}: {Unreadable}. The source is listed at its configured ref; "
                        + "no prebuilt bytes are adopted on this reading.", source.Name, unreadable);
                return ApplyPlan(source, SealedSyncGate.DecideFirstImport(repo, reading.Sources, identity));
            });
    }

    /// <summary>
    /// The plan → source mapping, pure and pinnable offline: a plan naming a commit re-refs the
    /// source to it; a plan resolving the branch leaves the source untouched (the same instance, so
    /// a caller can tell "pinned" from "as configured" without comparing refs); a hold carries its
    /// reason and no source to list.
    /// </summary>
    /// <param name="source">The source as configured.</param>
    /// <param name="plan">The seal's decision for its repository.</param>
    internal static ProvenSource ApplyPlan(ConfiguredPackageSource source, SealedSyncGate.FirstImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Proceed)
            return new ProvenSource(source, plan.HoldReason, plan.Reason);
        if (plan.AtBranchTip)
            return new ProvenSource(source, null, plan.Reason);
        return new ProvenSource(source with { GitRef = plan.Commit! }, null, plan.Reason);
    }

    /// <summary>
    /// A configured source after the seal has spoken: the source to list (pinned or as configured),
    /// or — when <paramref name="HoldReason"/> is set — a source held this boot.
    /// </summary>
    /// <param name="Source">The source to list; ignored when held.</param>
    /// <param name="HoldReason">Why nothing may be listed this boot, or null.</param>
    /// <param name="Reason">Log copy for whichever of the three this is — always populated.</param>
    internal sealed record ProvenSource(ConfiguredPackageSource Source, string? HoldReason, string Reason);

    /// <summary>
    /// One source's listing WITH the registry's plan-tier refusals folded in as refused rows
    /// (#4097). Only a <see cref="RegistryPackageSource"/> can answer with refusals — a git source
    /// read directly (a registry installing its own defaults) has no plan to refuse against — so
    /// every other source is its plain listing. A refused row carries <see cref="PackageManifest.Refusal"/>
    /// and is selected like the pre-installed package it is, so that the pass SKIPS it with the
    /// typed reason and records it, instead of never learning the package exists.
    /// </summary>
    private static IObservable<IReadOnlyList<PackageManifest>> ListWithRefusals(ConfiguredPackageSource source) =>
        source.Source is RegistryPackageSource registry
            ? registry.ListCatalog(source.GitRef).Select(listing => (IReadOnlyList<PackageManifest>)listing.Packages
                .Concat(listing.Refused
                    .Where(r => !listing.Packages.Any(p => string.Equals(p.Id, r.PackageId, StringComparison.Ordinal)))
                    .Select(r => PackageManifest.FromRefusal(r, source.Name)))
                .ToList())
            : source.Source.ListPackages(source.GitRef);

    /// <summary>
    /// <paramref name="selected"/> plus everything it transitively REQUIRES that the catalog can
    /// supply — the unattended twin of <see cref="PackageDependencyGraph.InstallClosure"/>, kept
    /// TOLERANT for the same reason the boot sort is: nobody is present to fix a malformed manifest,
    /// so an unresolvable requirement is named and stepped over rather than failing the pass.
    ///
    /// <para>A requirement the catalog does not carry is genuinely unfixable here (the instance was
    /// not granted it, or the source is down) — it is logged once at Warning, because the package
    /// that needs it will install and then fail at use, and that is a symptom nobody could otherwise
    /// trace back to a missing grant.</para>
    ///
    /// <para><b>A COMMERCIAL requirement is pulled in only on the deployment configuration's
    /// authority</b> — when it is in <paramref name="configAuthorized"/>, i.e. required (directly or
    /// transitively) by a package <see cref="PluginCatalogOptions.InstallByDefault"/> names EXACTLY
    /// (<see cref="ConfigAuthorizedRequirements"/>). Otherwise never: selection is source-scoped on
    /// purpose, an instance is routinely granted paid content it may buy but must not receive
    /// automatically, and a requirement edge of a wildcard-selected or baseline package would be a
    /// back door around that scoping.</para>
    ///
    /// <para>Cycles terminate: a package already in the result is never expanded twice.</para>
    /// </summary>
    /// <param name="catalog">Every package every configured source listed, deduped by id.</param>
    /// <param name="selected">The packages the baseline + operator patterns chose.</param>
    /// <param name="logger">Receives the unresolvable-requirement warning.</param>
    /// <param name="configAuthorized">The package ids the deployment configuration authorizes
    /// (named exactly, or required by one that is); null or empty = none.</param>
    /// <returns>The selection closed over its requirements, ordered by id (the sort orders it).</returns>
    internal static IReadOnlyList<PackageManifest> DependencyClosure(
        IReadOnlyList<PackageManifest> catalog,
        IReadOnlyList<PackageManifest> selected,
        ILogger? logger = null,
        IReadOnlySet<string>? configAuthorized = null)
    {
        var byId = catalog
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var closure = new Dictionary<string, PackageManifest>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var priced = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<PackageManifest>(selected);

        while (pending.Count > 0)
        {
            var package = pending.Pop();
            // Already expanded — this is also what makes a requirement cycle terminate.
            if (!closure.TryAdd(package.Id, package))
                continue;
            foreach (var requirement in package.Requires ?? [])
            {
                var id = PackageDependencyGraph.DependencyId(requirement);
                if (id.Length == 0 || closure.ContainsKey(id))
                    continue;
                if (!byId.TryGetValue(id, out var dependency))
                {
                    missing.Add(id);
                    continue;
                }
                // 🚨 A COMMERCIAL requirement is pulled in only when the deployment configuration
                // authorizes it (required by a package InstallByDefault names exactly). Otherwise
                // never: selection is deliberately source-scoped so that an instance granted paid
                // content (course catalogues, customer modules) does not auto-install what it merely
                // may buy — and a requirement edge must not become a back door around that. Without
                // that authority PackageEntitlement.Authorize would refuse it anyway: pulling it in
                // would buy nothing but a skip every boot.
                if (dependency.IsCommercial() && configAuthorized?.Contains(id) != true)
                {
                    priced.Add(id);
                    continue;
                }
                pending.Push(dependency);
            }
        }

        if (priced.Count > 0)
            logger?.LogWarning(
                "[DefaultInstall] {Count} required package(s) are COMMERCIAL and were not installed: "
                + "[{Priced}]. A paid or contact-sales dependency has to be acquired deliberately — "
                + "install it from the catalog as a global admin, provision it through a governed "
                + "package.provision activity, or name the package that needs it exactly "
                + "(Source/Package) in PluginCatalog:InstallByDefault.",
                priced.Count, string.Join(", ", priced));

        if (missing.Count > 0)
            logger?.LogWarning(
                "[DefaultInstall] {Count} required package(s) are not in any configured source and "
                + "cannot be installed: [{Missing}]. Whatever depends on them will install and then "
                + "fail at use — check the instance's plugin grants and that every source is "
                + "reachable.", missing.Count, string.Join(", ", missing));

        var selectedIds = selected
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var pulled = closure.Keys
            .Where(id => !selectedIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (pulled.Count > 0)
            logger?.LogInformation(
                "[DefaultInstall] {Count} package(s) pulled in as requirements of the selection — "
                + "[{Pulled}]", pulled.Count, string.Join(", ", pulled));

        return closure.Values
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>'s <see cref="PackageManifest.Source"/> is PROVENANCE
    /// rather than an authored claim: a registry stamps the source at its own boundary from its
    /// configured sources, and any other source's manifest is trusted only where its source IS the
    /// name that source was configured under (the lister stamps that name on every manifest that
    /// authors none). A directly read repository whose manifest authors ANOTHER source's name gets
    /// no deployment-configuration authority — it could otherwise claim that source's
    /// <c>Source/Package</c> entry (policy <c>deployment-config-authorizes-named-commercial</c>).
    /// Selection itself is unchanged; only the authority is withheld. Pure.
    /// </summary>
    private static bool ProvenanceIsTrusted(InstallCandidate candidate) =>
        // 🚨 An instance-local source (Doc/Architecture/InstanceRepositoryAccess) is NEVER trusted to
        // carry the deployment configuration's authority, whatever it is named: its administrator
        // chose the name, so a local repository called like a configured source must not inherit
        // that source's Source/Package entries — or the commercial requirements they admit.
        !candidate.Source.InstanceLocal
        && (candidate.Source.Source is RegistryPackageSource
            || string.Equals(candidate.Package.Source, candidate.Source.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether two source names are the same non-empty source (case-insensitive, like
    /// <see cref="PluginGrantEntry.Matches"/>). Pure.</summary>
    /// <summary>
    /// The listing the repair lane re-asserts an installed package from (#6512), or null when the
    /// record names a source and no listing is TRUSTED to be that source. Trusted means a registry's
    /// stamp, or the candidate's source being the name its source was configured under
    /// (<see cref="ProvenanceIsTrusted"/>); a manifest that merely authors the recorded name is not
    /// enough. A legacy record with no source keeps the first listing. Pure.
    /// </summary>
    /// <param name="listings">Every listing of one package id, in source order.</param>
    /// <param name="recordedSource">The source the install record was stamped with, or null.</param>
    private static InstallCandidate? RepairCandidate(IReadOnlyList<InstallCandidate> listings, string? recordedSource) =>
        string.IsNullOrWhiteSpace(recordedSource)
            ? listings.FirstOrDefault()
            : listings.FirstOrDefault(c => RepairProvenanceIsTrusted(c, recordedSource));

    /// <summary>
    /// Whether a listing may re-assert an install recorded under <paramref name="recordedSource"/>.
    /// REPAIR is not AUTHORIZATION: a package installed from an instance-local source is repaired from
    /// that source — the one configured under the recorded name, never a manifest that merely authors
    /// it — while the deployment configuration's authority (<see cref="ProvenanceIsTrusted"/>) stays
    /// closed to it. Listings are in source order and instance-local sources come last, so a
    /// deployment source of the same name always wins. Pure.
    /// </summary>
    private static bool RepairProvenanceIsTrusted(InstallCandidate candidate, string recordedSource) =>
        candidate.Source.InstanceLocal
            ? SameSource(candidate.Source.Name, recordedSource) && SameSource(candidate.Package.Source, recordedSource)
            : ProvenanceIsTrusted(candidate) && SameSource(candidate.Package.Source, recordedSource);

    private static bool SameSource(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every package REQUIRED — directly or transitively, through free and commercial packages
    /// alike — by a package the deployment configuration names exactly, mapped to the named
    /// package it was first reached from (ordinal order of the named roots). The named packages
    /// themselves are not in the map. A requirement the catalog does not carry is not followed
    /// (<see cref="DependencyClosure"/> names it). Cycles terminate. Pure.
    ///
    /// <para>This is the closure the deployment configuration AUTHORIZES besides what it names:
    /// "this instance comes up with X" means X working, and X does not work without what it
    /// requires. It is handed to <see cref="DeploymentConfigAuthority.Admit"/>, so the entitlement
    /// gate admits exactly this set and nothing a wildcard merely covers.</para>
    /// </summary>
    /// <param name="catalog">Every package every configured source listed.</param>
    /// <param name="named">The packages <see cref="PluginCatalogOptions.InstallByDefault"/> names exactly.</param>
    /// <returns>Requirement id → the named package that requires it.</returns>
    internal static ImmutableDictionary<string, string> ConfigAuthorizedRequirements(
        IReadOnlyList<PackageManifest> catalog, IReadOnlyList<PackageManifest> named)
    {
        var byId = catalog
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var namedIds = named.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
        var result = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        foreach (var root in named.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var seen = ImmutableHashSet.Create(StringComparer.Ordinal, root.Id);
            var pending = ImmutableStack.Create(root);
            while (!pending.IsEmpty)
            {
                pending = pending.Pop(out var package);
                foreach (var requirement in package.Requires ?? [])
                {
                    var id = PackageDependencyGraph.DependencyId(requirement);
                    if (id.Length == 0 || seen.Contains(id) || !byId.TryGetValue(id, out var dependency))
                        continue;
                    seen = seen.Add(id);
                    if (!namedIds.Contains(id) && !result.ContainsKey(id))
                        result = result.Add(id, root.Id);
                    pending = pending.Push(dependency);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Why the UNATTENDED default install must not even ATTEMPT <paramref name="package"/> — or
    /// null when it may proceed. Pure.
    ///
    /// <para>🚨 <b>This is a CLASSIFICATION, not retry tuning (#2536).</b> A commercial package is
    /// refused by <see cref="PackageEntitlement.Authorize"/> for want of an authorizing principal,
    /// and on this lane that want is PERMANENT: no human can appear at boot to pay or approve.
    /// Recording such a package as FAILED and re-attempting it at the next boot ("that retry is the
    /// repair") was wrong twice — the retry could never succeed, and the per-boot <c>fail:</c>
    /// advertised a defect where there is a standing decision. Such a package is recorded as
    /// SKIPPED with this reason instead — durable on the ledger, never re-attempted as a repair.</para>
    ///
    /// <para>What changes the outcome is an EVENT the next pass observes, never a retry: a Global
    /// Admin installs the package from the catalog (the lane with an authorizing principal), or the
    /// package's own terms change — it stops being commercial. The classification is re-derived
    /// from the CURRENT manifest on every pass, so it moves the moment its input does; nothing
    /// polls the refused operation.</para>
    /// </summary>
    /// <param name="package">The selected candidate's manifest, as the catalog lists it now.</param>
    /// <returns>The speaking skip reason, or null when the pass may install it.</returns>
    internal static string? TerminalSkipReason(PackageManifest package) =>
        TerminalSkipReason(package, configAuthorized: false);

    /// <summary>
    /// <see cref="TerminalSkipReason(PackageManifest)"/> for a candidate the deployment
    /// configuration may authorize. 🚨 A commercial package the configuration names EXACTLY (or
    /// that such a package requires) is NOT skipped: the deployment record is its authorizing
    /// principal (<see cref="DeploymentConfigAuthority"/>, policy
    /// <c>deployment-config-authorizes-named-commercial</c>). A plan-tier refusal still wins — the registry does not serve
    /// what the instance's plan does not cover, and no configuration on the instance changes that.
    /// Pure.
    /// </summary>
    /// <param name="package">The selected candidate's manifest.</param>
    /// <param name="configAuthorized">Whether the deployment configuration authorizes it.</param>
    /// <returns>The speaking skip reason, or null when the pass may install it.</returns>
    internal static string? TerminalSkipReason(PackageManifest package, bool configAuthorized) =>
        package.Refusal is { } refused
            // #4097 — the registry refuses it to this instance's PLAN. Not an authorization this
            // lane lacks; a licence the instance does not hold. Re-derived from the listing each
            // pass, so a plan upgrade lifts it on the next boot.
            ? refused.Describe()
            : package.IsCommercial() && !configAuthorized ? PackageEntitlement.Reason(package, null) : null;

    /// <summary>Installs the selected packages sequentially and folds their outcomes into one summary.</summary>
    private IObservable<DefaultInstallSummary> InstallAll(IReadOnlyList<InstallCandidate> candidates) =>
        candidates.Count == 0
            ? Observable.Return(DefaultInstallSummary.Empty)
            // 🚨 An UNINSTALLED package is never re-installed by an unattended pass — not by the
            // seed, not by the platform baseline, not by a feature flag (PackageUninstallExecutor,
            // Doc/Architecture/PackageUninstall). Only a person installing it again lifts this.
            : PackageUninstallExecutor.UninstalledHere(hub)
                .SelectMany(uninstalled =>
                {
                    var blocked = candidates.Where(c => uninstalled.Contains(c.Package.Id)).Select(c => c.Package.Id).ToList();
                    if (blocked.Count > 0)
                        logger.LogInformation(
                            "[DefaultInstall] {Count} declared package(s) were UNINSTALLED on this instance and are not "
                            + "re-installed by an unattended pass: [{Ids}]. Installing one from the catalog lifts this.",
                            blocked.Count, string.Join(", ", blocked));
                    return InstallAllCore(blocked.Count == 0
                        ? candidates
                        : candidates.Where(c => !uninstalled.Contains(c.Package.Id)).ToList());
                });

    private IObservable<DefaultInstallSummary> InstallAllCore(IReadOnlyList<InstallCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Observable.Return(DefaultInstallSummary.Empty);

        // The terminal skips FIRST — what this unattended pass must not even attempt (see
        // TerminalSkipReason). They seed the aggregate so the ledger records them with their
        // reasons, and they are reported ONCE, at Warning: actionable ("needs a Global Admin"),
        // not a per-boot fail: with a stack trace (#2536).
        var skipped = candidates
            .Select(c => (c.Package.Id, Reason: TerminalSkipReason(c.Package, c.ConfigAuthority is not null)))
            .Where(x => x.Reason is not null)
            .Select(x => new DefaultInstallSkip(x.Id, x.Reason!))
            .ToImmutableList();
        var installable = skipped.Count == 0
            ? candidates
            : candidates.Where(c => TerminalSkipReason(c.Package, c.ConfigAuthority is not null) is null).ToList();
        if (skipped.Count(x => candidates.Any(c => c.Package.Id == x.Package && !c.Package.IsRefused)) > 0)
            logger.LogWarning(
                "[DefaultInstall] {Count} declared package(s) require an authorization this "
                + "unattended install can never obtain and are SKIPPED, not failed: [{Skipped}]. "
                + "They are recorded with their reasons on {Ledger} and no boot re-attempts them — "
                + "a Global Admin installing them from the catalog, a governed package.provision "
                + "activity signed for them, naming them exactly (Source/Package) in "
                + "PluginCatalog:InstallByDefault — a whole-source pattern names nothing — or the "
                + "package ceasing to be commercial, is what changes this.",
                skipped.Count(x => candidates.Any(c => c.Package.Id == x.Package && !c.Package.IsRefused)),
                string.Join(", ", skipped
                    .Where(x => candidates.Any(c => c.Package.Id == x.Package && !c.Package.IsRefused))
                    .Select(s => s.Package)),
                SeedLedgerPath);

        // 🚨 #4097 — the plan-tier refusals, TYPED, beside the string-reasoned skips above: the
        // ledger and the activation record carry them as data (package, module, tier, plan), and
        // the log line names the cause the instance never used to see. Warning, once per pass,
        // like the other skips — this is a standing fact about the plan, not a per-boot error.
        var tierRefused = candidates
            .Select(c => c.Package.Refusal)
            .Where(r => r is not null)
            .Select(r => r!)
            .DistinctBy(r => r.PackageId, StringComparer.Ordinal)
            .ToImmutableList();
        if (tierRefused.Count > 0)
            logger.LogWarning(
                "[DefaultInstall] {Count} package(s) the registry declares in this instance's default "
                + "set are REFUSED by its plan tier and are not installed: {Refused}. Raise the "
                + "instance's plan on the registry to receive them; nothing on this instance can.",
                tierRefused.Count, string.Join("; ", tierRefused.Select(r => r.Describe())));

        var seed = DefaultInstallSummary.Empty with { Skipped = skipped, TierRefused = tierRefused };
        if (installable.Count == 0)
            return Observable.Return(seed);
        logger.LogInformation(
            "[DefaultInstall] {Count} package(s), in dependency order — {Packages}",
            installable.Count, string.Join(", ", installable.Select(c => c.Package.Id)));
        // 🚨 PHASE 1 — MODULES, THEN PHASE 2 — NODES (see LandModulesFirst). Strictly sequential:
        // the content phase is subscribed only once every module of the selection has been decided.
        return LandModulesFirst(installable)
            .SelectMany(phase1 => installable
                .Select(Install)
                .ToObservable()
                .Concat()
                .Aggregate(seed with { ModulesLanded = phase1.Landed }, (acc, one) => acc.Add(one))
                // 🚨 ONE proposal per pass, at its END: checked against the install records AND the
                // whole selection's declared floors, and blocked by any carrier phase 1 could not
                // clear. Phase 2's per-package installs propose nothing (proposeModuleSet: false) —
                // each would be checked against the records written so far and could publish a
                // module phase 1 pre-landed for a package whose floor a LATER package refuses.
                // Only a pass that ran a module wave proposes: a module-declaring package from a source
                // that serves no bundles (a git or registered source) lands nothing on either phase, and
                // a proposal without a wave would publish whatever the record holds, cleared by nobody.
                .SelectMany(summary => !phase1.HadWave
                    ? Observable.Return(summary)
                    : ProposeCheckedForTheSelection(
                            installable.Select(c => c.Package).ToImmutableList(),
                            BlockedAfterPhase2(phase1.Blocked, phase1.Cleared, summary))
                        .Select(verdict => summary with { ModuleSetVerdict = verdict })));

        // Phase 2's own verdicts are NEWER than phase 1's: a package it HELD (ownership re-read),
        // SKIPPED (a terminal refusal before the pass, or one visible only at install time) or FAILED
        // without phase 1 having cleared it blocks its module too. A fresh carrier phase 1 cleared
        // does not block on failing — that is the bootstrap's expected first pass, and blocking it
        // would keep the modules from ever loading.
        IReadOnlySet<string> BlockedAfterPhase2(
            IReadOnlySet<string> phase1Blocked, IReadOnlySet<string> phase1Cleared, DefaultInstallSummary summary)
        {
            // A FAILED package blocks too — unless it is a fresh carrier phase 1 CLEARED (its module
            // landed or was proven up to date, every gate passed): that failure is the node
            // validation the bootstrap's first pass expects. An already-installed carrier that fails
            // (a fetch, a parameter) never reached WithModule, so nothing cleared its module here.
            var failedUncleared = candidates
                .Where(c => (summary.Failures ?? []).Contains(c.Package.Id)
                            && !string.IsNullOrWhiteSpace(c.Package.Module)
                            && !phase1Cleared.Contains(c.Package.Module!))
                .Select(c => c.Package.Id);
            var refusedNow = (summary.Held ?? []).Select(h => h.Package)
                .Concat((summary.Skipped ?? []).Select(s => s.Package))
                .Concat(failedUncleared)
                .ToImmutableHashSet(StringComparer.Ordinal);
            // Through ALL candidates, not just the installable ones: a TERMINAL skip (entitlement,
            // licence, plan tier) left `installable` before phase 1, and an entry an earlier pass
            // landed for it must block this proposal all the same.
            return phase1Blocked
                .Concat(candidates
                    .Where(c => refusedNow.Contains(c.Package.Id) && !string.IsNullOrWhiteSpace(c.Package.Module))
                    .Select(c => c.Package.Module!))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 🚨 <b>PHASE 1 of the default install: land the compiled MODULE of every selected package that
    /// is not installed yet, BEFORE any package's nodes are written</b> (policy
    /// <c>default-install-modules-first</c>).
    ///
    /// <para><b>Why the order is the fix.</b> A mixed package's module used to land only AFTER its
    /// content (<c>CatalogLayoutAreas.InstallOrUpdateCore</c>'s <c>WithModule</c> runs on the
    /// content's success), and content is validated against the NodeTypes this process has
    /// registered (<c>PackageInstaller.ValidateBulkTypes</c> — "NodeType(s) not registered"). So
    /// on an image that does not ship the modules the packages deliver, the store's own bootstrap
    /// is a cycle with no entry point: <c>Store</c>'s nodes include <c>Skill</c> (registered by the
    /// <c>MeshWeaver.AI</c> module the <c>AI</c> package delivers), <c>AI</c>'s root is typed
    /// <c>Store/Plugin</c> (declared by the <c>Store</c> package), and every other package's root
    /// is <c>Store/Plugin</c> too. Each install failed validation, so no module ever landed, so no
    /// restart could ever register the types — measured on a fresh control instance whose image
    /// carries none of them (2026-10-13: <c>0 installed, 0 up to date, 48 failed</c>, <c>/data/modules</c>
    /// empty).</para>
    ///
    /// <para><b>What breaks the cycle.</b> A module's bytes do not depend on any node: they are
    /// fetched from the registry, link-probed against the running platform and landed
    /// (<see cref="PluginBundleClient.AdoptModuleOutcome"/>). Landing them first, for the whole
    /// selection, means the NEXT process start registers every NodeType those modules contribute
    /// (restart-as-activation — a brand-new module is <c>NotHeld</c> and cannot go live in this
    /// process, <c>Doc/Architecture/LiveModuleUpdate</c>), and the landing raises the pending-restart
    /// marker that the self-updater takes. That boot's pass then finds <c>Skill</c>/<c>Agent</c>
    /// registered, installs <c>Store</c> first (the dependency order already puts it before every
    /// package that requires it) and everything rooted on <c>Store/Plugin</c> after it. The
    /// "NodeType must be registered" validation is untouched: this pass's content failures are
    /// recorded as FAILED with their cause, and the next pass is their retry.</para>
    ///
    /// <para><b>Scope — fresh installs only.</b> A package that already has an install record keeps
    /// its module on the funnel it always used (module after content, inside the floor and
    /// update-policy holds), so this phase never lands drift those holds exist to gate. A package
    /// whose install record cannot be READ is not treated as fresh either. The same refusals the
    /// install funnel applies before a file travels apply here before a byte lands: the entitlement
    /// (with the same deployment-config principal), licence acceptance and the declared parameters — a package the
    /// funnel would refuse lands no module ahead of that refusal. The platform floor is the
    /// adoption's own <c>floorHold</c>.</para>
    ///
    /// <para>Never fails the pass: the adoption absorbs every failure into its outcome (logged by
    /// the client), and the content phase runs whatever this phase answered.</para>
    /// </summary>
    /// <param name="installable">The pass's installable candidates, in dependency order.</param>
    /// <returns>Cold; emits the entry-assembly names of the modules this phase LANDED, once.</returns>
    private IObservable<(ImmutableList<string> Landed, IReadOnlySet<string> Blocked, IReadOnlySet<string> Cleared, bool HadWave)> LandModulesFirst(
        IReadOnlyList<InstallCandidate> installable)
    {
        var carriers = installable
            .Select(c => (Candidate: c,
                Bundles: (c.Source.Source as RegistryPackageSource)?.Bundles ?? c.Source.Bundles))
            .Where(x => !string.IsNullOrWhiteSpace(x.Candidate.Package.Module) && x.Bundles is not null)
            .ToList();
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        IReadOnlySet<string> none = ImmutableHashSet<string>.Empty;
        if (carriers.Count == 0 || persistence is null)
            return Observable.Return((ImmutableList<string>.Empty, none, none, false));

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();

        // (Fresh: a not-yet-installed carrier this phase decided; Landed: the module it newly landed;
        // Blocked: the module of a carrier this phase could NOT clear — an unreadable install record,
        // missing parameters, an ownership hold, an entitlement or licence refusal, or an adoption that
        // neither landed nor proved the existing entry up to date).
        IObservable<(bool Fresh, string? Landed, string? Blocked)> One(InstallCandidate candidate, PluginBundleClient bundles)
        {
            var package = candidate.Package;
            return persistence.Read($"{PackageInstaller.InstalledPartition}/{package.Id}", hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty()
                .Select(record => (Fresh: record is null, Unreadable: (string?)null))
                .Catch((Exception ex) => Observable.Return((Fresh: false, Unreadable: (string?)ex.Message)))
                .SelectMany(state =>
                {
                    if (state.Unreadable is { } cause)
                    {
                        logger.LogWarning(
                            "[DefaultInstall] module phase: the install record of {Id} could not be read "
                            + "({Cause}) — its module {Module} is not landed ahead of its content; the "
                            + "install funnel decides it as before.", package.Id, cause, package.Module);
                        return Observable.Return((Fresh: false, Landed: (string?)null, Blocked: package.Module));
                    }
                    if (!state.Fresh)
                        return Observable.Return((Fresh: false, Landed: (string?)null, Blocked: (string?)null));
                    if (PackageParameters.Missing(configuration, package) is { Count: > 0 })
                        // The content phase refuses it by name (PackageParameters.Require, at Error);
                        // saying it twice adds nothing, landing its module ahead would add a module
                        // for a package this environment cannot run.
                        return Observable.Return((Fresh: false, Landed: (string?)null, Blocked: package.Module));
                    var principal = AuthorizingPrincipal(candidate);
                    // 🚨 The SAME ownership decision the node install takes (OwnershipHold): a
                    // candidate whose partition another writer keeps current is HELD and adopts
                    // nothing — so no module of it lands ahead either. The node install reports the
                    // hold; here it only means "no module ahead".
                    return OwnershipHold(candidate, PartitionOf(candidate))
                        // Fail closed: an ownership question that could not be answered lands nothing
                        // ahead; the node install asks it again and reports what it finds.
                        .Catch((Exception ex) => Observable.Return<string?>($"the partition's ownership could not be read: {ex.Message}"))
                        .SelectMany(hold => hold is not null
                            ? Observable.Return((Fresh: false, Landed: (string?)null, Blocked: package.Module))
                            : Gated(principal));
                    IObservable<(bool Fresh, string? Landed, string? Blocked)> Gated(string? principal) =>
                        PackageEntitlement.Authorize(hub, package, principal, logger)
                        // The licence gate the installer applies after the entitlement
                        // (PackageInstaller.Install): no licensed byte lands before its terms are
                        // accepted, and a deployment-config principal cannot accept on anyone's behalf.
                        .SelectMany(_ => LicenseAcceptanceGate.Require(hub, package, principal, logger))
                        .SelectMany(_ => bundles.AdoptModuleOutcome(package.Id, package.Module!,
                                $"{PackageInstaller.InstalledPartition}/{package.Id}")
                            // Bounded per package exactly like the reconciler's adoption
                            // (RegistryUpdateReconciler.PerPackageAdoptBudget): a trickling transfer
                            // must not starve the remaining modules and the whole content phase.
                            .Timeout(RegistryUpdateReconciler.PerPackageAdoptBudget))
                        .Select(outcome =>
                        {
                            if (outcome.FilesLanded > 0)
                                return (Fresh: true, Landed: package.Module, Blocked: (string?)null);
                            // 🚨 Only SkipUpToDate proves an existing entry IS the artifact served now.
                            // Every other zero-land outcome (a floor hold, a declined or failed fetch,
                            // a refused landing) may leave an OLDER generation in the record, and a
                            // proposal must not activate it under the newer content phase 2 installs.
                            if (outcome.Failure is null && outcome.Verdict?.Action == ModuleUpdateAction.SkipUpToDate)
                                return (Fresh: true, Landed: (string?)null, Blocked: (string?)null);
                            logger.LogWarning(
                                "[DefaultInstall] module phase: {Module} of {Id} did not land — {Outcome}. "
                                + "Packages whose nodes need its NodeTypes fail validation this pass and are "
                                + "retried by the next.", package.Module, package.Id,
                                outcome.Failure ?? $"{outcome.Verdict?.Action}: {outcome.Verdict?.Reason}");
                            return (Fresh: true, Landed: (string?)null, Blocked: package.Module);
                        })
                        // A refusal is the content phase's to report (it applies the same gate and
                        // records it as a skip or a failure); here it only means "no module ahead".
                        // Anything else (the budget expiring, an unexpected fault) is named and this
                        // carrier steps aside, so the remaining carriers still run.
                        .Catch((Exception ex) =>
                        {
                            // 🚨 A gate refusal is NOT a decided fresh carrier: it must not trigger
                            // the module-set proposal, or an entry an earlier pass landed would be
                            // proposed — and activated — against the current refusal.
                            if (ex is PackageAuthorizationException or LicenseAcceptanceRequiredException)
                                return Observable.Return((Fresh: false, Landed: (string?)null, Blocked: package.Module));
                            logger.LogWarning(ex,
                                "[DefaultInstall] module phase: {Module} of {Id} did not land — {Cause}. "
                                + "The content phase decides the package as before.",
                                package.Module, package.Id, ex.Message);
                            return Observable.Return((Fresh: true, Landed: (string?)null, Blocked: package.Module));
                        });
                });
        }

        return accessService.RunAsSystem(() => carriers
                .Select(x => One(x.Candidate, x.Bundles!)
                    .Select(o => (o.Fresh, o.Landed, o.Blocked, Module: x.Candidate.Package.Module!)))
                .ToObservable()
                .Concat()
                .ToList()
                .Select(outcomes =>
                {
                    var landed = outcomes.Where(o => o.Landed is not null).Select(o => o.Landed!).ToImmutableList();
                    // 🚨 The set is derived from the WHOLE activation record, so a proposal would
                    // also carry a module an earlier pass landed for a package this phase could not
                    // clear NOW (a gate refusal, an ownership hold, missing parameters, an adoption
                    // that neither landed nor proved the entry up to date, or an install record it
                    // could not read — fail closed). The pass's one proposal (at its end) is blocked
                    // by an ENABLED activation entry for any such module; a module that was never
                    // landed cannot be activated by it, so it blocks nothing.
                    IReadOnlySet<string> blocked = outcomes.Where(o => o.Blocked is not null).Select(o => o.Blocked!)
                        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                    if (landed.Count > 0)
                        logger.LogInformation(
                            "[DefaultInstall] module phase: landed {Count} module(s) BEFORE any package content — "
                            + "[{Modules}]. A newly landed module loads at a restart once the module set naming it "
                            + "is proposed (once, at the end of this pass); a package whose nodes need its NodeTypes "
                            + "fails validation in this pass and is installed by a pass after that restart.",
                            landed.Count, string.Join(", ", landed));
                    // Cleared: a FRESH carrier that passed every gate and whose module landed now or was
                    // proven up to date — the one kind whose phase-2 failure (node validation before
                    // the module loads) is expected and must not block the proposal.
                    IReadOnlySet<string> cleared = outcomes
                        .Where(o => o.Fresh && o.Blocked is null)
                        .Select(o => o.Module)
                        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                    return (landed, blocked, cleared, true);
                }))
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "[DefaultInstall] module phase faulted: {Cause}. The content phase runs as before — "
                    + "each package's module still follows its content on the install funnel. No module of "
                    + "this selection is cleared for the pass's proposal.", ex.Message);
                // Fail CLOSED for activation: a wave that aborted established nothing about its
                // carriers, so every carrier's module blocks the pass's one proposal.
                IReadOnlySet<string> allCarriers = carriers.Select(x => x.Candidate.Package.Module!)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                return Observable.Return((ImmutableList<string>.Empty, allCarriers, none, true));
            });
    }

    /// <summary>
    /// The module-phase close: propose the set the activation record describes, checked against the
    /// install records AND <paramref name="selection"/> (<see cref="ModuleDependencyFloor.ProposeChecked(IMessageHub, ModuleLandingService, IReadOnlyList{PackageManifest})"/>).
    /// A refused or unreadable proposal leaves the mesh on its current set and is said at Warning —
    /// the same answer <c>CatalogLayoutAreas.ProposeMeshModuleSet</c> gives.
    /// </summary>
    /// <param name="selection">The manifests this pass is about to install.</param>
    /// <param name="blocked">The modules of carriers this pass could not clear — an enabled
    /// activation entry for any of them blocks the proposal.</param>
    private IObservable<string> ProposeCheckedForTheSelection(
        IReadOnlyList<PackageManifest> selection, IReadOnlySet<string> blocked)
    {
        var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
        if (landing is null)
            return Observable.Return("no module landing service on this host — nothing to propose");
        return landing.GetActivation().Take(1)
            .SelectMany(activation =>
            {
                var carried = activation.Entries
                    .Where(e => e.Enabled && blocked.Contains(e.Name))
                    .Select(e => e.Name)
                    .ToList();
                // 🚨 …and an ABSENT blocked module another selected package REQUIRES: the floor check
                // skips a dependency with no landed entry, so without this a wave whose AI failed to
                // land while Chat (requiring AI) landed would publish the half set.
                var blockedIds = selection
                    .Where(p => !string.IsNullOrWhiteSpace(p.Module) && blocked.Contains(p.Module!))
                    .Select(p => p.Id)
                    .ToImmutableHashSet(StringComparer.Ordinal);
                var requiredButBlocked = selection
                    .SelectMany(p => p.Requires.Select(PackageDependencyGraph.DependencyId))
                    .Where(blockedIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                string? withheld = carried.Count > 0
                    ? $"the activation record carries [{string.Join(", ", carried)}] for package(s) this pass "
                      + "could not clear (a gate refusal, an ownership hold, missing parameters or an unreadable "
                      + "install record), and a proposal would activate them"
                    : requiredButBlocked.Count > 0
                        ? $"package(s) [{string.Join(", ", requiredButBlocked)}] are required by this selection but "
                          + "their modules could not be cleared this pass; a set without them would load their "
                          + "dependents alone"
                        : null;
                if (withheld is null)
                    return Propose();
                var verdict = $"{ModuleSetWithheld}: {withheld}";
                logger.LogWarning("[DefaultInstall] module phase: proposing NO module set — {Verdict}", verdict);
                return Observable.Return(verdict);
            });

        IObservable<string> Propose() => ModuleDependencyFloor.ProposeChecked(hub, landing, selection)
            .Select(set => set is null
                ? $"{ModuleSetUnchanged}: nothing new to propose"
                : $"{ModuleSetProposed}: sequence {set.Sequence} — the landed modules load at the next restart")
            .DefaultIfEmpty($"{ModuleSetUnchanged}: nothing new to propose")
            .Do(verdict => logger.LogInformation("[DefaultInstall] module phase: {Verdict}", verdict))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "[DefaultInstall] module phase: the landed module set could not be proposed — the mesh "
                    + "stays on its current set and the next wave proposes again. Cause: {Cause}", ex.Message);
                return Observable.Return($"{ModuleSetRefused}: {ex.Message}");
            });
    }

    /// <summary>Verdict prefix: the pass proposed a module set.</summary>
    internal const string ModuleSetProposed = "proposed";

    /// <summary>Verdict prefix: the pass proposed, and the set was already current.</summary>
    internal const string ModuleSetUnchanged = "unchanged";

    /// <summary>Verdict prefix: the pass WITHHELD its proposal (an uncleared carrier).</summary>
    internal const string ModuleSetWithheld = "withheld";

    /// <summary>Verdict prefix: the proposal was refused (a dependency floor, an unreadable record).</summary>
    internal const string ModuleSetRefused = "refused";

    /// <summary>The principal a candidate is installed on the authority of — the deployment
    /// configuration's, for a commercial package it authorizes; null for every other (a free package
    /// needs none, and System authorizes nothing).</summary>
    private string? AuthorizingPrincipal(InstallCandidate candidate) =>
        candidate.ConfigAuthority is not null && candidate.Package.IsCommercial()
            ? hub.ServiceProvider.GetService<DeploymentConfigAuthority>()?.Principal
            : null;

    /// <summary>
    /// Installs (or re-reconciles) ONE package and re-asserts its public read — unless this lane
    /// may not write that partition at the ref it resolved, in which case it HOLDS
    /// (<see cref="UnprovenRefHold"/>, MeshWeaver#4588).
    ///
    /// <para>SYSTEM for the whole lifetime, for the same reason the catalog click is (which wraps
    /// this same <c>InstallOrUpdate</c> in <c>ImpersonateAsSystem</c>): an install is PROVISIONING
    /// — every partition it creates lands under the System identity with no user grants, so any
    /// step that authorises against an ambient identity fails closed. There is no user here at all
    /// (this runs on boot), so the impersonation widens nobody's rights.</para>
    ///
    /// <para>A failure is reported in the summary and stepped over: the packages are independent,
    /// and one unreachable or malformed package must not withhold the rest — a half-seeded instance
    /// with a named failure beats an unseeded one.</para>
    /// </summary>
    private IObservable<DefaultInstallSummary> Install(InstallCandidate candidate)
    {
        var partition = string.IsNullOrWhiteSpace(candidate.Package.TargetPartition)
            ? candidate.Package.Id
            : candidate.Package.TargetPartition!;

        // 🚨 GATE 1c (MeshWeaver#4588) — the ownership question is asked ONLY where this boot could
        // not prove its ref. A seal-pinned install lands the same tree the partition's own writer
        // is held to, which is #4259's whole design and is left exactly as it is; the residue is
        // the branch tip, and that is what may not be written into a partition somebody else keeps
        // current. Asking the seam for every candidate would put a bounded read in front of
        // installs that were never in question.
        // 🚨 GATE 1d (MeshWeaver#4625) — a PROVEN ref still has to be the partition's OWN
        // repository. #4259 pins both writers to a commit, which makes them agree only if the two
        // commits are trees of the same repository; where a package's `targetPartition` is
        // connected to a different repository — or a different SUBDIRECTORY of one — both writers
        // are pinned, both are "proven", and they still land two different trees. Nothing verified
        // that until now, and Copilot's review of #4619 spotted that the PR's own control arm
        // configured exactly this mismatch.
        //
        // 🚨 It holds ONLY on a DEFINITE disagreement. "Unknown" is the default answer of every
        // provider that has not implemented the identity seam, so treating it as a mismatch would
        // hold every proven install on the fleet's normal shape — which is the stated cost that
        // kept #4625 open rather than folded into #4619. See
        // PartitionContentOwnership.ImportedFromAnotherRepository for the full asymmetry.
        return OwnershipHold(candidate, partition)
            .SelectMany(reason => reason is not null
                ? HoldForThePartitionsOwnWriter(candidate, partition, reason)
                : Land(candidate, partition));
    }

    /// <summary>The target partition of a candidate — its declared <c>targetPartition</c>, else its id. Pure.</summary>
    private static string PartitionOf(InstallCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Package.TargetPartition)
            ? candidate.Package.Id
            : candidate.Package.TargetPartition!;

    /// <summary>
    /// Why this unattended lane may NOT write <paramref name="partition"/> for
    /// <paramref name="candidate"/> — gate 1c / 1d below (MeshWeaver#4588, #4625) — or null when it
    /// may. ONE decision for both phases: the node install holds on it, and the module phase lands no
    /// module for a held candidate (a hold adopts nothing, so neither may the phase before it).
    /// </summary>
    private IObservable<string?> OwnershipHold(InstallCandidate candidate, string partition) =>
        candidate.RefIsProven
            // 🚨 The folder is the SOURCE's configured prefix PLUS the module's own, through the one
            // implementation provisioning uses (review on #4649). A package listing stores
            // `SourceFolder` relative to `Subdir`, so passing it alone would compare `repo#module`
            // with the real `repo#prefix/module` and FALSELY HOLD the normal same-tree shape.
            //
            // 🚨 …and RunAsSystem, because this gate READS. The boot pass is subscribed on the task
            // pool with no system scope, and `_GitSync` nodes are RLS-filtered: without it the read
            // sees no config, answers "no mismatch", and gate 1d is INERT on exactly the deployed
            // instances it exists for — a gate that cannot fail, which is the one shape this whole
            // family of fixes is about. The publication-arrival path takes the same precaution.
            ? hub.ServiceProvider.GetRequiredService<AccessService>()
                .RunAsSystem(() => PartitionContentOwnership.ImportedFromAnotherRepository(
                    hub, partition, candidate.Source.RepoPath,
                    ModuleDiscoveryService.Subdirectory(
                        candidate.Source.Subdir,
                        candidate.Package.SourceFolder ?? candidate.Package.Id)))
            // The NON-logging overload: HoldForThePartitionsOwnWriter says the whole thing once,
            // with the verdict's reason inside it. The logging overload here would warn a second
            // time about the same hold, and a contract that says "said once" has to mean it.
            : PartitionContentOwnership.Observe(hub, partition)
                .Select(ownership => UnprovenRefHold(ownership, candidate.Source.GitRef));

    /// <summary>
    /// 🚨 <b>An unattended install never lands an UNPROVEN ref in a partition another writer keeps
    /// current</b> — Systemorph/MeshWeaver#4588. Pure, so every arm is drivable without a mesh.
    ///
    /// <para><b>Measured on memex.systemorph.com, 2026-09-17.</b> The control instance's plugin
    /// source is a registry — no <c>RepoPath</c>, so <see cref="ProvenRef"/> answers "no repository
    /// to attribute a seal to" and the lane lists at the configured ref. At 14:34:30Z it installed
    /// <c>Plugins/Hosting</c> 1.22.1 from <c>HEAD</c> into <c>Hosting</c>, a partition whose
    /// <c>Hosting/_GitSync</c> re-imports the same subdirectory at the commit sealed for the
    /// running framework — then <c>061976bc</c> (2026-09-15), which does not carry
    /// <c>Hosting/Issue/Source/FleetWatchCadence.cs</c>. Thirteen minutes later the install record
    /// claimed that file and the node was gone, and three Hosting NodeTypes could not compile. At
    /// 16:03Z the seal advanced to <c>d98fc2ac</c> and its import — a delta from the PREVIOUS seal —
    /// wrote <c>IssueLayoutAreas.cs</c> (changed between the two seals) and left
    /// <c>Test/IssueTests.cs</c> alone (unchanged between them), so the mesh kept the installer's
    /// <c>main</c> copy of the test beside the seal's copy of the view:
    /// <c>CS0117 'IssueLayoutAreas' does not contain a definition for 'Facts'</c>. The mix is the
    /// one <c>Doc/Architecture/OnePartitionOneBookkeeping</c> describes, with the two writers'
    /// roles reversed — which is why making only the INSTALLER's delta full cannot close the class:
    /// the other writer's delta has the identical blind spot.</para>
    ///
    /// <para><b>Why a hold and not a repair.</b> A rewrite cannot hold: the partition's own writer
    /// runs again minutes later and prunes or reverts whatever the sealed tree does not carry, and
    /// the next boot detects the same shortfall and rewrites it — the loop
    /// <c>CatalogLayoutAreas.SkipOrHeal</c> names ("a detection that REPEATS at an unchanged module
    /// version is a repair that did not hold"). The only non-thrashing remedy the invariant admits
    /// is one writer, and here the other writer is the one with the proven tree.</para>
    ///
    /// <para><b><see cref="PartitionContentOwner.Undetermined"/> holds too</b>, and the two
    /// directions are asymmetric on purpose: a hold that was wrong is re-derived and lifted at the
    /// next boot, while an install that was wrong has already put a tree into a partition it does
    /// not own — which no later pass can take back. "I could not tell" is never "clear to write".</para>
    /// </summary>
    /// <param name="ownership">Who keeps this package's target partition current.</param>
    /// <param name="gitRef">The ref this boot resolved — log copy, so the hold names what it held.</param>
    /// <returns>The speaking hold reason, or <c>null</c> when the install may proceed.</returns>
    internal static string? UnprovenRefHold(PartitionContentOwnershipVerdict ownership, string gitRef)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return ownership.InstallerOwnsTheContent
            ? null
            : $"{ownership.Because}. This boot could only resolve '{gitRef}', which no publication "
              + "sealed for this instance names, so landing it would put a tree that partition's "
              + "own writer does not carry into a partition it keeps current — the mix that parked "
              + "three Hosting NodeTypes on memex.systemorph.com (MeshWeaver#4588)";
    }

    /// <summary>
    /// The hold: nothing is fetched, no content is written and no module is adopted — but the
    /// package's DECLARED ACCESS is still re-asserted, because that is create-only, writes nothing
    /// in the steady state, and is what heals a partition left unreadable; withholding it would
    /// trade a content defect for an access one. Reported as a SKIP with its reason, never as a
    /// failure (a retry cannot change a seal) and never in silence.
    /// </summary>
    /// <param name="candidate">The held candidate.</param>
    /// <param name="partition">Its target partition.</param>
    /// <param name="reason">Why it is held — <see cref="UnprovenRefHold"/>'s sentence.</param>
    /// <returns>A cold observable emitting the one-skip summary exactly once.</returns>
    private IObservable<DefaultInstallSummary> HoldForThePartitionsOwnWriter(
        InstallCandidate candidate, string partition, string reason)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var package = candidate.Package;
        logger.LogWarning(
            "[DefaultInstall] {Id} → {Partition} is HELD, not installed — {Reason}. That partition's "
            + "content is delivered by its own writer at the commit sealed for this instance, and a "
            + "human's Update click stays the documented escape. Recorded as SKIPPED on {Ledger}; "
            + "nothing retries it, and the next boot re-derives this — a seal that names this "
            + "source, or a partition that stops tracking one, lifts it.",
            package.Id, partition, reason, SeedLedgerPath);
        // 🚨 RunAsSystem, never Observable.Using(ImpersonateAsSystem) (#1790): the scope opens at
        // Subscribe and closes on the way out of that same Subscribe, so the impersonation cannot
        // be left latched on whichever thread the access write happens to terminate on.
        return accessService
            .RunAsSystem(() => PackageInstaller.ReassertDeclaredAccess(hub, package, partition, logger))
            .Catch((Exception exception) =>
            {
                // A hold is not an install, so an access re-assert that could not run must not turn
                // it into a FAILED one — it is said, at Warning, and the hold stands.
                logger.LogWarning(exception,
                    "[DefaultInstall] {Id} is held, and its declared access could not be "
                    + "re-asserted: {Cause}. The hold stands; this is not a failed install.",
                    package.Id, exception.Message);
                return Observable.Return(Unit.Default);
            })
            .Select(_ => DefaultInstallSummary.Empty with
            {
                Held = [new DefaultInstallHold(package.Id, reason)],
            });
    }

    /// <summary>
    /// The install itself, once <see cref="Install"/> has established that this lane may write this
    /// partition at this ref.
    /// </summary>
    /// <param name="candidate">The candidate to land.</param>
    /// <param name="partition">Its target partition.</param>
    /// <returns>A cold observable emitting this package's summary exactly once.</returns>
    private IObservable<DefaultInstallSummary> Land(InstallCandidate candidate, string partition)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var package = candidate.Package;
        // 🚨 The AUTHORIZING PRINCIPAL of a commercial package the deployment configuration names
        // (or that such a package requires): deployment-config:{instance}, explicitly named — never
        // an ambient identity (this runs as System, which authorizes nothing). PackageEntitlement
        // verifies it against the current configuration; it is stamped on the install record
        // (AuthorizedBy, re-verified by every unattended update) and on the ledger. A free package
        // needs no principal and gets none — nothing changes for it.
        var principal = AuthorizingPrincipal(candidate);
        if (principal is not null)
            logger.LogInformation(
                "[DefaultInstall] commercial package {Id} is installed on the authority of {Principal} "
                + "({Reason}) — the deployment record is changed only by a reviewed, App-merged pull "
                + "request. Installing grants nobody access: the plan and the subscriptions decide "
                + "who reads it.", package.Id, principal, candidate.ConfigAuthority);

        // 🚨 RunAsSystem, not Observable.Using(ImpersonateAsSystem) (#1790). Rx disposes a Using's
        // resource on whichever thread TERMINATES the inner sequence — for a cross-hub install that
        // is the owning hub's response thread — so the store and the restore land on different
        // threads and the boot subscriber is left running as system-security. RunAsSystem opens the
        // scope at Subscribe and closes it on the way out of that same Subscribe; the whole cold
        // pipeline is composed inside the factory, so what is ISSUED is still issued impersonated
        // and the emission-time behaviour is unchanged.
        return accessService
            .RunAsSystem(() => CatalogLayoutAreas
                    .InstallOrUpdate(hub, candidate.Source.Source, candidate.Source.GitRef, package, logger,
                        authorizingUserId: principal,
                        // The pass proposes ONCE, at its end, checked against the whole selection.
                        proposeModuleSet: false)
                    .Take(1)
                    .Do(result => logger.LogInformation(
                        "[DefaultInstall] {Id} → {Partition}: {Written} written, {Unchanged} unchanged",
                        package.Id, partition, result.Written, result.Unchanged))
                    // The declared access is re-asserted even when nothing installed — that is what
                    // heals an instance whose partition was left unreadable, and it is free once in
                    // place.
                    .SelectMany(result => PackageInstaller
                        .EnsureDeclaredAccess(hub, package, partition, logger)
                        .Select(_ => result))
                    // 🚨 AFTER the content install, never before: the seeder re-keys each assembly
                    // under THIS instance's own node version, so the NodeType node has to exist.
                    // Run earlier and every seed declines — not corrupting, but a silent no-op that
                    // looks exactly like a registry serving nothing.
                    .SelectMany(result => AdoptPrebuilt(candidate, package)
                        .Select(_ => result)))
            .Select(result => new DefaultInstallSummary(
                Installed: result.Written > 0 ? 1 : 0,
                UpToDate: result.Written > 0 ? 0 : 1,
                Failed: 0,
                Packages: [package.Id])
            {
                Authorized = principal is null
                    ? ImmutableList<DefaultInstallAuthorization>.Empty
                    : [new DefaultInstallAuthorization(package.Id, principal, candidate.ConfigAuthority!, DateTimeOffset.UtcNow)],
            })
            // 🚨 The safety net behind TerminalSkipReason (#2536): a refusal that only becomes
            // visible AT install time — commerciality stamped registry-side after listing, or a
            // licence that requires an acceptance no boot-time principal can give — is STILL
            // terminal for this lane. Record it as a SKIP carrying the refusal's own reason, never
            // as a failure to retry: retrying an authorization refusal re-fails identically every
            // boot, and what changes it is an event (an admin install, changed terms), not a retry.
            .Catch((Exception exception) => exception
                    is not PackageAuthorizationException and not LicenseAcceptanceRequiredException
                ? Observable.Throw<DefaultInstallSummary>(exception)
                : Observable.Defer(() =>
                {
                    logger.LogWarning(
                        "[DefaultInstall] {Id} was refused and is SKIPPED, not failed — no boot "
                        + "re-attempts it (recorded on {Ledger}): {Reason}",
                        package.Id, SeedLedgerPath, exception.Message);
                    return Observable.Return(DefaultInstallSummary.Empty with
                    {
                        Skipped = [new DefaultInstallSkip(package.Id, exception.Message)],
                    });
                }))
            .Catch((Exception exception) =>
            {
                logger.LogError(exception,
                    "[DefaultInstall] installing package {Id} failed — continuing with the rest. It "
                    + "is recorded as FAILED on {Ledger}, stays OFF the seeded list, and the next "
                    + "boot re-attempts it; that retry is the repair", package.Id, SeedLedgerPath);
                // 🚨 NAMED, not just counted. The id has to travel so the ledger can keep it off
                // the seeded list — counting it and still listing it under Packages is what made a
                // failed install permanent (#2254).
                return Observable.Return(new DefaultInstallSummary(0, 0, 1, [package.Id])
                {
                    Failures = [package.Id],
                    // 🚨 …and WHY. The id alone left the ledger and the boot's summary line saying
                    // "Anthropic failed" with nothing to triage from: the cause lived only in a
                    // separate per-package line, which a reader of the summary or the ledger never
                    // sees (#5826).
                    FailureCauses = [DefaultInstallFailure.Of(package.Id, exception)],
                });
            });
    }

    /// <summary>
    /// Adopts the registry's prebuilt assemblies for this package, so the install does not pay for
    /// a compile it can skip.
    ///
    /// <para>Never fails the install. Zero adopted is the NORMAL outcome whenever the registry runs
    /// a different framework build, and compiling is the correct, always-available fallback — so a
    /// bundle that is missing, refused or unreadable is logged and stepped over, exactly like a
    /// registry that serves no bundles at all.</para>
    /// </summary>
    private IObservable<int> AdoptPrebuilt(InstallCandidate candidate, PackageManifest package)
    {
        var bundles = candidate.Source.Bundles;

        // No registry behind this source: nothing to adopt, and nothing worth a log line on boot.
        if (bundles is null)
            return Observable.Return(0);

        return AbsorbUnlessPrebuiltRequired(bundles.Adopt(package.Id), logger, package.Id);
        // A package's compiled MODULE (#1664) is deliberately NOT adopted here: the module branch
        // lives inside CatalogLayoutAreas.InstallOrUpdate — the one orchestrator this lane (and the
        // manual click, and the content auto-update) already funnels through — riding the
        // RegistryPackageSource's own Bundles handle. A second call here would land drift the
        // update policy is supposed to gate (the reconciler's module pass owns drift).
    }

    /// <summary>
    /// The absorb policy on the adoption lane: any ordinary failure is logged and stepped over —
    /// compiling is the correct, always-available fallback on a default mesh — EXCEPT the named
    /// <see cref="PrebuiltRequiredException"/>, which only exists on a mesh that opted into
    /// <see cref="PrebuiltAssemblySeeder.RequirePrebuiltConfigKey"/> and must therefore PROPAGATE:
    /// swallowing it here would restore the exact silent compile the flag forbids, one call site
    /// above where it was refused (#2193 §A). The failure surfaces in this lane's install summary,
    /// naming the package. Internal for the DefaultInstallPrebuiltPolicyTest pin
    /// (InternalsVisibleTo).
    /// </summary>
    internal static IObservable<int> AbsorbUnlessPrebuiltRequired(
        IObservable<int> adoption, ILogger? logger, string packageId) =>
        adoption.Catch((Exception exception) =>
        {
            if (exception is PrebuiltRequiredException)
                return Observable.Throw<int>(exception);
            logger?.LogInformation(exception,
                "[DefaultInstall] {Id}: no prebuilt assemblies adopted — compiling instead",
                packageId);
            return Observable.Return(0);
        });

    /// <summary>One package the default install should carry, and the source it came from.</summary>
    /// <param name="Source">The source the package was listed from.</param>
    /// <param name="Package">The package manifest.</param>
    private sealed record InstallCandidate(ConfiguredPackageSource Source, PackageManifest Package)
    {
        /// <summary>
        /// Whether a RECONCILED lane selected it — the platform's own <c>preInstalled</c> baseline,
        /// this environment's feature flags, or the operator's
        /// <see cref="PluginCatalogOptions.InstallByDefault"/> patterns over a
        /// <see cref="ConfiguredPackageSource.LocalCheckout"/> — as opposed to those same patterns
        /// over a fetched source, which seed once. A reconciled candidate is exempt from the seed
        /// ledger: it re-asserts on every boot, which is the entire difference between a policy
        /// (or a mirrored working tree) and a seed.
        /// </summary>
        public bool Reconciled { get; init; }

        /// <summary>
        /// Whether ONLY a whole-source pattern (<c>Source/*</c>) selected it — see
        /// <see cref="HeldForGovernedProvision"/>, which holds such a package back once the
        /// instance has been seeded.
        /// </summary>
        public bool WildcardOnly { get; init; }

        /// <summary>
        /// Why the DEPLOYMENT CONFIGURATION authorizes this candidate — it is named exactly in
        /// <see cref="PluginCatalogOptions.InstallByDefault"/>, or required by a package that is —
        /// or null when it does not. A commercial candidate with an authority is INSTALLED under
        /// the principal <c>deployment-config:{instance}</c> (<see cref="DeploymentConfigAuthority"/>);
        /// one without is skipped (<see cref="TerminalSkipReason(PackageManifest, bool)"/>).
        /// </summary>
        public string? ConfigAuthority { get; init; }

        /// <summary>
        /// Whether the ref this candidate would be installed at was PROVEN for this instance: the
        /// seal named a commit (<see cref="ApplyPlan"/> re-reffed the source, so it is a different
        /// instance), or the source is a working tree the operator mirrors — MeshWeaver#3359, where
        /// the operator IS the authority and there is no commit to pin.
        ///
        /// <para>🚨 <c>false</c> is the residue the Sync-Ref Contract names and does not remove: a
        /// source no seal can be attributed to — a remote registry, a registered
        /// <see cref="IPackageSource"/>, a repository this instance publishes nothing of — listed
        /// at its configured branch. <see cref="UnprovenRefHold"/> is what that residue may not do
        /// to a partition somebody else keeps current.</para>
        /// </summary>
        public bool RefIsProven { get; init; }
    }

    /// <summary>
    /// The default install run against an EXPLICIT source list — the one seam
    /// <see cref="InstallDefaults"/> and the tests share, so the selection + ordering + install
    /// behaviour is exercised against any <see cref="IPackageSource"/> rather than only a live HTTP
    /// registry. There is no second implementation behind it: production differs only in where the
    /// source list comes from (<see cref="Sources"/>).
    /// </summary>
    /// <param name="sources">The sources to list, in precedence order.</param>
    /// <param name="baseline">Whether packages declaring <c>preInstalled</c> are selected.</param>
    /// <param name="wanted">The operator's source-scoped <c>Source/Package</c> patterns.</param>
    /// <param name="composition">The environment's feature-flag composition (includes + excludes).</param>
    /// <param name="defaultApps">Whether packages declaring <c>defaultApp</c> are selected.</param>
    internal IObservable<DefaultInstallSummary> InstallFrom(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted,
        FeatureComposition? composition = null,
        bool defaultApps = false) =>
        Candidates(
                sources, baseline, defaultApps, wanted,
                Parse((composition ?? FeatureComposition.Empty).Included),
                Parse((composition ?? FeatureComposition.Empty).Excluded))
            .SelectMany(selection => InstallAll(selection.Candidates)
                .Select(summary => summary with
                {
                    ListingIncomplete = selection.ListingIncomplete,
                    SourceUnreachable = selection.SourceUnreachable,
                }));

    /// <summary>
    /// 🚨 <b>RE-ASSERT already-installed packages through the ONE install funnel</b> —
    /// MeshWeaver#4812, the half of #3485 that had no caller.
    ///
    /// <para>#3485 made <c>CatalogLayoutAreas.InstallOrUpdate</c>'s up-to-date exit OBSERVE the
    /// mesh and heal an incomplete install, and put the sweep that finds them into the boot repair
    /// pass — the one complete inventory of what is installed. But the funnel only runs for a
    /// package some lane VISITS: the platform baseline and the environment's flags on every boot,
    /// the operator's seed once, the update reconciler only when the module hash MOVED (an equal
    /// hash returns before the funnel), and a human's click. A package installed by hand from a
    /// source that has not changed since is visited by nothing, so "reinstalling it now repairs
    /// it" was true only of a click nobody made — measured on memex.meshweaver.cloud, where the
    /// sweep named the same shortfall on every boot for three weeks.</para>
    ///
    /// <para><b>What this is and is not.</b> It is the boot lane's own machinery — the configured
    /// sources at their PROVEN ref (the Sync-Ref Contract, #4259), the ownership holds (#4588,
    /// #4625), <c>RunAsSystem</c>, the declared-access re-assert, the prebuilt adoption — applied to
    /// an explicit set of installed packages, so the repair lands exactly where and how the boot
    /// install would. It is NOT an update lane: a named package is re-asserted only where the
    /// source still serves the module version the record carries, so the funnel can only skip or
    /// heal, never move the package to a newer build. A moved hash is an UPDATE, and whether an
    /// update lands unattended is the package's own policy (<see cref="PackageUpdateReconciler"/>)
    /// or a human's click — both of which restore absent declared nodes as they go (#4259). A
    /// package no configured source lists any more cannot be re-fetched by anything, and is named
    /// so: its record is an orphan for the admin list.</para>
    ///
    /// <para>The seed ledger is deliberately not written: every package here is already installed,
    /// so nothing about "seeded once" changes. Feature-flag exclusions are not applied either — an
    /// installed package is installed; restoring what it lost is not installing what the operator
    /// excluded.</para>
    /// </summary>
    /// <param name="targets">The installed packages to re-assert — each with the PARTITION and
    /// module version its install record carries. A record with no module identity cannot be
    /// re-asserted (the caller says so).</param>
    /// <returns>A cold observable emitting the pass's summary exactly once.</returns>
    internal IObservable<DefaultInstallSummary> ReassertInstalled(IReadOnlyList<ReassertTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return Observable.Return(DefaultInstallSummary.Empty);
        var recordedModuleVersions = targets.ToImmutableDictionary(
            t => t.PackageId, t => t.ModuleVersion, StringComparer.Ordinal);
        var recordedPartitions = targets.ToImmutableDictionary(
            t => t.PackageId, t => t.Partition, StringComparer.Ordinal);
        var recordedSources = targets.ToImmutableDictionary(
            t => t.PackageId, t => t.Source, StringComparer.Ordinal);
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        return Sources(options).SelectMany(sources =>
        {
            if (sources.Count == 0)
            {
                logger.LogWarning(
                    "[PackageRepair] {Count} installed package(s) need re-asserting but this "
                    + "installation has NO package sources — nothing can re-fetch them: [{Ids}]. "
                    + "Configure PluginCatalog:Sources or PluginCatalog:RegistryUrl.",
                    recordedModuleVersions.Count, string.Join(", ", recordedModuleVersions.Keys));
                return Observable.Return(DefaultInstallSummary.Empty);
            }
            return sources
                .Select(ListAtProvenRef)
                .ToObservable()
                .Concat()
                .ToList()
                .SelectMany(answers =>
                {
                    var listingIncomplete = answers.Any(a => a.Failed);
                    var sourceUnreachable = answers.Any(a => a.Failed && !a.Held);
                    // Bound by (source, id), not id alone, and by PROVENANCE, not by what a manifest
                    // says (#6512): a record that names its source is re-asserted only from a listing
                    // whose source is trusted to be that one — a registry's stamp, or the name the
                    // source was configured under. A directly read repository can AUTHOR any
                    // `source` in its package.json, so "the candidate says it is from X" would let
                    // another repository listing the same id land its files over the installed
                    // package. Only a legacy record that names no source takes the first listing.
                    var catalog = answers
                        .SelectMany(a => a.Candidates)
                        .GroupBy(c => c.Package.Id, StringComparer.Ordinal)
                        .ToImmutableDictionary(
                            g => g.Key,
                            g => RepairCandidate(g.ToList(), recordedSources.GetValueOrDefault(g.Key)),
                            StringComparer.Ordinal);

                    var atRecordedHash = ImmutableList.CreateBuilder<InstallCandidate>();
                    foreach (var (id, recorded) in recordedModuleVersions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        if (catalog.TryGetValue(id, out var bound) && bound is null)
                        {
                            logger.LogWarning(
                                "[PackageRepair] {Id} is NOT re-asserted: its record names source "
                                + "'{Source}', and no listing whose provenance is trusted to be that "
                                + "source serves it — only listings that merely CLAIM it, or other "
                                + "sources. A repair never lands another repository's files over the "
                                + "installed package (MeshWeaver#6512).",
                                id, recordedSources.GetValueOrDefault(id));
                            continue;
                        }
                        if (bound is not { } candidate)
                        {
                            // Say WHICH of two things this is: the listing was partial (a held or
                            // failed source may well be the one that serves it), or every source
                            // answered and none serves it any more — a retired module, whose
                            // record is an orphan.
                            logger.LogWarning(listingIncomplete
                                ? "[PackageRepair] {Id} cannot be re-asserted this boot: no source "
                                  + "that answered lists it, and at least one source was held or "
                                  + "failed to list — re-attempted at the next process start."
                                : "[PackageRepair] {Id} cannot be re-asserted: no configured source "
                                  + "lists it any more. If the module was retired, its install "
                                  + "record is an orphan — remove it from Catalog → orphaned "
                                  + "install records (MeshWeaver#4812).", id);
                            continue;
                        }
                        // 🚨 The repair lands where the SWEEP LOOKED (review on #4985). The
                        // candidate's targetPartition is what InstallAll writes into; where it has
                        // moved since the record was stamped, a "repair" would populate a partition
                        // nobody observed and leave the observed one short. A moved target is an
                        // update decision, not a repair.
                        var candidatePartition = PackageInstaller.TargetPartitionOf(id, candidate.Package);
                        if (!string.Equals(candidatePartition, recordedPartitions[id], StringComparison.Ordinal))
                        {
                            logger.LogWarning(
                                "[PackageRepair] {Id} is NOT re-asserted unattended: its record was "
                                + "installed into '{Recorded}' but the source now targets "
                                + "'{Served}', so a repair would write a partition the sweep never "
                                + "observed. A human's Update click is the path (MeshWeaver#4812).",
                                id, recordedPartitions[id], candidatePartition);
                            continue;
                        }
                        if (!string.Equals(candidate.Package.ModuleVersion, recorded, StringComparison.Ordinal))
                        {
                            logger.LogWarning(
                                "[PackageRepair] {Id} is NOT re-asserted unattended: its record "
                                + "carries module {Recorded} but the source now serves {Served}, so a "
                                + "repair would be an UPDATE. Whether that lands unattended is the "
                                + "package's own update policy (the update reconciler applies "
                                + "'Auto'); a human's Update click applies the rest — and both "
                                + "restore absent declared nodes as they go (MeshWeaver#4259). "
                                + "Until then the shortfall stands as reported.",
                                id, recorded, candidate.Package.ModuleVersion ?? "(none)");
                            continue;
                        }
                        // A commercial package the deployment configuration authorized is REPAIRED
                        // under that same authority — re-derived here, after the default pass has
                        // published its requirement closure — instead of being classified as
                        // unauthorized and skipped on every boot. The gate re-verifies it.
                        atRecordedHash.Add(candidate with
                        {
                            Reconciled = true,
                            // …and the configuration's authority only from the RECORDED source:
                            // another source listing the same id derives none.
                            ConfigAuthority = ProvenanceIsTrusted(candidate)
                                              && SameSource(candidate.Package.Source, recordedSources.GetValueOrDefault(id))
                                ? hub.ServiceProvider.GetService<DeploymentConfigAuthority>()?.Because(candidate.Package)
                                : null,
                        });
                    }
                    if (atRecordedHash.Count == 0)
                        return Observable.Return(DefaultInstallSummary.Empty with
                        {
                            ListingIncomplete = listingIncomplete,
                            SourceUnreachable = sourceUnreachable,
                        });

                    logger.LogInformation(
                        "[PackageRepair] re-asserting {Count} installed package(s) at their recorded "
                        + "module version through the install lane — [{Ids}]. The funnel observes "
                        + "the mesh and heals what is short; a package found whole is skipped "
                        + "(MeshWeaver#4812).",
                        atRecordedHash.Count, string.Join(", ", atRecordedHash.Select(c => c.Package.Id)));
                    var byId = atRecordedHash.ToDictionary(c => c.Package.Id, StringComparer.Ordinal);
                    var ordered = PackageDependencyGraph
                        .InDependencyOrder(atRecordedHash.Select(c => c.Package).ToList(), logger)
                        .Where(p => byId.ContainsKey(p.Id))
                        .Select(p => byId[p.Id])
                        .ToList();
                    return InstallAll(ordered)
                        .Select(summary => summary with
                        {
                            ListingIncomplete = listingIncomplete,
                            SourceUnreachable = sourceUnreachable,
                        });
                });
        });
    }

    /// <summary>
    /// One installed package to re-assert, as its install RECORD describes it
    /// (<see cref="ReassertInstalled"/>): the partition the sweep observed, and the module version
    /// the funnel may skip-or-heal at.
    /// </summary>
    /// <param name="PackageId">The package id — the record's node id.</param>
    /// <param name="Partition">The partition the record was installed into.</param>
    /// <param name="ModuleVersion">The module version the record carries.</param>
    /// <param name="Source">The source the install record was stamped with — the repair re-asserts
    /// from a listing of THAT source when one lists the package, and derives the deployment
    /// configuration's authority only from it. Null when the record names none.</param>
    internal sealed record ReassertTarget(string PackageId, string Partition, string ModuleVersion, string? Source = null);

    /// <summary>
    /// Runs the PRODUCTION default-install pass on demand — the identical selection, ordering and
    /// install the boot pass performs, reading this installation's real configuration. Cold: the
    /// work runs on Subscribe. Exists so a test can assert the second boot writes nothing, and so
    /// the opt-out is exercised through the real decision rather than a re-implementation of it.
    /// </summary>
    internal IObservable<DefaultInstallSummary> RunDefaultInstall() =>
        InstallDefaults(hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions());

    /// <summary>
    /// 🚨 <b>The repair of a default package the boot could not install, without waiting for the next
    /// boot</b> (MeshWeaver#5825). When the last pass of this process left something undone
    /// (<see cref="LeftUndone"/>: a failed package, a source that did not answer its listing, or the
    /// pass itself faulting — never a source that is HELD, which releases at the next process start
    /// and is not listed again before it), runs the default-install pass again — the identical selection and install
    /// (<see cref="InstallDefaults"/>), which re-attempts what the ledger records as FAILED and skips
    /// what is installed. When the last pass left nothing undone, does nothing at all: no source is
    /// listed.
    ///
    /// <para><b>Who calls it, and why not a timer.</b> <see cref="RegistryUpdateReconciler"/>, at the
    /// end of a reconcile that a successful feed read made possible, on its serialized lane. A boot
    /// install fails because a registry did not answer; the registry answering is the event that
    /// changes the outcome, and the reconciler already observes it. Before this, the only repair was
    /// the next boot — measured on the control instance, 2026-09-27: <c>Anthropic</c> stayed
    /// uninstalled after one stalled HTTPS read, on an instance whose next boot is whenever the next
    /// image rolls.</para>
    ///
    /// <para>A package that fails again stays recorded and is re-attempted on the next such
    /// reconcile; each attempt logs its cause at Error exactly as the boot pass does, so a package
    /// that cannot be installed stays loud. Never faults.</para>
    /// </summary>
    /// <param name="because">The event that prompted the repair, for the log.</param>
    /// <returns>Cold; emits once when the repair pass has ended, or immediately when there was
    /// nothing to repair.</returns>
    internal IObservable<Unit> RetryFailedDefaults(string because) =>
        Observable.Defer(() =>
        {
            if (repairOwed is not { } undone)
                return Observable.Return(Unit.Default);
            logger.LogInformation(
                "[DefaultInstall] running the default install again: in the last pass {Undone} — {Because}.",
                undone, because);
            return RunDefaultInstall()
                .Do(summary =>
                {
                    repairOwed = LeftUndone(summary);
                    if (summary.Failed > 0)
                        logger.LogError(
                            "[DefaultInstall] the repair pass still has FAILURES: {Summary}. They stay "
                            + "recorded on {Ledger} and are re-attempted when a registry next answers a "
                            + "feed read, and on the next boot.", summary, SeedLedgerPath);
                    else
                        logger.LogInformation("[DefaultInstall] the repair pass reconciled: {Summary}", summary);
                })
                .Select(_ => Unit.Default)
                .DefaultIfEmpty(Unit.Default)
                .Catch((Exception ex) =>
                {
                    logger.LogError(ex,
                        "[DefaultInstall] the repair pass did not complete — it is re-attempted when a "
                        + "registry next answers a feed read, and on the next boot.");
                    return Observable.Return(Unit.Default);
                });
        });
}

/// <summary>
/// What the default-install SEED has already delivered to this installation, ever.
///
/// <para>🚨 This is the difference between "repair" and "fight the operator". A package absent
/// from the ledger has never been seeded, so installing it repairs a bad config, a failed install
/// or a package newly added to the repo. A package ON the ledger is left alone even when it is
/// absent, because the only way it can be absent is that someone removed it deliberately.</para>
/// </summary>
public record DefaultInstallLedger
{
    /// <summary>Package ids the seed has DELIVERED (installed, or already current). Append-only in
    /// practice. 🚨 A package whose install FAILED never appears here — it belongs on
    /// <see cref="Failed"/> and is retried next boot; that retry is the repair (#2254).</summary>
    public ImmutableList<string> Seeded { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Package ids a default-install pass selected and could NOT deliver, and which no later pass
    /// has delivered since — i.e. the default packages this installation is currently MISSING.
    ///
    /// <para>Exists because the alternative is grepping a boot log: a package dropped by a
    /// NodeOps-hub timeout left nothing durable behind, so an instance silently short of a
    /// declared package was undetectable (#2254).</para>
    ///
    /// <para>Written as a SNAPSHOT of the last pass that attempted anything, not as an accumulating
    /// list: an id drops off the moment a pass delivers it AND the moment it stops being declared
    /// by default. So a non-empty list always means "declared, attempted, and still missing right
    /// now" — never "was once a problem".</para>
    /// </summary>
    public ImmutableList<string> Failed { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// WHY each of <see cref="Failed"/> could not be delivered on the last pass that attempted
    /// anything — the exception's type and message, per package. A SNAPSHOT with exactly
    /// <see cref="Failed"/>'s semantics, so an entry leaves the moment its package is delivered or
    /// stops being declared.
    ///
    /// <para>Exists because a failed id with no cause is the silent half of a skip: the ledger said
    /// <c>failed: ["Anthropic"]</c>, the boot summary said <c>FAILED: [Anthropic]</c>, and the cause
    /// — a registry fetch that never sent its headers — was only in a separate per-package line
    /// nobody reading the ledger sees (#5826).</para>
    /// </summary>
    public ImmutableList<DefaultInstallFailure> FailureCauses { get; init; } =
        ImmutableList<DefaultInstallFailure>.Empty;

    /// <summary>
    /// The declared default packages the last pass deliberately did NOT act on, with the reason
    /// each — the terminal-skip classification (#2536): an authorization the unattended lane can
    /// never obtain. 🚨 NOT failures — a failure is retried next boot because a retry can repair
    /// it; a skip is a standing decision no retry can change, so nothing re-attempts it and no
    /// per-boot error is raised. What changes it is an event the next pass observes: a Global
    /// Admin installs the package from the catalog, or its terms change (it stops being
    /// commercial) — the classification is re-derived from the current manifest each pass.
    ///
    /// <para>A SNAPSHOT with the same semantics as <see cref="Failed"/>: an entry drops off the
    /// moment the package stops being declared by default or stops being refusable. A skipped
    /// package is never <see cref="Seeded"/> — so should it ever become free, the seed lane
    /// installs it like any package it has not delivered yet.</para>
    /// </summary>
    public ImmutableList<DefaultInstallSkip> Skipped { get; init; } =
        ImmutableList<DefaultInstallSkip>.Empty;

    /// <summary>
    /// The packages the last pass HELD, with the reason each (MeshWeaver#4588) — another writer
    /// keeps that partition's content current and the pass could prove no commit for the ref it
    /// resolved. A SNAPSHOT, like <see cref="Skipped"/>, but of a transient fact rather than a
    /// standing one: the next boot re-derives it from the seal and the partition, so an entry
    /// disappears by itself the moment either changes. Kept apart from <see cref="Skipped"/> so a
    /// reader is never told "authorization, not retried" about a hold that lifts on its own.
    /// </summary>
    public ImmutableList<DefaultInstallHold> Held { get; init; } = ImmutableList<DefaultInstallHold>.Empty;

    /// <summary>
    /// The default-set packages the registry refused to this instance's PLAN on the last pass
    /// (#4097), typed. A snapshot with <see cref="Skipped"/>'s semantics: an entry drops off the
    /// moment the registry stops refusing the package (a plan upgrade) or stops declaring it in
    /// the default set. Every entry is also in <see cref="Skipped"/> with the same sentence.
    /// </summary>
    public ImmutableList<PlanTierRefusal> TierRefused { get; init; } = ImmutableList<PlanTierRefusal>.Empty;

    /// <summary>
    /// The COMMERCIAL packages this lane installed on the deployment configuration's authority —
    /// named exactly in <see cref="PluginCatalogOptions.InstallByDefault"/>, or required by one
    /// that is — with the principal (<c>deployment-config:{instance}</c>), the reason and when.
    /// The audit trail that answers "who decided this priced package lands here": the deployment
    /// record, changed by a reviewed, App-merged pull request. CUMULATIVE, not a snapshot: it
    /// records a decision that was acted on, which stays true after the package is removed (and
    /// the seeded entry still keeps the seed from re-installing it).
    /// </summary>
    public ImmutableList<DefaultInstallAuthorization> Authorized { get; init; } =
        ImmutableList<DefaultInstallAuthorization>.Empty;

    /// <summary>When the ledger last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// What one default-install pass did: how many packages were written, how many were already
/// current, how many failed, and which packages the pass covered. The BOOT pass's summary is
/// emitted once on <see cref="InstanceAutoRegistrationService.Completed"/>, so both the boot log
/// line and a test read the same outcome. A later repair pass
/// (<c>InstanceAutoRegistrationService.RetryFailedDefaults</c>) produces one too and does NOT emit
/// it there — <c>Completed</c> stays the boot's answer; the repair's outcome is its log line and
/// the ledger.
/// </summary>
/// <param name="Installed">Packages that actually wrote content this pass.</param>
/// <param name="UpToDate">Packages already at the catalog's content version.</param>
/// <param name="Failed">Packages whose install threw (logged, stepped over).</param>
/// <param name="Packages">The package ids the pass ATTEMPTED, in install order — successes AND
/// failures. 🚨 Never the ledger's input: use <see cref="Delivered"/> for that.</param>
public readonly record struct DefaultInstallSummary(
    int Installed, int UpToDate, int Failed, ImmutableList<string> Packages)
{
    /// <summary>
    /// The ids of the packages that FAILED this pass — the named subset of
    /// <see cref="Packages"/> that <see cref="Failed"/> only counts.
    ///
    /// <para>🚨 Without the names, "covered" and "delivered" were the same list and a failed
    /// package was ledgered as seeded, so the seed lane skipped it forever and the boot-log line
    /// was the only trace it had ever been attempted (#2254).</para>
    /// </summary>
    public ImmutableList<string> Failures { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// WHY each of <see cref="Failures"/> failed — one entry per failed package, carrying the
    /// exception's type and message. The ledger records it, and the summary line names it, so a
    /// missing package is diagnosable from either without the per-package log line (#5826).
    /// </summary>
    public ImmutableList<DefaultInstallFailure> FailureCauses { get; init; } =
        ImmutableList<DefaultInstallFailure>.Empty;

    /// <summary>
    /// The packages this pass deliberately did NOT attempt, with the reason each — the
    /// terminal-skip classification (#2536): an authorization the unattended installer can never
    /// obtain (a commercial package with no authorizing principal, a licence nobody is present to
    /// accept). Distinct from <see cref="Failures"/> in every consequence: a failure was attempted,
    /// errored, and is retried next boot (that retry is the repair); a skip is a standing decision,
    /// re-derived from the current manifest each pass and never retried. A skip is counted in
    /// neither <see cref="Failed"/> nor <see cref="Packages"/>, so it can neither be ledgered as
    /// seeded nor reported as a failure.
    /// </summary>
    public ImmutableList<DefaultInstallSkip> Skipped { get; init; } =
        ImmutableList<DefaultInstallSkip>.Empty;

    /// <summary>
    /// The packages this pass HELD, with the reason each (MeshWeaver#4588) — it may not write that
    /// partition at the ref it could resolve, because another writer keeps the content current.
    ///
    /// <para>🚨 Deliberately NOT <see cref="Skipped"/>, and the difference is the whole point of
    /// keeping two lists: a skip is a STANDING decision about an authorization this lane can never
    /// hold, and the summary says so ("authorization, not retried"); a hold is a fact about THIS
    /// boot's ref and this partition's writer, re-derived from scratch on the next one. A seal that
    /// names this source, or a partition that stops being written by anything else, lifts it with
    /// no retry and no human. Spelling the two the same way would advertise a permanent refusal
    /// where there is a transient one — the mirror of the #2536 mistake that made this lane say
    /// "failed" about a standing decision.</para>
    /// </summary>
    public ImmutableList<DefaultInstallHold> Held { get; init; } = ImmutableList<DefaultInstallHold>.Empty;

    /// <summary>
    /// The default-set packages the registry REFUSED to this instance's plan tier this pass
    /// (#4097), typed — package, module, required tier, instance plan. Every one is also in
    /// <see cref="Skipped"/> with the same sentence as its reason; this is the data the ledger
    /// and the activation record keep, so <c>/health</c>, the package card and the self-updater
    /// can name the plan instead of the consequence.
    /// </summary>
    public ImmutableList<PlanTierRefusal> TierRefused { get; init; } = ImmutableList<PlanTierRefusal>.Empty;

    /// <summary>
    /// The commercial packages this pass DELIVERED on the deployment configuration's authority,
    /// with the principal and the reason — what the ledger's
    /// <see cref="DefaultInstallLedger.Authorized"/> trail records.
    /// </summary>
    public ImmutableList<DefaultInstallAuthorization> Authorized { get; init; } =
        ImmutableList<DefaultInstallAuthorization>.Empty;

    /// <summary>
    /// The modules this pass's MODULE PHASE landed ahead of any package content (the platform
    /// bootstrap — <c>InstanceAutoRegistrationService.LandModulesFirst</c>), by entry-assembly name.
    /// A newly landed module loads at a restart once a proposed module set names it, so a package whose nodes need its NodeTypes
    /// is in <see cref="Failures"/> of THIS pass and is delivered by the pass after that restart.
    /// </summary>
    public ImmutableList<string> ModulesLanded { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// What THIS pass decided about the mesh's module set, once, at its end (policy
    /// <c>default-install-modules-first</c>): <c>proposed: …</c>, <c>unchanged: …</c>,
    /// <c>withheld: …</c> (a carrier the pass could not clear) or <c>refused: …</c> (a declared floor,
    /// an unreadable record). Null when the pass ran no module wave. Other lanes (the reconciler's
    /// wave close) propose on their own; this is this lane's verdict only.
    /// </summary>
    public string? ModuleSetVerdict { get; init; }

    /// <summary>
    /// 🚨 True when at least one source's listing FAILED this pass (#4097), so what the pass did
    /// not see is unknown rather than absent. <see cref="TierRefused"/> is then a partial answer:
    /// the ledger keeps its previous refusals and the activation markers are left standing,
    /// because a registry that was down at boot has not lifted anybody's plan.
    /// </summary>
    public bool ListingIncomplete { get; init; }

    /// <summary>
    /// True when at least one source did not ANSWER its listing this pass — the part of
    /// <see cref="ListingIncomplete"/> that a later pass of the same process can change, and so the
    /// part <c>InstanceAutoRegistrationService.RetryFailedDefaults</c> repairs. A source that is
    /// HELD makes the listing incomplete without setting this: a hold releases at the next process
    /// start, so a repair pass neither waits on it nor lists it.
    /// </summary>
    public bool SourceUnreachable { get; init; }

    /// <summary>
    /// The ids this pass actually DELIVERED — installed or already current. The ledger's input:
    /// what the seed may stop re-asserting.
    /// </summary>
    public ImmutableList<string> Delivered
        => (Packages ?? ImmutableList<string>.Empty)
            .RemoveRange(Failures ?? ImmutableList<string>.Empty);

    /// <summary>
    /// Exactly ONE cause per package in <see cref="Failures"/>, in ordinal package order: the first
    /// one <see cref="FailureCauses"/> names for it, or <see cref="DefaultInstallFailure.Unrecorded"/>
    /// when none was recorded. What the ledger stores and the summary line prints. Pure.
    ///
    /// <para>🚨 The two lists are filled by whoever records a failure, and nothing in the type ties
    /// them together: today ONE site does (the per-package catch of the default install) and it
    /// fills both, but a second site that named a failed package and forgot its cause would put
    /// back the exact silence #5826 removed — a failed id with nothing to triage from — and neither
    /// the ledger nor the summary line would show the gap. Deriving the causes FROM the failures
    /// makes that gap an entry that says so. A cause for a package that did not fail is dropped:
    /// the ledger's causes have exactly <c>Failed</c>'s semantics.</para>
    /// </summary>
    /// <returns>One cause per failed package.</returns>
    internal ImmutableList<DefaultInstallFailure> CausePerFailure()
    {
        var recorded = (FailureCauses ?? ImmutableList<DefaultInstallFailure>.Empty)
            .GroupBy(f => f.Package, StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return (Failures ?? ImmutableList<string>.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => recorded.GetValueOrDefault(id) ?? DefaultInstallFailure.Unrecorded(id))
            .ToImmutableList();
    }

    /// <summary>A pass that covered nothing.</summary>
    public static DefaultInstallSummary Empty { get; } = new(0, 0, 0, ImmutableList<string>.Empty);

    /// <summary>Folds one package's outcome into the running total.</summary>
    public DefaultInstallSummary Add(DefaultInstallSummary other) => new(
        Installed + other.Installed,
        UpToDate + other.UpToDate,
        Failed + other.Failed,
        Packages.AddRange(other.Packages))
    {
        Failures = (Failures ?? ImmutableList<string>.Empty)
            .AddRange(other.Failures ?? ImmutableList<string>.Empty),
        FailureCauses = (FailureCauses ?? ImmutableList<DefaultInstallFailure>.Empty)
            .AddRange(other.FailureCauses ?? ImmutableList<DefaultInstallFailure>.Empty),
        Skipped = (Skipped ?? ImmutableList<DefaultInstallSkip>.Empty)
            .AddRange(other.Skipped ?? ImmutableList<DefaultInstallSkip>.Empty),
        Held = (Held ?? ImmutableList<DefaultInstallHold>.Empty)
            .AddRange(other.Held ?? ImmutableList<DefaultInstallHold>.Empty),
        TierRefused = (TierRefused ?? ImmutableList<PlanTierRefusal>.Empty)
            .AddRange(other.TierRefused ?? ImmutableList<PlanTierRefusal>.Empty),
        Authorized = (Authorized ?? ImmutableList<DefaultInstallAuthorization>.Empty)
            .AddRange(other.Authorized ?? ImmutableList<DefaultInstallAuthorization>.Empty),
        ModulesLanded = (ModulesLanded ?? ImmutableList<string>.Empty)
            .AddRange(other.ModulesLanded ?? ImmutableList<string>.Empty),
        ModuleSetVerdict = other.ModuleSetVerdict ?? ModuleSetVerdict,
        ListingIncomplete = ListingIncomplete || other.ListingIncomplete,
        SourceUnreachable = SourceUnreachable || other.SourceUnreachable,
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Installed} installed, {UpToDate} up to date, {Failed} failed "
        + $"[{string.Join(", ", Packages)}]"
        + (Failures is { Count: > 0 } f ? $" — FAILED: [{string.Join(", ", f)}]" : "")
        + (CausePerFailure() is { Count: > 0 } c
            ? $" — CAUSES: [{string.Join("; ", c.Select(x => $"{x.Package}: {x.Cause}"))}]"
            : "")
        + (Skipped is { Count: > 0 } s
            ? $" — SKIPPED (authorization, not retried): [{string.Join(", ", s.Select(x => x.Package))}]"
            : "")
        + (Authorized is { Count: > 0 } a
            ? " — AUTHORIZED by the deployment configuration: "
              + $"[{string.Join(", ", a.Select(x => $"{x.Package} ({x.InstalledBy})"))}]"
            : "")
        + (Held is { Count: > 0 } h
            ? " — HELD (another writer owns the partition; re-derived next boot): "
              + $"[{string.Join(", ", h.Select(x => x.Package))}]"
            : "")
        + (ModulesLanded is { Count: > 0 } m
            ? $" — MODULES LANDED ahead of content: [{string.Join(", ", m)}]"
            : "")
        + (ModuleSetVerdict is { Length: > 0 } v ? $" — MODULE SET {v}" : "");
}

/// <summary>
/// One package a default-install pass attempted and could NOT deliver, and why (#5826) — recorded
/// on the <see cref="DefaultInstallLedger"/> beside the failed id, so the cause outlives the boot
/// log line that carried it. A failure is retried by the next pass; this says what the last
/// attempt ran into.
/// </summary>
/// <param name="Package">The package id.</param>
/// <param name="Cause">The exception's type and message, innermost cause appended when it differs.</param>
public sealed record DefaultInstallFailure(string Package, string Cause)
{
    /// <summary>The longest cause kept — a ledger entry, not a stack trace.</summary>
    internal const int MaxCauseLength = 600;

    /// <summary>What <see cref="Unrecorded"/> says in place of a cause.</summary>
    internal const string UnrecordedCause =
        "no cause was recorded — the site that marked this package failed did not say why";

    /// <summary>
    /// The entry for a package that was recorded as failed with NO cause beside it — a defect in
    /// whatever recorded the failure, made visible instead of leaving a failed id with nothing to
    /// triage from (<see cref="DefaultInstallSummary.CausePerFailure"/>). Pure.
    /// </summary>
    /// <param name="package">The package id.</param>
    /// <returns>The ledger entry.</returns>
    public static DefaultInstallFailure Unrecorded(string package) => new(package, UnrecordedCause);

    /// <summary>
    /// The failure of <paramref name="package"/> as <paramref name="exception"/> describes it: the
    /// outer type and message, and the innermost exception's when it is a different one (a Polly
    /// timeout wraps the socket read that actually stalled). Bounded to
    /// <see cref="MaxCauseLength"/>. Pure.
    /// </summary>
    /// <param name="package">The package id.</param>
    /// <param name="exception">What the install threw.</param>
    /// <returns>The ledger entry.</returns>
    public static DefaultInstallFailure Of(string package, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var cause = $"{exception.GetType().Name}: {exception.Message}";
        var inner = exception;
        while (inner.InnerException is { } next)
            inner = next;
        if (!ReferenceEquals(inner, exception))
            cause += $" ← {inner.GetType().Name}: {inner.Message}";
        return new DefaultInstallFailure(package,
            cause.Length <= MaxCauseLength ? cause : cause[..MaxCauseLength] + "…");
    }
}

/// <summary>
/// One package this unattended pass did not write, because it may not write that partition at the
/// ref it could resolve (MeshWeaver#4588): another writer keeps the content current and this boot
/// could prove no commit. Recorded on the <see cref="DefaultInstallLedger"/> beside the skips so
/// the decision is diagnosable without grepping a boot log.
///
/// <para>🚨 A hold is NOT a <see cref="DefaultInstallSkip"/>. A skip is an authorization this lane
/// can never obtain — standing, and nothing but an event outside it changes the answer. A hold is
/// about THIS boot's ref and THIS partition's writer: it is re-derived from scratch every pass, so
/// a seal that names the source, or a partition that stops being written by anything else, lifts it
/// with no retry and nobody's intervention. Nor is it a failure: retrying cannot prove a ref.</para>
/// </summary>
/// <param name="Package">The package id.</param>
/// <param name="Reason">Why it was held — who owns the partition's content, and which ref this boot
/// could resolve.</param>
public sealed record DefaultInstallHold(string Package, string Reason);

/// <summary>
/// One package the unattended default install deliberately does not act on, and why (#2536): the
/// declared default set names it, but acting on it needs an authorization this lane can never hold
/// — a commercial package with no authorizing principal, or a licence nobody is present to accept.
///
/// <para>Recorded on the <see cref="DefaultInstallLedger"/> so the standing decision is durable
/// and diagnosable without grepping a boot log. It does NOT mean the package is absent: a Global
/// Admin may well have installed it through the catalog — it means the unattended lane does not
/// manage it (neither installs nor auto-updates it), which stays true either way.</para>
/// </summary>
/// <param name="Package">The package id.</param>
/// <param name="Reason">The speaking refusal reason (see <see cref="PackageEntitlement.Reason"/>).</param>
public sealed record DefaultInstallSkip(string Package, string Reason);

/// <summary>
/// One COMMERCIAL package the unattended default install INSTALLED on the deployment
/// configuration's authority — recorded on the <see cref="DefaultInstallLedger"/> so the audit
/// trail names who decided it lands on this instance.
/// </summary>
/// <param name="Package">The package id.</param>
/// <param name="InstalledBy">The authorizing principal — <c>deployment-config:{instance}</c>
/// (<see cref="DeploymentConfigAuthority.Principal"/>), the same value stamped on the install
/// record's <see cref="PackageManifest.AuthorizedBy"/>.</param>
/// <param name="Reason">Why the configuration authorizes it: named in
/// <c>PluginCatalog:InstallByDefault</c> as <c>Source/Package</c>, or required by a package that is.</param>
/// <param name="InstalledAt">When the pass that installed it ran.</param>
public sealed record DefaultInstallAuthorization(
    string Package, string InstalledBy, string Reason, DateTimeOffset InstalledAt);
