using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Composite snapshot of everything <see cref="NodeTypeService"/> knows about a single
/// NodeType at one moment in time. Replaces the 8 separate <c>ConcurrentDictionary</c>
/// fields (<c>_hubConfigurations</c>, <c>_creatableTypesRules</c>,
/// <c>_notCreatableTypes</c>, <c>_accessRules</c>, <c>_compilationErrors</c>,
/// <c>_compilationSucceededAt</c>, <c>_compilingInProgress</c>, <c>_releaseKeys</c>)
/// with one immutable record per NodeType.
///
/// <para>The snapshot's contents are driven by properties on the NodeType
/// <see cref="MeshNode"/> itself — <see cref="NodeTypeDefinition.CompilationStatus"/>,
/// <see cref="NodeTypeDefinition.CompilationError"/>, <see cref="MeshNode.AssemblyLocation"/> —
/// per the actor-pattern + dirty-flag-on-owner architecture. The mirror is a passive
/// observer of the MeshNode's reactive stream.</para>
/// </summary>
public sealed record NodeTypeRuntime(
    string NodeTypePath,
    Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration,
    string? AssemblyLocation,
    CreatableTypesRules? CreatableTypesRules,
    bool NotCreatable,
    INodeTypeAccessRule? AccessRule,
    string? CompilationError,
    CompilationStatus Status,
    DateTimeOffset? CompilingSince,
    DateTimeOffset? LastSuccessfulCompileAt,
    string? ReleaseKey);

/// <summary>
/// Live mirror of a single NodeType's runtime state. Backed by
/// <see cref="IWorkspace.GetMeshNodeStream"/> on the NodeType's path — when the
/// underlying <see cref="MeshNode"/> emits (initial frame, IsDirty flip, RequestedStatus
/// change, compile-watcher result write), the mirror re-projects to a fresh
/// <see cref="NodeTypeRuntime"/> snapshot.
///
/// <para>Sync getters on <see cref="NodeTypeService"/> read <see cref="Current"/>
/// directly — Replay(1).RefCount means the latest snapshot is always materialised
/// after first subscribe. The keep-alive subscription in <see cref="Mirror"/>'s
/// constructor ensures Current is updated even when no external subscriber is
/// listening.</para>
/// </summary>
public sealed class NodeTypeRuntimeMirror : IDisposable
{
    private readonly BehaviorSubject<NodeTypeRuntime?> _current;
    private readonly IDisposable _keepAlive;
    private readonly ILogger? _logger;

    /// <summary>
    /// The cache key — a stable NodeType path. Used by
    /// <see cref="NodeTypeService"/>'s <see cref="IMemoryCache"/> entries and as the
    /// observable's identity for diagnostic logging.
    /// </summary>
    public string NodeTypePath { get; }

    /// <summary>
    /// Reactive stream of NodeType runtime snapshots. Emits the latest projection
    /// every time the underlying <see cref="MeshNode"/> changes. <c>Replay(1).RefCount()</c>
    /// — multiple subscribers share one upstream subscription.
    /// </summary>
    public IObservable<NodeTypeRuntime?> Stream => _current.AsObservable();

    /// <summary>
    /// Latest projection of the NodeType's runtime state, updated synchronously by the
    /// keep-alive subscription. Sync getters on <see cref="INodeTypeService"/> read
    /// this directly.
    /// </summary>
    public NodeTypeRuntime? Current => _current.Value;

