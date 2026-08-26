using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
/// Resolves the token a consumer presents to a registry: an explicitly configured token always
/// wins; otherwise the stored <see cref="PluginRegistryCredential"/> from a first-startup
/// auto-registration, decrypted; otherwise empty (unauthenticated — only an open dev/e2e registry
/// answers). Reads run as System: the credential is deployment infrastructure in the Admin
/// partition, not something the browsing user could (or should) read.
/// </summary>
public sealed class RegistryTokenResolver(IMessageHub hub, ILogger<RegistryTokenResolver> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The effective token for <paramref name="registry"/>. Cold; emits once.</summary>
    public IObservable<string> ResolveToken(PluginRegistryReference registry)
    {
        if (!string.IsNullOrWhiteSpace(registry.Token))
            return Observable.Return(registry.Token.Trim());

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
    public void Dispose()
    {
        subscriptions.Dispose();
        completed.Dispose();
    }

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
                logger.LogError(ex,
                    "First-startup instance registration against {Url} failed (401 = invalid or "
                    + "revoked bootstrap key, 409 = instance id already taken). Continuing to the "
                    + "install phase, which uses whatever key this installation already holds.",
                    registry.Url);
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
                if (summary.Failed > 0)
                    logger.LogError(
                        "[DefaultInstall] reconciled with FAILURES: {Summary}. This installation is "
                        + "missing {Count} declared package(s) — they are recorded on {Ledger} and "
                        + "re-attempted on the next boot.",
                        summary, summary.Failed, SeedLedgerPath);
                else
                    logger.LogInformation("[DefaultInstall] reconciled: {Summary}", summary);
            })
            // A failed pass must not leave the completion signal hanging forever — report it as a
            // pass that installed nothing, having already logged the cause at Error.
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "First-startup plugin provisioning failed; no retry is attempted.");
                return Observable.Return(DefaultInstallSummary.Empty);
            })
            .Subscribe(completed));
    }

    /// <summary>
    /// Phase 1 — make sure this installation holds an instance key: auto-register when a bootstrap
    /// key is configured and nothing is stored yet. Emits once when a credential is in place (or
    /// when none is needed because a token is configured explicitly).
    /// </summary>
    private IObservable<Unit> EnsureRegistered(PluginCatalogOptions options, PluginRegistryReference registry)
    {
        var bootstrapKey = options.BootstrapKey?.Trim() ?? "";
        if (bootstrapKey.Length == 0)
            return Observable.Return(Unit.Default);    // manual token (or none) — nothing to register

        if (!string.IsNullOrWhiteSpace(registry.Token))
        {
            logger.LogInformation(
                "PluginCatalog:BootstrapKey is set but a registry token is already configured — the "
                + "explicit token wins; skipping auto-registration.");
            return Observable.Return(Unit.Default);
        }
        var instanceId = options.InstanceId?.Trim() ?? "";
        if (instanceId.Length == 0)
        {
            logger.LogError(
                "PluginCatalog:BootstrapKey is set but PluginCatalog:InstanceId is not. The instance "
                + "id is a stable global identity and is never derived from a machine or pod name — "
                + "set it explicitly.");
            return Observable.Empty<Unit>();           // misconfigured → do not go on to install
        }

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
                        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
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
                                ProtectedKey = protector?.Protect(result.InstanceKey) ?? result.InstanceKey,
                                RegisteredAt = DateTimeOffset.UtcNow,
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
    ///     Suppressible with <see cref="PluginCatalogOptions.InstallPreInstalledPackages"/>.</item>
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
    ///     express and the flags can.</item>
    /// </list>
    ///
    /// <para>Installs run SEQUENTIALLY (<c>Concat</c>) — each one writes a partition's worth of
    /// nodes and may compile node types; a parallel fan-out on a cold starting pod is how you
    /// saturate it.</para>
    /// </summary>
    private IObservable<DefaultInstallSummary> InstallDefaults(PluginCatalogOptions options)
    {
        var wanted = options.InstallByDefault
            .Select(PluginGrantEntry.TryParse)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        var baseline = options.InstallPreInstalledPackages;
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

        return composition.SelectMany(composed => InstallSelection(options, wanted, baseline, composed));
    }

    /// <summary>The decision, once the three selection signals are known: list the sources, select,
    /// close over dependencies, drop what the seed already delivered, install in order.</summary>
    private IObservable<DefaultInstallSummary> InstallSelection(
        PluginCatalogOptions options,
        IReadOnlyList<PluginGrantEntry> wanted,
        bool baseline,
        FeatureComposition composition)
    {
        var included = Parse(composition.Included);
        var excluded = Parse(composition.Excluded);
        if (!baseline && wanted.Count == 0 && included.Count == 0)
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

                return Candidates(sources, baseline, wanted, included, excluded)
                    // Drop anything the seed has already delivered once. Done AFTER listing because
                    // the decision is per PACKAGE, and only the listing knows which packages a
                    // pattern covers.
                    //
                    // 🚨 A RECONCILED candidate is exempt: the ledger records what the SEED
                    // delivered, and the seed's whole point is that it does not re-assert. A
                    // package this environment's flags declare must re-assert on every boot — that
                    // is the difference between the two lanes — so it is never dropped here, the
                    // same exemption the platform's own preInstalled baseline already has.
                    .Select(candidates => (IReadOnlyList<InstallCandidate>)candidates
                        .Where(c => c.Reconciled || !seeded.Contains(c.Package.Id))
                        .ToList())
                    .SelectMany(InstallAll)
                    .SelectMany(summary => RecordSeeded(ledger, summary).Select(_ => summary));
            }));
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
        // 🚨 A pass that attempted NOTHING knows nothing. A source listing that failed yields an
        // empty summary, and treating that as "no package is missing" would erase the record of a
        // genuinely missing one — the ledger would then be at its most optimistic exactly when the
        // instance is least healthy.
        if (summary.Packages.Count == 0)
            return Observable.Return(Unit.Default);

        var already = ledger.Seeded.ToImmutableHashSet(StringComparer.Ordinal);
        // 🚨 Delivered, NOT covered. summary.Packages includes the failures.
        var delivered = summary.Delivered.Where(id => !already.Contains(id)).ToList();

        var failures = summary.Failures
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableList();

        if (delivered.Count == 0 && failures.SequenceEqual(ledger.Failed, StringComparer.Ordinal))
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

        var configured = services.GetService<IConfiguration>() is { } config
            ? PackageSources.FromConfiguration(hub, config, logger)
            : [];

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
                        var bundles = new PluginBundleClient(hub, registry.Url, token);
                        return new ConfiguredPackageSource(
                            new RegistryPackageSource(hub, registry.Url, token) { Bundles = bundles },
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
            .ToList());
    }

    /// <summary>
    /// Whether this installation has NO install records yet — the gate on the operator's
    /// <see cref="PluginCatalogOptions.InstallByDefault"/> seed. Read as System: the
    /// <c>Plugins</c> partition is written only under System, and this runs on startup with no user
    /// identity in scope.
    /// </summary>
    private IObservable<bool> IsFreshInstallation()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{PackageInstaller.InstalledPartition} scope:children "
                    + $"nodeType:{PackageInstaller.PackageNodeType}")))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(installed => installed.Items.Count == 0);
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

    private IObservable<IReadOnlyList<InstallCandidate>> Candidates(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted,
        IReadOnlyList<FlagPattern> included,
        IReadOnlyList<FlagPattern> excluded) =>
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
            .Select(source => source.Source.ListPackages(source.GitRef)
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
                .Select(packages => packages
                    .Select(p => string.IsNullOrEmpty(p.Source) ? p with { Source = source.Name } : p)
                    .Select(p => new InstallCandidate(source, p))
                    .ToList())
                .Catch((Exception exception) =>
                {
                    logger.LogWarning(exception,
                        "[DefaultInstall] listing {Name} @ {Ref} failed — its packages are skipped "
                        + "this boot", source.Name, source.GitRef);
                    return Observable.Return(new List<InstallCandidate>());
                }))
            .ToObservable()
            .Concat()
            .ToList()
            .Select(perSource =>
            {
                var catalog = perSource
                    .SelectMany(list => list)
                    .GroupBy(c => c.Package.Id, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(c => c.Package.Id, StringComparer.Ordinal)
                    .ToList();
                bool IsIncluded(InstallCandidate c) =>
                    included.Any(i => i.Entry.Matches(c.Package.Source ?? "", c.Package.Id));
                var selected = catalog
                    .Where(c => (baseline && c.Package.PreInstalled)
                                || IsIncluded(c)
                                || wanted.Any(w => w.Matches(c.Package.Source ?? "", c.Package.Id)))
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
                if (wanted.Count > 0
                    && !selected.Any(c => wanted.Any(w => w.Matches(c.Package.Source ?? "", c.Package.Id))))
                    logger.LogWarning(
                        "The default install matched no packages for the operator's patterns "
                        + "(wanted [{Wanted}]; {Baseline} pre-installed package(s) were still "
                        + "selected). If the registry predates source-stamped catalog entries, a "
                        + "Source/* pattern cannot match — it fails closed rather than guessing.",
                        string.Join(", ", wanted), selected.Count);

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
                    catalog.Select(c => c.Package).ToList(),
                    selected.Select(c => c.Package).ToList(),
                    logger);

                // The TOLERANT sort: a cycle warns and still yields every package exactly once
                // (the requirement closing the loop is ignored; order within the cycle is
                // arbitrary — see InDependencyOrder's remarks) rather than refusing, because
                // nobody is present at boot to fix a malformed repo and one bad package must not
                // strand the whole instance. The Install CLICK uses the same graph with the strict
                // policy (PackageDependencyGraph.InstallClosure).
                var ordered = PackageDependencyGraph.InDependencyOrder(withDependencies, logger);
                var bySource = catalog.ToDictionary(c => c.Package.Id, StringComparer.Ordinal);
                return (IReadOnlyList<InstallCandidate>)ordered
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
                    // Whether this candidate belongs to a RECONCILED lane (the platform baseline or
                    // this environment's flags) rather than the seed-once one. Read by the ledger
                    // filter: a reconciled package re-asserts on every boot, a seeded one never
                    // does.
                    .Select(c => c with
                    {
                        Reconciled = (baseline && c.Package.PreInstalled) || IsIncluded(c),
                    })
                    .ToList();
            }));

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
    /// <para><b>A COMMERCIAL requirement is never pulled in.</b> Selection is source-scoped on
    /// purpose: an instance is routinely granted paid content it may buy but must not receive
    /// automatically, and a requirement edge would otherwise be a back door around that scoping.
    /// The boot has no authorizing principal, so such an install would be refused anyway.</para>
    ///
    /// <para>Cycles terminate: a package already in the result is never expanded twice.</para>
    /// </summary>
    /// <param name="catalog">Every package every configured source listed, deduped by id.</param>
    /// <param name="selected">The packages the baseline + operator patterns chose.</param>
    /// <param name="logger">Receives the unresolvable-requirement warning.</param>
    /// <returns>The selection closed over its requirements, ordered by id (the sort orders it).</returns>
    internal static IReadOnlyList<PackageManifest> DependencyClosure(
        IReadOnlyList<PackageManifest> catalog,
        IReadOnlyList<PackageManifest> selected,
        ILogger? logger = null)
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
                // 🚨 A COMMERCIAL requirement is never pulled in. Selection is deliberately
                // source-scoped so that an instance granted paid content (course catalogues,
                // customer modules) does not auto-install what it merely may buy — and a
                // requirement edge must not become a back door around that. The boot has no
                // authorizing principal either, so PackageEntitlement.Authorize would refuse it
                // anyway: pulling it in would buy nothing but a failed install every boot.
                if (dependency.IsCommercial())
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
                + "install it from the catalog as a global admin.",
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

    /// <summary>Installs the selected packages sequentially and folds their outcomes into one summary.</summary>
    private IObservable<DefaultInstallSummary> InstallAll(IReadOnlyList<InstallCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Observable.Return(DefaultInstallSummary.Empty);
        logger.LogInformation(
            "[DefaultInstall] {Count} package(s), in dependency order — {Packages}",
            candidates.Count, string.Join(", ", candidates.Select(c => c.Package.Id)));
        return candidates
            .Select(Install)
            .ToObservable()
            .Concat()
            .Aggregate(DefaultInstallSummary.Empty, (acc, one) => acc.Add(one));
    }

    /// <summary>
    /// Installs (or re-reconciles) ONE package and re-asserts its public read.
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
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var package = candidate.Package;
        var partition = string.IsNullOrWhiteSpace(package.TargetPartition)
            ? package.Id
            : package.TargetPartition!;

        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => CatalogLayoutAreas
                    .InstallOrUpdate(hub, candidate.Source.Source, candidate.Source.GitRef, package, logger)
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
                Packages: [package.Id]))
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
        /// Whether a RECONCILED lane selected it — the platform's own <c>preInstalled</c> baseline or
        /// this environment's feature flags — as opposed to the seed-once
        /// <see cref="PluginCatalogOptions.InstallByDefault"/>. A reconciled candidate is exempt from
        /// the seed ledger: it re-asserts on every boot, which is the entire difference between a
        /// per-environment policy and a seed.
        /// </summary>
        public bool Reconciled { get; init; }
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
    internal IObservable<DefaultInstallSummary> InstallFrom(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted,
        FeatureComposition? composition = null) =>
        Candidates(
                sources, baseline, wanted,
                Parse((composition ?? FeatureComposition.Empty).Included),
                Parse((composition ?? FeatureComposition.Empty).Excluded))
            .SelectMany(InstallAll);

    /// <summary>
    /// Runs the PRODUCTION default-install pass on demand — the identical selection, ordering and
    /// install the boot pass performs, reading this installation's real configuration. Cold: the
    /// work runs on Subscribe. Exists so a test can assert the second boot writes nothing, and so
    /// the opt-out is exercised through the real decision rather than a re-implementation of it.
    /// </summary>
    internal IObservable<DefaultInstallSummary> RunDefaultInstall() =>
        InstallDefaults(hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions());
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

    /// <summary>When the ledger last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// What one default-install pass did: how many packages were written, how many were already
/// current, how many failed, and which packages the pass covered. Emitted once per pass on
/// <see cref="InstanceAutoRegistrationService.Completed"/>, so both the boot log line and a test
/// read the same outcome.
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
    /// The ids this pass actually DELIVERED — installed or already current. The ledger's input:
    /// what the seed may stop re-asserting.
    /// </summary>
    public ImmutableList<string> Delivered
        => (Packages ?? ImmutableList<string>.Empty)
            .RemoveRange(Failures ?? ImmutableList<string>.Empty);

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
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Installed} installed, {UpToDate} up to date, {Failed} failed "
        + $"[{string.Join(", ", Packages)}]"
        + (Failures is { Count: > 0 } f ? $" — FAILED: [{string.Join(", ", f)}]" : "");
}
