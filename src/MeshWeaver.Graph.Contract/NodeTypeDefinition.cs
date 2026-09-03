using System.Collections.Immutable;
using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Content type for NodeType MeshNodes.
/// Properties like Name, Icon, Order, Namespace are on MeshNode itself.
/// This record holds only NodeType-specific configuration.
/// </summary>
public record NodeTypeDefinition
{
    /// <summary>
    /// Optional per-type override for the "+"/Create action. When set, the generic
    /// <c>CreateLayoutArea</c> invokes this INSTEAD of building
    /// the standard type/name/namespace form and renders whatever control the observable
    /// yields — e.g. a <see cref="RedirectControl"/> to a bespoke composer (Thread opens
    /// the new-chat composer), or a validation/error control that refuses the create. The
    /// arguments are the create <see cref="LayoutAreaHost"/> and the resolved target
    /// namespace; yield <c>null</c> to fall back to the default form.
    /// </summary>
    /// <remarks>
    /// <c>[JsonIgnore]</c>: a delegate can't round-trip as JSON, so this is honoured only
    /// for statically-registered NodeTypes (read in-process via <c>FindStaticNode</c>).
    /// Dynamically-compiled types fall through to the default form.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<LayoutAreaHost, string, IObservable<UiControl?>>? BuildCreate { get; init; }

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
    /// Optional authored markdown home page for a PARTITION-ROOT node whose content is this
    /// NodeTypeDefinition (a plugin-package Space root — e.g. UWDeepfield, whose root's content
    /// IS the partition-level compile config and therefore cannot be a <c>Space</c>).
    /// The Space Overview renders it exactly like <c>Space.Body</c> (recovered in
    /// <c>SpaceLayoutAreas.ResolveSpace</c>'s foreign-typed probe). Declared as a first-class
    /// member so typed round-trips and compile write-backs (<c>with</c>-expressions on this
    /// record) PRESERVE the authored page instead of silently dropping an unknown JSON member.
    /// </summary>
    public string? Body { get; init; }

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
    /// For a <b>built-in / static-linked</b> NodeType node — a NodeType-catalog partition root
    /// such as <c>@Harness</c> (<c>nodeType:NodeType</c>, id = the type name) — the name of the
    /// registered static C# NodeType whose <see cref="Mesh.MeshNode.HubConfiguration"/> this node
    /// links to. When set, enrichment resolves the node's hub configuration from the static
    /// registry by THIS name — NOT compiled from <see cref="Configuration"/>/<see cref="Sources"/>,
    /// and NOT via the node's own <see cref="Mesh.MeshNode.NodeType"/> (which is <c>"NodeType"</c> and
    /// would otherwise activate the NodeType editor). It is the persisted half of the
    /// NodeType-catalog dissociation: Postgres owns the single node at the bare partition path,
    /// while the in-memory static definition (registered definition-only — see
    /// <see cref="Mesh.MeshNode.IsDefinitionOnly"/>) still supplies the non-serialisable delegate.
    /// <c>null</c> for ordinary NodeTypes (framework built-ins served in-memory, or dynamic types
    /// compiled from <see cref="Configuration"/>/<see cref="Sources"/>).
    /// See <c>Doc/Architecture/NodeTypeCatalogs.md</c>.
    /// </summary>
    public string? StaticTypeName { get; init; }

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
    /// Explicit list of NodeType paths that can be created from instances of this type.
    /// If null, computed automatically from hierarchy (child NodeTypes).
    /// Example: ["ACME/Project/Todo", "ACME/Project/Story"]
    /// </summary>
    public List<string>? CreatableTypes { get; init; }

    /// <summary>
    /// If true, includes global types (Markdown, NodeType) in creatable list.
    /// Default: true.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.Never"/> is
    /// required: the property initializer defaults to <c>true</c>, so an
    /// explicit <c>false</c> equals <c>default(bool)</c> and the hub's global
    /// <c>WhenWritingDefault</c> policy would omit it — the value then
    /// round-trips back to <c>true</c> via the initializer, silently
    /// re-enabling global types on a type that opted out.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
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
    /// Where instances of this type LIVE, as mesh query strings whose <c>namespace:</c>/<c>path:</c>
    /// leg names the partitions — <c>namespace:Admin/Menu</c>, <c>namespace:A|B|C</c>,
    /// <c>path:Ops/Mail</c> (#3039; Plugins#1127). Authored on the type and installed with the
    /// package that owns it, so ownership stays decentral. The storage layer reads it through
    /// <see cref="INodeTypeInstanceLocations"/> and INTERSECTS the declared partitions with the ones
    /// an unanchored <c>nodeType:X</c> query would have UNION-ed, so a declaration can only ever
    /// drop branches whose schema cannot hold an instance.
    ///
    /// <para><b>Null or empty means undeclared: the query fans out over everything, as today.</b>
    /// The contract is fail-open — an unparseable entry, a wildcarded first segment, or a type the
    /// planner has never heard of all answer "cannot narrow". An OVER-stated declaration (naming a
    /// partition with no instances) costs one zero-row branch and changes no answer; an UNDER-stated
    /// one <b>silently loses rows</b> — nothing errors, nothing logs — so declare every location
    /// instances can be written to, or declare nothing.</para>
    ///
    /// <para>🚨 <b>Refused for the permission fold's own types</b> — <c>Role</c>,
    /// <c>GroupMembership</c>, <c>AccessAssignment</c>, <c>PartitionAccessPolicy</c> and every
    /// type-declared gate (<c>Mesh.Security.NeverNarrowedNodeTypes</c>): in that fold a short read is
    /// a vanished grant (#2011) or a deny that fails OPEN. <c>InstanceLocationDeclarationValidator</c>
    /// refuses such a declaration at the write boundary and the static fold refuses it at startup,
    /// naming the reason; the planner refuses it again at query time. See
    /// <c>Doc/Architecture/UnanchoredSecurityReads</c>.</para>
    /// </summary>
    public IReadOnlyList<string>? InstanceLocations { get; init; }

