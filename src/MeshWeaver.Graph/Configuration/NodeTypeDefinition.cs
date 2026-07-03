using System.Collections.Immutable;
using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
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
    /// <see cref="MeshWeaver.Graph.CreateLayoutArea"/> invokes this INSTEAD of building
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
    /// <see cref="Sources"/> — see <see cref="CodeQueryResolver"/>. Shown under
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
