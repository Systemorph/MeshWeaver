using System.Collections.Immutable;
using System.Runtime.InteropServices;
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
    private static bool IsArchitecture(string segment) => segment.ToLowerInvariant() switch
    {
        "x64" or "x86" or "arm64" or "arm" or "armel" or "armv6" or "s390x" or "ppc64le"
            or "loongarch64" or "riscv64" or "wasm" or "mips64" => true,
        _ => false,
    };

    /// <summary>
    /// The RID fallback chain for <paramref name="rid"/>, nearest first, ending in <c>any</c> — a
    /// hand-written image of NuGet's runtime graph for the families a portal runs on:
    /// <c>linux-x64 → linux → unix-x64 → unix → any</c>,
    /// <c>linux-musl-x64 → linux-musl → linux-x64 → linux → unix-x64 → unix → any</c>,
    /// <c>osx-arm64 → osx → unix-arm64 → unix → any</c>, <c>win-x64 → win → any</c>.
    /// A distro-versioned RID (<c>ubuntu.22.04-x64</c>) falls back to <c>linux</c>.
    /// </summary>
    /// <param name="rid">A runtime identifier; <see cref="RuntimeInformation.RuntimeIdentifier"/> when null.</param>
    public static ImmutableArray<string> RidFallbackChain(string? rid = null)
    {
        rid = string.IsNullOrWhiteSpace(rid) ? RuntimeInformation.RuntimeIdentifier : rid.Trim();
        var chain = ImmutableArray.CreateBuilder<string>();
        void Add(string r)
        {
            if (!chain.Contains(r, StringComparer.OrdinalIgnoreCase)) chain.Add(r);
        }

        var dash = rid.LastIndexOf('-');
        var arch = dash > 0 && IsArchitecture(rid[(dash + 1)..])
            ? rid[(dash + 1)..]
            : null;
        var os = arch is null ? rid : rid[..dash];

        // The OS family, nearest first.
        var family = new List<string> { os };
        bool isWindows = os.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        if (os.Equals("linux-musl", StringComparison.OrdinalIgnoreCase)
            || os.StartsWith("alpine", StringComparison.OrdinalIgnoreCase))
        {
            family.Add("linux-musl");
            family.Add("linux");
        }
        else if (os.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
            family.Add("osx");
        else if (isWindows)
            family.Add("win");
        else if (!os.Equals("unix", StringComparison.OrdinalIgnoreCase)
                 && !os.Equals("any", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("freebsd", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("android", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("ios", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("maccatalyst", StringComparison.OrdinalIgnoreCase)
                 && !os.StartsWith("browser", StringComparison.OrdinalIgnoreCase))
            family.Add("linux"); // linux itself, or a distro RID (ubuntu.22.04, rhel.9, …)

        foreach (var f in family)
        {
            if (f.Equals("any", StringComparison.OrdinalIgnoreCase)) continue;
            if (arch is not null) Add($"{f}-{arch}");
            Add(f);
        }
        if (!isWindows && !os.StartsWith("browser", StringComparison.OrdinalIgnoreCase))
        {
            if (arch is not null) Add($"unix-{arch}");
            Add("unix");
        }
        Add("any");
        return chain.ToImmutable();
    }

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
        var files = relativeFiles.Select(f => f.Replace('\\', '/').TrimStart('/')).ToList();
        string Full(string rel) => Path.Combine(packageFolder, rel.Replace('/', Path.DirectorySeparatorChar));

        var lib = NearestGroup(files, "lib/", framework);

        ImmutableArray<string> runtime = [];
        string? runtimeRid = null;
        foreach (var rid in ridChain)
        {
            var group = NearestGroup(files, $"runtimes/{rid}/lib/", framework);
            if (group.Length == 0) continue;
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
            native = hits;
            break;
        }

        var compile = lib.Length > 0 ? lib : runtime;
        if (runtime.Length == 0) runtime = lib;

        return new PackageAssets(
            [.. compile.Select(Full)],
            [.. runtime.Select(Full)],
            [.. native.Select(Full)],
            runtimeRid);
    }

    /// <summary>
    /// The direct-child <c>.dll</c> files of the <c>{prefix}&lt;tfm&gt;/</c> folder nearest to
    /// <paramref name="framework"/>; empty when no folder under <paramref name="prefix"/> is compatible.
    /// </summary>
    private static ImmutableArray<string> NearestGroup(List<string> files, string prefix, NuGetFramework framework)
    {
        var groups = new Dictionary<NuGetFramework, List<string>>();
        foreach (var f in files)
        {
            if (!f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = f[prefix.Length..];
            var slash = rest.IndexOf('/');
            // Only lib/<tfm>/<file> — satellite resources live one level deeper and are not probed.
            if (slash <= 0 || rest.IndexOf('/', slash + 1) >= 0) continue;
            if (!rest.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var tfm = NuGetFramework.ParseFolder(rest[..slash]);
            if (tfm.IsUnsupported) continue;
            if (!groups.TryGetValue(tfm, out var list)) groups[tfm] = list = [];
            list.Add(f);
        }
        if (groups.Count == 0) return [];
        var nearest = new FrameworkReducer().GetNearest(framework, groups.Keys);
        return nearest is not null && groups.TryGetValue(nearest, out var chosen) ? [.. chosen] : [];
    }
}