    /// <summary>
    /// When <c>true</c>, this NodeType's CURRENT baked assembly joins the kernel's
    /// cell-scripting surface (issue #1649): every kernel session resolves it per session
    /// through the compilation cache — metadata reference for compile-time visibility plus a
    /// session-scoped runtime bind into its collectible load context — so <c>--render</c> /
    /// executable cells can call the type's <c>Source/</c> API by bare name. Authored in the
    /// pack's <c>index.json</c> as <c>cellSurface: true</c>; explicit opt-in keeps the script
    /// surface a declaration instead of a load-order accident.
    ///
    /// <para>🚨 Single-home rule, enforced at compile time: a cell-surface NodeType's
    /// <c>Source/</c> must not be <c>shared=</c>-consumed by any other NodeType. <c>shared=</c>
    /// recompiles the source INTO each consumer's assembly, so a consumer would put the same
    /// public types into a second assembly — and with both in a session's reference set every
    /// bare-name cell call is ambiguous (<c>CS0433</c>). The compile of such a consumer fails
    /// with a message naming this type (see <c>MeshNodeCompilationService</c>).</para>
    /// </summary>
    public bool CellSurface { get; init; }

    /// <summary>
    /// When <c>true</c>, a top-level instance of this type OWNS its own partition — a
    /// dedicated backing store (a Postgres schema). The partition is provisioned, and the
    /// creator made its Admin, when the top-level instance is created (the NodeType is
    /// loaded from the <c>CreateNodeRequest</c> and consulted; no registry). This is the
    /// ONLY trigger for schema creation — the storage layer never conjures a schema for an
    /// arbitrary path segment, and a write whose partition isn't provisioned is refused.
    /// Set on <c>User</c> and <c>Space</c>. See <c>Doc/Architecture/PartitionStorageRouting.md</c>.
    /// </summary>
    public bool OwnsPartition { get; init; }

    /// <summary>
    /// The table instances of this type persist to within their owning partition's schema —
    /// e.g. <c>"user_activities"</c>, <c>"threads"</c>, <c>"access"</c>, <c>"annotations"</c>,
    /// <c>"code"</c>. Null/empty → the partition's primary <c>mesh_nodes</c> table. This is
    /// the declarative, single-sourced replacement for the central
    /// <c>PartitionDefinition.StandardTableMappings</c> / <c>NodeTypeToSuffix</c> dictionaries
    /// and the <c>_Thread</c>/<c>_Access</c>/… path-suffix matching: a node's table comes from
    /// its NodeType config, not the shape of its path.
    /// </summary>
    public string? StorageTable { get; init; }

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
    /// <para>
    /// An entry may carry an optional <c>name=</c> prefix, e.g.
    /// <c>"shared=@SocialMedia/Post/Source/Platform"</c> — the GUI's source tree
    /// groups the resolved files under that name. Unnamed entries fall into the
    /// default <c>src</c> group. The name is display-only; the compiler strips it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// If null or empty, defaults to <c>["namespace:Source scope:subtree"]</c>
    /// — the conventional <c>Source/</c> sibling folder, shown as group <c>src</c>.
    /// Add more entries to pull in shared code, e.g.
    /// <c>["namespace:Source scope:subtree", "shared=@SocialMedia/Post/Source/Platform"]</c>.
    /// (Note: the <c>@@path</c> form used inside a <em>code file's body</em> is a
    /// separate feature — inline include — handled during code-content resolution.)
    /// </remarks>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>
    /// Locations of the Code nodes classified as tests for this NodeType. Same
    /// query syntax, <c>name=</c> grouping, and expansion rules as
    /// <see cref="Sources"/> — see <c>CodeQueryResolver</c>. Shown under
    /// "Tests" in the NodeType side menu alongside Sources, and compiled together
    /// so tests can reference the NodeType's production code.
    /// </summary>
    /// <remarks>
    /// If null or empty, defaults to <c>["namespace:Test scope:subtree"]</c>
    /// — the conventional <c>Test/</c> sibling folder, shown as group <c>test</c>.
    /// Mirrors <see cref="Sources"/> so a NodeType with a bespoke Sources list
    /// usually wants a bespoke Tests list too.
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
    /// Human-readable summary — see <see cref="CompilationDiagnostics"/> for the
    /// structured, per-source-file form that drives the Monaco error overlay.
    /// </summary>
    public string? CompilationError { get; init; }

    /// <summary>
    /// Structured per-source-file Roslyn diagnostics from the last FAILED compile —
    /// kept in their native <see cref="DiagnosticInfo"/> form (id, severity, message,
    /// and a per-file <see cref="SourceLocation"/> line/column range) rather than
    /// flattened to a string, so the Settings → Progress error page can render each
    /// affected source in a Monaco editor with the errors MARKED at their exact
    /// position (the IDE-style overlay) and link straight to the Code node. Populated
    /// when <see cref="CompilationStatus"/> is <see cref="Mesh.Services.CompilationStatus.Error"/>;
    /// <c>null</c>/empty otherwise. Produced by the same per-file-tree compilation the
    /// LSP uses (<c>SpeculativeCompilation</c> / <c>CompilationInputs</c>), so a
    /// diagnostic's <see cref="SourceLocation.SourcePath"/> is the Code MeshNode path.
    /// </summary>
    public ImmutableList<DiagnosticInfo>? CompilationDiagnostics { get; init; }

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

