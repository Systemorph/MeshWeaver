using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// One NodeType's SHIPPED build — the compiled bytes a published bundle carries for it — as a
/// <see cref="IShippedBuildSource"/> hands them back.
/// </summary>
/// <param name="NodeTypePath">The NodeType these bytes implement, as the bundle's manifest names it.</param>
/// <param name="Assembly">The compiled assembly.</param>
/// <param name="Pdb">Its symbols, when the bundle carried them.</param>
/// <param name="Origin">Where the bytes came from (a registry and package) — for the log line only.</param>
public sealed record ShippedBuild(string NodeTypePath, byte[] Assembly, byte[]? Pdb, string Origin);

/// <summary>
/// A build a NodeType RECORD names and this process's <see cref="IAssemblyStore"/> does not hold —
/// the key <see cref="ShippedBuildRefetch"/> re-lands shipped bytes under.
/// </summary>
/// <param name="NodeTypePath">The NodeType.</param>
/// <param name="Version">The store version the record names (<c>LastCompiledVersion</c>).</param>
/// <param name="ContentPath">The record's content path (<c>LatestAssemblyPath</c>), or null.</param>
/// <param name="AssemblyMvid">The record's published MVID (<c>LatestAssemblyMvid</c>, "N" hex), or null
/// when the record predates the field.</param>
public sealed record MissingBuild(string NodeTypePath, long Version, string? ContentPath, string? AssemblyMvid);

/// <summary>
/// 🚨 <b>Where a pod gets bytes its record names and its own store lacks — instead of compiling</b>
/// (MeshWeaver#6052 ask 2).
///
/// <para>A NodeType record is SHARED (database); the assembly store behind it may not be — a
/// per-pod <c>FileSystemAssemblyStore</c> (collection <c>local</c>) holds bytes for the pod that
/// wrote them only. So a type adopted or compiled on pod A leaves a record pointing at bytes pod B
/// never held, and before this seam pod B's only recoveries were a recompile routed to the type's
/// owner (which lands the bytes on the owner's pod again) or the assembly-unavailable card. The
/// bytes the record names were, in the common case, a bundle the registry SHIPPED — so they can be
/// fetched again, exactly, by anyone who can reach that registry.</para>
///
/// <para>Optional: a host that registers none keeps today's behaviour. The plugin catalog
/// registers one that reads the configured registries (<c>/api/plugins/bundles</c> or the OCI
/// publication).</para>
/// </summary>
public interface IShippedBuildSource
{
    /// <summary>
    /// The shipped builds the source can find for <paramref name="nodeTypePaths"/> — any subset,
    /// possibly empty. Compatibility (policy <c>platform-backwards-compatibility</c>) is the
    /// SOURCE's to judge: it returns only bytes keyed to the running platform's compatibility key
    /// and inside their declared platform range — never gated on an exact build identity. Whether
    /// they are the build a RECORD names is judged by <see cref="ShippedBuildRefetch"/>, by MVID.
    /// Emits once and completes; a source that cannot be reached emits an empty list and logs why.
    /// </summary>
    /// <param name="nodeTypePaths">The NodeTypes whose shipped bytes are wanted.</param>
    IObservable<IReadOnlyList<ShippedBuild>> Fetch(IReadOnlyCollection<string> nodeTypePaths);
}

/// <summary>
/// Re-lands SHIPPED bytes under the key a NodeType record names — the one place the
/// "bytes missing on this pod" state is turned back into a resolvable build without a compile
/// (MeshWeaver#6052 ask 2). See <see cref="IShippedBuildSource"/>.
///
/// <para>🚨 <b>Only the build the record names is landed.</b> A shipped build whose MVID differs
/// from the record's <see cref="MissingBuild.AssemblyMvid"/> is a DIFFERENT build (the record names
/// a local compile on another pod, or a newer adoption); landing it under the record's version
/// would hand the bind-time identity check (<c>ServedBuildIdentity.Mismatch</c>) bytes it must
/// refuse, and the refusal would recompile anyway — so it is not landed, and the line says so. A
/// record without an MVID (it predates the field) cannot be checked here; its bytes land and the
/// bind-time check stays the gate, as it is for every legacy record.</para>
///
/// <para>Content-addressed stores make the landing exact: the same bytes under the same version
/// land on the same content-hashed name the record already names, so the record needs no write and
/// no other pod is disturbed.</para>
/// </summary>
public static class ShippedBuildRefetch
{
    /// <summary>
    /// The record's build: from the store when it holds it, else re-landed from the registered
    /// <see cref="IShippedBuildSource"/> and resolved again, else null — the caller's existing
    /// miss recovery runs unchanged. Never faults on the refetch half: a refetch fault is logged
    /// with its cause and reads as the miss it is.
    /// </summary>
    /// <param name="services">The mesh's service provider (resolves the optional source and the pool).</param>
    /// <param name="store">The store the record's bytes live in.</param>
    /// <param name="build">The record's key.</param>
    /// <param name="logger">Logger for the refetch outcome.</param>
    public static IObservable<string?> ResolveOrRefetch(
        IServiceProvider services, IAssemblyStore store, MissingBuild build, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(build);
        return store.TryGetBuildPath(build.NodeTypePath, build.Version, build.ContentPath, build.AssemblyMvid)
            .Take(1)
            .SelectMany(path =>
            {
                if (!string.IsNullOrEmpty(path))
                    return Observable.Return<string?>(path);
                var source = services.GetService<IShippedBuildSource>();
                if (source is null)
                    return Observable.Return<string?>(null);
                return Land(services, store, source, [build], logger)
                    .SelectMany(landed => landed.Contains(build.NodeTypePath)
                        ? store.TryGetBuildPath(build.NodeTypePath, build.Version, build.ContentPath, build.AssemblyMvid)
                            .Take(1)
                        : Observable.Return<string?>(null));
            });
    }

