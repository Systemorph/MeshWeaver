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
/// node Overview builds its source from the node's <see cref="PluginCatalogContent"/>, while the
/// platform-admin <see cref="PluginCatalogSettingsTab"/> builds a <see cref="RegistryPackageSource"/>
/// pointed at the configured registry — both render + install through the same helpers here.</para>
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
            .StartWith((UiControl?)Controls.Markdown("*Loading catalog…*"));
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
            .CombineLatest(installed, (available, inst) =>
                (UiControl?)BuildCatalog(host, source, sourceRef, description, sourceLabel, available, inst))
            .StartWith((UiControl?)Controls.Markdown("*Loading catalog…*"));
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
        IReadOnlyList<PackageManifest> available, IReadOnlyList<MeshNode> installed)
    {
        var installedById = installed
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
            .GroupBy(m => m!.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!, StringComparer.Ordinal);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("width: 100%; max-width: 900px; margin: 0 auto; padding: 16px;");

        container = container.WithView(Controls.H1("Plugin Catalog").WithStyle("margin: 0 0 4px 0;"));

        if (!string.IsNullOrWhiteSpace(description))
            container = container.WithView(Controls.Markdown(description!).WithStyle("margin-bottom: 8px;"));

        container = container.WithView(Controls.Body(
                source is null
                    ? "No source configured."
                    : $"Source: {sourceLabel ?? "registry"} — {available.Count} package(s) available.")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px; display: block;"));

        if (available.Count == 0)
            container = container.WithView(Controls.Markdown("*No installable packages found.*"));

        var n = 0;
        foreach (var pkg in available)
        {
            n++;
            installedById.TryGetValue(pkg.Id, out var inst);
            container = container.WithView(BuildCard(host, source, sourceRef, pkg, inst), $"pkg-{n}");
        }

        return container;
    }

    private static UiControl BuildCard(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, PackageManifest pkg, PackageManifest? installed)
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
        else if (source is not null)
        {
            var label = installed is null ? "Install" : $"Update to v{pkg.Version}";
            card = card.WithView(Controls.Button(label)
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    InstallPackage(host, source, sourceRef, pkg);
                    return Task.CompletedTask;
                }));
        }

        return card;
    }

    // Fire the install; the Plugins-registry stream re-emits on completion → card flips.
    internal static void InstallPackage(LayoutAreaHost host, IPackageSource source, string sourceRef, PackageManifest pkg)
    {
        var logger = Logger(host);

        // Capture the caller's identity NOW — the click delivery stamped AccessService.Context. The
        // fetch runs off-hub (GitCli / Http IIoPool), so the install continuation lands on a pool
        // thread where that AsyncLocal is wiped; re-establish it for the install's whole lifetime via
        // Observable.Using so the CreateOrUpdate writes run as the user and don't fail closed.
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var user = accessService?.Context;

        var install = InstallOrUpdate(host.Hub, source, sourceRef, pkg, logger);
        (accessService is null
                ? install
                : Observable.Using(() => accessService.SwitchAccessContext(user), _ => install))
            .Subscribe(
                result => logger?.LogInformation("Installed {Id}: {Written} written, {Unchanged} unchanged.",
                    pkg.Id, result.Written, result.Unchanged),
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
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg, ILogger? logger)
    {
        IObservable<InstallResult> Full() =>
            source.FetchPackageFiles(pkg, sourceRef)
                .SelectMany(files => PackageInstaller.Install(hub, pkg, files, sourceRef, logger));

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
                return IncrementalUpdate(hub, source, sourceRef, pkg, record, logger)
                    .Catch<InstallResult, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "Incremental update of {Id} failed; falling back to full install.", pkg.Id);
                        return Full();
                    });
            });
    }

    // The manifest-diff fast path: fetch only manifest.lock, diff, fetch only the changed files.
    private static IObservable<InstallResult> IncrementalUpdate(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg,
        PackageManifest record, ILogger? logger)
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
                        hub, pkg, newManifest, changedFiles, removedNodePaths, sourceRef, logger));
            });
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.Catalog");
}