    /// <summary>
    /// Path of the most recent compilation <see cref="MeshWeaver.Data.ActivityLog"/> persisted under
    /// <c>{nodeTypePath}/_activity/{logId}</c>. Set by the compile watcher every time a
    /// compile completes (success or failure) so the layout area can render a clickable
    /// "Last compilation" link, and so anyone observing the NodeType remote stream can
    /// jump straight to the executed-source-queries / matched-Code-paths / Roslyn-output
    /// trace without re-running the pipeline. <c>null</c> until the first compile finishes.
    /// </summary>
    public string? LastCompilationActivityPath { get; init; }

    /// <summary>
    /// Path of the latest <c>Release</c> MeshNode at <c>{nodeTypePath}/Release/{version}</c>
    /// — the active compiled artefact for this NodeType. Set by the compile watcher
    /// after a successful compile + Release node creation; preserves the previous value
    /// across failed compiles so consumers (NodeTypeService, per-node hub activation,
    /// the layout area) keep loading the last-known-good release until a fresh one ships.
    ///
    /// <para>Read this field instead of resolving the active release through a query —
    /// the value is on the NodeType MeshNode itself, no <c>Query</c> round-trip
    /// required. See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
    /// </summary>
    public string? LatestReleasePath { get; init; }

    /// <summary>
    /// Optional pin to a specific historical <c>Release</c> MeshNode at
    /// <c>{nodeTypePath}/Release/{version}</c>. When set, every per-instance hub of
    /// this NodeType activates against that release's <c>AssemblyPath</c> instead of
    /// whichever assembly the latest compile produced. When <c>null</c> (the default),
    /// activations resolve to the most recent compile (<see cref="LatestReleasePath"/>)
    /// — i.e. "always serve latest".
    ///
    /// <para>Use this for production pinning, A/B rollout, or to roll back to a
    /// previous release without retracting the more-recent one. Authoring a fresh
    /// release with <c>CreateReleaseRequest</c> updates <see cref="LatestReleasePath"/>
    /// but does <em>not</em> change <see cref="RequestedReleasePath"/> — instances
    /// stay on the pinned release until this field is cleared or repointed.</para>
    /// </summary>
    public string? RequestedReleasePath { get; init; }

    /// <summary>
    /// Stream-update trigger for "create a new release now" (the per-NodeType
    /// hub's watcher observes this field, runs <c>DispatchPendingFlip</c> when
    /// it moves past <see cref="LastReleaseRequestHandledAt"/>, and the
    /// auto-watcher kicks Roslyn). Set via
    /// <c>workspace.GetMeshNodeStream(nodeTypePath).Update(...)</c> — never
    /// post a <c>CreateReleaseRequest</c> from new code. See
    /// <c>RequestViaStreamUpdate.md</c>.
    ///
    /// <para>Carries the trigger timestamp so multiple requests are distinct
    /// (idempotent CompareAndSwap on the watcher side via the last-handled
    /// stamp).</para>
    /// </summary>
    public DateTimeOffset? RequestedReleaseAt { get; init; }

    /// <summary>
    /// Whether the corresponding <see cref="RequestedReleaseAt"/> trigger
    /// should bypass the "sources match the last compile" short-circuit and
    /// always dispatch a fresh compile. Mirrors the legacy
    /// <c>CreateReleaseRequest.Force</c> flag.
    /// </summary>
    public bool RequestedReleaseForce { get; init; }

    /// <summary>
    /// The user id that requested the current release (the caller of
    /// <c>hub.RequestNodeTypeRelease(...)</c>, who passed the <see cref="Mesh.Security.Permission.Compile"/>
    /// gate at the entry point). Carried on the NodeType node so the credential split holds
    /// across the watcher → compile → release-node-create chain: the "pure" compilation that
    /// fills the assembly cache runs as <b>System</b> (it must succeed on read-only partitions),
    /// but the resulting <c>Release</c> MeshNode is stamped to THIS user (owner = caller) so the
    /// release is attributable to the person who authored it. <c>null</c> when no user-initiated
    /// release is pending (e.g. the System-driven Doc-release seed, or the first-build kickoff),
    /// in which case the release node is created under System.
    /// </summary>
    public string? RequestedReleaseBy { get; init; }

    /// <summary>
    /// Set by the per-NodeType release watcher after it has reacted to a
    /// <see cref="RequestedReleaseAt"/> flip. The watcher only dispatches when
    /// <c>RequestedReleaseAt &gt; LastReleaseRequestHandledAt</c>, preventing
    /// re-fire on every subsequent stream emission that still carries the same
    /// trigger timestamp.
    /// </summary>
    public DateTimeOffset? LastReleaseRequestHandledAt { get; init; }

