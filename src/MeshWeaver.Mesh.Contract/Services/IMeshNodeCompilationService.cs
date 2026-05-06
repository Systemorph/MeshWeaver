using System.Collections.Immutable;
using MeshWeaver.Data;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Result from compiling a MeshNode assembly.
/// <see cref="Log"/> carries the executed-query / matched-source-paths /
/// compiler-output trace so the consumer can surface "compile saw N source
/// files" without re-running the pipeline.
/// <see cref="CompiledSources"/> is the per-source <c>{path → version}</c>
/// snapshot the compile actually consumed — empty when the cache hit short-
/// circuited the recompile; populated to the full input set otherwise. The
/// compile watcher persists it onto the NodeType MeshNode so the next
/// recompile-needed check is a data comparison instead of a timing guess.
/// </summary>
public record NodeCompilationResult(
    string? AssemblyLocation,
    IReadOnlyList<NodeTypeConfiguration> NodeTypeConfigurations,
    ActivityLog? Log = null,
    ImmutableDictionary<string, long>? CompiledSources = null);

/// <summary>
/// Service for on-demand compilation of dynamic MeshNode assemblies.
/// Compiles C# type definitions from DataModel and caches the resulting assemblies.
/// Implemented in MeshWeaver.Graph, consumed optionally by MeshWeaver.Hosting.Orleans.
///
/// <para>
/// 100% reactive — every method returns <see cref="IObservable{T}"/>. Compose with
/// <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. NEVER bridge to <c>Task</c>
/// or <c>await</c> from hub-reachable code — that deadlocks the hub action block. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. Tests bridge at their own edge with
/// <c>.FirstAsync().ToTask(ct)</c>.
/// </para>
/// </summary>
public interface IMeshNodeCompilationService
{
    /// <summary>
    /// Reactive: emits the assembly location (DLL path) for the node, or null if the
    /// node does not have a NodeType definition.
    /// </summary>
    IObservable<string?> GetAssemblyLocation(MeshNode node);

    /// <summary>
    /// Reactive: emits the full compilation result (assembly + extracted NodeType
    /// configurations) for the node.
    /// <para>
    /// Optional <paramref name="sourcesOverride"/> lets the caller hand the freshly-
    /// observed source set in instead of letting the compiler re-fetch via the cached
    /// <c>workspace.GetQuery</c> SyncedQuery. The latter's first emission can be
    /// stale on the just-modified Code node — the upstream <c>ObserveQuery</c> has
    /// emitted the post-update event but the SyncedQuery's downstream gate only
    /// fires once every query has reported, with whatever cached value sits in the
    /// Replay(1) buffer. <c>HandleCreateRelease</c> already runs an uncached
    /// <c>IMeshService.ObserveQuery</c> to evaluate <c>IsSourcesUpToDate</c>, so the
    /// sources it has seen by the time it kicks off the compile are the ones the
    /// compile must consume — passing them through closes the staleness window
    /// between trigger and compile-side fetch (root cause of the V2-compile-bytes-
    /// are-V1 outcome in <c>CodeEditRecompileTest</c>).
    /// </para>
    /// </summary>
    IObservable<NodeCompilationResult?> CompileAndGetConfigurations(
        MeshNode node,
        IReadOnlyList<MeshNode>? sourcesOverride = null);

    /// <summary>
    /// Loads NodeType configurations from an already-compiled assembly without
    /// triggering a new compile. Used when <see cref="MeshNode.AssemblyLocation"/>
    /// is already populated (e.g. the compile watcher just finished V2 and set it).
    /// </summary>
    IObservable<NodeCompilationResult?> GetConfigurationsFromExistingAssembly(MeshNode node);
}
