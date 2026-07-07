using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a system log entry for diagnostic and monitoring purposes.
/// </summary>
/// <param name="Service">The name of the service that generated the log.</param>
/// <param name="ServiceId">Unique identifier for the service instance.</param>
/// <param name="Level">Log level (e.g., Info, Warning, Error).</param>
/// <param name="Timestamp">When the log entry was created.</param>
/// <param name="Message">The log message content.</param>
/// <param name="Exception">Exception details if applicable.</param>
/// <param name="Properties">Additional contextual properties.</param>
public record SystemLog(
    string Service,
    string ServiceId,
    string Level,
    DateTimeOffset Timestamp,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, object>? Properties
)
{
    /// <summary>
    /// Unique identifier for the log entry.
    /// </summary>
    public long Id { get; init; }
}

/// <summary>
/// Represents a message log entry for tracking message flow in the mesh.
/// </summary>
/// <param name="Service">The name of the service that processed the message.</param>
/// <param name="ServiceId">Unique identifier for the service instance.</param>
/// <param name="Timestamp">When the message was logged.</param>
/// <param name="Address">The hub address that processed the message.</param>
/// <param name="MessageId">Unique identifier for the message.</param>
/// <param name="Message">The message content as key-value pairs.</param>
/// <param name="Sender">Address of the message sender.</param>
/// <param name="Target">Address of the message target.</param>
/// <param name="State">Current state of the message delivery.</param>
/// <param name="AccessContext">Security and access context information.</param>
/// <param name="Properties">Additional message properties.</param>
public record MessageLog(
    string Service,
    string ServiceId,
    DateTimeOffset Timestamp,
    string Address,
    string MessageId,
    IReadOnlyDictionary<string, object?>? Message,
    string? Sender,
    string? Target,
    string? State,
    IReadOnlyDictionary<string, object?>? AccessContext,
    IReadOnlyDictionary<string, object?>? Properties)
{
    /// <summary>
    /// Unique identifier for the message log entry.
    /// </summary>
    public long Id { get; init; }
}
/// <summary>
/// Represents a node in the mesh that can handle requests.
/// The Id is the local identifier within a namespace (e.g., "Root", "Alice", "Story1").
/// The Namespace is the container path (e.g., "type", "Systemorph/type/Project").
/// Path is derived as {Namespace}/{Id} and serves as the unique identifier.
/// </summary>
public record MeshNode([property: Key] string Id, [property: Editable(false)] string? Namespace = null)
{
    /// <summary>
    /// The path for the built-in NodeType type definition node.
    /// Nodes with NodeType = NodeTypePath are type definitions.
    /// </summary>
    public const string NodeTypePath = "NodeType";

    /// <summary>
    /// The full path derived from Namespace and Id.
    /// For nodes without a namespace, this equals Id.
    /// </summary>
    public string Path => string.IsNullOrEmpty(Namespace) ? (Id) : $"{Namespace}/{Id}";

    /// <summary>
    /// Path of the main/primary node. For main nodes, MainNode == Path.
    /// For satellite nodes (comments, threads, approvals), this points to the primary node they belong to.
    /// </summary>
    [Editable(false)]
    public string MainNode { get; init; } = string.IsNullOrEmpty(Namespace) ? Id : $"{Namespace}/{Id}";

    /// <summary>
    /// Single segments as used for matching and addressing.
    /// Must be a computed property (not a readonly field) so that it reflects
    /// updated Namespace/Id after record copy via 'with {}'.
    /// </summary>
    [JsonIgnore, NotMapped]
    public IReadOnlyList<string> Segments =>
        string.IsNullOrEmpty(Namespace)
            ? (string.IsNullOrEmpty(Id) ? Array.Empty<string>() : Id.Split('/'))
            : Namespace.Split('/').Append(Id).ToArray();

    /// <summary>
    /// Extracts the prefix from a path.
    /// For template paths like "$template/graph/org/3", extracts "graph/org".
    /// For regular paths, returns the path as-is.
    /// </summary>
    private static string ExtractPrefix(string path)
    {

        // Format: $template/{prefix}/{segments}
        // e.g., "$template/graph/org/3" -> "graph/org"
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path.Substring(0, lastSlash) : path;
    }

