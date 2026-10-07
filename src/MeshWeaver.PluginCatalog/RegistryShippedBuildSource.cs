using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The plugin catalog's <see cref="IShippedBuildSource"/> — where a pod gets the SHIPPED bytes
/// a NodeType record names when its own assembly store lacks them (MeshWeaver#6052 ask 2), instead
/// of compiling them locally or serving the assembly-unavailable card.
///
/// <para><b>Which package.</b> A plugin installs into the partition named by its folder, so a
/// NodeType's package is its path's FIRST SEGMENT (<c>Publish/Slide</c> → <c>Plugins/Publish</c>);
/// the install record must exist, or this installation never installed that package and there is
/// nothing it may ask a registry for. A type whose first segment names no installed package is
/// reported as not found — never guessed.</para>
///
/// <para><b>Which registry.</b> The configured registries, in order, each with the key this
/// installation already uses for them (<see cref="RegistryTokenResolver"/>); the first that serves
/// a compatible bundle answers. The read is <see cref="PluginBundleClient.FetchShippedBuilds"/>:
/// same index, same download route (OCI artifact by digest, else HTTP), same compatibility rule as
/// every adoption (policy <c>platform-backwards-compatibility</c>), and it writes nothing.</para>
///
/// <para><b>One download per package, shared.</b> Every activation of every instance of a type
/// whose bytes are missing asks; the in-flight read of a package is shared through an instance
/// <see cref="PromiseCache{TKey, TValue}"/> and released pair-exact once it SETTLES (never on one
/// asker's cancellation), so concurrent askers cost one download and the bytes are not held for
/// the life of the process. A fault evicts (the cache's contract), so a later ask is a genuinely
/// new attempt.</para>
/// </summary>
public sealed class RegistryShippedBuildSource : IShippedBuildSource
{
    private readonly IMessageHub hub;
    private readonly ILogger<RegistryShippedBuildSource>? logger;
    private readonly PromiseCache<string, IReadOnlyList<ShippedBuild>> inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the source.</summary>
    /// <param name="hub">The mesh hub — resolves the catalog options, the token resolver, the storage
    /// adapter the install records are read through, and the pools the bundle client runs on.</param>
    public RegistryShippedBuildSource(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<RegistryShippedBuildSource>();
    }

    /// <summary>The package a NodeType path belongs to: its first path segment. Pure.</summary>
    /// <param name="nodeTypePath">The NodeType path.</param>
    public static string? PackageOf(string nodeTypePath)
    {
        if (string.IsNullOrWhiteSpace(nodeTypePath))
            return null;
        var slash = nodeTypePath.IndexOf('/');
        var head = slash < 0 ? nodeTypePath : nodeTypePath[..slash];
        return head.Length == 0 ? null : head;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<ShippedBuild>> Fetch(IReadOnlyCollection<string> nodeTypePaths)
    {
        ArgumentNullException.ThrowIfNull(nodeTypePaths);
        var wanted = nodeTypePaths.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = wanted
            .Select(PackageOf)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (packages.Count == 0)
            return Observable.Return<IReadOnlyList<ShippedBuild>>([]);

        return packages
            .Select(FetchPackage)
            .Concat()
            .Aggregate(ImmutableList<ShippedBuild>.Empty, (all, builds) =>
                all.AddRange(builds.Where(b => wanted.Contains(b.NodeTypePath))))
            .Select(all => (IReadOnlyList<ShippedBuild>)all);
    }

    private IObservable<IReadOnlyList<ShippedBuild>> FetchPackage(string packageId)
        => Observable.Defer(() =>
        {
            // ONE read per package while it is in flight: hot and replayed (the IoPool `Run` shape —
            // the read is subscribed once into a ReplaySubject and terminates by itself), so a
            // subscriber that cancels never cancels it for the others.
            var shared = inFlight.GetOrAdd(packageId, id =>
            {
                var settled = new ReplaySubject<IReadOnlyList<ShippedBuild>>(1);
                ReadPackage(id).Subscribe(settled);
                return settled.AsObservable();
            });
            // Released once SETTLED, never on a subscriber's cancellation, and pair-exact — a
            // replacement a later caller installed is never dropped. A fault is evicted by the cache
            // itself. The bytes are therefore held only while the read is in flight.
            return shared.Do(_ => { }, () => inFlight.Release(packageId, shared));
        });

    private IObservable<IReadOnlyList<ShippedBuild>> ReadPackage(string packageId)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var tokens = hub.ServiceProvider.GetService<RegistryTokenResolver>();
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        var registries = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries);
        if (storage is null || tokens is null || registries.Count == 0)
        {
            logger?.LogInformation(
                "Shipped-build source: cannot ask for {Package} — {Missing}",
                packageId,
                registries.Count == 0
                    ? "this installation reads no plugin registry (PluginCatalog:Registries is empty)"
                    : "the storage adapter or registry token resolver is not registered");
            return Observable.Return<IReadOnlyList<ShippedBuild>>([]);
        }

        var recordPath = $"{PackageInstaller.InstalledPartition}/{packageId}";
        return storage.Read(recordPath, hub.JsonSerializerOptions)
            .Take(1)
            .SelectMany(record =>
            {
                if (record is null)
                {
                    logger?.LogInformation(
                        "Shipped-build source: {Package} is not installed here ({RecordPath} absent) — "
                        + "no registry is asked for its NodeTypes", packageId, recordPath);
                    return Observable.Return<IReadOnlyList<ShippedBuild>>([]);
                }
                return registries
                    .Select(registry => Observable.Defer(() => tokens.ResolveToken(registry).Take(1)
                        .SelectMany(token => new PluginBundleClient(hub, registry.Url, token)
                            .FetchShippedBuilds(packageId))))
                    .Concat()
                    .Where(builds => builds.Count > 0)
                    .Take(1)
                    .DefaultIfEmpty([]);
            });
    }
}