    /// <summary>
    /// Fetches the shipped bytes for every <paramref name="missing"/> build in ONE source call
    /// (one download per package, not per type) and lands each that IS the build its record
    /// names. Emits the NodeType paths whose bytes are now resolvable; never faults — a source
    /// fault is logged and emits the empty set, so a caller falls through to its own recovery.
    /// </summary>
    /// <param name="services">The mesh's service provider (resolves the file-system I/O pool).</param>
    /// <param name="store">The store to land into.</param>
    /// <param name="source">Where shipped bytes come from.</param>
    /// <param name="missing">The records' keys.</param>
    /// <param name="logger">Logger for every outcome.</param>
    public static IObservable<ImmutableHashSet<string>> Land(
        IServiceProvider services, IAssemblyStore store, IShippedBuildSource source,
        IReadOnlyList<MissingBuild> missing, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(missing);
        var empty = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
            return Observable.Return(empty);
        var pool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        var byPath = missing
            .GroupBy(m => m.NodeTypePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return Observable.Defer(() => source.Fetch(byPath.Keys.ToImmutableList()))
            .Take(1)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Shipped-build refetch: the source could not be asked for {Count} NodeType(s) whose "
                    + "bytes this process lacks ({Types}) — {Cause}. The existing miss recovery runs.",
                    byPath.Count, string.Join(", ", byPath.Keys), ex.Message);
                return Observable.Return<IReadOnlyList<ShippedBuild>>([]);
            })
            .SelectMany(shipped =>
            {
                var matched = shipped
                    .Where(s => byPath.ContainsKey(s.NodeTypePath))
                    .GroupBy(s => s.NodeTypePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Build: byPath[g.Key], Shipped: g.First()))
                    .ToList();
                var unserved = byPath.Keys
                    .Where(p => matched.All(m => !string.Equals(m.Build.NodeTypePath, p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (unserved.Count > 0)
                    logger?.LogInformation(
                        "Shipped-build refetch: no shipped build was found for {Count} NodeType(s) — {Types}. "
                        + "Their bytes stay missing here and the existing recovery runs.",
                        unserved.Count, string.Join(", ", unserved));
                if (matched.Count == 0)
                    return Observable.Return(empty);
                return matched
                    .Select(m => LandOne(pool, store, m.Build, m.Shipped, logger))
                    .Concat()
                    .Where(path => path is not null)
                    .Aggregate(empty, (set, path) => set.Add(path!));
            });
    }

    private static IObservable<string?> LandOne(
        IIoPool pool, IAssemblyStore store, MissingBuild build, ShippedBuild shipped, ILogger? logger)
    {
        var shippedMvid = MvidOf(shipped.Assembly);
        if (shippedMvid is null)
        {
            logger?.LogWarning(
                "Shipped-build refetch: the bytes {Origin} carries for {NodeType} are not a readable "
                + "assembly — not landed.", shipped.Origin, build.NodeTypePath);
            return Observable.Return<string?>(null);
        }
        if (!string.IsNullOrEmpty(build.AssemblyMvid)
            && !string.Equals(build.AssemblyMvid, shippedMvid, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Shipped-build refetch: {Origin} ships build {Shipped} of {NodeType}, but its record names "
                + "build {Recorded} (version {Version}) — a DIFFERENT build (a compile or adoption made "
                + "elsewhere). Not landed: binding it would be refused by the bind-time identity check. "
                + "The existing recovery runs.",
                shipped.Origin, shippedMvid, build.NodeTypePath, build.AssemblyMvid, build.Version);
            return Observable.Return<string?>(null);
        }
        return pool.InvokeObservable(_ => store.Put(build.NodeTypePath, build.Version, shipped.Assembly, shipped.Pdb).Take(1))
            .Select(localPath =>
            {
                logger?.LogInformation(
                    "Shipped-build refetch: landed {NodeType} v{Version} (build {Mvid}) from {Origin} at "
                    + "{LocalPath} — the record's bytes are resolvable on this process without a compile "
                    + "(MeshWeaver#6052).",
                    build.NodeTypePath, build.Version, shippedMvid, shipped.Origin, localPath);
                return (string?)build.NodeTypePath;
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Shipped-build refetch: storing the shipped bytes of {NodeType} v{Version} failed — "
                    + "{Cause}. The existing recovery runs.", build.NodeTypePath, build.Version, ex.Message);
                return Observable.Return<string?>(null);
            });
    }

    /// <summary>The module MVID ("N" hex) of an in-memory assembly, or null when the bytes are not
    /// a readable PE. Metadata only — nothing is loaded.</summary>
    /// <param name="assembly">The assembly bytes.</param>
    public static string? MvidOf(byte[]? assembly)
    {
        if (assembly is not { Length: > 0 })
            return null;
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(
                System.Collections.Immutable.ImmutableArray.Create(assembly));
            if (!pe.HasMetadata)
                return null;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