    /// <summary>
    /// Content-collection name where the latest compiled assembly for this NodeType
    /// lives (e.g. <c>"nodetype-cache"</c>). Pair with <see cref="LatestAssemblyPath"/>
    /// to fetch the bytes via <c>IContentCollection</c>. Set by the compile watcher
    /// after a successful Roslyn compile uploads the assembly to the blob container;
    /// the same pair is denormalised onto the produced <see cref="NodeTypeRelease"/>
    /// so pinned-release activations can read it without crossing back to the
    /// NodeType MeshNode.
    /// <para>
    /// 🚨 This pair, not <c>MeshNode.AssemblyLocation</c>, is the authoritative
    /// "where do I load the assembly from" hint for every silo. <c>AssemblyLocation</c>
    /// is <c>[JsonIgnore]</c> and only valid in the process that ran the compile —
    /// cross-silo activation MUST resolve through these fields.
    /// </para>
    /// </summary>
    public string? LatestAssemblyCollection { get; init; }

    /// <summary>
    /// Path inside <see cref="LatestAssemblyCollection"/> where the latest compiled
    /// assembly's bytes live (e.g. <c>"TestData/PinType/v2-abc123.dll"</c>). Together
    /// with <see cref="LatestAssemblyCollection"/> forms the cross-silo durable
    /// reference to the latest compile output.
    /// </summary>
    public string? LatestAssemblyPath { get; init; }

    /// <summary>
    /// 🚨 <b>The MVID of the bytes the last successful build PRODUCED — the identity a served
    /// assembly can be checked against.</b> Lower-case hex, no separators; null on a node stamped
    /// before this field existed, or by a producer that had no bytes to read.
    ///
    /// <para><see cref="LatestAssemblyPath"/> is an ADDRESS, not an identity. The store key is
    /// <c>(nodeTypePath, <see cref="LastCompiledVersion"/>)</c>, a recompile of an
    /// already-<c>Ok</c> type does not rewrite this node, and each pod resolves those bytes through
    /// its own local cache — so the path can match perfectly while the bytes behind it differ per
    /// replica. Every staleness check that compared paths was therefore structurally unable to see
    /// the state in Systemorph/MeshWeaver#2471: a portal serving stale compiled code while
    /// reporting <c>Ok</c>, surviving two NodeType recycles, four instance recycles and a forced
    /// compile, with the <c>$Banner</c> stale-build adornment empty throughout.</para>
    ///
    /// <para>An MVID is minted per emitted assembly, so it answers the question the path cannot:
    /// <i>are these the bytes this node is talking about?</i> Written by every success stamp
    /// (<c>NodeTypeCompilationHelpers.ApplyCompileSuccess</c>, the adoption path in
    /// <c>PrebuiltAssemblySeeder</c>, and <c>NodeTypeContractHandler</c>'s write-back) and read at
    /// bind time by <c>NodeTypeEnrichmentHelpers</c>; see <see cref="ServedBuildIdentity"/>.</para>
    ///
    /// <para>🚨 Null is "I do not know", never "mismatch" — see
    /// <see cref="ServedBuildIdentity.Mismatch"/>. A detector that fired on every legacy node's
    /// first boot would be turned off before it ever caught anything.</para>
    /// </summary>
    public string? LatestAssemblyMvid { get; init; }

    /// <summary>
    /// Free-form release notes captured next to the "Create Release" button on
    /// the Configuration view. Auto-saved through the same form-debounce path
    /// every other editable field uses (no manual read-on-click). Surfaced on
    /// the Releases pane alongside each historical compile activity so the
    /// user sees what changed in each release without opening the activity log.
    /// </summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Snapshot of <c>{sourceNodePath → MeshNode.Version}</c> for every Code/Test
    /// node that participated in the most recent successful compile. Written by the
    /// compile watcher when <see cref="CompilationStatus"/> settles to
    /// <see cref="Mesh.Services.CompilationStatus.Ok"/>.
    ///
    /// <para>The snapshot is the persistent, cross-restart, cross-silo answer to
    /// "is the cached assembly still valid?": comparing the live versions of the
    /// source nodes against this dictionary catches the three change shapes the
    /// LastModified-only check misses:
    /// <list type="bullet">
    ///   <item><b>Source added</b> — a new path appears in the current set that
    ///     was absent from the snapshot.</item>
    ///   <item><b>Source removed</b> — a path is present in the snapshot but
    ///     missing from the current set; the cached DLL still embeds the deleted
    ///     code and must be rebuilt.</item>
    ///   <item><b>Source modified</b> — same path exists in both, but its version
    ///     bumped.</item>
    /// </list>
    /// </para>
    ///
    /// <para><c>null</c> until the first successful compile completes; cleared when the
    /// NodeType moves to <see cref="Mesh.Services.CompilationStatus.Error"/> so an
    /// error-state NodeType always re-runs source discovery on the next compile.</para>
    /// </summary>
    public IReadOnlyDictionary<string, long>? CompiledSources { get; init; }

    /// <summary>
    /// Live snapshot of <c>{sourceNodePath → MeshNode.LastModified.UtcTicks}</c> for
    /// every Code/Test node that currently feeds this NodeType. Maintained by the
    /// per-NodeType hub's sources watcher (<c>NodeTypeCompilationHelpers.InstallSourcesWatcher</c>)
    /// — every emission of the synced query over <see cref="Sources"/> + <see cref="Tests"/>
    /// recomputes this dictionary against the live nodes and writes back on change.
    ///
    /// <para>Together with <see cref="CompiledSources"/> drives <see cref="IsDirty"/>:
    /// they differ exactly when an edit/add/remove has landed on a dependent source
    /// since the last successful compile.</para>
    ///
    /// <para>The compile reads sources by paths from this snapshot — each path
    /// re-fetched via <c>workspace.GetMeshNodeStream(path).Take(1)</c> — so Roslyn
    /// always sees authoritative content, not the index-lagged query result.</para>
    /// </summary>
    public IReadOnlyDictionary<string, long>? CurrentSourceVersions { get; init; }

