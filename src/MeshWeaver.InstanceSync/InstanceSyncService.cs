using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// Config store + shared helpers for the remote-instance sync feature. A Space's sync sources
/// live at <c>{space}/_Sync/{sourceId}</c> (content <see cref="InstanceSyncConfig"/>); this
/// service owns their lifecycle (add / watch / remove) and the read-modify-write status +
/// manifest updates the <see cref="InstanceSyncWorker"/> performs. Mirrors
/// <c>GitHubSyncService</c>'s config section — the GUI binds the standard node-content editor
/// directly to each config node's stream; nothing here replicates node state.
///
/// <para>🚨 Reactive end-to-end (no <c>async</c>/<c>await</c>/<c>Task</c> in any signature);
/// all remote I/O lives behind <c>IRemoteMeshClient</c> (IoPool-bounded).</para>
/// </summary>
public sealed class InstanceSyncService(
    IMessageHub hub,
    IMeshService meshService,
    ILogger<InstanceSyncService>? logger = null)
{
    /// <summary>The satellite segment holding a Space's sync registry: <c>{space}/_Sync</c>.</summary>
    public const string ConfigId = "_Sync";

    /// <summary>The <see cref="MeshNode.NodeType"/> of a sync-source config node.</summary>
    public const string ConfigNodeType = "InstanceSyncConfig";

    /// <summary>The <see cref="MeshNode.NodeType"/> identifying a Space (the unit instance sync acts on).</summary>
    public const string SpaceNodeType = "Space";

    /// <summary>The sync-source config path: <c>{spacePath}/_Sync/{sourceId}</c>.</summary>
    public static string ConfigPath(string spacePath, string sourceId) =>
        $"{spacePath}/{ConfigId}/{sourceId}";

    /// <summary>The namespace all of a space's sync sources share: <c>{spacePath}/_Sync</c>.</summary>
    public static string ConfigNamespace(string spacePath) => $"{spacePath}/{ConfigId}";

    /// <summary>The containing space of a config path (its first segment — spaces are top-level).</summary>
    public static string SpaceOf(string configPath) => configPath.Split('/', 2)[0];

    // ══════════════════════════════════════════════════════════════════════════
    //  Config lifecycle
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Live stream of ALL of the space's sync-source config nodes, ordered by source id.
    /// Re-emits when a source is added, removed, or edited — the synced <c>GetQuery</c> over
    /// the <c>_Sync</c> namespace (same pattern as <c>GitHubSyncService.WatchConfigNodes</c>).
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> WatchConfigNodes(string spacePath)
    {
        var ns = ConfigNamespace(spacePath);
        return hub.GetWorkspace()
            .GetQuery($"instsync-cfgs:{spacePath}", $"namespace:{ns} nodeType:{ConfigNodeType}")
            .Select(nodes => (IReadOnlyList<MeshNode>)(nodes ?? [])
                .Where(n => string.Equals(n.Namespace, ns, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>Live stream of one sync-source config node (or null when absent).</summary>
    public IObservable<MeshNode?> WatchConfigNode(string spacePath, string sourceId)
    {
        var path = ConfigPath(spacePath, sourceId);
        return hub.GetWorkspace()
            .GetQuery($"instsync-cfg:{path}", $"path:{path}")
            .Select(nodes => nodes?.FirstOrDefault(n =>
                string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>One-shot typed config read off the synced query (null when the node is absent
    /// or degraded). Eventually consistent — fine for GUI/list reads and test polling.</summary>
    public IObservable<InstanceSyncConfig?> ReadConfig(string spacePath, string sourceId) =>
        WatchConfigNode(spacePath, sourceId).Take(1).Select(Extract);

    /// <summary>
    /// AUTHORITATIVE one-shot config read — the owning hub's current state via the node stream,
    /// never the (lagged) query index. The worker MUST use this: a drain poked by a config
    /// change event would otherwise race the index and act on the pre-change snapshot (e.g.
    /// read Active=true right after the user paused) with no second poke coming. CQRS rule:
    /// never query for a single node's content you're about to act on.
    /// </summary>
    public IObservable<InstanceSyncConfig?> ReadConfigAuthoritative(string spacePath, string sourceId) =>
        hub.GetMeshNode(ConfigPath(spacePath, sourceId), TimeSpan.FromSeconds(15))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Select(Extract);

    /// <summary>
    /// Adds a sync source to the space: creates <c>{space}/_Sync/{sourceId}</c> (defaults, id =
    /// sanitized name). Create-on-absent — adding an existing id returns the node untouched.
    /// The remote URL / token are filled in afterwards through the standard node editor bound
    /// to the returned node's path.
    /// </summary>
    public IObservable<MeshNode> AddSyncSource(string spacePath, string name)
    {
        var sourceId = SanitizeSourceId(name);
        if (string.IsNullOrEmpty(sourceId))
            return Observable.Throw<MeshNode>(new ArgumentException(
                "The sync-source name must contain at least one letter or digit.", nameof(name)));

        // Capture identity synchronously BEFORE the async existence-read hop — the SelectMany
        // continuation can run without the AsyncLocal AccessContext, and CreateNode captures the
        // context at its call. Same async-boundary fix as GitHubSyncService.EnsureConfigNode.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;
        return WatchConfigNode(spacePath, sourceId).Take(1).SelectMany(existing =>
        {
            if (existing is not null) return Observable.Return(existing);
            var node = new MeshNode(sourceId, ConfigNamespace(spacePath))
            {
                NodeType = ConfigNodeType,
                Name = name,
                State = MeshNodeState.Active,
                MainNode = spacePath,
                Content = new InstanceSyncConfig(),
            };
            var create = accessService is null || ctx is null
                ? meshService.CreateNode(node)
                : Observable.Using(() => accessService.SwitchAccessContext(ctx), _ => meshService.CreateNode(node));
            // A concurrent caller may win the create race — fall back to reading the node.
            return create.Catch<MeshNode, Exception>(_ =>
                WatchConfigNode(spacePath, sourceId).Where(n => n is not null).Select(n => n!).Take(1));
        });
    }

    /// <summary>Removes (cancels) a sync source — deletes its config node; the coordinator
    /// stops the worker on the resulting change-feed event.</summary>
    public IObservable<bool> RemoveSyncSource(string spacePath, string sourceId) =>
        meshService.DeleteNode(ConfigPath(spacePath, sourceId));

    /// <summary>Sanitizes a display name into a node id: letters/digits/dash/underscore only.</summary>
    private static string SanitizeSourceId(string name) =>
        new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');

    // ══════════════════════════════════════════════════════════════════════════
    //  Status + manifest updates (read-modify-write via stream.Update)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read-modify-write of the config content via <c>GetMeshNodeStream(path).Update</c> — the
    /// owning hub serialises writers, so worker status stamps never clobber a concurrent GUI
    /// edit of the connection fields. Cold; the caller subscribes.
    /// </summary>
    public IObservable<MeshNode> UpdateConfig(string configPath, Func<InstanceSyncConfig, InstanceSyncConfig> update) =>
        hub.GetWorkspace().GetMeshNodeStream(configPath).Update(node =>
        {
            var current = Extract(node) ?? new InstanceSyncConfig();
            return node with { Content = update(current) };
        });

    /// <summary>
    /// Appends a local change to the durable manifest, coalescing by path: only the LATEST
    /// pending state of a node matters because the drain pushes CURRENT content. Ordering of
    /// first-appearance is preserved so the drain replays roughly in change order.
    /// </summary>
    public IObservable<MeshNode> AppendPending(string configPath, PendingChange change) =>
        UpdateConfig(configPath, cfg => cfg with
        {
            PendingChanges = Coalesce(cfg.PendingChanges, change),
        });

    /// <summary>
    /// Removes drained entries from the manifest — only entries whose version is ≤ the drained
    /// version, so a write racing the drain stays pending and is pushed on the next pass.
    /// </summary>
    public IObservable<MeshNode> RemoveDrained(string configPath, IReadOnlyCollection<PendingChange> drained) =>
        UpdateConfig(configPath, cfg => cfg with
        {
            PendingChanges = cfg.PendingChanges
                .Where(p => !drained.Any(d =>
                    string.Equals(d.Path, p.Path, StringComparison.Ordinal) && p.Version <= d.Version))
                .ToImmutableList(),
        });

    private static ImmutableList<PendingChange> Coalesce(ImmutableList<PendingChange> pending, PendingChange change)
    {
        var index = pending.FindIndex(p => string.Equals(p.Path, change.Path, StringComparison.Ordinal));
        return index < 0 ? pending.Add(change) : pending.SetItem(index, change);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Shared helpers (snapshot, filtering, remapping, equality)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Snapshot of the space's syncable nodes: root + descendants, minus satellite/governance
    /// subtrees (any <c>_</c>-prefixed segment after the root — including <c>_Sync</c> itself,
    /// so the sync registry and its tokens never replicate) and minus
    /// <see cref="SyncBehavior"/>-excluded nodes. Ordered parents-first (path depth) so the
    /// space root lands on the remote before its children.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> SnapshotSpaceNodes(string spacePath)
    {
        var root = hub.GetWorkspace().GetMeshNodeStream(spacePath)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(30));
        var descendants = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{spacePath} scope:descendants"))
            .Take(1).Select(c => (IEnumerable<MeshNode>)c.Items);
        return root.CombineLatest(descendants, (r, desc) =>
        {
            var all = new List<MeshNode>();
            if (r is not null) all.Add(r);
            all.AddRange(desc);
            return FilterSyncable(all, spacePath);
        });
    }

    /// <summary>Content-node filter — same semantics as GitHub sync's export filter.</summary>
    public static IReadOnlyList<MeshNode> FilterSyncable(List<MeshNode> all, string partition)
    {
        var excludedRoots = all
            .Where(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren)
            .Select(n => n.Path)
            .ToArray();
        bool UnderExcluded(string p) =>
            excludedRoots.Any(r => p.StartsWith(r + "/", StringComparison.Ordinal));

        return all
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && !n.Segments.Skip(1).Any(s => s.StartsWith('_'))
                        && n.SyncBehavior == SyncBehavior.Include
                        && !UnderExcluded(n.Path))
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(n => n.Segments.Count)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether a local path is inside the synced space AND syncable (no satellite
    /// segments — which also keeps the sync's own config/manifest writes out of the loop).</summary>
    public static bool IsSyncablePath(string path, string spacePath)
    {
        if (!string.Equals(path, spacePath, StringComparison.Ordinal)
            && !path.StartsWith(spacePath + "/", StringComparison.Ordinal))
            return false;
        var segments = path.Split('/');
        return !segments.Skip(1).Any(s => s.StartsWith('_'));
    }

    /// <summary>Maps a local path inside the space to its path on the remote space (and back —
    /// the mapping is symmetric).</summary>
    public static string RemapPath(string path, string fromSpace, string toSpace) =>
        string.Equals(path, fromSpace, StringComparison.Ordinal)
            ? toSpace
            : toSpace + path[fromSpace.Length..];

    /// <summary>
    /// Rebases a node onto a target path, dropping instance-local bookkeeping (version and
    /// audit stamps — the receiving instance stamps its own) so the payload is pure content.
    /// </summary>
    public static MeshNode RebaseNode(MeshNode node, string targetPath) =>
        MeshNode.FromPath(targetPath) with
        {
            Name = node.Name,
            Description = node.Description,
            NodeType = node.NodeType,
            Category = node.Category,
            Icon = node.Icon,
            Order = node.Order,
            State = node.State,
            Content = node.Content,
            SyncBehavior = node.SyncBehavior,
        };

    /// <summary>
    /// Content-level equality of two nodes (name, type, state, content as canonical JSON) — the
    /// convergence guard that terminates the push/pull echo: a change that round-trips back
    /// value-equal is dropped instead of re-written.
    /// </summary>
    public bool ContentEquals(MeshNode? a, MeshNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || !string.Equals(a.NodeType, b.NodeType, StringComparison.Ordinal)
            || a.State != b.State)
            return false;
        return JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(a.Content, hub.JsonSerializerOptions),
            JsonSerializer.SerializeToNode(b.Content, hub.JsonSerializerOptions));
    }

    /// <summary>Typed content extraction — tolerant of degraded JsonElement content, loud on failure.</summary>
    public InstanceSyncConfig? Extract(MeshNode? node) =>
        node.ContentAs<InstanceSyncConfig>(hub.JsonSerializerOptions, logger);

    /// <summary>
    /// Runs an operation under the system identity — for worker-originated writes (feed
    /// callbacks, drain/retry timers) that have NO ambient AccessContext; PostPipeline fails
    /// closed without one. Status/manifest stamps on the sync registry are infrastructure
    /// writes, the same identity model as <c>StaticRepoImporter</c>. GUI-originated calls keep
    /// the user's own context and never go through here.
    /// </summary>
    public IObservable<T> AsSystem<T>(Func<IObservable<T>> operation)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(() => accessService.ImpersonateAsSystem(), _ => operation());
    }
}
