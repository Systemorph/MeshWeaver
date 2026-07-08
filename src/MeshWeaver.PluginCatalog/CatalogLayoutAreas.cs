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
/// The catalog browse/install view for a <c>PluginCatalog</c> node: lists the installable packages
/// found in the node's configured source git repo at its ref, shows each package's install status
/// (comparing against the <c>Plugins</c> install registry), and offers an Install / Update button
/// that runs the git-based install. Reactive end to end — after an install the registry stream
/// re-emits and the affected card flips to "Installed" with no manual refresh.
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
    /// Renders the catalog: the configured source's packages (live) joined with the install
    /// registry (live) into a list of package cards with Install / Update / Installed status.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the catalog view.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Catalog(LayoutAreaHost host, RenderingContext _)
    {
        var installed = ObserveInstalled(host);
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<PluginCatalogContent>(host.Hub.JsonSerializerOptions))
            .Select(cfg => ObserveAvailable(host, cfg).Select(available => (Cfg: cfg, Available: available)))
            .Switch()
            .CombineLatest(installed, (a, inst) => (UiControl?)BuildCatalog(host, a.Cfg, a.Available, inst))
            .StartWith((UiControl?)Controls.Markdown("*Loading catalog…*"));
    }

    // Live list of installable packages from the node's configured source (local git repo or a
    // remote GitHub repo) at its ref.
    private static IObservable<IReadOnlyList<PackageManifest>> ObserveAvailable(
        LayoutAreaHost host, PluginCatalogContent? cfg)
    {
        var source = BuildSource(host, cfg);
        if (source is null)
            return Observable.Return<IReadOnlyList<PackageManifest>>([]);
        return source.ListPackages(cfg!.SourceRef)
            .Catch<IReadOnlyList<PackageManifest>, Exception>(ex =>
            {
                Logger(host)?.LogWarning(ex,
                    "Catalog: failed to list packages from {Src}@{Ref}", cfg.SourceRepoPath, cfg.SourceRef);
                return Observable.Return<IReadOnlyList<PackageManifest>>([]);
            })
            .StartWith((IReadOnlyList<PackageManifest>)[]);
    }

    // Selects the package source from the catalog config: a URL uses the GitHub-fetch source (reusing
    // GitSync's client — anonymous for a public repo), a local path uses the git-CLI source. Both are
    // git-based, no NuGet.
    private static IPackageSource? BuildSource(LayoutAreaHost host, PluginCatalogContent? cfg)
    {
        if (cfg?.SourceRepoPath is not { Length: > 0 } src)
            return null;
        var subdir = cfg.SourceSubdir ?? "";
        if (IsUrl(src))
        {
            var client = host.Hub.ServiceProvider.GetService<IGitHubRepoClient>();
            if (client is null)
            {
                Logger(host)?.LogWarning(
                    "Catalog source {Src} is a URL but no IGitHubRepoClient is registered.", src);
                return null;
            }
            return new GitHubPackageSource(client.Fetch, src, token: "", subdir, Logger(host));
        }
        var git = new GitCli(host.Hub.ServiceProvider.GetRequiredService<IoPoolRegistry>());
        return new GitPackageSource(git, src, subdir, Logger(host));
    }

    private static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("git@", StringComparison.Ordinal);

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
        LayoutAreaHost host, PluginCatalogContent? cfg,
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

        if (!string.IsNullOrWhiteSpace(cfg?.Description))
            container = container.WithView(Controls.Markdown(cfg!.Description!).WithStyle("margin-bottom: 8px;"));

        container = container.WithView(Controls.Body(
                cfg?.SourceRepoPath is { Length: > 0 } p
                    ? $"Source: {p} @ {cfg.SourceRef} — {available.Count} package(s) available."
                    : "No source configured. Set SourceRepoPath on this catalog node.")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px; display: block;"));

        if (available.Count == 0)
            container = container.WithView(Controls.Markdown("*No installable packages found.*"));

        var n = 0;
        foreach (var pkg in available)
        {
            n++;
            installedById.TryGetValue(pkg.Id, out var inst);
            container = container.WithView(BuildCard(host, cfg, pkg, inst), $"pkg-{n}");
        }

        return container;
    }

    private static UiControl BuildCard(
        LayoutAreaHost host, PluginCatalogContent? cfg, PackageManifest pkg, PackageManifest? installed)
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

        var upToDate = installed is not null
            && string.Equals(installed.Version, pkg.Version, StringComparison.Ordinal);

        if (upToDate)
        {
            card = card.WithView(Controls.Body($"✓ Installed v{installed!.Version}")
                .WithStyle("color: var(--success-foreground, #107c10); font-weight: 600;"));
        }
        else
        {
            var label = installed is null ? "Install" : $"Update to v{pkg.Version}";
            card = card.WithView(Controls.Button(label)
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    InstallPackage(host, cfg, pkg);
                    return Task.CompletedTask;
                }));
        }

        return card;
    }

    // Fire the git-based install; the Plugins-registry stream re-emits on completion → card flips.
    private static void InstallPackage(LayoutAreaHost host, PluginCatalogContent? cfg, PackageManifest pkg)
    {
        var logger = Logger(host);
        var source = BuildSource(host, cfg);
        if (source is null)
        {
            logger?.LogWarning("Install '{Id}' skipped: catalog has no usable source.", pkg.Id);
            return;
        }

        // Capture the caller's identity NOW — the click delivery stamped AccessService.Context. The
        // fetch runs on the Process IIoPool (GitCli), so the install continuation lands on a pool
        // thread where that AsyncLocal is wiped; re-establish it for the install's whole lifetime via
        // Observable.Using so the CreateOrUpdate writes run as the user and don't fail closed.
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var user = accessService?.Context;

        source.FetchPackageFiles(pkg, cfg!.SourceRef)
            .SelectMany(files => accessService is null
                ? PackageInstaller.Install(host.Hub, pkg, files, cfg.SourceRef, logger)
                : Observable.Using(
                    () => accessService.SwitchAccessContext(user),
                    _ => PackageInstaller.Install(host.Hub, pkg, files, cfg.SourceRef, logger)))
            .Subscribe(
                count => logger?.LogInformation("Installed {Id}: {Count} node(s).", pkg.Id, count),
                ex => logger?.LogWarning(ex, "Install of {Id} failed.", pkg.Id));
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.Catalog");
}
