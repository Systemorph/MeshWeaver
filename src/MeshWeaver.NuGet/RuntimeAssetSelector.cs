using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MeshWeaver.Utils;
using NuGet.Frameworks;

namespace MeshWeaver.NuGet;

/// <summary>
/// The assets one package contributes for one target framework on one runtime.
/// </summary>
/// <param name="Compile">Full paths of the assemblies to reference at compile time — the package's
/// <c>lib/&lt;tfm&gt;/</c> group, or, for a package that ships no <c>lib/</c>, its runtime group.</param>
/// <param name="Runtime">Full paths of the managed assemblies to LOAD at run time —
/// <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c> for the nearest RID in the fallback chain that has a
/// compatible framework, else <c>lib/&lt;tfm&gt;/</c>.</param>
/// <param name="Native">Full paths of the native libraries for the nearest RID that ships any
/// (<c>runtimes/&lt;rid&gt;/native/</c>).</param>
/// <param name="RuntimeRid">The RID whose managed group was chosen, or <c>null</c> when the generic
/// <c>lib/</c> group serves run time.</param>
public sealed record PackageAssets(
    ImmutableArray<string> Compile,
    ImmutableArray<string> Runtime,
    ImmutableArray<string> Native,
    string? RuntimeRid)
{
    /// <summary>No assets at all.</summary>
    public static readonly PackageAssets None = new([], [], [], null);
}

/// <summary>
/// Picks a package's compile, runtime and native assets the way NuGet restore does for a
/// RID-specific project: a <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c> group REPLACES the generic
/// <c>lib/&lt;tfm&gt;/</c> group at run time. That replacement is load-bearing for packages whose
/// generic <c>lib/</c> assembly is a "not supported on this platform" stub — every .NET SQL Server
/// client (Microsoft.Data.SqlClient 5.x–7.x, System.Data.SqlClient 4.9) ships its real
/// implementation only under <c>runtimes/unix/lib/</c> and <c>runtimes/win/lib/</c>.
/// </summary>
public static class RuntimeAssetSelector
{
    /// <summary>
    /// The RID fallback chain for <paramref name="rid"/>, nearest first, ending in <c>any</c>:
    /// <c>linux-x64 → linux → unix-x64 → unix → any</c> on the portal image. The one definition is
    /// <see cref="RuntimeIdentifierGraph.FallbackChain"/>, shared with the module native-asset
    /// resolver so the two never disagree about which <c>runtimes/&lt;rid&gt;/</c> folder serves a RID.
    /// </summary>
    /// <param name="rid">A runtime identifier; <see cref="RuntimeInformation.RuntimeIdentifier"/> when null.</param>
    public static ImmutableArray<string> RidFallbackChain(string? rid = null)
        => RuntimeIdentifierGraph.FallbackChain(rid);

    /// <summary>
    /// Selects <paramref name="packageFolder"/>'s assets for <paramref name="framework"/> on the
    /// first RID of <paramref name="ridChain"/> that has them.
    /// </summary>
    /// <param name="packageFolder">The extracted package root.</param>
    /// <param name="relativeFiles">The package's files relative to its root (either separator).</param>
    /// <param name="framework">The framework the consumer runs on.</param>
    /// <param name="ridChain">The RID fallback chain, nearest first (<see cref="RidFallbackChain"/>).</param>
    public static PackageAssets Select(
        string packageFolder,
        IEnumerable<string> relativeFiles,
        NuGetFramework framework,
        IReadOnlyList<string> ridChain)
    {
        var files = relativeFiles.Select(f => f.Replace('\\', '/').TrimStart('/')).ToImmutableArray();
        string Full(string rel) => Path.Combine(packageFolder, rel.Replace('/', Path.DirectorySeparatorChar));

        // null = the package has no compatible group there; EMPTY = it has one and it is
        // deliberately empty (NuGet's `_._` placeholder) — which still WINS, exactly as restore
        // treats it, so an older TFM, a farther RID or lib/ is never loaded in its place.
        var lib = NearestGroup(files, "lib/", framework);

        ImmutableArray<string>? runtime = null;
        string? runtimeRid = null;
        foreach (var rid in ridChain)
        {
            var group = NearestGroup(files, $"runtimes/{rid}/lib/", framework);
            if (group is null) continue;
            runtime = group;
            runtimeRid = rid;
            break;
        }

        ImmutableArray<string> native = [];
        foreach (var rid in ridChain)
        {
            var prefix = $"runtimes/{rid}/native/";
            var hits = files
                .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && f.Length > prefix.Length
                            && f.IndexOf('/', prefix.Length) < 0)
                .ToImmutableArray();
            if (hits.Length == 0) continue;
            native = hits.Where(f => !IsEmptyPlaceholder(f)).ToImmutableArray();
            break;
        }

        var compile = lib ?? runtime ?? [];
        var load = runtime ?? lib ?? [];

        return new PackageAssets(
            [.. compile.Select(Full)],
            [.. load.Select(Full)],
            [.. native.Select(Full)],
            runtimeRid);
    }

    private static bool IsEmptyPlaceholder(string relativePath)
        => relativePath.EndsWith("/_._", StringComparison.Ordinal);

    /// <summary>
    /// The direct-child <c>.dll</c> files of the <c>{prefix}&lt;tfm&gt;/</c> folder nearest to
    /// <paramref name="framework"/>: <c>null</c> when no folder under <paramref name="prefix"/> is
    /// compatible, EMPTY when the nearest one holds only NuGet's <c>_._</c> placeholder.
    /// </summary>
    private static ImmutableArray<string>? NearestGroup(ImmutableArray<string> files, string prefix, NuGetFramework framework)
    {
        var groups = files
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Tail: f[prefix.Length..]))
            // Only <prefix><tfm>/<file> — satellite resources live one level deeper and are not probed.
            .Where(x => x.Tail.IndexOf('/') is > 0 and var slash && x.Tail.IndexOf('/', slash + 1) < 0)
            .Where(x => x.Tail.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || IsEmptyPlaceholder(x.File))
            .Select(x => (x.File, Tfm: NuGetFramework.ParseFolder(x.Tail[..x.Tail.IndexOf('/')])))
            .Where(x => !x.Tfm.IsUnsupported)
            .GroupBy(x => x.Tfm)
            .ToImmutableDictionary(
                g => g.Key,
                g => g.Select(x => x.File).Where(f => !IsEmptyPlaceholder(f)).ToImmutableArray());
        if (groups.IsEmpty) return null;
        var nearest = new FrameworkReducer().GetNearest(framework, groups.Keys);
        return nearest is not null && groups.TryGetValue(nearest, out var chosen) ? chosen : null;
    }
}
