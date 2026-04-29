using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Distinct lifecycle states of a NodeType's compile. Consumers (e.g. MCP
/// <c>GetDiagnostics</c>) must distinguish <see cref="Unknown"/> — "nothing
/// is recorded because no compile has run since the last invalidation" —
/// from <see cref="Ok"/> — "the last compile actually succeeded". Returning
/// the former as the latter causes false-green diagnostics (edit → recycle →
/// diagnostics reports Ok → user navigates → fresh compile fails).
/// </summary>
public enum CompilationStatus
{
    /// <summary>No compile has completed since the last invalidation.</summary>
    Unknown,

    /// <summary>
    /// Caller has requested a compile (set on the NodeType MeshNode via stream.Update);
    /// the per-NodeType hub's compile watcher will pick this up, flip to
    /// <see cref="Compiling"/>, and run Roslyn. Used as the trigger signal in the
    /// stream-update / sync-stream-broadcast slow path that replaces
    /// <c>GetCompilationPathRequest</c>.
    /// </summary>
    Pending,

    /// <summary>A compile is currently running.</summary>
    Compiling,

    /// <summary>The most recent compile completed successfully.</summary>
    Ok,

    /// <summary>The most recent compile failed; <c>GetCompilationError</c> has the text.</summary>
    Error
}