    /// <summary>
    /// Constructs a mirror over <paramref name="meshNodeStream"/>. <paramref name="project"/>
    /// transforms each <see cref="MeshNode"/> emission into a <see cref="NodeTypeRuntime"/>.
    /// </summary>
    public NodeTypeRuntimeMirror(
        string nodeTypePath,
        IObservable<MeshNode?> meshNodeStream,
        Func<MeshNode, NodeTypeRuntime> project,
        ILogger? logger = null)
    {
        NodeTypePath = nodeTypePath;
        _logger = logger;
        _current = new BehaviorSubject<NodeTypeRuntime?>(null);

        // Keep-alive: even with no external subscriber, the mirror stays subscribed
        // to the upstream MeshNode stream so Current reflects the latest state at any
        // time. The MemoryCache eviction callback disposes this subscription.
        _keepAlive = meshNodeStream
            .Where(n => n != null)
            .Select(n => project(n!))
            .Subscribe(
                runtime => _current.OnNext(runtime),
                ex => _logger?.LogWarning(ex,
                    "NodeTypeRuntimeMirror for {NodeTypePath} faulted; snapshot frozen",
                    nodeTypePath));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _keepAlive.Dispose();
        _current.OnCompleted();
        _current.Dispose();
    }
}

/// <summary>
/// <see cref="IMemoryCache"/>-backed registry of <see cref="NodeTypeRuntimeMirror"/>
/// instances, keyed by NodeType path. Replaces <see cref="NodeTypeService"/>'s
/// <c>_compilationTasks</c> + the 8 scattered state dictionaries with a single
/// sliding-expiration cache.
///
/// <para>Eviction (5-min idle by default) disposes the underlying mirror, which
/// disposes its keep-alive subscription to <c>workspace.GetMeshNodeStream</c>.</para>
/// </summary>
public sealed class NodeTypeMirrorRegistry : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly IMessageHub _hub;
    private readonly Func<string, MeshNode, NodeTypeRuntime> _project;
    private readonly ILogger? _logger;
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Constructs the registry. <paramref name="project"/> is the function that turns
    /// a NodeType <see cref="MeshNode"/> emission into a fresh <see cref="NodeTypeRuntime"/>
    /// snapshot — typically reading <see cref="NodeTypeDefinition.CompilationStatus"/>,
    /// <see cref="NodeTypeDefinition.CompilationError"/>, and
    /// <see cref="MeshNode.AssemblyLocation"/>.
    /// </summary>
    public NodeTypeMirrorRegistry(
        IMessageHub hub,
        Func<string, MeshNode, NodeTypeRuntime> project,
        ILogger? logger = null)
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _hub = hub;
        _project = project;
        _logger = logger;
    }

    /// <summary>
    /// Returns the mirror for <paramref name="nodeTypePath"/>, creating it on first
    /// access. Subsequent calls within the idle TTL return the same cached mirror —
    /// <see cref="NodeTypeRuntimeMirror.Current"/> reads are O(1) and require no
    /// storage round-trip.
    /// </summary>
    public NodeTypeRuntimeMirror Get(string nodeTypePath) =>
        _cache.GetOrCreate(nodeTypePath, entry =>
        {
            entry.SlidingExpiration = IdleTtl;

            var workspace = _hub.GetWorkspace();
            var stream = workspace.GetMeshNodeStream(nodeTypePath);

            var mirror = new NodeTypeRuntimeMirror(
                nodeTypePath,
                stream,
                node => _project(nodeTypePath, node),
                _logger);

            entry.RegisterPostEvictionCallback(static (_, value, _, _) =>
            {
                (value as NodeTypeRuntimeMirror)?.Dispose();
            });

            return mirror;
        })!;

    /// <summary>
    /// Returns the current snapshot for <paramref name="nodeTypePath"/> if a mirror
    /// is materialised AND its keep-alive has seen at least one emission. Returns
    /// <c>null</c> for cold cache or pre-Initial state — sync getters use this for
    /// "do I have data yet?" checks.
    /// </summary>
    public NodeTypeRuntime? TryGetCurrent(string nodeTypePath) =>
        _cache.TryGetValue(nodeTypePath, out var v) && v is NodeTypeRuntimeMirror mirror
            ? mirror.Current
            : null;

    /// <summary>
    /// Evicts the mirror for <paramref name="nodeTypePath"/>, disposing its
    /// subscription. Next <see cref="Get"/> rebuilds from scratch.
    /// </summary>
    public void Invalidate(string nodeTypePath) => _cache.Remove(nodeTypePath);

    /// <inheritdoc/>
    public void Dispose() => (_cache as IDisposable)?.Dispose();
}
