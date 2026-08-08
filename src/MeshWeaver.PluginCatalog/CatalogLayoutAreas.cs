using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The catalog browse/install view: lists the installable packages a <see cref="IPackageSource"/>
/// offers at a git ref, shows each package's install status (comparing against the <c>Plugins</c>
/// install registry), and offers an Install / Update button that runs the install. Reactive end to
/// end — after an install the registry stream re-emits and the affected card flips to "Installed"
/// with no manual refresh.
///
/// <para>The rendering is source-agnostic (<see cref="RenderFromSource"/>): the <c>PluginCatalog</c>
/// node Overview builds its source from the node's <see cref="PluginCatalogContent"/> and renders +
/// installs through the helpers here. (The old platform-admin "Plugin Catalog" settings tab that
/// also consumed this rendering was retired — browsing and provisioning is the Store's job; the
/// global-settings About tab shows the read-only installed inventory via
/// <see cref="ObserveInstalledManifests"/>.)</para>
/// </summary>
public static class CatalogLayoutAreas
{
    /// <summary>Area name for the catalog browse view.</summary>
    public const string CatalogArea = "Catalog";

    /// <summary>
    /// Registers the <c>PluginCatalog</c> node views: the catalog browse as the default Overview,
    /// plus the standard create/delete areas.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register on.</param>
    /// <returns>The configuration with the catalog views registered.</returns>
    public static MessageHubConfiguration AddPluginCatalogViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(s => s.WithContentType<PluginCatalogContent>())
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(CatalogArea, Catalog)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>The default Overview is the catalog.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx) => Catalog(host, ctx);

    /// <summary>
    /// Renders the catalog for a <c>PluginCatalog</c> node: builds the source from the node's
    /// <see cref="PluginCatalogContent"/> and renders through <see cref="RenderFromSource"/>.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the catalog view.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Catalog(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<PluginCatalogContent>(host.Hub.JsonSerializerOptions))
            .Select(cfg => RenderFromSource(
                host, BuildSource(host, cfg?.SourceRepoPath, cfg?.SourceSubdir),
                cfg?.SourceRef ?? "HEAD", cfg?.Description,
                cfg?.SourceRepoPath is { Length: > 0 } p ? $"{p} @ {cfg.SourceRef}" : null))
            .Switch()
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingCatalog")));
    }

    /// <summary>
    /// Renders the catalog from an arbitrary <paramref name="source"/>: the source's packages (live)
    /// joined with the install registry (live) into package cards with Install / Update / Installed
    /// status. Shared by the node Overview and the platform-admin settings tab.
    /// </summary>
    internal static IObservable<UiControl?> RenderFromSource(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel)
    {
        var installed = ObserveInstalled(host);
        return ObserveAvailable(host, source, sourceRef)
            .CombineLatest(installed, ObserveViewerIsGlobalAdmin(host),
                (available, inst, isAdmin) => (UiControl?)BuildCatalog(
                    host, source, sourceRef, description, sourceLabel, available, inst, isAdmin))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingCatalog")));
    }

    /// <summary>
    /// The viewer's global-admin status as a LIVE flag for the catalog view: false until the
    /// permission evaluator positively confirms admin, then tracking it — the stream stays live and
    /// <c>DistinctUntilChanged</c>, so a later revocation (or a faulted-and-caught emission) flips
    /// it back to false and the view re-renders in the non-admin shape. That is the point: the flag
    /// follows the grant rather than latching. Never <c>Take(1)</c> on the first emission — the
    /// evaluator seeds a premature <c>false</c> before its <c>AccessAssignment</c> query lands,
    /// which would freeze an admin's view into the non-admin shape; and never <c>Take(1)</c> at
    /// all, because this feeds a live data-bound view.
    /// </summary>
    private static IObservable<bool> ObserveViewerIsGlobalAdmin(LayoutAreaHost host)
    {
        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return(false);
        return host.Hub.IsGlobalAdmin(viewerId!)
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .StartWith(false)
            // After StartWith, so the evaluator's own seeded false does not re-render the view.
            .DistinctUntilChanged();
    }

    /// <summary>The signed-in viewer's id, or null when nobody is signed in.</summary>
    internal static string? ResolveViewerId(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
    }

    // Live list of installable packages from the given source at its ref.
    private static IObservable<IReadOnlyList<PackageManifest>> ObserveAvailable(
        LayoutAreaHost host, IPackageSource? source, string sourceRef)
    {
        if (source is null)
            return Observable.Return<IReadOnlyList<PackageManifest>>([]);
        return source.ListPackages(sourceRef)
            .Catch<IReadOnlyList<PackageManifest>, Exception>(ex =>
            {
                Logger(host)?.LogWarning(ex, "Catalog: failed to list packages @ {Ref}", sourceRef);
                return Observable.Return<IReadOnlyList<PackageManifest>>([]);
            })
            .StartWith((IReadOnlyList<PackageManifest>)[]);
    }

    // Selects the git-based package source for a repo path/subdir (delegates to the shared factory so
    // the node view and the registry endpoints build sources identically). Null when unconfigured.
    internal static IPackageSource? BuildSource(LayoutAreaHost host, string? sourceRepoPath, string? sourceSubdir) =>
        PackageSources.FromRepo(host.Hub, sourceRepoPath, sourceSubdir, Logger(host));

    /// <summary>
    /// The live installed-plugin inventory: every <c>Package</c> record in the install registry,
    /// deserialized to its <see cref="PackageManifest"/> and sorted by display name. This is the
    /// read-only "what is running on this instance" view the About tab shows every user — the
    /// catalog cards above join the SAME records against a package source for install status.
    /// </summary>
    public static IObservable<IReadOnlyList<PackageManifest>> ObserveInstalledManifests(LayoutAreaHost host)
        => ObserveInstalled(host).Select(nodes => (IReadOnlyList<PackageManifest>)nodes
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
            .Select(m => m!)
            .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList());

    // Live map of installed packages (the Plugins registry children), as a list.
    private static IObservable<IReadOnlyList<MeshNode>> ObserveInstalled(LayoutAreaHost host)
    {
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        return mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items.ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType switch
                    {
                        QueryChangeType.Added or QueryChangeType.Updated => map.SetItem(item.Path, item),
                        QueryChangeType.Removed => map.Remove(item.Path),
                        _ => map
                    };
                return map;
            })
            .Select(m => (IReadOnlyList<MeshNode>)m.Values.ToList())
            .StartWith((IReadOnlyList<MeshNode>)[]);
    }

    private static UiControl BuildCatalog(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel,
        IReadOnlyList<PackageManifest> available, IReadOnlyList<MeshNode> installed, bool viewerIsGlobalAdmin)
    {
        var installedById = installed
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
            .GroupBy(m => m!.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!, StringComparer.Ordinal);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("width: 100%; max-width: 900px; margin: 0 auto; padding: 16px;");

        container = container.WithView(Controls.H1(host.Localize("ui.pluginCatalog")).WithStyle("margin: 0 0 4px 0;"));

        if (!string.IsNullOrWhiteSpace(description))
            container = container.WithView(Controls.Markdown(description!).WithStyle("margin-bottom: 8px;"));

        container = container.WithView(Controls.Body(
                source is null
                    ? "No source configured."
                    : $"Source: {sourceLabel ?? "registry"} — {available.Count} package(s) available.")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px; display: block;"));

        if (available.Count == 0)
            container = container.WithView(Controls.Markdown(host.Localize("ui.mdNoPackages")));

        // The whole catalog + what is already installed are what a click needs to resolve the
        // package's dependency closure (PackageDependencyGraph.InstallClosure) — both are already
        // in hand here, so the card carries them down rather than re-listing the source on click.
        var installedIds = installedById.Keys.ToImmutableHashSet(StringComparer.Ordinal);

        var n = 0;
        foreach (var pkg in available)
        {
            n++;
            installedById.TryGetValue(pkg.Id, out var inst);
            container = container.WithView(
                BuildCard(host, source, sourceRef, pkg, inst, viewerIsGlobalAdmin, available, installedIds),
                $"pkg-{n}");
        }

        var orphans = Orphaned(available, installed, host.Hub.JsonSerializerOptions);
        if (orphans.Count > 0)
        {
            container = container.WithView(Controls.H2(host.Localize("ui.orphanedInstallRecords"))
                .WithStyle("margin: 24px 0 4px 0;"));
            container = container.WithView(Controls.Markdown(host.Localize("ui.mdOrphanedInstallRecords"))
                .WithStyle("margin-bottom: 8px;"));
            var o = 0;
            foreach (var orphan in orphans)
            {
                o++;
                container = container.WithView(
                    BuildOrphanCard(host, orphan, viewerIsGlobalAdmin), $"orphan-{o}");
            }
        }

        return container;
    }

    /// <summary>
    /// The install records this source no longer offers — a record whose package left the registry
    /// (#840). These have no catalog card, so before this list existed nothing could remove them:
    /// the <c>Plugins/_Policy</c> caps delete for every user identity, and the only system-identity
    /// removal was the (card-driven) Uninstall.
    ///
    /// <para>Deliberately computed ONLY against a NON-EMPTY available list. An empty list means
    /// either "the registry offers nothing" or "listing it failed" (<see cref="ObserveAvailable"/>
    /// catches a failure to an empty list, and the stream starts empty) — and those are
    /// indistinguishable here. Offering to remove EVERY install record because a registry was
    /// briefly unreachable is exactly the kind of destructive guess this must never make.</para>
    /// </summary>
    internal static IReadOnlyList<PackageManifest> Orphaned(
        IReadOnlyList<PackageManifest> available, IReadOnlyList<MeshNode> installed,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (available.Count == 0)
            return [];
        var availableIds = available.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return installed
            .Select(n => n.ContentAs<PackageManifest>(options))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id) && !availableIds.Contains(m.Id))
            .Select(m => m!)
            .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // An orphaned install record: what it says it is, and — for a global admin — the Remove action
    // that runs the installer's system-impersonated removal (the same identity that wrote it).
    private static UiControl BuildOrphanCard(
        LayoutAreaHost host, PackageManifest orphan, bool viewerIsGlobalAdmin)
    {
        var card = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px dashed var(--neutral-stroke-rest); border-radius: 8px; " +
                       "padding: 14px 16px; margin-bottom: 12px;");

        card = card.WithView(Controls.Body(orphan.Name ?? orphan.Id)
            .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"));

        card = card.WithView(Controls.Body(
                $"{orphan.Id}  ·  v{orphan.Version}  ·  → {orphan.TargetPartition ?? orphan.Id}")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"));

        if (!viewerIsGlobalAdmin)
            return card.WithView(Controls.Body(host.Localize("ui.requiresGlobalAdmin"))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block;"));

        return card.WithView(Controls.Button(host.Localize("ui.removeInstallRecord"))
            .WithClickAction(ctx =>
            {
                RemoveInstallRecord(host, orphan.Id);
                return Task.CompletedTask;
            }));
    }

    /// <summary>
    /// Removes an orphaned install record through the installer's sanctioned system-impersonated
    /// primitive. The AUTHORIZATION is the global-admin gate on the surface that offered the action
    /// (<see cref="BuildOrphanCard"/>) — the same "the click authorizes, the SYSTEM executes"
    /// division the install path uses; the removal itself must run as System because the
    /// <c>Plugins</c> partition policy denies delete to every user identity by design.
    /// </summary>
    internal static void RemoveInstallRecord(LayoutAreaHost host, string packageId)
    {
        var logger = Logger(host);
        PackageInstaller.RemoveInstalledRecord(host.Hub, packageId, logger)
            .Subscribe(
                removed => logger?.LogInformation(
                    "Orphaned install record {Id}: {Result}.", packageId, removed ? "removed" : "not found"),
                ex => logger?.LogWarning(ex, "Removing orphaned install record {Id} failed.", packageId));
    }

    private static UiControl BuildCard(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, PackageManifest pkg,
        PackageManifest? installed, bool viewerIsGlobalAdmin,
        IReadOnlyList<PackageManifest> catalog, IReadOnlySet<string> installedIds)
    {
        var card = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; " +
                       "padding: 14px 16px; margin-bottom: 12px;");

        card = card.WithView(Controls.Body(pkg.Name ?? pkg.Id)
            .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"));

        if (!string.IsNullOrWhiteSpace(pkg.Description))
            card = card.WithView(Controls.Body(pkg.Description!).WithStyle("display: block; margin-bottom: 6px;"));

        card = card.WithView(Controls.Body($"v{pkg.Version}  ·  {pkg.Kind}  ·  → {pkg.TargetPartition}")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"));

        // ModuleVersion (the module's OWN content hash from manifest.lock) beats the whole-repo
        // commit sha: an unrelated commit no longer flips every card to "Update". The commit-sha
        // compare stays the fallback for manifest-less packages.
        var upToDate = installed is not null
            && (!string.IsNullOrEmpty(pkg.ModuleVersion) && !string.IsNullOrEmpty(installed.ModuleVersion)
                ? string.Equals(installed.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal)
                : string.Equals(installed.Version, pkg.Version, StringComparison.Ordinal));

        if (upToDate)
        {
            card = card.WithView(Controls.Body($"✓ Installed v{installed!.Version}")
                .WithStyle("color: var(--success-foreground, #107c10); font-weight: 600;"));
        }
        else if (pkg.IsCommercial() && !viewerIsGlobalAdmin)
        {
            // A commercial package needs Global Admin to install or sync (#830). The real
            // enforcement is on the ACTION (PackageEntitlement, inside the installer); this is only
            // so a viewer is not offered a button whose click would be refused.
            card = card.WithView(Controls.Body(host.Localize("ui.requiresGlobalAdmin"))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block;"));
        }
        else if (source is not null)
        {
            var label = installed is null ? "Install" : $"Update to v{pkg.Version}";
            card = card.WithView(Controls.Button(label)
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    InstallPackage(host, source, sourceRef, pkg, catalog, installedIds);
                    return Task.CompletedTask;
                }));
        }

        return card;
    }

    /// <summary>
    /// Fire the install; the Plugins-registry stream re-emits on completion → the card flips.
    ///
    /// <para>Installs the package's DEPENDENCY CLOSURE, not just the package: every requirement
    /// (<see cref="PackageManifest.Requires"/>) the instance does not yet have is installed first,
    /// in dependency order, on the one Concat so each finishes before the next begins. Without it a
    /// click on a dependent simply fails — the installer refuses an instance whose NodeType is not
    /// present ("NodeType(s) not registered: Training/Tour"), naming a path that appears in neither
    /// the package the user clicked nor any error they can act on. The unattended boot pass
    /// (<see cref="InstanceAutoRegistrationService"/>) has derived this order for a while; the
    /// click did not, which is the half #636 closes.</para>
    /// </summary>
    /// <param name="catalog">Every package the source offers — the dependency resolution universe.
    /// Omitted (a single-package caller) means only <paramref name="pkg"/> installs, exactly as
    /// before.</param>
    /// <param name="installedIds">Package ids already in the install registry; those dependencies
    /// are skipped rather than re-installed.</param>
    internal static void InstallPackage(
        LayoutAreaHost host, IPackageSource source, string sourceRef, PackageManifest pkg,
        IReadOnlyList<PackageManifest>? catalog = null, IReadOnlySet<string>? installedIds = null)
    {
        var logger = Logger(host);

        // 🚨 The install runs under SYSTEM for its WHOLE lifetime — an install is PROVISIONING,
        // not a user data write. Post core #804 every partition the installer creates lands under
        // the System identity with no user grants, so the CLICKING user legitimately holds NOTHING
        // on it mid-install — any step that authorises against the ambient identity then fails
        // closed. The previous code re-established the clicking USER's context here instead, and
        // #817's batch topology made exactly such a step deterministic: the self-typed root's
        // reconciliation read (PackageInstaller.RootRetypeReconciled → the per-user gate in
        // MeshNodeStreamCache) ran as the user and every Store install died with
        // "User '…' lacks Read permission on 'Store'" (education CI, 2026-08-05, first image
        // carrying #817). System is also what the OTHER install triggers already do — the
        // PluginUpdateWatcher wraps this very InstallOrUpdate in ImpersonateAsSystem, and the
        // Store plugin's SystemInstall/Provisioning sources do the same. Authorisation for the
        // TRIGGER stays where it belongs: on the catalog surface the click came from.
        // REQUIRED, never optional: a missing AccessService would silently run the install under
        // the ambient (user) identity — the exact regression this fix removes. Same treatment the
        // PluginUpdateWatcher already gives it.
        var accessService = host.Hub.ServiceProvider.GetRequiredService<AccessService>();

        // WHO authorized the install — captured HERE, while the ambient identity is still the
        // clicking user's, because the install below runs entirely as System. A commercial package
        // requires this principal to be a global admin (#830); a free one ignores it. The check
        // itself lives in the installer, so the machine paths cannot bypass it.
        var authorizingUserId = ResolveViewerId(host);

        IReadOnlyList<PackageManifest> closure;
        try
        {
            closure = PackageDependencyGraph.InstallClosure(
                pkg, catalog ?? [pkg], installedIds ?? ImmutableHashSet<string>.Empty, logger);
        }
        catch (InvalidOperationException ex)
        {
            // A declared cycle: there is no order that works, so installing anything would fail
            // later with a NodeType path naming neither package. Refuse with the named loop —
            // the boot pass deliberately keeps the tolerant behaviour instead (it must not strand
            // a whole instance over one malformed package).
            logger?.LogWarning(ex, "Install of {Id} refused: {Reason}", pkg.Id, ex.Message);
            return;
        }

        if (closure.Count > 1)
            logger?.LogInformation(
                "Installing {Id} with {Count} dependency package(s) first — {Closure}",
                pkg.Id, closure.Count - 1, string.Join(", ", closure.Select(p => p.Id)));

        // Sequential (Concat): a dependency's install must COMPLETE before the dependent's begins,
        // which is what makes its NodeType nodes present for the dependent's type validation.
        var install = closure
            .Select(p => InstallOrUpdate(host.Hub, source, sourceRef, p, logger, authorizingUserId)
                .Do(result => logger?.LogInformation(
                    "Installed {Id}: {Written} written, {Unchanged} unchanged.",
                    p.Id, result.Written, result.Unchanged)))
            .ToObservable()
            .Concat();

        Observable.Using(() => accessService.ImpersonateAsSystem(), _ => install)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Install of {Id} failed.", pkg.Id));
    }

    /// <summary>
    /// The install/update orchestrator. For a manifest-carrying node-repo package it skips or
    /// narrows the work by the module manifest: an installed record with the SAME
    /// <see cref="PackageManifest.ModuleVersion"/> means nothing to sync (no fetch, no record
    /// rewrite); a differing one fetches only <c>manifest.lock</c>, diffs it against the record's
    /// installed-files baseline and updates just the changed nodes (pruning removed ones). Every
    /// other case — no manifest, no baseline, a shared-Source change (whose blast radius is every
    /// type in the package), or ANY error on the incremental path — falls back to the legacy full
    /// install, which is always correct.
    /// </summary>
    internal static IObservable<InstallResult> InstallOrUpdate(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg, ILogger? logger,
        string? authorizingUserId = null)
    {
        // The entitlement gate runs FIRST, before a single file travels: a commercial package needs
        // a global admin (#830), and fetching a package that may not be installed is work nobody
        // asked for. The installer carries the same gate — that one is the enforcement (no caller
        // can bypass it), this one is where the refusal is cheapest.
        return PackageEntitlement.Authorize(hub, pkg, authorizingUserId, logger)
            .SelectMany(_ => InstallOrUpdateCore(hub, source, sourceRef, pkg, logger, authorizingUserId));
    }

    private static IObservable<InstallResult> InstallOrUpdateCore(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg, ILogger? logger,
        string? authorizingUserId)
    {
        IObservable<InstallResult> Full() =>
            source.FetchPackageFiles(pkg, sourceRef)
                .SelectMany(files => PackageInstaller.Install(
                    hub, pkg, files, sourceRef, logger,
                    authorizingUserId: authorizingUserId));

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (pkg.Kind != PackageKind.NodeRepo || string.IsNullOrEmpty(pkg.ModuleVersion) || persistence is null)
            return Full();

        // The authoritative install record (the same read UpsertIfChanged uses) — the diff baseline.
        return persistence.Read($"{PackageInstaller.InstalledPartition}/{pkg.Id}", hub.JsonSerializerOptions)
            .Take(1)
            .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            .Catch<PackageManifest?, Exception>(_ => Observable.Return<PackageManifest?>(null))
            .SelectMany(record =>
            {
                if (record is not null
                    && string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
                {
                    logger?.LogInformation(
                        "Package {Id} is up to date (module {ModuleVersion}); nothing to sync.",
                        pkg.Id, pkg.ModuleVersion);
                    return Observable.Return(new InstallResult(0, 0));
                }
                if (record?.InstalledFiles is not { Count: > 0 })
                    return Full();
                return IncrementalUpdate(hub, source, sourceRef, pkg, record, logger, authorizingUserId)
                    .Catch<InstallResult, Exception>(ex =>
                    {
                        // A REFUSAL is not a failure to fall back from — the full install would be
                        // refused identically, and retrying it would bury the reason under a
                        // second, misleading log line.
                        if (ex is PackageAuthorizationException)
                            return Observable.Throw<InstallResult>(ex);
                        logger?.LogWarning(ex,
                            "Incremental update of {Id} failed; falling back to full install.", pkg.Id);
                        return Full();
                    });
            });
    }

    // The manifest-diff fast path: fetch only manifest.lock, diff, fetch only the changed files.
    private static IObservable<InstallResult> IncrementalUpdate(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg,
        PackageManifest record, ILogger? logger, string? authorizingUserId)
    {
        var manifestPath = $"{pkg.Id}/{ModuleManifest.FileName}";
        return source.FetchPackageFiles(pkg, sourceRef, [manifestPath])
            .SelectMany(files =>
            {
                var newManifest = files
                    .Where(f => ModuleManifest.IsManifestPath(f.RelativePath))
                    .Select(f => ModuleManifest.TryParse(f.Content, logger))
                    .FirstOrDefault(m => m is not null);
                if (newManifest is null)
                    throw new InvalidOperationException(
                        $"Package '{pkg.Id}' ships no parseable {ModuleManifest.FileName}.");

                var delta = newManifest.DiffFrom(record.InstalledFiles);

                // A change to the package's SHARED Source/Test (partition-level compile inputs)
                // affects every type in the package — the full install's release-all handles that;
                // the delta's owner-derivation would miss siblings.
                var sharedPrefixes = new[] { $"{pkg.Id}/Source/", $"{pkg.Id}/Test/" };
                if (delta.AddedOrChangedFiles.Concat(delta.RemovedFiles)
                    .Any(p => sharedPrefixes.Any(s => p.StartsWith(s, StringComparison.Ordinal))))
                    throw new InvalidOperationException(
                        $"Package '{pkg.Id}' changed shared Source/Test files; full install required.");

                var changedNodePaths = delta.AddedOrChangedFiles
                    .Select(PackageInstaller.NodePathForFile)
                    .Where(p => p is not null).Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);
                // Removed FILES prune their nodes — unless the node is still fed by a changed file
                // (the `X.json` → `X/index.json` layout move maps both to node X).
                var removedNodePaths = delta.RemovedFiles
                    .Select(PackageInstaller.NodePathForFile)
                    .Where(p => p is not null && !changedNodePaths.Contains(p))
                    .Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);

                logger?.LogInformation(
                    "Updating {Id} incrementally: {Changed} changed file(s), {Removed} removed → module {ModuleVersion}.",
                    pkg.Id, delta.AddedOrChangedFiles.Count, delta.RemovedFiles.Count, newManifest.ModuleVersion);

                return (delta.AddedOrChangedFiles.Count == 0
                        ? Observable.Return((IReadOnlyList<PackageFile>)[])
                        : source.FetchPackageFiles(pkg, sourceRef, delta.AddedOrChangedFiles))
                    .SelectMany(changedFiles => PackageInstaller.InstallNodeRepoDelta(
                        hub, pkg, newManifest, changedFiles, removedNodePaths, sourceRef, logger,
                        authorizingUserId));
            });
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.Catalog");
}
