using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    public void Dispose() => subscriptions.Dispose();

    private void Start()
    {
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
        var registry = options?.EffectiveRegistries.FirstOrDefault();
        if (registry is null)
            return;                                   // no registry configured → nothing to do

        // Two independent phases, sequenced: first make sure this installation HAS an instance key
        // (auto-registering when a bootstrap key is configured), then seed the packages a fresh
        // deployment should come up with. The second phase runs for a MANUALLY tokened install too
        // — "a new instance ships with the platform plugins" is not conditional on how it got its key.
        subscriptions.Add(EnsureRegistered(options!, registry)
            // 🚨 A failed registration must NOT sink the install phase. The phases are independent:
            // what matters to phase 2 is whether a usable key can be RESOLVED, not how phase 1
            // went. The case that forced this: two replicas start together, both see no stored
            // credential, one registers and the other gets 409 — the loser's convergence read can
            // still miss the sibling's just-committed write, and it would then skip installing
            // although the deployment holds a perfectly good key moments later. Phase 2 resolves
            // the token itself and fails closed (a warning + a 401 from the registry) when there
            // genuinely is none.
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "First-startup instance registration against {Url} failed (401 = invalid or "
                    + "revoked bootstrap key, 409 = instance id already taken). Continuing to the "
                    + "install phase, which uses whatever key this installation already holds.",
                    registry.Url);
                return Observable.Return(Unit.Default);
            })
            .SelectMany(_ => InstallDefaults(options!, registry))
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "First-startup plugin provisioning against {Url} failed; no retry is attempted.",
                    registry.Url)));
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
    /// Phase 2 — seed the packages a FRESH installation should come up with
    /// (<see cref="PluginCatalogOptions.InstallByDefault"/>): list the registry catalog with this
    /// installation's key, keep the entries matching the configured <c>Source/Package</c> patterns,
    /// and install them through the SAME path the catalog tab's Install button uses
    /// (<c>CatalogLayoutAreas.InstallOrUpdate</c>) — no parallel installer.
    ///
    /// <para>Gated on the installation having NO install records: this seeds a new deployment
    /// rather than continuously asserting a policy, so an admin who later uninstalls a package is
    /// not fought by the next restart. Installs run SEQUENTIALLY (<c>Concat</c>) — each one writes
    /// a partition's worth of nodes and may compile node types; a parallel fan-out on a cold
    /// starting pod is how you saturate it.</para>
    /// </summary>
    private IObservable<Unit> InstallDefaults(PluginCatalogOptions options, PluginRegistryReference registry)
    {
        var wanted = options.InstallByDefault
            .Select(PluginGrantEntry.TryParse)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        if (wanted.Count == 0)
            return Observable.Return(Unit.Default);

        var resolver = hub.ServiceProvider.GetRequiredService<RegistryTokenResolver>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Already provisioned? Read the install records as System — the "Plugins" partition is
        // written only under System, and this runs on startup with no user identity in scope.
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{PackageInstaller.InstalledPartition} scope:children "
                    + $"nodeType:{PackageInstaller.PackageNodeType}")))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(installed =>
            {
                if (installed.Items.Count > 0)
                {
                    logger.LogDebug(
                        "{Count} package(s) already installed — skipping the first-startup default install.",
                        installed.Items.Count);
                    return Observable.Return(Unit.Default);
                }

                return resolver.ResolveToken(registry).SelectMany(token =>
                {
                    if (token.Length == 0)
                        logger.LogWarning(
                            "Installing defaults from {Url} with NO instance key — only an open "
                            + "dev/e2e registry will answer.", registry.Url);

                    return InstallSelected(
                        hub, new RegistryPackageSource(hub, registry.Url, token),
                        registry.Ref, wanted, logger);
                });
            });
    }

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
    /// Selects the catalog entries matching <paramref name="wanted"/> and installs them, in
    /// dependency order, through the same path the catalog tab's Install button uses. Split out
    /// from <see cref="InstallDefaults"/> so the selection + install behaviour is testable against
    /// any <see cref="IPackageSource"/> rather than only a live HTTP registry.
    /// </summary>
    internal static IObservable<Unit> InstallSelected(
        IMessageHub hub, IPackageSource source, string sourceRef,
        IReadOnlyList<PluginGrantEntry> wanted, ILogger logger) =>
        source.ListPackages(sourceRef).SelectMany(packages =>
        {
            // Source-scoped match: a registry that does not stamp Source matches nothing, so an
            // old registry installs nothing rather than the wrong thing.
            var selected = packages
                .Where(p => wanted.Any(w => w.Matches(p.Source ?? "", p.Id)))
                .ToList();
            if (selected.Count == 0)
            {
                logger.LogWarning(
                    "First-startup default install matched no packages of the {Total} the registry "
                    + "serves (wanted [{Wanted}]). If the registry predates source-stamped catalog "
                    + "entries, a Source/* pattern cannot match.",
                    packages.Count, string.Join(", ", wanted));
                return Observable.Return(Unit.Default);
            }

            var ordered = InDependencyOrder(selected, logger);
            logger.LogInformation(
                "First-startup default install: {Count} package(s), in dependency order — {Packages}",
                ordered.Count, string.Join(", ", ordered.Select(p => p.Id)));

            return ordered
                .Select(pkg => CatalogLayoutAreas
                    .InstallOrUpdate(hub, source, sourceRef, pkg, logger)
                    .Do(result => logger.LogInformation(
                        "Installed default package '{Id}' ({Written}/{Total} node(s) written)",
                        pkg.Id, result.Written, result.Total))
                    // One failing package must not abort the rest — a half-seeded instance with a
                    // named failure beats an unseeded one.
                    .Catch((Exception ex) =>
                    {
                        logger.LogError(ex,
                            "Default install of package '{Id}' failed — continuing with the rest.",
                            pkg.Id);
                        return Observable.Empty<InstallResult>();
                    }))
                .Concat()
                .Select(_ => Unit.Default)
                .DefaultIfEmpty(Unit.Default)   // every package failed → still complete
                .LastAsync();
        });
}
