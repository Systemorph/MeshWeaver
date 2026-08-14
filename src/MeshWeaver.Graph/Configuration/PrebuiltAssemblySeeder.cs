using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// without standing up a mesh. <c>FrameworkVersion</c> is MeshWeaver.Graph's MVID — a CONTENT
    /// identity — and the assembly-store key carries its first eight characters. Seeding bytes built
    /// against different framework content therefore writes them under the LIVE framework's tag,
    /// where the store reports them as a usable build: the rebuild that was needed is suppressed,
    /// and the ABI mismatch surfaces as a <c>TypeLoadException</c> inside a collectible ALC at
    /// activation — no overlay, no compile error, nothing to grep.</para>
    ///
    /// <para>Declining is always safe (the caller compiles, as it does today). Adopting on faith is
    /// not. So anything short of an exact match declines — including an absent identity, which is
    /// what a producer that predates MVID recording emits.</para>
    /// </summary>
    internal static string? DeclineReason(string? frameworkMvid) =>
        string.IsNullOrEmpty(frameworkMvid)
            ? $"the producer recorded no framework identity, so it cannot be shown ABI-compatible "
              + $"with the live framework {NodeTypeCompilationHelpers.FrameworkVersion}"
            : !string.Equals(frameworkMvid, NodeTypeCompilationHelpers.FrameworkVersion, StringComparison.Ordinal)
                ? $"built against framework {frameworkMvid}, live framework is "
                  + $"{NodeTypeCompilationHelpers.FrameworkVersion}"
                : null;

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
    /// <param name="frameworkMvid">The MVID of the MeshWeaver.Graph assembly the bytes were
    /// compiled against, as recorded by the producer. <c>null</c> declines the seed.</param>
    /// <param name="logger">Diagnostics. Every decline is logged with its reason — a silent decline
    /// looks exactly like a successful adoption that later recompiles for no visible cause.</param>
    public static IObservable<bool> Seed(
        IMessageHub hub,
        string nodeTypePath,
        byte[] assemblyBytes,
        byte[]? pdbBytes,
        string? frameworkMvid,
        ILogger? logger = null)
    {
        // 🚨 THE GATE. FrameworkVersion is MeshWeaver.Graph's MVID — a CONTENT identity, not a
        // version string — and the assembly-store key carries the first eight characters of it. So
        // seeding bytes built against different framework content writes them under the LIVE
        // framework's tag, where the store reports them as a usable build: the rebuild that was
        // needed is suppressed, and the ABI mismatch surfaces as a TypeLoadException inside a
        // collectible ALC at activation — no overlay, no compile error, nothing to grep.
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
