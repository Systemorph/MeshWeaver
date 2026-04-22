using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Content type for NodeType MeshNodes.
/// Properties like Name, Icon, Order, Namespace are on MeshNode itself.
/// This record holds only NodeType-specific configuration.
/// </summary>
public record NodeTypeDefinition
{
    /// <summary>
    /// Emoji character to use as icon. Takes precedence over MeshNode.Icon if set.
    /// Example: "📝", "📁", "🎯"
    /// </summary>
    public string? Emoji { get; init; }

    /// <summary>
    /// Description of this node type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Default values for initializing new instances of this type.
    /// Keys are property names, values are default values.
    /// </summary>
    public Dictionary<string, object>? DefaultValues { get; init; }

    /// <summary>
    /// Query string for getting "children" to display in the Details view.
    /// Uses IMeshService with the specified query pattern.
    /// Example: "nodeType:Type/Organization scope:descendants" finds all nodes
    /// of type "Type/Organization" anywhere in the hierarchy.
    /// If null, defaults to namespace-based children query (direct children only).
    /// </summary>
    public string? ChildrenQuery { get; init; }

    /// <summary>
    /// Lambda expression for configuring the message hub.
    /// Signature: Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;
    /// Example: "config => config.AddData(d => d.AddSource(s => s.WithType&lt;Person&gt;()))"
    /// Should call WithDefaultViews() to add standard views (Details, Edit, Thumbnail, etc).
    /// </summary>
    public string? HubConfiguration { get; init; }

    /// <summary>
    /// Lambda expression source code for hub configuration.
    /// Signature: Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;
    /// Example: "config => config.AddData(d => d.AddSource(...))"
    /// This is compiled at runtime and assigned to HubConfiguration.
    /// </summary>
    public string? Configuration { get; init; }

    /// <summary>
    /// List of NodeType paths this type depends on.
    /// Used for Monaco autocomplete to include types from dependencies.
    /// Example: ["type/Person", "type/Organization"]
    /// </summary>
    public List<string>? Dependencies { get; init; }

    /// <summary>
    /// Content collections to register for this node type.
    /// Each collection can be FileSystem, EmbeddedResource, or Hub-based.
    /// The collections are registered via extension methods in the generated hub configuration.
    /// </summary>
    public List<ContentCollectionConfig>? ContentCollections { get; init; }

    /// <summary>
    /// Whether to show children section in the Details view.
    /// Default: true (show children if ChildrenQuery is set or has children).
    /// </summary>
    public bool ShowChildrenInDetails { get; init; } = true;

    /// <summary>
    /// Maximum number of children to show in the Details view before "Show more" link.
    /// Default: 10.
    /// </summary>
    public int DetailsChildrenLimit { get; init; } = 10;

    /// <summary>
    /// Explicit list of NodeType paths that can be created from instances of this type.
    /// If null, computed automatically from hierarchy (child NodeTypes).
    /// Example: ["ACME/Project/Todo", "ACME/Project/Story"]
    /// </summary>
    public List<string>? CreatableTypes { get; init; }

    /// <summary>
    /// If true, includes global types (Markdown, NodeType) in creatable list.
    /// Default: true.
    /// </summary>
    public bool IncludeGlobalTypes { get; init; } = true;

    /// <summary>
    /// Maximum width for the page content area (e.g., "960px", "1200px", "100%").
    /// Applied as CSS max-width on the outer container.
    /// If null, defaults to "100%" (no constraint).
    /// </summary>
    public string? PageMaxWidth { get; init; }

    /// <summary>
    /// Default namespace where instances of this type should be created.
    /// Empty string means root (top-level). Null means no default.
    /// Pre-selects the namespace in the Create form but does not restrict choices.
    /// </summary>
    public string? DefaultNamespace { get; init; }

    /// <summary>
    /// Restricts which namespaces are available when creating instances of this type.
    /// Empty string means root (top-level). Null means no restriction (user chooses freely).
    /// When set, the Create form only allows selection from these namespaces.
    /// </summary>
    public List<string>? RestrictedToNamespaces { get; init; }