    /// <summary>
    /// A REQUEST to the owning per-NodeType hub: "stamp <see cref="CompiledSources"/> from your own
    /// <see cref="CurrentSourceVersions"/>". Written by
    /// <c>PrebuiltAssemblySeeder.Seed</c> when it adopts a prebuilt assembly; consumed —
    /// exactly once — on the owner, which clears it in the same write that applies the stamp.
    ///
    /// <para>🚨 <b>Why the value cannot be written by the adopter</b> (#1834). A bundle's own
    /// source-version ticks are meaningless on the consumer (the producer records zeros; the mesh
    /// keys on ITS nodes' modification times), so adoption asserts "these bytes correspond to the
    /// live source set". Only the owner knows that set: the seeder writes CROSS-HUB, so its lambda
    /// diffs against the MIRROR's snapshot, and the mirror predates the first-activation write of
    /// <c>CurrentSourceVersions</c> that the seeder's own subscribe TRIGGERS
    /// (<c>NodeTypeCompilationHelpers.InstallSourcesWatcher</c>). Reading the field there stamped
    /// <c>CompiledSources = null</c> under a non-empty <c>CurrentSourceVersions</c> — i.e.
    /// <see cref="IsDirty"/> — so the release request that follows an install recompiled the type
    /// that had just been adopted. A request the owner fulfils has no such race: the owner's copy
    /// of both fields is authoritative by construction.</para>
    ///
    /// <para><b>What it asserts.</b> "The bytes correspond to the source set that is live when the
    /// owner fulfils this" — the same assertion the adopter used to make, now made where it is
    /// checkable. A source edit landing inside that (sub-second, install-time) window is therefore
    /// folded into the adopted build rather than recompiled, exactly as before; an explicit Compile
    /// remains the escape hatch.</para>
    ///
    /// <para><b>One-shot.</b> Every writer that fulfils it clears it in the SAME write
    /// (<c>InstallAdoptedSourceStampWatcher</c>, the release-request watcher's dispatch, and both
    /// terminal compile stamps), so it can never re-fire — and in particular can never re-stamp
    /// <c>CompiledSources</c> over a later compile's own snapshot, which would suppress a needed
    /// rebuild. Operational, never authored: stripped on export, preserved from the live node on
    /// import (<see cref="Mesh.NodeTypeOperationalContent"/>).</para>
    /// </summary>
    public DateTimeOffset? RequestedSourceStampAt { get; init; }

    /// <summary>
    /// The source fingerprint the PRODUCER recorded for the adopted bytes — a content hash over
    /// the Code/Test nodes the bundle was compiled from
    /// (<see cref="Mesh.PartitionSourceFingerprint"/>). Written by
    /// <c>PrebuiltAssemblySeeder.Seed</c> beside <see cref="RequestedSourceStampAt"/>;
    /// <c>null</c> for a locally-compiled build and for a LEGACY bundle published before producers
    /// recorded one.
    ///
    /// <para>🚨 <b>It must be a CONTENT hash, never a version one.</b>
    /// <see cref="CurrentSourceVersions"/> is <c>{path → LastModified.UtcTicks}</c> — mesh-LOCAL
    /// modification times, which the producer cannot know and does not have (the bake writes
    /// zeros). A fingerprint over ticks would therefore never match and every adoption would be
    /// refused. Content is the only thing both sides can compute the same way.</para>
    ///
    /// <para>Compared against <see cref="CurrentSourceFingerprint"/> by
    /// <c>NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp</c>, on the OWNER — the only party
    /// that holds both authoritatively (#1834).</para>
    /// </summary>
    public string? AdoptedSourceFingerprint { get; init; }

    /// <summary>
    /// Content fingerprint of the LIVE source set — the same
    /// <see cref="Mesh.PartitionSourceFingerprint"/> shape as
    /// <see cref="AdoptedSourceFingerprint"/>, computed over this NodeType's own Code/Test nodes
    /// by the sources watcher and written in the SAME update as
    /// <see cref="CurrentSourceVersions"/>.
    ///
    /// <para>🚨 <b>Computed only when it can be USED</b> — when the node carries an
    /// <see cref="AdoptedSourceFingerprint"/> or a pending
    /// <see cref="RequestedSourceStampAt"/>. Hashing every source node's serialised content on
    /// every source emission, for every NodeType, would put a SHA-256 over the whole source set on
    /// a path that today only reads timestamps, and a locally-compiled type has nothing to compare
    /// it against. So a type that never adopts pays nothing, and the field stays <c>null</c> —
    /// which is deliberately DISTINCT from "computed and empty".</para>
    /// </summary>
    public string? CurrentSourceFingerprint { get; init; }

    /// <summary>
    /// Where the current build came from, and whether an adopted one was ever checked against the
    /// source this node holds. See <see cref="Mesh.Services.BuildProvenance"/> for why this has to
    /// be READABLE ON THE RECORD rather than inferable — every other signal
    /// (<see cref="CompilationStatus"/>, <see cref="IsDirty"/>) reads clean for an adopted build,
    /// because the adoption writes the second one itself (#2813).
    ///
    /// <para>Operational, never authored: stripped on export, preserved from the live node on
    /// import. An authored value would forge a provenance claim — the same class of unearned claim
    /// this field exists to expose.</para>
    /// </summary>
    public Mesh.Services.BuildProvenance BuildProvenance { get; init; }

