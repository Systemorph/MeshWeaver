using System.Collections.Immutable;

namespace MeshWeaver.NuGet;

/// <summary>
/// The outcome of resolving a set of NuGet packages: the assemblies to reference, the
/// directories to probe for additional assemblies at load time, and the concrete versions chosen.
/// </summary>
/// <param name="AssemblyPaths">Full paths of the COMPILE assemblies: each package's <c>lib/&lt;tfm&gt;/</c>
/// group (its runtime group when it ships no <c>lib/</c>).</param>
/// <param name="ProbingDirectories">The directories a load context probes at run time, nearest first: the
/// directories of <see cref="RuntimeAssemblyPaths"/> (a package's RID-specific
/// <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c> group REPLACES its <c>lib/</c> group), followed by the
/// <c>runtimes/&lt;rid&gt;/native/</c> directories, which only the native probe reads
/// (<see cref="PackageAssemblyProbe"/>).</param>
/// <param name="ResolvedVersions">Map of package id to the concrete version that was resolved.</param>
public sealed record ResolvedPackageSet(
    ImmutableArray<string> AssemblyPaths,
    ImmutableArray<string> ProbingDirectories,
    ImmutableDictionary<string, string> ResolvedVersions)
{
    /// <summary>
    /// Full paths of the managed assemblies to LOAD at run time — <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c>
    /// for the running RID where the package ships one, else <c>lib/&lt;tfm&gt;/</c>
    /// (<see cref="RuntimeAssetSelector"/>).
    /// </summary>
    public ImmutableArray<string> RuntimeAssemblyPaths { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Full paths of the native libraries (<c>runtimes/&lt;rid&gt;/native/</c>) for the running RID.</summary>
    public ImmutableArray<string> NativeLibraryPaths { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>An empty result with no assemblies, probing directories or resolved versions.</summary>
    public static readonly ResolvedPackageSet Empty = new(
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableDictionary<string, string>.Empty);
}
