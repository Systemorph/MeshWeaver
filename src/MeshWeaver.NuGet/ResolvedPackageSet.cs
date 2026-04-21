using System.Collections.Immutable;

namespace MeshWeaver.NuGet;

public sealed record ResolvedPackageSet(
    ImmutableArray<string> AssemblyPaths,
    ImmutableArray<string> ProbingDirectories,
    ImmutableDictionary<string, string> ResolvedVersions)
{
    public static readonly ResolvedPackageSet Empty = new(
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableDictionary<string, string>.Empty);
}