    /// <summary>
    /// <see cref="DateTime"/> ticks for <c>1601-01-01</c> — the FILETIME epoch, and the value
    /// .NET returns from <c>FileInfo.LastWriteTimeUtc</c> for a file that DOES NOT EXIST
    /// (it does not throw). A node stamped with it has no real modification time.
    ///
    /// <para>🚨 This is not a curiosity: a source stamped 1601 records the SAME version before
    /// and after an edit, so <see cref="IsDirty"/> compares equal, no recompile is ever
    /// scheduled, and the type serves its previous assembly forever while every status field
    /// says <c>Ok</c>. Measured on memex 2026-08-18 (Systemorph/MeshWeaver#1836): an
    /// <c>Edu/Module</c> change imported, logged "Recompiling", minted a fresh release — and
    /// ran the old code, because six of its fourteen sources carried this value on BOTH sides
    /// of the comparison.</para>
    /// </summary>
    public const long UnknownSourceVersionTicks = 504911232000000000L;

    /// <summary>
    /// The per-source version key for the <see cref="CompiledSources"/> /
    /// <see cref="CurrentSourceVersions"/> snapshots: the node's modification time when it has
    /// a real one, else its <see cref="MeshNode.Version"/>.
    ///
    /// <para>The fallback is the point. <c>Version</c> is the owning hub's monotonic
    /// persistence counter — bumped by every write — so it CHANGES when the source changes,
    /// which is the only property the staleness comparison actually needs. Falling back to it
    /// turns an un-timestamped source from permanently-invisible into ordinarily comparable.</para>
    ///
    /// <para>Both snapshots MUST fold through this one function — the compiler's
    /// (<c>MeshNodeCompilationService.DiscoverSourceVersionSnapshot</c>) and the watcher's
    /// (<c>NodeTypeCompilationHelpers</c>) — or the two sides key differently and every type
    /// reads as permanently dirty, which is the same outage with the opposite sign.</para>
    ///
    /// <para>Nodes carrying a real timestamp are unaffected. A node stored with the 1601 stamp
    /// re-keys to its Version, so it differs from the recorded snapshot exactly ONCE,
    /// recompiles, and both sides then agree — a single self-healing compile per affected
    /// type, not a recompile storm.</para>
    /// </summary>
    /// <param name="node">A source (Code) node of the NodeType.</param>
    public static long SourceVersionOf(MeshNode node) =>
        node.LastModified.UtcTicks > UnknownSourceVersionTicks
            ? node.LastModified.UtcTicks
            : node.Version;

    /// <summary>
    /// 🚨 Does this NodeType TAKE PART in the compile lifecycle at all? — issue #3006.
    ///
    /// <para><c>true</c> when the definition has source to build (<see cref="Configuration"/>,
    /// <see cref="HubConfiguration"/> or <see cref="Sources"/>) or already carries a recorded
    /// compile state (<see cref="CompilationStatus"/>). <c>false</c> only for a pure MARKER type —
    /// a definition that names a shape and ships no code, so no compile is ever coming for it.</para>
    ///
    /// <para><b>Why it is a method and not an ad-hoc condition.</b> An absent
    /// <see cref="CompilationStatus"/> means two completely different things, and telling them
    /// apart is the whole of #3006: <i>"no compile will ever start"</i> (a marker / test-seeded
    /// type) and <i>"the first-build kickoff has not stamped <c>Pending</c> YET"</i> (a repo- or
    /// JSON-loaded type carrying a <c>Configuration</c> string, in the window before
    /// <c>InstallCompileWatcher</c>'s kickoff runs). Reading only the status conflates them, and
    /// an instance hub that activates in that window binds the mesh DEFAULT configuration —
    /// permanently, because enrichment binds once and the rebind watcher only fires on a change
    /// of the INSTANCE's own <c>NodeType</c>, never on a compile transition. Its areas never
    /// appear and every deep link to one answers the TERMINAL <c>area-not-found</c>.</para>
    ///
    /// <para>The rule already existed inline in <c>NodeTypeLayoutAreas.AppendSweepSummary</c>
    /// ("Only types that participate in compilation"); it lives here now so the sweep summary and
    /// the enrichment decision cannot drift apart — a NodeType the sweep counts as compiling and
    /// enrichment treats as inert is precisely the disagreement that produced the bug.</para>
    /// </summary>
    /// <param name="def">The definition to classify; <c>null</c> is not a participant.</param>
    public static bool ParticipatesInCompilation(NodeTypeDefinition? def) =>
        def is not null
        && (def.CompilationStatus is not null
            || !string.IsNullOrWhiteSpace(def.Configuration)
            || !string.IsNullOrWhiteSpace(def.HubConfiguration)
            || def.Sources is { Count: > 0 });

