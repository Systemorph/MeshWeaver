using NuGet.Frameworks;

namespace MeshWeaver.NuGet;

public interface INuGetAssemblyResolver
{
    Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default);
}
