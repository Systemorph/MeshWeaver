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
            ? Observable.Return(DefaultInstallSummary.Empty)
            : IsFreshInstallation().SelectMany(fresh =>
            {
                if (!fresh && wanted.Count > 0)
                    logger.LogDebug(
                        "Packages are already installed — the operator's InstallByDefault seed is "
                        + "skipped; the pre-installed baseline is still reconciled.");
                // Nothing left to select ⇒ do not list the sources at all. Listing is a network
                // round-trip per source; an opted-out instance that is already seeded must cost
                // nothing on every boot.
                return !baseline && !fresh
                    ? Observable.Return(DefaultInstallSummary.Empty)
                    : Candidates(sources, baseline, fresh ? wanted : [])
                        .SelectMany(InstallAll);
            }));
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
    private IObservable<IReadOnlyList<InstallCandidate>> Candidates(
        IReadOnlyList<ConfiguredPackageSource> sources,
        bool baseline,
        IReadOnlyList<PluginGrantEntry> wanted) =>
        sources
            .Select(source => source.Source.ListPackages(source.GitRef)
                .Take(1)
                .Select(packages => packages
                    .Where(p => (baseline && p.PreInstalled)
                                || wanted.Any(w => w.Matches(p.Source ?? "", p.Id)))
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
                var deduped = perSource
                    .SelectMany(list => list)
                    .GroupBy(c => c.Package.Id, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(c => c.Package.Id, StringComparer.Ordinal)
                    .ToList();
                if (deduped.Count == 0 && wanted.Count > 0)
                    logger.LogWarning(
                        "The default install matched no packages (wanted [{Wanted}]). If the "
                        + "registry predates source-stamped catalog entries, a Source/* pattern "
                        + "cannot match — it fails closed rather than guessing.",
                        string.Join(", ", wanted));
                var ordered = InDependencyOrder(deduped.Select(c => c.Package).ToList(), logger);
                var bySource = deduped.ToDictionary(c => c.Package.Id, StringComparer.Ordinal);
                return (IReadOnlyList<InstallCandidate>)ordered
                    .Select(p => bySource[p.Id])
                    .ToList();
            });

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
    /// Orders packages so a dependency is installed BEFORE anything that declares it
    /// (<see cref="PackageManifest.Requires"/>, entries shaped <c>Store@^1.0.0</c>) — a depth-first
    /// topological sort, falling back to catalog order within a cycle.
    ///
    /// <para>🚨 Not cosmetic: installing out of order FAILS. On the first live run, catalog
    /// (alphabetical) order put <c>Chess</c> before <c>Training</c>, and the install died with
    /// "NodeType(s) not registered: Training/Tour". A person clicking Install picks the order
    /// implicitly; an unattended install has to derive it.</para>
    ///
    /// <para>Dependencies outside <paramref name="packages"/> are ignored — the instance was not
    /// granted them, so they cannot be installed and there is nothing to order against.</para>
    /// </summary>
    internal static IReadOnlyList<PackageManifest> InDependencyOrder(
        IReadOnlyList<PackageManifest> packages, ILogger logger)
    {
        var byId = packages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ordered = new List<PackageManifest>(packages.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = visiting, 2 = done

        void Visit(PackageManifest pkg)
        {
            if (state.TryGetValue(pkg.Id, out var s))
            {
                if (s == 1)
                    logger.LogWarning(
                        "Dependency cycle involving package '{Id}' — installing it in catalog order.",
                        pkg.Id);
                return;
            }
            state[pkg.Id] = 1;
            foreach (var requirement in pkg.Requires)
            {
                // "Store@^1.0.0" → "Store". The version constraint is not resolved here: the
                // registry serves one version per package, so ordering is all that is available.
                var depId = requirement.Split('@')[0].Trim();
                if (depId.Length > 0 && byId.TryGetValue(depId, out var dep) && !ReferenceEquals(dep, pkg))
                    Visit(dep);
            }
            state[pkg.Id] = 2;
            ordered.Add(pkg);
        }

        foreach (var pkg in packages)
            Visit(pkg);
        return ordered;
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