    /// <summary>
    /// <c>true</c> iff <see cref="CurrentSourceVersions"/> differs from
    /// <see cref="CompiledSources"/> — i.e. an edit / add / remove has landed on a
    /// dependent source since the last successful compile, so the cached assembly
    /// no longer matches the source set. <b>Computed</b> from the two snapshots —
    /// not a persisted field — so the value can never drift out of sync with the
    /// fields it derives from across a partial-update / patch / replay cycle.
    /// JSON-ignored: cross-silo propagation only ships the two dictionaries, and
    /// each subscriber recomputes <c>IsDirty</c> locally.
    ///
    /// <para>UI binds the Compile button's enabled state to this. Tests observe
    /// the transition <c>edit source → IsDirty=true → recompile → IsDirty=false</c>
    /// — by observing <see cref="CurrentSourceVersions"/> equal to
    /// <see cref="CompiledSources"/> (i.e. <c>!IsDirty</c>).</para>
    ///
    /// <para>When both dictionaries are <c>null</c> (e.g. a NodeType that hasn't
    /// been compiled yet AND has no source children) the comparison treats them
    /// as both empty — <c>IsDirty=false</c>. The compile flow seeds
    /// <c>CompiledSources</c> to <c>ImmutableDictionary.Empty</c> on a sourceless
    /// success too, so the asymmetric-null states only persist for the brief
    /// window before the sources watcher publishes its first
    /// <c>CurrentSourceVersions</c>.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDirty
    {
        get
        {
            var current = CurrentSourceVersions;
            var compiled = CompiledSources;
            // Both null/empty → not dirty (nothing to compile, nothing changed).
            // One null + the other non-empty → dirty (added or removed sources).
            var currentEmpty = current is null || current.Count == 0;
            var compiledEmpty = compiled is null || compiled.Count == 0;
            if (currentEmpty && compiledEmpty) return false;
            if (currentEmpty != compiledEmpty) return true;
            if (current!.Count != compiled!.Count) return true;
            foreach (var kvp in current)
            {
                if (!compiled.TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The MeshWeaver framework version the most recent successful compile ran
    /// against — the semver of the <c>MeshWeaver.Graph</c> assembly
    /// (<c>AssemblyInformationalVersion</c> minus the <c>+gitSha</c> build
    /// suffix, e.g. <c>"3.0.0-preview2"</c>). See
    /// <c>NodeTypeCompilationHelpers.FrameworkVersion</c>.
    ///
    /// <para>A compiled NodeType assembly binds against the framework assemblies
    /// present at compile time. When MeshWeaver is redeployed at a new version
    /// those assemblies change and the cached DLL may be ABI-incompatible — so a
    /// release is only "usable" if it was compiled against the <em>current</em>
    /// framework version. The compile kickoff (<c>HasUsableBuild</c>) compares
    /// this against the live framework version and forces a recompile on
    /// mismatch; the recompile mints a NEW release, leaving the old one as
    /// history for instances still bound to it.</para>
    ///
    /// <para>Version (not a file timestamp): the same release deployed to many
    /// servers must compare equal everywhere — file write-times differ per
    /// machine, the assembly version does not.</para>
    ///
    /// <para><c>null</c> until the first successful compile completes.</para>
    /// </summary>
    public string? CompiledFrameworkVersion { get; init; }

    /// <summary>
    /// The deployment's installed-MODULE fingerprint the assembly was compiled under —
    /// <c>InstalledModulesFingerprint.Hash</c> (sorted module MVIDs; empty string = no
    /// modules; null = stamped before the feature, or by a mesh without the fingerprint
    /// registered). Recorded by every successful compile (#1644 step 1) and DECISIVE since
    /// #1664 Slice A: <c>NodeTypeCompilationHelpers.HasUsableBuild</c> invalidates a build
    /// stamped with a different non-null hash than the live set, and
    /// <c>HasStaleFrameworkBuild</c> re-drives the compile for it — a module-only update
    /// (store-installed modules land in <c>modules/</c> without changing the framework MVID)
    /// must invalidate baked builds that could reference it, which the framework rule cannot
    /// see. Null compares as MATCH: a null-hash build predates modules in the compile surface
    /// and is governed by the framework rule alone.
    /// </summary>
    public string? CompiledModulesHash { get; init; }

    /// <summary>
    /// The per-type DEPENDENCY RECORD the assembly was compiled with (#1707 slice 2): sorted
    /// <c>(referenced assembly name → surface-id)</c> pairs read from the EMITTED assembly's
    /// AssemblyRef table — the pruned, true set of what these bytes actually bind — plus the
    /// reserved <c>!toolchain</c> entry (see <c>MeshWeaver.Compiler.CompiledDependencies</c>).
    /// Platform entries carry the reference-assembly hash (<c>ref:</c> — moves only on a breaking
    /// surface change); module entries carry the exact build MVID (<c>mvid:</c>).
    ///
    /// <para>DECISIVE when present: <c>HasUsableBuild</c> / <c>HasStaleFrameworkBuild</c>, the
    /// bake probe's <c>Classify</c>, and the prebuilt seeder validate every entry against the
    /// live environment — so a module update invalidates ONLY its dependents, and a type that
    /// references no module is valid on ANY deployment regardless of composition (the
    /// instance-wide <see cref="CompiledModulesHash"/> stops keying record-stamped builds and
    /// remains only as the legacy rule for null-record stamps). Null = stamped before this
    /// feature; the legacy modules-hash rule governs.</para>
    /// </summary>
    public System.Collections.Immutable.ImmutableSortedDictionary<string, string>? CompiledDependencies { get; init; }

    /// <summary>
    /// 🚨 The COMPILE INPUTS the STANDING FAILURE VERDICT was formed from — the one thing a failed
    /// compile can honestly record, and the field that makes a failure RECOVERABLE (issue #1793).
    ///
    /// <para><b>The hole it closes.</b> A failed compile writes no assembly coordinates at all:
    /// <c>NodeTypeCompilationHelpers.ApplyCompileFailure</c> stamps neither
    /// <see cref="LatestAssemblyCollection"/> / <see cref="LatestAssemblyPath"/> nor
    /// <see cref="CompiledFrameworkVersion"/>. For a NodeType that never compiled successfully on
    /// this deployment those are therefore null forever — and EVERY automatic re-drive keys off
    /// something that only exists after a first success: the first-build kickoff needs
    /// <c>CompilationStatus is null</c>, the recovery kickoff needs <c>Compiling</c>, the
    /// framework-stale kickoff needs the assembly coordinates
    /// (<c>NodeTypeCompilationHelpers.HasStaleFrameworkBuild</c>), and the release watcher needs a
    /// human to move <see cref="RequestedReleaseAt"/>. So a redeploy, a framework bump, a module
    /// update or a fix to the failing code reached none of them — which is why the fix written FOR
    /// the fifteen types parked on memex-cloud could not reach the nodes it was written for.</para>
    ///
    /// <para><b>The shape.</b> An opaque, comparable token over the three inputs a verdict depends
    /// on — the framework identity, the installed-module fingerprint, and the source snapshot the
    /// compile consumed (<c>NodeTypeCompilationHelpers.BuildInputsToken</c>). The re-drive kickoff
    /// fires exactly when the LIVE inputs differ from this stamp, which gives ONE automatic retry
    /// per distinct set of inputs: a deployed framework, a module update, or an edited source each
    /// earn a fresh attempt, and a type that is simply broken is retried once and then left alone
    /// (loudly — see the give-up log in <c>InstallCompileWatcher</c>) instead of storming.</para>
    ///
    /// <para><b>Self-limiting by construction.</b> The kickoff stamps this field in the SAME write
    /// that flips <see cref="CompilationStatus"/> to <see cref="Mesh.Services.CompilationStatus.Pending"/>,
    /// so the re-drive's own bookkeeping makes its trigger false — a reconcile that fed itself is
    /// the 257,000-version write-storm shape, and this is what forecloses it.</para>
    ///
    /// <para><c>null</c> = no failure verdict has been recorded here (a never-attempted type, a
    /// successful one — <c>ApplyCompileSuccess</c> CLEARS it — or a node whose Error was baked into
    /// a file by an export, which is precisely the population that must get its one retry).</para>
    ///
    /// <para>🚨 Runtime state: never author it into a node file. <c>ShippedNodeTypeStateTest</c>
    /// bans it, because an authored token that happens to match the live inputs would suppress the
    /// very retry this field exists to enable.</para>
    /// </summary>
    public string? FailedBuildInputs { get; init; }

    /// <summary>
    /// The build-inputs token the in-flight compile was dispatched for, stamped by the RELEASE
    /// WATCHER on the commit where it flips <see cref="CompilationStatus"/> to Pending (#2544).
    ///
    /// <para>🚨 <b>Null means "no watcher dispatch vouches for the compile in flight".</b> Several
    /// kickoff paths — first build, recovery, framework-stale, the failed-verdict re-drive — flip
    /// to Pending WITHOUT going through the watcher, and they now clear this field explicitly.
    /// That is load-bearing rather than tidy: leaving a previous dispatch's token behind would let
    /// a later request be absorbed against a compile nobody recorded the inputs of, and if that
    /// compile fails the absorbed release request is simply lost.</para>
    ///
    /// <para>🚨 It exists so a release request can be recognised by WHAT it asks for, not by WHEN
    /// it was asked. <c>RequestedReleaseAt</c> is a fresh <c>UtcNow</c> on every write, so two
    /// requests for identical content are two distinct values that can never be seen as one. A
    /// trigger arriving while the type is Pending/Compiling was therefore parked and re-fired by
    /// the first compile's own terminal write-back — one logical event (a merge, a push, an
    /// install wave) became N sequential Roslyn compiles of the same sources, each invalidating the
    /// type's instance hubs and raising the "newer build available" adornment. Measured in
    /// production: pairs 65 ms apart, three inside 2 s, and seven compiles for one merge.</para>
    ///
    /// <para>When this equals the token the current request resolves to, the in-flight compile will
    /// produce byte-for-byte what the request asks for, so the request is CONSUMED rather than
    /// queued. <c>RequestedReleaseForce</c> remains the user's escape hatch and always compiles.</para>
    /// </summary>
    public string? DispatchedBuildInputs { get; init; }

    /// <summary>
    /// 🚨 Round-trip buffer for content members this compiled shape does not declare —
    /// schema evolution: a property written by a NEWER build, or one removed since the
    /// JSON was persisted. Without this, System.Text.Json silently DROPS such members on
    /// typed materialization (no exception, so the preserve-raw fallback in
    /// <c>ObjectPolymorphicConverter</c> never fires) and the per-node hub's persistence
    /// echo then persists the loss on pure activation — the content-narrowing
    /// silent-data-loss class (prod <c>Systemorph/Event/DAV2026</c> stripped to defaults;
    /// ~40 <c>samples/Graph/Data</c> NodeType files losing
    /// <c>showChildrenInDetails</c>/<c>detailsChildrenLimit</c>).
    /// <para><c>[JsonExtensionData]</c> captures the unknown members on read and re-emits
    /// them on write — and, being a real record property, it rides every <c>with</c>-copy,
    /// so edits made through the narrower shape keep them too. Never read this
    /// programmatically; it exists solely so unknown JSON survives the round-trip.
    /// <c>[Browsable(false)]</c> keeps it out of reflected content editors.</para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? UnknownMembers { get; init; }
}
