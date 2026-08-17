using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Adopts an assembly that was compiled somewhere else — CI, a bake service, an installed package —
/// as a NodeType's build, so the portal never compiles what has already been compiled.
///
/// <para>See <c>Doc/Architecture/PluginPackaging</c> for how such an assembly is produced. This is
/// the consuming half.</para>
/// </summary>
public static class PrebuiltAssemblySeeder
{
    /// <summary>
    /// Why a prebuilt assembly may NOT be adopted, or null when it may.
    ///
    /// <para>🚨 This is the whole safety argument, kept as one pure function so it can be tested
    /// without standing up a mesh. <c>FrameworkVersion</c> is the resolved framework build
    /// identity (<see cref="FrameworkBuildIdentity.FrameworkVersion"/> — surface hash / commit
    /// stamp / toolchain-anchor MVID) — and the assembly-store key carries its first eight
    /// characters. Seeding bytes built against a different framework therefore writes them under
    /// the LIVE framework's tag, where the store reports them as a usable build: the rebuild that
    /// was needed is suppressed, and the ABI mismatch surfaces as a <c>TypeLoadException</c>
    /// inside a collectible ALC at activation — no overlay, no compile error, nothing to
    /// grep.</para>
    ///
    /// <para>Declining is always safe (the caller compiles, as it does today). Adopting on faith is
    /// not. So anything short of an exact match declines — including an absent identity, which is
    /// what a producer that predates MVID recording emits.</para>
    /// </summary>
    /// <para>Public because the consuming side asks it BEFORE unpacking a downloaded bundle — the
    /// gate below is still the one that holds, but re-seeding a whole bundle only to decline every
    /// assembly individually wastes the download and buries the one reason in N identical lines.</para>
    public static string? DeclineReason(string? frameworkMvid) =>
        DeclineReason(frameworkMvid, NodeTypeCompilationHelpers.FrameworkVersion);

    /// <summary>
    /// <see cref="DeclineReason(string?)"/> against an EXPLICIT live identity — the pure core, so a
    /// test can stage a framework roll without rebuilding the framework (the same seam
    /// <c>NodeTypeBakeStatus.Classify</c> exposes for the same reason).
    /// </summary>
    public static string? DeclineReason(string? frameworkMvid, string liveFrameworkMvid) =>
        string.IsNullOrEmpty(frameworkMvid)
            ? $"the producer recorded no framework identity, so it cannot be shown ABI-compatible "
              + $"with the live framework {liveFrameworkMvid}"
            : !string.Equals(frameworkMvid, liveFrameworkMvid, StringComparison.Ordinal)
                ? $"built against framework {frameworkMvid}, live framework is "
                  + $"{liveFrameworkMvid}"
                : null;

    /// <summary>
    /// The LIVE framework identity a producer must record beside its bytes — the resolved
    /// framework build identity (<see cref="FrameworkBuildIdentity.FrameworkVersion"/>), exactly
    /// as <see cref="DeclineReason(string?)"/> compares it. The one public reading of this
    /// identity, so a producer (the CI bake, the registry bundle lane) and the consuming gate can
    /// never disagree about what "the framework identity" is.
    /// </summary>
    public static string LiveFrameworkMvid => NodeTypeCompilationHelpers.FrameworkVersion;

    /// <summary>
    /// Degradation warning from the live identity's resolution (a torn or unusable surface
    /// manifest fell back to the stamp/MVID layer — see
    /// <see cref="FrameworkBuildIdentity.ResolveProcessIdentityWithDiagnostics"/>), or null on
    /// the happy path. Public beside <see cref="LiveFrameworkMvid"/> so the process that
    /// announces the identity (the pre-warmer) can announce the degradation with it.
    /// </summary>
    public static string? LiveFrameworkIdentityWarning => NodeTypeCompilationHelpers.FrameworkVersionWarning;