/// <summary>
/// Service for managing NodeType data with caching and compilation.
/// </summary>
public interface INodeTypeService
{
    /// <summary>
    /// Gets the NodeTypeConfiguration for fast hub configuration lookup.
    /// In cached state, returns immediately without remote calls.
    /// </summary>
    NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath);

    /// <summary>
    /// Reactive enrichment: emits a MeshNode populated with its NodeType's
    /// <see cref="MeshNode.HubConfiguration"/> + <see cref="MeshNode.AssemblyLocation"/>.
    /// Composes via <c>SelectMany</c> against the per-NodeType-hub's
    /// <c>GetCompilationPathRequest</c> contract — never <c>await</c> in hub-reachable
    /// code (see <c>Doc/Architecture/AsynchronousCalls.md</c>).
    /// </summary>
    IObservable<MeshNode> EnrichWithNodeType(MeshNode node);

    /// <summary>
    /// Task-bridged overload retained for the grain-lifecycle boundary
    /// (<c>MessageHubGrain.OnActivateAsync</c>, etc.) where <c>await</c> is sanctioned.
    /// Hub handlers MUST use the <see cref="EnrichWithNodeType(MeshNode)"/> Observable
    /// overload directly.
    /// </summary>
    Task<MeshNode> EnrichWithNodeTypeAsync(MeshNode node, CancellationToken ct = default)
        => EnrichWithNodeType(node).FirstAsync().ToTask(ct);

    /// <summary>
    /// Gets the node types that can be created as children of the specified node.
    /// </summary>
    IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(string nodePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached HubConfiguration function for a node type.
    /// Applies the DefaultNodeHubConfiguration from MeshConfiguration if available.
    /// </summary>
    Func<MessageHubConfiguration, MessageHubConfiguration>? GetCachedHubConfiguration(string nodeTypePath);

    /// <summary>
    /// Gets the access rule extracted from the hub configuration for a node type.
    /// Returns null if no access rules are defined in the hub config.
    /// </summary>
    INodeTypeAccessRule? GetAccessRule(string nodeTypePath) => null;

    /// <summary>
    /// Returns the last compilation error recorded for the given NodeType path,
    /// or <c>null</c> if compilation has not failed. The error text includes the
    /// formatted Roslyn diagnostics as produced by
    /// <c>MeshNodeCompilationService</c>.
    /// </summary>
    /// <remarks>
    /// Used by MCP <c>Get</c> / <c>GetDiagnostics</c> so callers (e.g. the Coder
    /// agent) can verify that a NodeType they just created or updated actually
    /// compiles. The error is cached by <c>NodeTypeService</c> each time a compile
    /// fails and cleared when it succeeds.
    /// </remarks>
    string? GetCompilationError(string nodeTypePath) => null;

    /// <summary>
    /// Returns <c>true</c> if compilation for the given NodeType path is currently
    /// running (the task has been kicked off but not yet completed). Consumers can use
    /// this to render a "Compiling…" progress indicator so the user sees activity
    /// rather than a blank layout while they wait.
    /// </summary>
    bool IsCompiling(string nodeTypePath) => false;

    /// <summary>
    /// When compilation for <paramref name="nodeTypePath"/> is running, returns when
    /// it started (UTC). Otherwise <c>null</c>. Paired with <see cref="IsCompiling"/>
    /// to show elapsed-time feedback in progress overlays.
    /// </summary>
    DateTimeOffset? GetCompilationStartedAt(string nodeTypePath) => null;

    /// <summary>
    /// Flushes all cached state (compilation errors, cached tasks, cached hub
    /// configurations, etc.) for the given NodeType path, forcing a fresh compile
    /// on the next access. Paired with <c>DisposeRequest</c> to fully reset a
    /// stuck NodeType — disposing the hub alone is not enough because the
    /// service-level caches survive hub teardown.
    /// </summary>
    void InvalidateCache(string nodeTypePath) { }

    /// <summary>
    /// Snapshot of NodeType paths currently being compiled. Used by the portal
    /// to render a "Compiling…" progress indicator while a navigation request is
    /// blocked waiting on a compile.
    /// </summary>
    IReadOnlyCollection<string> GetCompilingPaths() => Array.Empty<string>();

    /// <summary>
    /// When the last successful compile for <paramref name="nodeTypePath"/>
    /// completed (UTC). Returns <c>null</c> if no compile has succeeded since
    /// the NodeType was last invalidated. Paired with <see cref="GetStatus"/>
    /// to let diagnostics distinguish "never compiled" from "compiled cleanly".
    /// </summary>
    DateTimeOffset? GetLastSuccessfulCompileAt(string nodeTypePath) => null;

    /// <summary>
    /// Four-state lifecycle of a NodeType's compile. Precedence:
    /// <list type="number">
    ///   <item><see cref="CompilationStatus.Compiling"/> if a compile is running.</item>
    ///   <item><see cref="CompilationStatus.Error"/> if the most recent compile failed
    ///     (<see cref="GetCompilationError"/> returns the text).</item>
    ///   <item><see cref="CompilationStatus.Ok"/> if a compile has succeeded since
    ///     the last invalidation (<see cref="GetLastSuccessfulCompileAt"/> is set).</item>
    ///   <item><see cref="CompilationStatus.Unknown"/> otherwise — no compile has
    ///     completed since invalidation; the caller should trigger one (e.g.
    ///     navigate to a layout area) before trusting any prior state.</item>
    /// </list>
    /// </summary>
    CompilationStatus GetStatus(string nodeTypePath)
    {
        if (IsCompiling(nodeTypePath)) return CompilationStatus.Compiling;
        if (!string.IsNullOrEmpty(GetCompilationError(nodeTypePath))) return CompilationStatus.Error;
        return GetLastSuccessfulCompileAt(nodeTypePath) is null
            ? CompilationStatus.Unknown
            : CompilationStatus.Ok;
    }

    /// <summary>
    /// Fully-reactive assembly path lookup. Per the rules in
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>, callers must compose this with
    /// <c>SelectMany</c> / <c>Subscribe</c> — never <c>await</c> — because the flow
    /// runs through the hub pipeline (<c>GetDataRequest</c> for the current NodeType
    /// node, <see cref="IAssemblyStore"/> for the cached bytes, and a hub-dispatched
    /// compile on cache miss) and any <c>await</c> on that path will deadlock.
    ///
    /// Contract: emits a single local filesystem path the caller can feed to
    /// <c>AssemblyLoadContext.LoadFromAssemblyPath</c>. The path corresponds to the
    /// assembly for the NodeType's current <see cref="Mesh.MeshNode.Version"/> — if the
    /// store has it, the path comes straight from there; otherwise the service compiles,
    /// stores, and emits the resulting path.
    /// </summary>
    IObservable<string> GetAssemblyPath(string nodeTypePath) =>
        System.Reactive.Linq.Observable.Throw<string>(
            new System.NotSupportedException(
                "IAssemblyStore-backed GetAssemblyPath is not wired on this INodeTypeService — "
                + "register a concrete store (AddFileSystemAssemblyStore / AddBlobAssemblyStore) "
                + "and a NodeTypeService that consumes it."));
}