    /// <summary>
    /// Creates a MeshNode from a full path by extracting Id and Namespace.
    /// E.g., "type/Root" becomes Id="Root", Namespace="type".
    /// </summary>
    public static MeshNode FromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be null or empty", nameof(path));

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
            return new MeshNode(path);

        var ns = path.Substring(0, lastSlash);
        var id = path.Substring(lastSlash + 1);
        return new MeshNode(id, ns);
    }

    /// <summary>
    /// Constant identifying the mesh input.
    /// </summary>
    public const string MeshIn = nameof(MeshIn);

    /// <summary>
    /// Human-readable name for display in UI.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Long-form description of this node. Used as the seed prompt for AI-assisted
    /// Name/Id/Icon generation in the Create dialog, and displayed in detail views.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The type/category of this node (e.g., "Northwind", "Todo", "Insurance").
    /// Used to identify the application type for routing and configuration.
    /// </summary>
    [Editable(false)]
    public string? NodeType { get; init; }

    /// <summary>
    /// Category for grouping in catalog views.
    /// When set, overrides NodeType as the grouping title.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Icon URL or identifier for display in UI.
    /// </summary>
    [ContentItem]
    public string? Icon { get; init; }

    /// <summary>
    /// Order for sorting (lower values appear first, null values appear last).
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Timestamp when this node was first created.
    /// Set once at creation time; never updated thereafter.
    /// </summary>
    [Editable(false)]
    public DateTimeOffset CreatedDate { get; init; }

    /// <summary>
    /// Identity (ObjectId / email) of the user or system that created this node.
    /// Stamped at creation time and never changed. Null for nodes that pre-date
    /// the field (e.g. seeded by file-system import without an authenticated user).
    /// </summary>
    [Editable(false)]
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Timestamp when this node was last modified.
    /// Used for cache invalidation of dynamically compiled assemblies.
    /// When reading from file system, defaults to file's last modified time if not specified in JSON.
    /// </summary>
    [Editable(false)]
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Identity (ObjectId / email) of the user or system that last modified this node.
    /// Stamped at every successful update; equal to <see cref="CreatedBy"/> immediately after creation.
    /// </summary>
    [Editable(false)]
    public string? LastModifiedBy { get; init; }

    /// <summary>
    /// The node's persistence clock — a version that increases <b>monotonically across
    /// activations</b> and identifies the last operation that mutated this node.
    /// <para>🚨 This is deliberately NOT the same clock as <see cref="IMessageHub.Version"/>.
    /// The hub clock resets to 0 on every (re)activation and also stamps the owning hub's
    /// LAYOUT-render Fulls; stamping <see cref="Version"/> straight from the fresh hub clock
    /// made it REGRESS after a deactivate → reactivate cycle (idle-release / recycle / replica
    /// restart) — the write-rollback / "v113 read back as v3" version regression of issue #325.
    /// The node's Version is advanced forward-only from its OWN persisted value via
    /// <see cref="NextVersion(long, long)"/>, so it survives a recycle without regressing while
    /// the hub clock (and thus layout Fulls) is never touched. See
    /// <c>Doc/Architecture/MeshNodeVersioning.md</c>.</para>
    /// </summary>
    [Editable(false)]
    public long Version { get; init; }

    /// <summary>
    /// Computes the next persistence <see cref="Version"/> for a write to a node that currently
    /// carries <paramref name="currentVersion"/>, given the owning hub's per-message clock
    /// <paramref name="hubVersion"/>. The result is <c>max(hubVersion, currentVersion + 1)</c>:
    /// it tracks the hub clock while that clock leads (preserving the "1 op = 1 version stamp"
    /// model within an activation), but never falls to or below the version the node already
    /// carries. After a recycle the hub clock has reset toward 0 while the node loaded its
    /// persisted <paramref name="currentVersion"/> verbatim, so the <c>+ 1</c> floor keeps the
    /// node's Version strictly increasing across activations — the fix for issue #325 that does
    /// NOT re-seed the shared hub clock (which would drop layout Fulls, the 2026-06-18 wedge).
    /// </summary>
    public static long NextVersion(long hubVersion, long currentVersion)
        => Math.Max(hubVersion, currentVersion + 1);

    /// <summary>
    /// The lifecycle state of this node.
    /// Transient nodes are awaiting hub confirmation.
    /// Active nodes have been validated and persisted.
    /// </summary>
    [Editable(false)]
    public MeshNodeState State { get; init; } = MeshNodeState.Active;

    /// <summary>
    /// The data model content for this node.
    /// The type depends on NodeType (e.g., Organization, Project, Story).
    /// </summary>
    /// <remarks>
    /// We deliberately do NOT mark this with <see cref="PreventLoggingAttribute"/>.
    /// Doing so caused validator pipelines (e.g. DeleteNode + INodeValidator) to
    /// observe a MeshNode whose Content was stripped — root cause is the
    /// <see cref="MeshWeaver.Messaging.Serialization.LoggingTypeInfoResolver"/>'s
    /// mutation of the inner resolver's <c>JsonTypeInfo.Properties</c> bleeding
    /// into the main serializer's view of MeshNode (an <c>object?</c> property
    /// participating in polymorphic resolution). The content payload is still
    /// large; if you turn on Debug, mind that. The catch-block in
    /// <see cref="MessageService"/> logs deliveries via the LogText helper, which
    /// still uses LoggingSerializerOptions for the envelope.
    /// </remarks>
    [Editable(false)]
    public object? Content { get; init; }

    /// <summary>
    /// Hub configuration function that configures the message hub for this node.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration { get; init; }

    /// <summary>
    /// Runtime-only marker for an in-memory static NodeType <b>definition</b> that has been
    /// dissociated from runtime node-serving because its partition is a DB-synced
    /// <i>NodeType catalog</i> (Harness, Agent, Skill — see
    /// <c>Doc/Architecture/NodeTypeCatalogs.md</c>). When <c>true</c>, this node:
    /// <list type="bullet">
    ///   <item>is NOT served as the runtime node at its <see cref="Path"/> — Postgres owns the
    ///     <c>nodeType:NodeType</c> partition root (the serve seams
    ///     <c>MeshDataSource.WithMeshNodes</c> / <c>MessageHubGrain.TryResolveStaticNode</c> /
    ///     the <c>CreateNode</c> existing-node probe skip it);</item>
    ///   <item>is NOT returned as a query result (<c>StaticNodeQueryProvider</c> excludes it),
    ///     so the bare partition path resolves to exactly one node;</item>
    ///   <item>IS still consulted as a <i>definition</i> via
    ///     <see cref="MeshWeaver.Mesh.Services.StaticNodeProviderExtensions.FindStaticNode"/> —
    ///     it supplies <see cref="HubConfiguration"/> by type name (enrichment of the catalog's
    ///     instances) and proves the type exists.</item>
    /// </list>
    /// <c>[JsonIgnore]/[NotMapped]</c>: it is never persisted (the in-memory definition node is
    /// never written to a partition) and, like <see cref="HubConfiguration"/>, is excluded from
    /// value equality.
    /// </summary>
    [JsonIgnore, NotMapped]
    public bool IsDefinitionOnly { get; init; }

    /// <summary>
    /// Pre-rendered HTML for markdown nodes.
    /// Populated at parse time for instant display during Blazor prerender phase.
    /// </summary>
    public string? PreRenderedHtml { get; init; }

    /// <summary>
    /// User's intended Id for this node. Used during creation flow
    /// when transient node path uses GUID but user wants specific Id.
    /// </summary>
    public string? DesiredId { get; init; }

    /// <summary>
    /// When set on a NodeType definition node, marks all instances of this type as satellite nodes.
    /// Satellite nodes' MainNode is set to their primary node path at creation time.
    /// </summary>
    [Editable(false)]
    public bool IsSatelliteType { get; init; }

    /// <summary>
    /// Contexts from which this node (or nodes of this type) should be excluded.
    /// Default inclusive: null/empty means visible everywhere.
    /// E.g., {"search"} excludes from main search but visible in create menus.
    /// For NodeType definition nodes, instances of that type inherit the exclusion.
    /// </summary>
    public IReadOnlyCollection<string>? ExcludeFromContext { get; init; }

    /// <summary>
    /// How this node participates in static-repo sync (import/export).
    /// <see cref="SyncBehavior.Include"/> (default) → import overwrites it from the static repo.
    /// <see cref="SyncBehavior.ExcludeThisOnly"/> → this node is skipped, its children still sync.
    /// <see cref="SyncBehavior.ExcludeThisAndChildren"/> → this node and all descendants are
    /// skipped. This is how a user "claims" an imported node/subtree by editing it so the next
    /// import won't clobber it. See <c>Doc/Architecture/StaticRepoImport.md</c>.
    /// </summary>
    public SyncBehavior SyncBehavior { get; init; } = SyncBehavior.Include;


    /// <summary>
    /// Gets or sets the global service configurations for this mesh node.
    /// </summary>
    [JsonIgnore, NotMapped]
    [Editable(false)]
    public ImmutableList<Func<IServiceCollection, IServiceCollection>> GlobalServiceConfigurations { get; set; } = [];

    /// <summary>
    /// Adds a global service registry configuration to this mesh node.
    /// </summary>
    /// <param name="services">Function to configure services.</param>
    /// <returns>A new MeshNode with the added service configuration.</returns>
    public MeshNode WithGlobalServiceRegistry(Func<IServiceCollection, IServiceCollection> services)
        => this with { GlobalServiceConfigurations = GlobalServiceConfigurations.Add(services) };

    /// <summary>
    /// Content-aware value equality. <see cref="Content"/> is an <c>object?</c> that —
    /// after a cross-hub sync round-trip, AND in the <c>IMeshNodeStreamCache</c>
    /// whose hub does not know domain types — is a <see cref="JsonElement"/>. A
    /// <c>JsonElement</c> has NO structural equality: two elements with byte-identical
    /// JSON are never <c>.Equals</c>. The compiler-synthesized record equality therefore
    /// reports EVERY re-synced node as "changed", defeating the
    /// <c>SynchronizationStream.SetCurrent</c> dedup so the whole node re-broadcasts on
    /// every push — the sync fan-out storm (a single thread node taking ~130
    /// <c>SetCurrentRequest</c>s for one streamed round). We compare <c>JsonElement</c>
    /// content with <see cref="JsonElement.DeepEquals(JsonElement, JsonElement)"/>.
    ///
    /// <para>The runtime-only wiring (<see cref="HubConfiguration"/>,
    /// <see cref="GlobalServiceConfigurations"/>) is <c>[JsonIgnore]/[NotMapped]</c>,
    /// reference-typed (a <c>Func</c> / a list of <c>Func</c>), and not part of the
    /// node's persisted value — it is deliberately EXCLUDED from value equality.
    /// Including it would compare delegates by reference and defeat the dedup just as
    /// surely as the JsonElement problem.</para>
    /// </summary>
    public virtual bool Equals(MeshNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id
               && Namespace == other.Namespace
               && MainNode == other.MainNode
               && Name == other.Name
               && Description == other.Description
               && NodeType == other.NodeType
               && Category == other.Category
               && Icon == other.Icon
               && Order == other.Order
               && CreatedDate.Equals(other.CreatedDate)
               && CreatedBy == other.CreatedBy
               && LastModified.Equals(other.LastModified)
               && LastModifiedBy == other.LastModifiedBy
               && Version == other.Version
               && State == other.State
               && PreRenderedHtml == other.PreRenderedHtml
               && DesiredId == other.DesiredId
               && IsSatelliteType == other.IsSatelliteType
               && SyncBehavior == other.SyncBehavior
               && SequenceEqualOrNull(ExcludeFromContext, other.ExcludeFromContext)
               && ContentEquals(Content, other.Content);
    }

    /// <summary>
    /// Hashes a cheap, stable subset of the equality fields (id, namespace, node type, version,
    /// last-modified, state). The subset guarantees equal nodes always hash equal without paying
    /// the cost of hashing the full <c>Content</c> payload on every lookup.
    /// </summary>
    /// <returns>A hash code consistent with the type's equality.</returns>
    public override int GetHashCode()
    {
        // Cheap, stable SUBSET of the equality fields — must be a subset so equal nodes
        // always hash equal (Content's precise compare lives in Equals; hashing a large
        // JsonElement per lookup would be wasteful and is unnecessary for correctness).
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Namespace);
        hash.Add(NodeType);
        hash.Add(Version);
        hash.Add(LastModified);
        hash.Add(State);
        return hash.ToHashCode();
    }

    private static bool SequenceEqualOrNull(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Count == b.Count && a.SequenceEqual(b);
    }

    /// <summary>
    /// Equality for the untyped <see cref="Content"/>: structural JSON compare when both
    /// sides are <see cref="JsonElement"/> (the cache / cross-hub-sync representation);
    /// otherwise the content type's own <c>Equals</c>. Mixed (one JsonElement, one typed)
    /// falls through to <c>false</c> — no <see cref="JsonSerializerOptions"/> is available
    /// here to bridge representations, and a missed dedup there is merely a wasted push,
    /// never incorrect.
    /// </summary>
    private static bool ContentEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is JsonElement ea && b is JsonElement eb)
            return JsonElement.DeepEquals(ea, eb);
        return a.Equals(b);
    }
}

