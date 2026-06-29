using MeshWeaver.NuGet;
using NuGet.Frameworks;

namespace Memex.Client.Services;

/// <summary>
/// No-op NuGet assembly resolver for the sandboxed device client. The real
/// <c>NuGetAssemblyResolver</c>'s constructor loads NuGet settings by walking up the directory tree to read
/// <c>NuGet.Config</c> — which the MacCatalyst/iOS sandbox DENIES ("Operation not permitted"), throwing
/// during construction and so crashing every per-node hub activation (<c>MeshNodeHubFactory →
/// IMeshNodeCompilationService</c>) — the node-area "eternal spinner". The device never compiles
/// <c>#r "nuget:…"</c> NodeTypes, so resolving to an empty set is correct. Registered BEFORE
/// <c>AddGraph()</c>, whose <c>AddNuGetResolver</c> uses <c>TryAddSingleton</c>, so this wins and the real
/// resolver is never constructed.
/// </summary>
public sealed class NoOpNuGetAssemblyResolver : INuGetAssemblyResolver
{
    public Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default) => Task.FromResult(ResolvedPackageSet.Empty);
}