    /// <summary>
    /// Locations of the Code nodes to compile with this NodeType's
    /// <see cref="Configuration"/> lambda. Each entry is either:
    /// <list type="bullet">
    ///   <item>A mesh query — e.g. <c>"namespace:Source scope:subtree"</c>,
    ///     <c>"namespace:SocialMedia/Post/Source scope:subtree"</c>. A
    ///     <c>namespace:X</c> with a single segment (no <c>/</c>, like
    ///     <c>Source</c>) is automatically rebased onto the owning NodeType's
    ///     path. The macro <c>$self</c> can be used anywhere in the query and
    ///     expands to that path.</item>
    ///   <item>A single-node shorthand — <c>"@path/to/code"</c> or
    ///     <c>"@@path/to/code"</c>. Resolves to both an exact-path match and a
    ///     namespace-subtree match, so it works for either a leaf Code node or a
    ///     folder of them.</item>
    /// </list>
    /// Every resolved query is ANDed with <c>nodeType:Code</c>, so non-code
    /// children never leak in. Matches are de-duplicated across entries.
    /// </summary>
    /// <remarks>
    /// If null or empty, defaults to <c>["namespace:Source scope:subtree"]</c>
    /// — the conventional <c>Source/</c> sibling folder. Add more entries to pull
    /// in shared code, e.g.
    /// <c>["namespace:Source scope:subtree", "@SocialMedia/Post/Source/Platform"]</c>.
    /// (Note: the <c>@@path</c> form used inside a <em>code file's body</em> is a
    /// separate feature — inline include — handled during code-content resolution.)
    /// </remarks>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>
    /// Locations of the Code nodes classified as tests for this NodeType. Same
    /// query syntax and expansion rules as <see cref="Sources"/> — see
    /// <see cref="CodeQueryResolver"/>. Shown under "Tests" in the Configuration
    /// side menu alongside Sources, and compiled together so tests can reference
    /// the NodeType's production code.
    /// </summary>
    /// <remarks>
    /// If null or empty, defaults to <c>["namespace:Test scope:subtree"]</c>
    /// — the conventional <c>Test/</c> sibling folder. Mirrors <see cref="Sources"/>
    /// so a NodeType with a bespoke Sources list usually wants a bespoke Tests list too.
    /// </remarks>
    public IReadOnlyList<string>? Tests { get; init; }

    /// <summary>
    /// Current lifecycle state of the NodeType's compile. Written through by
    /// <c>NodeTypeService</c> on every transition (start / success / failure / invalidate),
    /// so anyone who can address / stream this MeshNode can observe the compile status
    /// directly — no polling, no auxiliary service call. Callers that want to wait for a
    /// settled state subscribe with <c>hub.GetRemoteStream(new MeshNodeReference(path))</c>
    /// and filter for <see cref="CompilationStatus.Ok"/> or <see cref="CompilationStatus.Error"/>.
    /// </summary>
    public CompilationStatus? CompilationStatus { get; init; }

    /// <summary>
    /// Formatted Roslyn diagnostics when <see cref="CompilationStatus"/> is
    /// <see cref="Mesh.Services.CompilationStatus.Error"/>; otherwise <c>null</c>.
    /// </summary>
    public string? CompilationError { get; init; }

    /// <summary>
    /// UTC timestamp when the currently-running compile started. Non-null only while
    /// <see cref="CompilationStatus"/> is <see cref="Mesh.Services.CompilationStatus.Compiling"/>.
    /// </summary>
    public DateTimeOffset? LastCompileStartedAt { get; init; }

    /// <summary>
    /// UTC timestamp of the last compile that completed successfully. Non-null only when
    /// <see cref="CompilationStatus"/> is <see cref="Mesh.Services.CompilationStatus.Ok"/>;
    /// cleared on invalidation so the state correctly reflects "never compiled since reset".
    /// </summary>
    public DateTimeOffset? LastCompileSucceededAt { get; init; }

    /// <summary>
    /// The NodeType <see cref="Mesh.MeshNode.Version"/> that produced the currently-cached
    /// assembly. Compared against the live <c>MeshNode.Version</c> on every read — if they
    /// differ, the cached assembly is stale and a fresh compile is required. This is the
    /// cache key into <see cref="Mesh.Services.IAssemblyStore"/>: one entry per historical
    /// version of the NodeType, not a single "latest" slot that can drift out of sync
    /// across replicas.
    /// </summary>
    public long? LastCompiledVersion { get; init; }
}
