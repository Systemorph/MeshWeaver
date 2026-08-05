using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Turns "a plugin repo's CI went green" into "the instances that installed from it find out",
/// without anyone polling a registry or opening the catalog page.
///
/// <para><b>How the two halves meet.</b> <c>MeshWeaver.GitSync</c> writes a
/// <see cref="BuildCompletion"/> node at <c>Admin/_Build/{owner}.{repo}</c> when a
/// <c>workflow_run</c> completes successfully. This watcher SUBSCRIBES to that node's stream. There
/// is no call between the two projects and no interface either implements — the node is the entire
/// contract, so the producer knows nothing about plugins and this consumer knows nothing about
/// webhooks. (Its content type lives in <c>MeshWeaver.Graph</c>, which both already reference; a
/// handler interface in GitSync could not have worked, since PluginCatalog already depends on
/// GitSync and implementing it here would close a reference cycle.)</para>
///
/// <para><b>A green build is NOT a change.</b> The build node is rewritten on every green run —
/// doc-only commits, reverts, re-runs of an unchanged tree. Acting on the emission itself would
/// push an update to every instance on every green run. The decision is made per MODULE against
/// content identity, and it is deliberately NOT re-implemented here:
/// <see cref="CatalogLayoutAreas.InstallOrUpdate"/> already compares the catalog entry's
/// <c>ModuleVersion</c> (a hash over the module's sorted (path, file-hash) pairs, from its
/// CI-maintained <c>manifest.lock</c>) with the install record's, and no-ops when they are equal.
/// Keeping that comparison in ONE place is what stops the two paths — the Update button and this
/// watcher — from ever disagreeing about what "changed" means.</para>
///
/// <para><b>Reminder by default; unattended on opt-in — per record, seeded per deployment.</b> A
/// changed module raises a <c>Notification</c> satellite on the install record unless the record
/// opted in (<see cref="PackageManifest.AutoUpdate"/>), in which case the delta installs itself.
/// The opt-in is stamped at INSTALL time from the deployment's
/// <see cref="PluginCatalogOptions.AutoUpdateByDefault"/> — our deployments opt in via the Helm
/// chart, so every package they install auto-updates with no per-package step — and an opted-in
/// update is still fenced by the content-identity gate above, the additive install, and the
/// per-node <c>SyncBehavior</c> claim the installer honors. See
/// <see cref="PackageManifest.AutoUpdate"/> for the full policy.</para>
///
/// <para>Instance-scoped, not static: the subscriptions live and die with the mesh
/// (see <c>Doc/Architecture/NoStaticState</c>). Reactive throughout — no <c>async</c>/<c>await</c>,
/// no polling.</para>
///
/// <para>Full flow and configuration: <c>Doc/Architecture/PluginUpdateOnGreenBuild</c>.</para>
/// </summary>
public sealed class PluginUpdateWatcher : Microsoft.Extensions.Hosting.IHostedService, IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger? logger;
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>Build-node paths already subscribed, so re-reading the catalog set never opens a
    /// second subscription for the same repository. Instance state — one per mesh.</summary>
    private readonly ConcurrentDictionary<string, byte> watched = new(StringComparer.Ordinal);

    /// <summary>Catalog content by repository URL, so a reaction can rebuild the package source
    /// without re-reading the catalog node. Instance state — one per mesh.</summary>
    private readonly ConcurrentDictionary<string, PluginCatalogContent> watchedCatalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="PluginUpdateWatcher"/> class.</summary>
    public PluginUpdateWatcher(IMessageHub hub, ILogger<PluginUpdateWatcher>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// Hosted-service start — what actually activates the watcher on boot. Registered as an
    /// <c>IHostedService</c> in <c>AddPluginCatalog</c> (a bare singleton would sit inert: the host
    /// only starts services registered under the interface — the Copilot-caught gap that left the
    /// build-node subscription never opened). Delegates to <see cref="Start"/>; returns immediately
    /// (the subscription chain is reactive — nothing to await).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Hosted-service stop: closes every subscription (idempotent with <see cref="Dispose"/>).</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a build-node subscription for every catalog that names a source repository. Idempotent:
    /// calling it again after a catalog is added subscribes only the new repositories. (A catalog
    /// CREATED after boot is picked up on the next start — the snapshot read is deliberate; the
    /// per-repo <c>watched</c> guard makes re-running free.)
    /// </summary>
    public void Start()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Catalogs are read as SYSTEM: this runs on mesh startup with no ambient user, and a
        // catalog node is not anonymous-readable on an access-gated portal.
        var sub = Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"nodeType:{PluginCatalogConfigurationExtensions.CatalogNodeType}"))
                    .Take(1))
            .Subscribe(
                change =>
                {
                    foreach (var node in change.Items)
                        WatchCatalog(node);
                },
                ex => logger?.LogWarning(ex, "Plugin update watcher could not read the catalog set."));
        subscriptions.Add(sub);
    }

    private void WatchCatalog(MeshNode catalogNode)
    {
        var content = catalogNode.ContentAs<PluginCatalogContent>(hub.JsonSerializerOptions, logger);
        if (content?.SourceRepoPath is not { Length: > 0 } src)
            return;

        // A catalog source may be a local path rather than a repo URL; only a URL can ever have a
        // build node, because only a URL receives webhooks.
        (string Owner, string Repo) parsed;
        try { parsed = OctokitParse(src); }
        catch { return; }
        if (parsed.Owner.Length == 0 || parsed.Repo.Length == 0)
            return;

        var buildPath = BuildCompletion.PathFor(parsed.Owner, parsed.Repo);
        if (!watched.TryAdd(buildPath, 0))
            return; // already watching this repository for another catalog

        logger?.LogInformation(
            "Plugin update watcher: catalog {Catalog} watches {BuildPath} for green builds of {Src}.",
            catalogNode.Path, buildPath, src);

        // The canonical single-node read: one shared handle per path, live, authoritative.
        // Never QueryAsync — that index lags a write by design.
        watchedCatalogs[content.SourceRepoPath!] = content;

        var sub = hub.GetMeshNodeStream(buildPath)
            .Select(n => n?.ContentAs<BuildCompletion>(hub.JsonSerializerOptions, logger))
            .Where(b => b is not null && b.HeadSha.Length > 0)
            .Select(b => b!)
            // A rebuild of the SAME commit tells us nothing new. This is a cheap guard against
            // re-emission; it is NOT the "did anything change" decision — that is per module,
            // against ModuleVersion, and lives in InstallOrUpdate.
            .DistinctUntilChanged(b => b.HeadSha)
            .Subscribe(
                build => OnGreenBuild(catalogNode, content, build),
                ex => logger?.LogWarning(ex,
                    "Plugin update watcher: build stream for {BuildPath} faulted.", buildPath));
        subscriptions.Add(sub);
    }

    private void OnGreenBuild(MeshNode catalogNode, PluginCatalogContent content, BuildCompletion build)
    {
        logger?.LogInformation(
            "Plugin update watcher: {Repo} built green at {Sha} ({Workflow} #{Run}) — checking installed modules.",
            build.RepositoryUrl, build.HeadSha, build.WorkflowName, build.RunNumber);

        var source = PackageSources.FromRepo(hub, content.SourceRepoPath, content.SourceSubdir, logger, nodeRepo: true);
        if (source is null)
        {
            logger?.LogWarning(
                "Plugin update watcher: catalog {Catalog} has no usable package source; skipping.", catalogNode.Path);
            return;
        }

        source.ListPackages(build.HeadSha)
            .Subscribe(
                packages => ReconcileInstalled(packages, build),
                ex => logger?.LogWarning(ex,
                    "Plugin update watcher: listing packages of {Repo} at {Sha} failed.",
                    build.RepositoryUrl, build.HeadSha));
    }

    private void ReconcileInstalled(IReadOnlyList<PackageManifest> packages, BuildCompletion build)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return;

        foreach (var pkg in packages)
        {
            var recordPath = $"{PackageInstaller.InstalledPartition}/{pkg.Id}";
            storage.Read(recordPath, hub.JsonSerializerOptions)
                .Take(1)
                .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                // Not installed here → nothing to remind anyone about. A catalog lists far more
                // packages than any one instance installs, so this is the common case.
                .Where(record => record is not null)
                .Subscribe(
                    record => Reconcile(meshService, accessService, pkg, record!, recordPath, build),
                    ex => logger?.LogWarning(ex,
                        "Plugin update watcher: reading install record {Path} failed.", recordPath));
        }
    }

    private void Reconcile(
        IMeshService meshService, AccessService accessService,
        PackageManifest pkg, PackageManifest record, string recordPath, BuildCompletion build)
    {
        // THE gate: content identity, not the build event. Equal module hashes ⇒ this green build
        // changed nothing in this module ⇒ stay completely silent (no notification, no fetch).
        if (string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
            return;

        // What actually changed, for the reminder's message. Available without re-fetching
        // anything: the installed side is on the record (InstalledFiles), the candidate side rode
        // in on the catalog entry (ManifestFiles).
        var changed = 0;
        var removed = 0;
        if (record.InstalledFiles is { Count: > 0 } installed && pkg.ManifestFiles is { Count: > 0 } candidate)
        {
            foreach (var (path, hash) in candidate)
                if (!installed.TryGetValue(path, out var old) || !string.Equals(old, hash, StringComparison.Ordinal))
                    changed++;
            foreach (var path in installed.Keys)
                if (!candidate.ContainsKey(path))
                    removed++;
        }

        var detail = changed + removed > 0
            ? $"{changed} file(s) changed, {removed} removed"
            : "content changed";

        logger?.LogInformation(
            "Plugin update watcher: {Id} has an update ({Old} → {New}; {Detail}); autoUpdate={Auto}.",
            pkg.Id, record.ModuleVersion, pkg.ModuleVersion, detail, record.AutoUpdate);

        if (ShouldAutoApply(record))
        {
            ApplyUpdate(accessService, pkg, build, detail);
            return;
        }

        Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => NotificationService.CreateNotification(
                    meshService,
                    recordPath,
                    $"Update available: {pkg.Name ?? pkg.Id}",
                    $"A new build of {pkg.Name ?? pkg.Id} is available ({detail}). Built from {build.HeadSha[..Math.Min(7, build.HeadSha.Length)]}.",
                    NotificationType.System,
                    targetNodePath: recordPath))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "Plugin update watcher: raising the update reminder for {Id} failed.", pkg.Id));
    }

    /// <summary>
    /// The opt-in rule, extracted so it is pinnable on its own: a changed module installs
    /// unattended only for a record that opted in — by its own flag, stamped at install time from
    /// the deployment default (<see cref="PackageInstaller.SeedAutoUpdate"/>). Pure.
    /// </summary>
    // Internal for the BuildCompletionSubscriptionTest pin (InternalsVisibleTo).
    internal static bool ShouldAutoApply(PackageManifest record) => record.AutoUpdate;

    /// <summary>
    /// Unattended install of a changed module — reached only for a record that opted in.
    ///
    /// <para>Delegates to the very same <see cref="CatalogLayoutAreas.InstallOrUpdate"/> the Update
    /// button uses. That matters more than it looks: it keeps the manifest-diff fast path, the
    /// shared-Source/Test full-install fallback, and above all the "nothing to sync" comparison in
    /// ONE place, so the automatic path and the manual one can never disagree about what changed.</para>
    ///
    /// <para>Runs as SYSTEM: there is no user on a webhook-driven emission, and the install writes
    /// nodes. Errors are logged, never thrown — this is a background reaction, and a fault here must
    /// not tear down the subscription that serves every other package.</para>
    /// </summary>
    private void ApplyUpdate(AccessService accessService, PackageManifest pkg, BuildCompletion build, string detail)
    {
        var content = watchedCatalogs.TryGetValue(build.RepositoryUrl, out var c) ? c : null;
        var source = content is null
            ? null
            : PackageSources.FromRepo(hub, content.SourceRepoPath, content.SourceSubdir, logger, nodeRepo: true);
        if (source is null)
        {
            logger?.LogWarning(
                "Plugin update watcher: {Id} is due an auto-update but its catalog source is unavailable.", pkg.Id);
            return;
        }

        logger?.LogInformation(
            "Plugin update watcher: auto-updating {Id} to {Version} ({Detail}).",
            pkg.Id, pkg.ModuleVersion, detail);

        Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => CatalogLayoutAreas.InstallOrUpdate(hub, source, build.HeadSha, pkg, logger))
            .Subscribe(
                result => logger?.LogInformation(
                    "Plugin update watcher: {Id} auto-updated ({Result}).", pkg.Id, result),
                ex => logger?.LogWarning(ex,
                    "Plugin update watcher: auto-update of {Id} failed; the card still offers a manual Update.",
                    pkg.Id));
    }

    private static (string Owner, string Repo) OctokitParse(string url)
    {
        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0) return ("", "");
        var repo = trimmed[(slash + 1)..];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        var rest = trimmed[..slash];
        var prevSlash = rest.LastIndexOf('/');
        var owner = prevSlash >= 0 ? rest[(prevSlash + 1)..] : rest;
        return (owner, repo);
    }

    /// <inheritdoc />
    public void Dispose() => subscriptions.Dispose();
}
