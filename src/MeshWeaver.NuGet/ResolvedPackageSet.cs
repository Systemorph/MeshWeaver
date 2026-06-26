using System.Collections.Immutable;

namespace MeshWeaver.NuGet;

/// <summary>
/// The outcome of resolving a set of NuGet packages: the assemblies to reference, the
/// directories to probe for additional assemblies at load time, and the concrete versions chosen.
/// </summary>
/// <param name="AssemblyPaths">Full paths to the resolved <c>lib</c> assemblies.</param>
/// <param name="ProbingDirectories">Distinct directories containing the resolved assemblies, used for runtime assembly probing.</param>
/// <param name="ResolvedVersions">Map of package id to the concrete version that was resolved.</param>
public sealed record ResolvedPackageSet(
    ImmutableArray<string> AssemblyPaths,
    ImmutableArray<string> ProbingDirectories,
    ImmutableDictionary<string, string> ResolvedVersions)
{
    /// <summary>An empty result with no assemblies, probing directories or resolved versions.</summary>
    public static readonly ResolvedPackageSet Empty = new(
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableDictionary<string, string>.Empty);
}
