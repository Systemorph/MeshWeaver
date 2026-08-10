using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
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
        var registry = options.EffectiveRegistries.FirstOrDefault();

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

        subscriptions.Add(registered
            .SelectMany(_ => InstallDefaults(options))
            // 🚨 SubscribeOn the thread pool, NOT the host-startup thread. The chain is synchronous
            // right up to its first genuinely-async leaf (an in-memory or already-cached source
            // lists its packages inline), so subscribing here would run the whole install ON the
            // startup thread — re-entering the hub schedulers mid-init, which deadlocks. Same fix,
            // same reason, as StaticRepoImportHostedService.
            .SubscribeOn(TaskPoolScheduler.Default)
            .Do(summary => logger.LogInformation("[DefaultInstall] reconciled: {Summary}", summary))
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
    /// <para><b>Two selection signals, one decision.</b> They answer different questions and both
    /// feed the same ordered install:</para>
    /// <list type="bullet">
    ///   <item><b>The package's own <c>preInstalled</c> declaration</b> — the PLATFORM's baseline
    ///     (the Agents and Skills libraries, Essentials, …). Reconciled on EVERY boot, because it
    ///     is what the platform requires to function and what must survive a self-update; it is
    ///     also the only thing that can heal an instance whose baseline partition was lost (#902).
    ///     Suppressible with <see cref="PluginCatalogOptions.InstallPreInstalledPackages"/>.</item>
    ///   <item><b>The operator's <see cref="PluginCatalogOptions.InstallByDefault"/> patterns</b> —
    ///     the extras a FRESH deployment seeds itself with, source-scoped so an instance granted
    ///     paid course content never auto-installs it. Gated on having NO install records: this
    ///     seeds a new deployment rather than asserting a policy, so an admin who later uninstalls
    ///     a package is not fought by the next restart.</item>
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
        if (!baseline && wanted.Count == 0)
            return Observable.Return(DefaultInstallSummary.Empty);

        return Sources(options).SelectMany(sources => sources.Count == 0
            // 🚨 Asked to install, with nowhere to install FROM. Silence here would recreate the
            // original "healthy boot, zero plugins" failure for the no-sources misconfiguration —
            // the same shape, one layer up. Say it, and say what to set.
            ? Observable.Defer(() =>
            {
                if (wanted.Count > 0)
                    logger.LogError(
                        "[DefaultInstall] {Count} InstallByDefault pattern(s) are configured "
                        + "([{Wanted}]) but this installation has NO package sources — nothing can "
                        + "be installed. Configure PluginCatalog:Sources (a registry serving its own "
                        + "repos) or PluginCatalog:RegistryUrl (a consumer).",
                        wanted.Count, string.Join(", ", wanted));
                return Observable.Return(DefaultInstallSummary.Empty);
            })
            : SeedLedger().SelectMany(seeded =>
            {
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

                return Candidates(sources, baseline, wanted)
                    // Drop anything the seed has already delivered once. Done AFTER listing because
                    // the decision is per PACKAGE, and only the listing knows which packages a
                    // pattern covers.
                    .Select(candidates => (IReadOnlyList<InstallCandidate>)candidates
                        .Where(c => c.Package.PreInstalled || !seeded.Contains(c.Package.Id))
                        .ToList())
                    .SelectMany(InstallAll)
                    .SelectMany(summary => RecordSeeded(seeded, summary).Select(_ => summary));
            }));
    }

    /// <summary>Node holding the default-install ledger — what the SEED has delivered, ever.</summary>
    private const string SeedLedgerPath = PackageInstaller.InstalledPartition + "/_DefaultInstallLedger";

    /// <summary>The ledger's own node type — deliberately distinct from <c>Package</c> so it never
    /// appears in installed-package enumerations.</summary>
    public const string LedgerNodeType = "DefaultInstallLedger";

    /// <summary>
    /// The package ids the default-install seed has already delivered. Empty on a fresh instance
    /// and whenever the ledger cannot be read — failing OPEN here is deliberate: the cost of a
    /// missing ledger is re-installing a package (idempotent, content-identity gated), whereas
    /// failing closed would silently skip the repair this whole mechanism exists to perform.
    /// </summary>
    private IObservable<ImmutableHashSet<string>> SeedLedger()
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(SeedLedgerPath, TimeSpan.FromSeconds(10)))
            .Take(1)
            .Select(node => node?.ContentAs<DefaultInstallLedger>(hub.JsonSerializerOptions) is { } led
                ? led.Seeded.ToImmutableHashSet(StringComparer.Ordinal)
                : ImmutableHashSet<string>.Empty)
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "Could not read the default-install ledger at {Path} — treating as empty.",
                    SeedLedgerPath);
                return Observable.Return(ImmutableHashSet<string>.Empty);
            });
    }

    /// <summary>
    /// Appends the packages this pass covered to the ledger, so they are never seeded again.
    /// Records what was COVERED (installed or already current), not what merely succeeded: a
    /// package that FAILED stays off the ledger and is retried next boot — that retry is the
    /// repair. Written as System; the Plugins partition is System-owned.
    /// </summary>
    private IObservable<Unit> RecordSeeded(
        ImmutableHashSet<string> already, DefaultInstallSummary summary)
    {
        var delivered = summary.Packages.Where(id => !already.Contains(id)).ToList();
        if (delivered.Count == 0)
            return Observable.Return(Unit.Default);

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("_DefaultInstallLedger", PackageInstaller.InstalledPartition)
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
        var registries = options.EffectiveRegistries;
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
                    .Select(token => new ConfiguredPackageSource(
                        new RegistryPackageSource(hub, registry.Url, token),
                        string.IsNullOrWhiteSpace(registry.Ref) ? "HEAD" : registry.Ref,
                        string.IsNullOrWhiteSpace(registry.Name) ? registry.Url : registry.Name)))
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

    private IObservable<IReadOnlyList<InstallCandidate>> Candidates(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted) =>
        Observable.Defer(() =>
        {
            // Config error, reported BEFORE any listing: a pattern naming a source that does not
            // exist here can never install anything, and silence is the worst possible answer.
            if (UnmatchablePatterns(wanted, sources.Select(s => s.Name).ToList()) is { Count: > 0 } bad)
                logger.LogError(
                    "[DefaultInstall] {Count} InstallByDefault pattern(s) name a source this "
                    + "installation does not have: [{Bad}]. Configured sources: [{Sources}]. Those "
                    + "patterns will install NOTHING — fix the names so they agree (a source's name "
                    + "is what grants and install-defaults are written against).",
                    bad.Count, string.Join(", ", bad),
                    string.Join(", ", sources.Select(s => s.Name)));
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
                var selected = catalog
                    .Where(c => (baseline && c.Package.PreInstalled)
                                || wanted.Any(w => w.Matches(c.Package.Source ?? "", c.Package.Id)))
                    .ToList();
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
                // therefore no catalog page to install it from by hand. Exactly the state `atioz`
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
                if (byId.TryGetValue(id, out var dependency))
                    pending.Push(dependency);
                else
                    missing.Add(id);
            }
        }

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
                        .Select(_ => result)))
            .Select(result => new DefaultInstallSummary(
                Installed: result.Written > 0 ? 1 : 0,
                UpToDate: result.Written > 0 ? 0 : 1,
                Failed: 0,
                Packages: [package.Id]))
            .Catch((Exception exception) =>
            {
                logger.LogError(exception,
                    "[DefaultInstall] installing package {Id} failed — continuing with the rest; the "
                    + "instance is missing it until the next boot or a manual install", package.Id);
                return Observable.Return(new DefaultInstallSummary(0, 0, 1, [package.Id]));
            });
    }

    /// <summary>One package the default install should carry, and the source it came from.</summary>
    private sealed record InstallCandidate(ConfiguredPackageSource Source, PackageManifest Package);

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
    internal IObservable<DefaultInstallSummary> InstallFrom(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted) =>
        Candidates(sources, baseline, wanted).SelectMany(InstallAll);

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
    /// <summary>Package ids the seed has delivered. Append-only in practice.</summary>
    public ImmutableList<string> Seeded { get; init; } = ImmutableList<string>.Empty;

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
/// <param name="Packages">The package ids the pass covered, in install order.</param>
public readonly record struct DefaultInstallSummary(
    int Installed, int UpToDate, int Failed, ImmutableList<string> Packages)
{
    /// <summary>A pass that covered nothing.</summary>
    public static DefaultInstallSummary Empty { get; } = new(0, 0, 0, ImmutableList<string>.Empty);

    /// <summary>Folds one package's outcome into the running total.</summary>
    public DefaultInstallSummary Add(DefaultInstallSummary other) => new(
        Installed + other.Installed,
        UpToDate + other.UpToDate,
        Failed + other.Failed,
        Packages.AddRange(other.Packages));

    /// <inheritdoc />
    public override string ToString() =>
        $"{Installed} installed, {UpToDate} up to date, {Failed} failed "
        + $"[{string.Join(", ", Packages)}]";
}