    /// <summary>
    /// Seeds <paramref name="assemblyBytes"/> as the build for <paramref name="nodeTypePath"/>.
    ///
    /// <para>Cold: the write runs on Subscribe. Emits <c>true</c> when the assembly was adopted and
    /// <c>false</c> when it was declined — a decline is not an error, it is the caller's signal to
    /// compile normally.</para>
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="nodeTypePath">Mesh path of the NodeType this assembly implements.</param>
    /// <param name="assemblyBytes">The compiled assembly.</param>
    /// <param name="pdbBytes">Symbols, when the assembly does not embed them.</param>
    /// <param name="frameworkMvid">The framework build identity the bytes were compiled against,
    /// as recorded by the producer. <c>null</c> declines the seed.</param>
    /// <param name="logger">Diagnostics. Every decline is logged with its reason — a silent decline
    /// looks exactly like a successful adoption that later recompiles for no visible cause.</param>
    /// <param name="dependencies">The producer's per-type DEPENDENCY RECORD for these bytes
    /// (#1707 slice 2), when the bundle carries one: validated against THIS environment before
    /// adoption (a module the type binds must be the exact build here too) and stamped onto
    /// <see cref="NodeTypeDefinition.CompiledDependencies"/> on adopt so the ongoing validity
    /// checks judge the adopted build the same way they judge a locally-compiled one. Null =
    /// legacy bundle; the framework gate alone decides, and no record is stamped.</param>
    public static IObservable<bool> Seed(
        IMessageHub hub,
        string nodeTypePath,
        byte[] assemblyBytes,
        byte[]? pdbBytes,
        string? frameworkMvid,
        ILogger? logger = null,
        IReadOnlyDictionary<string, string>? dependencies = null)
    {
        // 🚨 THE GATE. FrameworkVersion is the resolved framework build identity — a content/
        // surface identity, not a version string — and the assembly-store key carries the first
        // eight characters of it. So seeding bytes built against a different framework writes
        // them under the LIVE framework's tag, where the store reports them as a usable build:
        // the rebuild that was needed is suppressed, and the ABI mismatch surfaces as a
        // TypeLoadException inside a collectible ALC at activation — no overlay, no compile
        // error, nothing to grep.
        //
        // Declining is always safe; adopting on faith is not. So an identity that is absent or
        // different declines, and the caller compiles.
        if (DeclineReason(frameworkMvid) is { } reason)
        {
            logger?.LogInformation(
                "Prebuilt assembly for {NodeTypePath} DECLINED: {Reason} — compiling instead",
                nodeTypePath, reason);
            return Observable.Return(false);
        }

        // The per-type dependency record (#1707 slice 2): the framework gate above proves the
        // PLATFORM surface matches; this proves the MODULE/toolchain bindings do too — the store
        // key cannot see either, so an unvalidated adopt would suppress a needed rebuild exactly
        // like a framework mismatch would.
        if (dependencies is not null
            && CompiledDependencies.FindMismatch(
                dependencies,
                NodeTypeCompilationHelpers.DependencyIdResolverOf(hub),
                NodeTypeCompilationHelpers.ProcessToolchainId) is { } dependencyMismatch)
        {
            logger?.LogInformation(
                "Prebuilt assembly for {NodeTypePath} DECLINED: dependency record mismatch — "
                + "{Mismatch} — compiling instead",
                nodeTypePath, dependencyMismatch);
            return Observable.Return(false);
        }

        var workspace = hub.GetWorkspace();

        return workspace.GetMeshNodeStream(nodeTypePath)
            .Where(node => node is not null)
            .Take(1)
            .SelectMany(node =>
            {
                if (node!.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions) is null)
                {
                    logger?.LogInformation(
                        "Prebuilt assembly for {NodeTypePath} DECLINED: node content is not a "
                        + "NodeTypeDefinition", nodeTypePath);
                    return Observable.Return(false);
                }

                // 🚨 ONE version, used twice. ApplyCompileSuccess documents why: the stamp must name
                // the SAME version the store upload used, or activation resolves a store key with no
                // bytes behind it, TryGetAssemblyPath misses, and the instance silently falls back
                // to the default configuration.
                var version = node.Version;
                var store = hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

                return store
                    .PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes)
                    .SelectMany(location => workspace.GetMeshNodeStream(nodeTypePath)
                        .Update(current =>
                        {
                            var def = current?.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                            if (current is null || def is null)
                                return current!;

                            return current with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    CompilationDiagnostics = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    LastCompiledVersion = version,
                                    LatestAssemblyCollection = location.Collection,
                                    LatestAssemblyPath = location.ContentPath,
                                    CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                                    // The producer compiled exactly the sources it shipped beside
                                    // these bytes, so the snapshot IS the installed set. Leaving it
                                    // unset would leave IsDirty comparing an empty snapshot against
                                    // live CurrentSourceVersions and recompile immediately —
                                    // adopting the assembly and then throwing it away.
                                    CompiledSources = def.CurrentSourceVersions?.ToImmutableDictionary()
                                                      ?? def.CompiledSources,
                                    // The producer's dependency record (#1707 slice 2) — validated
                                    // above; stamped so ongoing validity checks judge the adopted
                                    // build like a locally-compiled one. Legacy bundles (null)
                                    // leave any prior stamp untouched.
                                    CompiledDependencies = dependencies is null
                                        ? def.CompiledDependencies
                                        : dependencies.ToImmutableSortedDictionary(
                                            kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                                },
                            };
                        }))
                    .Select(_ =>
                    {
                        logger?.LogInformation(
                            "Prebuilt assembly ADOPTED for {NodeTypePath} at version {Version} "
                            + "(framework {Framework}) — no compile needed",
                            nodeTypePath, version, NodeTypeCompilationHelpers.FrameworkVersion);
                        return true;
                    });
            });
    }
}
