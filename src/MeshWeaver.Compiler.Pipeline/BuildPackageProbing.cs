using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>Every load of an already-built NodeType assembly first arms the package probe for that
/// build.</b> A build whose sources declared <c>#r "nuget:…"</c> records those references in its own
/// metadata (<see cref="NuGetBuildRecord"/>); before its bytes are loaded — by the post-compile scan,
/// by the hydration shortcut (<c>GetConfigurationsFromExistingAssembly</c>: start-up registration,
/// <c>EnrichWithNodeType</c>, adoption of a cached or baked build) or by a kernel joining the cell
/// surface — the references are re-resolved through <see cref="INuGetAssemblyResolver"/> (memoized
/// per process; a warm package folder answers without the network) and the resulting probing
/// directories are registered for THAT path
/// (<see cref="ICompilationCacheService.RegisterBuildProbingDirectories"/>). The load context then
/// binds the package's runtime asset (<c>runtimes/&lt;rid&gt;/lib</c>, never the <c>lib/</c>
/// placeholder) after host unification, exactly as on the process that compiled it
/// (<c>Doc/Architecture/NuGetRuntimeAssets</c>).
///
/// <para>Until 2026-10-16 the probe was armed only by the emit, in memory, so a replica that never
/// compiled the type — or the compiling one after a restart — failed <c>GetTypes()</c> with
/// <c>Could not load file or assembly 'Microsoft.Data.SqlClient'</c> on every load path.</para>
/// </summary>
internal static class BuildPackageProbing
{
    /// <summary>
    /// The package references recorded in the build at <paramref name="assemblyLocation"/> — empty
    /// for an in-memory build, a missing file, or a build that declared none. A metadata header read;
    /// nothing is loaded.
    /// </summary>
    public static IReadOnlyList<NuGetPackageReference> RecordedReferences(string? assemblyLocation)
        => string.IsNullOrEmpty(assemblyLocation)
           || assemblyLocation.StartsWith("memory://", StringComparison.Ordinal)
            ? []
            : NuGetBuildRecord.Read(assemblyLocation);

    /// <summary>
    /// <see cref="RecordedReferences"/> read on <paramref name="readPool"/> (it opens the file, so it
    /// never runs on the caller's signalling thread), then <see cref="Arm"/>.
    /// </summary>
    public static IObservable<Unit> ReadAndArm(
        string assemblyLocation,
        IIoPool readPool,
        ICompilationCacheService cacheService,
        INuGetAssemblyResolver? resolver,
        IIoPool resolvePool,
        TimeSpan bound,
        ILogger logger)
        // atomic-blocking-leaf: one read of the build's recorded NuGet references beside the assembly
        => readPool.InvokeBlocking(_ => RecordedReferences(assemblyLocation))
            .SelectMany(recorded => Arm(
                assemblyLocation, recorded, cacheService, resolver, resolvePool, bound, logger));

    /// <summary>
    /// Resolves <paramref name="references"/> (the build's recorded set) and registers the probing
    /// directories for <paramref name="assemblyLocation"/>; emits one <see cref="Unit"/> and completes.
    /// Emits immediately when there is nothing to resolve.
    ///
    /// <para>The resolution is the resolver's reactive <see cref="INuGetAssemblyResolver.Resolve"/>,
    /// which runs its NuGet leaf on the Http lane itself — independent of the Compile lane a kernel
    /// script holds while it waits for a NodeType to activate — and bounds it by its own budget. It
    /// is deliberately NOT wrapped in <paramref name="resolvePool"/> any more: a slot of the Http lane
    /// held while waiting on a leaf of the same lane can starve that lane. The bound (or the caller's
    /// disposal) ends this wait; <paramref name="resolvePool"/> stays in the signature for its
    /// callers.</para>
    ///
    /// <para>A resolution that fails or does not answer within <paramref name="bound"/> is logged as
    /// a WARNING naming the packages and the cause, and the load goes ahead: it then fails with the
    /// loader's own message, which is the verdict the caller records — the warning says why.</para>
    /// </summary>
    public static IObservable<Unit> Arm(
        string assemblyLocation,
        IReadOnlyList<NuGetPackageReference> references,
        ICompilationCacheService cacheService,
        INuGetAssemblyResolver? resolver,
        IIoPool resolvePool,
        TimeSpan bound,
        ILogger logger)
    {
        if (references.Count == 0)
            return Observable.Return(Unit.Default);
        var packages = string.Join("; ", references.Select(NuGetBuildRecord.Format));
        if (resolver is null)
        {
            logger.LogWarning(
                "The build at {AssemblyLocation} records NuGet package(s) {Packages} but this process has no "
                + "INuGetAssemblyResolver registered — its package assemblies cannot be bound",
                assemblyLocation, packages);
            return Observable.Return(Unit.Default);
        }

        return resolver.Resolve(references)
            .Timeout(bound)
            .Do(resolved =>
            {
                cacheService.RegisterBuildProbingDirectories(assemblyLocation, resolved.ProbingDirectories);
                // Information, not Debug: it is the one positive evidence that a build's package
                // closure reached the load context on THIS process (2026-10-16: an image without
                // this lane and one with it were indistinguishable in the log). Written only for
                // builds that record a #r "nuget:" package, once per load of such a build.
                logger.LogInformation(
                    "Armed the package probe for {AssemblyLocation}: {Packages} → {Directories} probing directories",
                    assemblyLocation, packages, resolved.ProbingDirectories.Length);
            })
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "Could not resolve the NuGet package(s) {Packages} recorded in the build at {AssemblyLocation} — "
                    + "loading it without them, so a type that needs them will fail to load",
                    packages, assemblyLocation);
                return Observable.Return(Unit.Default);
            });
    }
}
