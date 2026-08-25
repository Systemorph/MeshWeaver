using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The registry-backed <see cref="IModuleSourceBrowser"/> (MeshWeaver#2193 §C): lists a module's
/// compile inputs from the package's <c>manifest.lock</c> as the registry serves it, and reads a
/// file's text through the registry's authenticated <c>/api/plugins/files</c> route with a
/// one-path filter — the very lane installs already use, so nothing new is exposed and nothing
/// a consumer could not already fetch is served. The registry resolves the package from its
/// curated catalog and reads with ITS credential; this mesh presents only the instance key it
/// already holds.
///
/// <para>Registries are the ones this mesh installs from (<see cref="PluginCatalogOptions"/>,
/// legacy single entry folded in exactly as the default install does). A package is looked up
/// across them in configured order; the first registry advertising it serves it. Reactive
/// end-to-end; cold; every call is its own read — the browse surface is rare and small, and a
/// cache here would only hide a registry that stopped serving.</para>
/// </summary>
public sealed class RegistrySourceBrowser(
    IMessageHub hub,
    RegistryTokenResolver tokens,
    IOptions<PluginCatalogOptions> options,
    ILogger<RegistrySourceBrowser>? logger = null) : IModuleSourceBrowser
{
    private sealed record Hit(RegistryPackageSource Source, string GitRef, PackageManifest Package);

    public IObservable<IReadOnlyList<ModuleSourceFile>> ListSources(string packageId) =>
        Find(packageId).SelectMany(hit => hit is null
            ? Observable.Return<IReadOnlyList<ModuleSourceFile>>([])
            : hit.Source.FetchPackageFiles(hit.Package, hit.GitRef, [ManifestPath(packageId)])
                .Take(1)
                .Select(files => SourcesOf(files
                    .FirstOrDefault(f => ModuleManifest.IsManifestPath(f.RelativePath))?.Content, logger)));

    public IObservable<string?> FetchSource(string packageId, string nodePath) =>
        Find(packageId).SelectMany(hit => hit is null
            ? Observable.Return<string?>(null)
            : hit.Source.FetchPackageFiles(hit.Package, hit.GitRef, [ManifestPath(packageId)])
                .Take(1)
                .SelectMany(files =>
                {
                    var file = SourcesOf(files
                            .FirstOrDefault(f => ModuleManifest.IsManifestPath(f.RelativePath))?.Content, logger)
                        .FirstOrDefault(f => string.Equals(f.NodePath, nodePath, StringComparison.Ordinal));
                    return file is null
                        ? Observable.Return<string?>(null)
                        : hit.Source.FetchPackageFiles(hit.Package, hit.GitRef, [file.RelativePath])
                            .Take(1)
                            .Select(list => list.FirstOrDefault()?.Content);
                }));

    // ─────────────────────────────────────────────────────────────────── pure

    /// <summary>The package's manifest path inside its files — the same path the installer's
    /// incremental update fetches.</summary>
    public static string ManifestPath(string packageId) => $"{packageId}/{ModuleManifest.FileName}";

    /// <summary>The compile inputs a manifest lists, as browsable files: every entry under a
    /// <c>Source/</c>/<c>Test/</c> directory (<see cref="NodeFileMapper.IsCompileInputPath"/>),
    /// keyed by the node path it would have had. No manifest, or an unparseable one, lists
    /// nothing. Pure.</summary>
    public static IReadOnlyList<ModuleSourceFile> SourcesOf(string? manifestJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return [];
        var manifest = ModuleManifest.TryParse(manifestJson!, logger);
        if (manifest is null)
            return [];
        return manifest.Files.Keys
            .Where(NodeFileMapper.IsCompileInputPath)
            .Select(ToSourceFile)
            .OrderBy(f => f.NodePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A repo file → the node it would have been: the installer's own path mapping
    /// (<see cref="NodeFileMapper.FromRelativePath"/>), so a browsed file and an imported node
    /// share one address. Pure.</summary>
    public static ModuleSourceFile ToSourceFile(string relativePath)
    {
        var (id, ns) = NodeFileMapper.FromRelativePath(relativePath);
        var nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        var slash = relativePath.LastIndexOf('/');
        return new ModuleSourceFile(nodePath, relativePath, slash < 0 ? relativePath : relativePath[(slash + 1)..]);
    }

    // The first configured registry advertising the package, with the source to fetch it from.
    private IObservable<Hit?> Find(string packageId)
    {
        var registries = RegistryTokenResolver.WithLegacyTokens(options.Value, options.Value.EffectiveRegistries);
        if (registries.Count == 0)
            return Observable.Return<Hit?>(null);
        return registries
            .Select(registry => tokens.ResolveToken(registry).Take(1)
                .SelectMany(token =>
                {
                    var gitRef = string.IsNullOrWhiteSpace(registry.Ref) ? "HEAD" : registry.Ref;
                    var source = new RegistryPackageSource(hub, registry.Url, token);
                    return source.ListPackages(gitRef).Take(1)
                        .Select(packages => packages
                            .Where(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))
                            .Select(p => (Hit?)new Hit(source, gitRef, p))
                            .FirstOrDefault())
                        .Catch<Hit?, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "Source browsing: listing {Registry} for '{Package}' failed — trying the next registry",
                                registry.Url, packageId);
                            return Observable.Return<Hit?>(null);
                        });
                }))
            .ToObservable()
            .Concat()
            .FirstOrDefaultAsync(hit => hit is not null)
            .Select(hit => hit);
    }
}
