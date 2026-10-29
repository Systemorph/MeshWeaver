using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The module host's answer to <see cref="INodeTypeDependencyFloor"/> (MeshWeaver#6195): the
/// requirements the package root of a NodeType declares (<c>content.requires</c> on the partition
/// root — <c>Hosting</c> for <c>Hosting/Deployment</c>, which is where every package's
/// <c>index.json</c> lands), judged against the modules THIS process loaded
/// (<see cref="ILoadedPackageModules"/>) by the same rule the import and the installer use
/// (<see cref="PackageDependencyFloorGate.FirstUnmet"/>).
///
/// <para>Absent means "not judged": a root that declares nothing, a root that cannot be read, a
/// dependency with no loaded version here and a range that cannot be read all answer <c>null</c>,
/// and the compile runs as it did before the rule existed. A failed read is logged and abstains —
/// the gate errs towards the compile it replaced, never towards silencing a real one.</para>
/// </summary>
/// <param name="hub">The mesh hub — reads the package root.</param>
/// <param name="modules">What this process has loaded, per package.</param>
/// <param name="logger">Diagnostics.</param>
/// <param name="readBudget">How long one reading may take before it abstains;
/// <see cref="DefaultReadBudget"/> when omitted. See <see cref="Unmet"/> for why it is bounded.</param>
public sealed class NodeTypeDependencyFloorReader(
    IMessageHub hub, ILoadedPackageModules modules, ILogger<NodeTypeDependencyFloorReader>? logger = null,
    TimeSpan? readBudget = null)
    : INodeTypeDependencyFloor
{
    /// <summary>
    /// The default bound on one reading. Both reads behind it answer from memory or one indexed
    /// query on a healthy mesh (the landing service's served snapshot, the path resolver), so the
    /// bound expires only on a reading that went SILENT — never on a slow one.
    /// </summary>
    public static readonly TimeSpan DefaultReadBudget = TimeSpan.FromSeconds(30);

    private readonly TimeSpan budget = readBudget ?? DefaultReadBudget;

    /// <summary>
    /// Whether this process meets the declared dependency floor of the package that owns
    /// <paramref name="nodeTypePath"/> — see <see cref="INodeTypeDependencyFloor.Unmet"/>.
    ///
    /// <para>🚨 <b>Bounded, and an expired bound ABSTAINS.</b> The contract promises exactly one
    /// emission, and neither read behind it can promise that on its own: the path resolver
    /// documents that a resolution whose query loses its Initial snapshot stays SILENT, and that
    /// its caller recovers through the caller's own bound (<c>PathResolutionService</c>, pinned by
    /// <c>PathResolutionCachePoisonTest.HungFirstQuery_DoesNotPoisonCache</c>). This reading sits in
    /// front of every compile and every hydration, and nothing encloses it before Roslyn's own
    /// bound, so a silent read left the NodeType at <c>Compiling</c> for good. An expired bound is
    /// the same outcome as a failed read: logged, not judged, and the compile runs.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's own path.</param>
    public IObservable<string?> Unmet(string nodeTypePath)
    {
        if (PackageRootOf(nodeTypePath) is not { } root)
            return Observable.Return<string?>(null);
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        // The loaded modules FIRST: a process that loaded no package module (a mesh without a module
        // host, every test mesh) has nothing to judge, so it never reads the root at all.
        return modules.Read()
            .Take(1)
            .DefaultIfEmpty(ImmutableDictionary<string, LoadedPackageModule>.Empty)
            .SelectMany(loaded => loaded.IsEmpty
                ? Observable.Return<string?>(null)
                // RunAsSystem (never Observable.Using, #1790): the package root is read as
                // infrastructure, exactly as the compile that asks reads its sources.
                : access.RunAsSystem(() => ReadRoot(root))
                    .Take(1)
                    .Select(node => Judge(root, node, loaded, hub.JsonSerializerOptions)))
            .DefaultIfEmpty(null)
            .Take(1)
            .Timeout(budget, Observable.Throw<string?>(new TimeoutException(
                $"the reading did not answer within {budget.TotalSeconds:0}s")))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Dependency floor of {NodeTypePath}: its package root '{Root}' or the loaded modules "
                    + "could not be read, so the floor is NOT judged and the compile runs (#6195).",
                    nodeTypePath, root);
                return Observable.Return<string?>(null);
            });
    }

    /// <summary>
    /// 🚨 The package root as PERSISTED, read through the path resolver — the same authoritative,
    /// change-feed-invalidated read routing itself resolves an address with — and NEVER through the
    /// root's own hub. This read sits on the activation path: hydrating or compiling a NodeType is
    /// what an instance's activation waits for, and a package root is commonly an instance of a
    /// type its own package defines (<c>Store</c> is a <c>Store/Catalog</c>). Reading it through
    /// its hub made activating that root wait for itself, until the read's budget ran out and the
    /// floor was silently not judged. Emits the node, or null when the path names no node of its
    /// own (an ancestor match, a partition-root virtual match, or nothing).
    /// </summary>
    /// <param name="root">The package root's path.</param>
    private IObservable<MeshNode?> ReadRoot(string root) =>
        hub.ServiceProvider.GetRequiredService<IPathResolver>().ResolvePath(root)
            .Take(1)
            .Select(resolution => resolution is { Node: { } node }
                                  && string.IsNullOrEmpty(resolution.Remainder)
                                  // Case-insensitive, as the resolver itself matches paths: a
                                  // NodeType reference may spell its root differently from the
                                  // persisted node (`hosting/Deployment` against `Hosting`), and
                                  // discarding that root would leave the floor NOT JUDGED.
                                  && string.Equals(resolution.Prefix.Trim('/'), root, StringComparison.OrdinalIgnoreCase)
                ? node
                : null);

    /// <summary>
    /// The judgement over an already-read package root: its <see cref="RequiresOf"/> against what
    /// this process loaded, by <see cref="PackageDependencyFloorGate.FirstUnmet"/>. <c>null</c> when
    /// nothing is declared, nothing declared is unmet, or nothing could be judged. Pure.
    /// </summary>
    /// <param name="root">The package root's path, for the sentence.</param>
    /// <param name="rootNode">The package root as read, or null when it could not be read.</param>
    /// <param name="loaded">Package id → the module this process has loaded for it.</param>
    /// <param name="options">The reading hub's serializer options.</param>
    public static string? Judge(
        string root, MeshNode? rootNode, IReadOnlyDictionary<string, LoadedPackageModule> loaded,
        JsonSerializerOptions options)
    {
        var requires = RequiresOf(rootNode, options);
        return requires.IsEmpty ? null : Describe(root, PackageDependencyFloorGate.FirstUnmet(requires, loaded));
    }

    /// <summary>
    /// The partition root that carries a package's <c>index.json</c> — the first segment of the
    /// NodeType's path — or null when the path names no child of a root (a root itself, or empty).
    /// Pure.
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path.</param>
    public static string? PackageRootOf(string? nodeTypePath)
    {
        if (string.IsNullOrWhiteSpace(nodeTypePath))
            return null;
        var trimmed = nodeTypePath.Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash <= 0 ? null : trimmed[..slash];
    }

    /// <summary>
    /// The <c>requires</c> entries a package root's content declares, read through JSON so it works
    /// whether the content materialised as the owning package's CLR type or stayed an untyped
    /// <see cref="JsonElement"/>. Anything that is not a string array reads as no requirements. Pure.
    /// </summary>
    /// <param name="root">The package root node, or null when it could not be read.</param>
    /// <param name="options">The reading hub's serializer options.</param>
    public static ImmutableList<string> RequiresOf(MeshNode? root, JsonSerializerOptions options)
    {
        if (root?.Content is not { } content)
            return [];
        var element = content is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(content, content.GetType(), options);
        if (element.ValueKind != JsonValueKind.Object)
            return [];
        if (!(element.TryGetProperty("requires", out var requires)
              || element.TryGetProperty("Requires", out requires))
            || requires.ValueKind != JsonValueKind.Array)
            return [];
        return [.. requires.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(text => text.Length > 0)];
    }

    /// <summary>The sentence <see cref="Unmet"/> returns, or null when nothing is unmet. Pure.</summary>
    /// <param name="root">The package root.</param>
    /// <param name="unmet">The first unmet requirement, or null.</param>
    public static string? Describe(string root, UnmetPackageDependency? unmet) =>
        unmet is null
            ? null
            : $"package '{root}' requires {unmet.Requirement}, but this replica runs '{unmet.Module}' "
              + $"(package '{unmet.Dependency}') at {unmet.LoadedVersion}, which does not satisfy it";
}