/// <summary>
/// Visibility conventions for mesh nodes — the single source of truth every query
/// backend (Postgres, Cosmos, storage-adapter, static) consults to decide whether a
/// node appears in a given UI/query <c>context</c> (e.g. <c>"search"</c>, <c>"create"</c>).
///
/// <para>Two independent mechanisms hide a node:</para>
/// <list type="number">
///   <item><b>Explicit opt-out</b> — <see cref="MeshNode.ExcludeFromContext"/> on the node
///     (or, for type definitions, inherited by every instance) lists the contexts to hide
///     from. This is the only way to hide from non-search contexts such as <c>"create"</c>.</item>
///   <item><b>Dotfile convention</b> — any node whose path has a segment starting with
///     <c>'_'</c> (<c>{user}/_Memex/ModelProvider</c>, <c>{p}/_Access/…</c>, <c>{p}/_Thread/ThreadComposer</c>)
///     is a hidden/system ("dotfile") node and is excluded from <c>"search"</c>, exactly the
///     way Unix hides dot-folders. The <c>'_'</c> prefix means <i>hidden</i> ONLY — it is
///     decoupled from satellite-table routing (only the registered suffixes in
///     <see cref="PartitionDefinition.TableMappings"/> route to satellite tables; a fresh
///     segment like <c>_Memex</c> stays in <c>mesh_nodes</c>).</item>
/// </list>
/// </summary>
public static class MeshNodeVisibility
{
    /// <summary>The search context — the one context the dotfile convention auto-hides from.</summary>
    public const string SearchContext = "search";

    /// <summary>
    /// True when <paramref name="path"/> has any segment starting with <c>'_'</c> — a
    /// hidden/system ("dotfile") path. Empty/null is not hidden.
    /// </summary>
    public static bool IsHiddenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == '/')
            {
                if (i > start && path[start] == '_') return true;
                start = i + 1;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="node"/> must be hidden from <paramref name="context"/> —
    /// either by its explicit <see cref="MeshNode.ExcludeFromContext"/> opt-out, or (for
    /// the <see cref="SearchContext"/>) because it lives on a hidden dotfile path.
    /// </summary>
    public static bool IsExcludedFromContext(this MeshNode node, string? context)
    {
        if (string.IsNullOrEmpty(context)) return false;
        if (node.ExcludeFromContext?.Contains(context) == true) return true;
        return context == SearchContext && IsHiddenPath(node.Path);
    }
}

