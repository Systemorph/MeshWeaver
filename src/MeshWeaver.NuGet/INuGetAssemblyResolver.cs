using NuGet.Frameworks;

namespace MeshWeaver.NuGet;

/// <summary>
/// Resolves a set of requested NuGet package references — including their transitive
/// dependencies — down to concrete on-disk assembly paths, using pure NuGet client
/// libraries (no MSBuild/SDK required).
/// </summary>
public interface INuGetAssemblyResolver
{
    /// <summary>
    /// Resolves the <paramref name="requested"/> packages (plus transitive dependencies) for the
    /// given target framework and returns their assembly paths, probing directories and the
    /// concrete versions selected.
    /// </summary>
    /// <param name="requested">The package references to resolve.</param>
    /// <param name="targetFramework">The target framework to resolve assets for; defaults to the resolver's default framework when <c>null</c>.</param>
    /// <param name="ct">Token used to cancel the resolution.</param>
    /// <returns>A <c>ResolvedPackageSet</c> describing the resolved assemblies, probing directories and versions.</returns>
    Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default);
}
