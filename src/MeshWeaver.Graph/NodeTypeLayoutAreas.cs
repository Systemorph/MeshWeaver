using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Reactive;
using System.Reactive.Threading.Tasks;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// Uses standard MeshNodeView.Search with NodeTypeCatalogMode for showing instances.
/// - Overview: Split view with left menu and configuration/code display (default)
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeLayoutAreas
{
    /// <summary>Area name for the NodeType instance Search layout area.</summary>
    public const string SearchArea = "Search";
    /// <summary>Area name for the NodeType Overview layout area (the default split view with side menu and configuration/code display).</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the NodeType Configuration layout area.</summary>
    public const string ConfigurationArea = "Configuration";
    /// <summary>Area name for the HubConfiguration view layout area.</summary>
    public const string HubConfigViewArea = "HubConfig";
    /// <summary>Area name for the HubConfiguration edit layout area (Monaco editor).</summary>
    public const string HubConfigEditArea = "HubConfigEdit";
    /// <summary>Area name for the NodeType Releases layout area.</summary>
    public const string ReleasesArea = "Releases";

    /// <summary>
    /// "Code" area on the per-NodeType hub: renders one source/test Code file
    /// (the area Id is the Code node's path) INSIDE the shared <see cref="Shell"/>,
    /// so navigating the Sources/Tests trees keeps the NodeType side menu. The
    /// Code node's own page (<see cref="CodeLayoutAreas"/>) remains for direct
    /// navigation from search results etc.
    /// </summary>
    public const string CodeArea = "Code";

    /// <summary>
    /// "Progress" area name on the per-NodeType hub. GUI clients data-bind here
    /// after receiving a <see cref="MeshWeaver.Messaging.DeliveryFailure"/> with
    /// <see cref="MeshWeaver.Messaging.ErrorType.CompilationInProgress"/>: the
    /// area renders a live status line driven by the NodeType's own MeshNode
    /// stream (<c>CompilationStatus</c>, <c>CompilationError</c>) and, when a
    /// compile activity is in flight (<c>LastCompilationActivityPath</c>),
    /// embeds <see cref="ActivityLayoutAreas.ProgressArea"/> on the activity hub
    /// so the user sees Roslyn diagnostics line-by-line.
    /// </summary>
    public const string ProgressArea = "Progress";

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFileDataId = "codeFile";
    private const string CodeNodesDataId = "codeNodes";

    /// <summary>
    /// Gets the OWN MeshNode of the layout host via the canonical
    /// <c>MeshNodeReference</c> reducer (per Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private static IObservable<MeshNode?> GetNodeStream(LayoutAreaHost host)
        => host.Workspace.GetMeshNodeStream();

    /// <summary>
    /// Adds NodeType catalog views plus all standard node views (Settings, Files, Threads, Chat,
    /// AccessControl, etc.). Use this for NodeType definitions that should also support
    /// the full node management experience (e.g., Organization, custom domain types).
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddNodeTypeView()
            .AddLayout(layout => layout.WithDefaultArea(OverviewArea));

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// Every primary area (Overview, Configuration, Releases, Search) renders inside the
    /// shared <see cref="Shell"/> — one side menu with the type's concerns (source/test
    /// trees grouped by query name, releases, instances, related types) framing the content.
    /// Includes UCR areas ($Data, $Schema, $Model) for unified content references.
    /// Note: $Content is registered by ContentCollectionsExtensions.AddContentCollections.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration
            .Set(new NodeTypeCatalogMode())  // Enable NodeType catalog mode
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(SearchArea, Search)  // standard instance search, wrapped in the shell
                .WithView(OverviewArea, Overview)
                .WithView(ConfigurationArea, Configuration)
                .WithView(HubConfigViewArea, HubConfigView)
                .WithView(HubConfigEditArea, HubConfigEdit)
                .WithView(ReleasesArea, Releases)
                .WithView(CodeArea, Code)
                .WithView(ProgressArea, Progress)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                // $Model on a NodeType shows the INSTANCE data model (compiled types),
                // not the definition hub's own registry — diagram with a JSON toggle.
                .WithView(MeshNodeLayoutAreas.ModelArea, NodeTypeDataModel));

    /// <summary>
    /// Compile-progress view for a NodeType. Subscribes to the NodeType's own
    /// MeshNode stream and renders a status line + (when a compile activity is
    /// in flight) an embedded <see cref="ActivityLayoutAreas.ProgressArea"/>
    /// from the activity hub. GUI clients land here after the routing grain
    /// returns <see cref="MeshWeaver.Messaging.ErrorType.CompilationInProgress"/>;
    /// the area keeps updating as the NodeType's state transitions through
    /// Pending → Compiling → Ok/Error.
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.Path;
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not NodeTypeDefinition def)
                    return (UiControl?)Controls.Markdown(
                        "*This node has no NodeTypeDefinition — nothing to compile.*");

                // Compile finished cleanly → redirect to the now-addressable node. The user
                // only landed on Progress because an instance area NACKed CompilationInProgress;
                // once it's Ok the real view resolves, so send them there. "When it ends, we redirect."
                if (def.CompilationStatus == CompilationStatus.Ok)
                    return (UiControl?)Controls.Redirect($"/{nodePath}");

                var nodeName = node.Name ?? node.Id;
                var (icon, header, body) = RenderProgressLines(def);
                var stack = Controls.Stack
                    .WithStyle("padding: 12px; gap: 8px;")
                    // Title carries the NodeType name: "⏳ Compiling… — <NodeType>".
                    .WithView(Controls.Markdown($"### {icon} {header} — {nodeName}"));
                if (!string.IsNullOrEmpty(body))
                    stack = stack.WithView(Controls.Markdown(body));

                if (def.CompilationStatus == CompilationStatus.Error)
                {
                    // Compilation-failed is a LEGAL terminal status. Surface it as a proper page:
                    // the flat summary is in `body` above; here, for each affected source file, a
                    // link to the Code node + a read-only Monaco editor with the errors MARKED at
                    // their exact position (the captured per-file CompilationDiagnostics drive the
                    // overlay) — so the user sees WHAT broke and WHERE, never an indefinite spinner.
                    // Do NOT embed the activity LayoutAreaControl here — the compile activity is
                    // history and may be unaddressable; subscribing to an inexistent address is the
                    // resubscribe storm that wedged the portal. The Recompile button flips
                    // RequestedReleaseAt + Force on the NodeType's OWN node; the compile watcher reacts.
                    stack = AppendCompileErrorSources(stack, def);
                    stack = stack.WithView(Controls.Button("Recompile")
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction(_ =>
                        {
                            host.Workspace.GetMeshNodeStream()
                                .Update(curr => curr?.Content is NodeTypeDefinition cd
                                    ? curr with
                                    {
                                        Content = cd with
                                        {
                                            RequestedReleaseAt = DateTimeOffset.UtcNow,
                                            RequestedReleaseForce = true
                                        }
                                    }
                                    : curr!)
                                .Subscribe(_ => { },
                                    ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                                        ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                                        .LogWarning(ex, "Recompile trigger failed for {Path}", nodePath));
                            return Task.CompletedTask;
                        }));
                }
                else if (!string.IsNullOrEmpty(def.LastCompilationActivityPath))
                {
                    // Live activity log = the "show details" of an IN-FLIGHT compile
                    // (Compiling / Pending only — the activity is fresh and being written).
                    // Embedding it for a terminal state risks a subscription to a gone
                    // activity node → the inexistent-address storm. Roslyn diagnostics
                    // stream in line by line via the activity hub's ProgressArea.
                    stack = stack.WithView(new LayoutAreaControl(
                            new Address(def.LastCompilationActivityPath!),
                            new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                        .WithStyle("margin-top: 8px; padding: 12px; background: var(--neutral-layer-3); border-radius: 4px; min-height: 48px;"));
                }
                return (UiControl?)stack;
            });
    }

    /// <summary>
    /// For each source file a FAILED compile flagged, append a link to the Code node and a
    /// read-only Monaco editor showing that file with its diagnostics MARKED at their exact
    /// position (the IDE-style error overlay). Driven by the captured, structured
    /// <see cref="NodeTypeDefinition.CompilationDiagnostics"/> (per-file <see cref="DiagnosticInfo"/>),
    /// so the markers land exactly where Roslyn flagged them and the editor reads the live
    /// source straight from the Code node's content stream (single source of truth, no replica).
    /// Location-less diagnostics (assembly-level) stay in the flat summary rendered above.
    /// No-op when there are no structured diagnostics (e.g. an older compile before capture).
    /// </summary>
    private static StackControl AppendCompileErrorSources(StackControl stack, NodeTypeDefinition def)
    {
        foreach (var view in BuildCompileErrorSourceViews(def))
            stack = stack.WithView(view);
        return stack;
    }

    /// <summary>
    /// Pure, testable builder for the compile-error source views: for each source file the failed
    /// compile flagged (grouped by <see cref="DiagnosticInfo"/> <see cref="SourceLocation.SourcePath"/>,
    /// ordinal-ordered so the page is deterministic), emits — IN ORDER — a markdown link to the
    /// Code node followed by a read-only <see cref="CodeEditorControl"/> bound to that node's source
    /// with the diagnostics MARKED at their exact position (the IDE-style error overlay). One
    /// link + one editor per file. Location-less diagnostics (assembly-level) are left to the flat
    /// summary. Empty when there are no structured diagnostics.
    /// </summary>
    internal static IReadOnlyList<UiControl> BuildCompileErrorSourceViews(NodeTypeDefinition def)
    {
        var located = (def.CompilationDiagnostics ?? [])
            .Where(d => d.Location is { } loc && !string.IsNullOrEmpty(loc.SourcePath))
            .GroupBy(d => d.Location!.SourcePath, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (located.Count == 0)
            return [];

        var views = new List<UiControl>(located.Count * 2);
        foreach (var group in located)
        {
            var sourcePath = group.Key;
            var fileName = sourcePath.Split('/').LastOrDefault() ?? sourcePath;
            var errorCount = group.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnCount = group.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var counts = errorCount > 0
                ? $"{errorCount} error{(errorCount == 1 ? "" : "s")}"
                : $"{warnCount} warning{(warnCount == 1 ? "" : "s")}";

            // Link straight to the source Code node so the user can open and fix it.
            views.Add(Controls.Markdown($"##### [{fileName}](/{sourcePath}) — {counts}")
                .WithStyle("margin: 16px 0 4px 0;"));

            var markers = group
                .Select(d => new CodeEditorDiagnostic(
                    d.Location!.Range.Start.Line, d.Location.Range.Start.Character,
                    d.Location.Range.End.Line, d.Location.Range.End.Character,
                    (int)d.Severity, d.Message, d.Id))
                .ToList();

            views.Add(new CodeEditorControl()
                .WithLanguage("csharp")
                .WithReadonly(true)
                .WithLineNumbers(true)
                .WithMinimap(false)
                .WithHeight("360px")
                .WithDiagnostics(markers) with
            {
                // Node-bound: read the source straight from the Code node's content stream
                // (live, single source of truth — no /data replica). bindContent:true targets
                // the CodeConfiguration content; "Code" is its source-text field.
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(sourcePath, bindContent: true),
                Value = new JsonPointerReference("Code")
            });
        }
        return views;
    }

    private static (string Icon, string Header, string Body) RenderProgressLines(NodeTypeDefinition def)
    {
        var hasSource = !string.IsNullOrWhiteSpace(def.Configuration)
            || !string.IsNullOrWhiteSpace(def.HubConfiguration)
            || (def.Sources is { Count: > 0 });

        // Cache-hit (Status=Ok with the usable-assembly fields populated) — the
        // routing grain re-used the existing assembly without re-running Roslyn.
        // Surface that as a discrete state so the operator sees "we didn't burn
        // CPU to re-prove this assembly works."
        if (def.CompilationStatus == CompilationStatus.Ok
            && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath))
        {
            var coll = def.LatestAssemblyCollection!;
            var path = def.LatestAssemblyPath!;
            return ("✓", "Compiled",
                $"Using cached assembly `{coll}/{path}` (compile version `{def.LastCompiledVersion}`, framework `{def.CompiledFrameworkVersion}`).");
        }

        return def.CompilationStatus switch
        {
            CompilationStatus.Compiling => ("⏳", "Compiling…",
                $"Running Roslyn against {(def.Sources?.Count ?? 0)} source binding(s). The activity log below streams diagnostics live."),
            CompilationStatus.Pending => ("▶", "Compile queued",
                "Initiating compilation — the per-NodeType compile watcher has flipped status to Pending and the activity hub is being created."),
            CompilationStatus.Error => ("✗", "Compilation failed",
                string.IsNullOrEmpty(def.CompilationError)
                    ? "The last compile failed — no diagnostic captured. Click **Recycle** on the parent NodeType to retry."
                    : $"```text\n{def.CompilationError}\n```"),
            // null / Unknown — split on whether there's anything to compile at all.
            _ => hasSource
                ? ("…", "Waiting for compile to start",
                   "Sources are present but no compile activity has been kicked yet. Activation will trigger one on first instance request.")
                : ("·", "No compile required",
                   "This NodeType has no `Configuration` / `HubConfiguration` / `Sources` — instances activate against the default node-hub config.")
        };
    }

    /// <summary>
    /// Shared shell for every primary NodeType area: a horizontal splitter with the
    /// type's side menu (Overview / Configuration / source &amp; test trees / Releases /
    /// Instances / Related) on the left and the area's content on the right. One menu
    /// built from one stream combination — every area shows the same, fully populated
    /// navigation (the previous per-area menus passed nulls and rendered empty
    /// Sources/Tests sections on some pages).
    /// </summary>
    private static UiControl Shell(
        LayoutAreaHost host,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> mainContent)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();

        var sourceGroupsStream = GetCodeGroupsStream(host, tests: false);
        var testGroupsStream = GetCodeGroupsStream(host, tests: true);
        var nodeTypesStream = QueryNodesStream(host,
            $"path:{hubPath} nodeType:NodeType scope:descendants");
        var agentsStream = QueryNodesStream(host,
            $"path:{hubPath} nodeType:Agent scope:descendants");

        // `shell-splitter` (standard-page-layout.css) gives both panes a definite-height
        // flex context: the menu and the content scroll independently, the splitter bar
        // is draggable (Min/Max bound) and carries the collapse/expand chevrons for the
        // menu pane. Height fills the parent .layout-area-container instead of a
        // viewport-minus-magic-number guess.
        return Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(
                (h, c) => GetNodeStream(host)
                    .CombineLatest(sourceGroupsStream, testGroupsStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (node, sourceGroups, testGroups, nodeTypes, agents) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        return BuildSideMenu(hubAddress, node, sourceGroups, testGroups, nodeTypes, agents, host.Hub.JsonSerializerOptions);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("480px").WithCollapsible(true)
            )
            .WithView(
                // The splitter pane wants a non-nullable control stream; a null
                // emission from the content area renders as the loading state.
                // No wrapper control: the pane's child div is the scroll container
                // (`.shell-splitter .fluent-multi-splitter-pane > div` in
                // standard-page-layout.css) — tests and embeddings see the area's
                // actual control as the pane content.
                (h, c) => mainContent(h, c).Select(v => v ?? RenderLoading("Loading...")),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Resolves the NodeType's Sources or Tests into named groups: each
    /// <see cref="CodeQueryGroup"/> (from the <c>name=</c> prefix, default
    /// <c>src</c>/<c>test</c>) paired with the live query results for its queries.
    /// Re-resolves whenever the definition changes, so renames/new queries appear
    /// without a reload.
    /// </summary>
    private static IObservable<IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)>> GetCodeGroupsStream(
        LayoutAreaHost host, bool tests)
    {
        return GetNodeStream(host)
            .Select(node =>
            {
                if (node == null)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var def = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
                var groups = tests
                    ? CodeQueryResolver.GroupAll(def?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)
                    : CodeQueryResolver.GroupAll(def?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName);
                if (groups.Count == 0)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var streams = groups.Select(g =>
                    RunQueries(host, g.ExpandedQueries).Select(nodes => (Group: g, Nodes: nodes)));
                return Observable.CombineLatest(streams)
                    .Select(list => (IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>)list.ToList());
            })
            .Switch();
    }

    /// <summary>
    /// Live query stream that degrades to an empty list on error (logged at Warning so
    /// silent timeouts surface in test output) — one bad query must not blank the menu.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> QueryNodesStream(LayoutAreaHost host, string query)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NodeTypeLayoutAreas));
        // 🚨 Live, access-carrying, shared-handle query — NOT IMeshService.Query(...).Take(1). The
        // latter is the hand-woven trap from DebuggingMessageFlow/AsynchronousCalls: a Take(1) on a
        // query that NEVER emits an Initial (a not-yet-provisioned partition, a cold descendant scope)
        // hangs forever, so the Shell's side-menu CombineLatest never completes and the NodeType GUI
        // shell never renders (the FutuRe/LineOfBusiness 50s render deadlock). GetQuery emits
        // empty-on-absent and re-emits on change, so the menu always renders.
        return host.Workspace.GetQuery(query, query)
            .Select(nodes => (IReadOnlyList<MeshNode>)nodes.ToList())
            .Catch<IReadOnlyList<MeshNode>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Query '{Query}' failed; falling back to empty list", query);
                return Observable.Return((IReadOnlyList<MeshNode>)[]);
            });
    }

    /// <summary>
    /// Renders the Overview area for a NodeType — the landing page. Inside the shared
    /// <see cref="Shell"/>: compile status, the markdown Description, a Configuration
    /// summary, the named source/test queries, and the latest three releases with a
    /// link to the full release history.
    /// </summary>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, OverviewContent);

    private static IObservable<UiControl?> OverviewContent(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();

        // Seeded with empty so the overview renders immediately and fills in the
        // releases section when the query result lands (subscribe-all-upfront).
        var releasesStream = QueryNodesStream(host,
                $"namespace:{hubPath}/Release nodeType:{ReleaseNodeType.NodeType}")
            .StartWith((IReadOnlyList<MeshNode>)[]);

        return GetNodeStream(host)
            .CombineLatest(releasesStream)
            .Select(tuple =>
            {
                var (node, releases) = tuple;
                if (node == null)
                    return RenderLoading("Loading...");
                var typeDef = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);

                var content = Controls.Stack.WithWidth("100%")
                    .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, typeDef)
                        + " padding-top: 8px; padding-bottom: 32px; gap: 8px;");
                content = content.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

                // Compile-state banner + Compile button — the "ability to compile"
                // affordance on the landing page. Bound to IsDirty / CompilationStatus.
                content = content.WithView(BuildCompileStatusPanel(host, typeDef));

                // Markdown description from the mesh node's NodeTypeDefinition.
                if (!string.IsNullOrEmpty(typeDef?.Description))
                    content = content.WithView(Controls.Markdown(typeDef.Description));

                content = content.WithView(BuildConfigurationSection(hubAddress, node, typeDef));
                content = content.WithView(NodeTypeDataModelAreas.BuildOverviewSection(host));
                content = content.WithView(BuildQueriesSection("Source queries",
                    CodeQueryResolver.GroupAll(typeDef?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName)));
                content = content.WithView(BuildQueriesSection("Test queries",
                    CodeQueryResolver.GroupAll(typeDef?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)));
                content = content.WithView(BuildLatestReleasesSection(hubAddress, releases, host.Hub.JsonSerializerOptions));

                return (UiControl?)content;
            });
    }

    internal static UiControl BuildSectionHeader(string title, string? href = null, string? linkLabel = null)
    {
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: baseline; margin-top: 24px; " +
                       "padding-bottom: 6px; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.H3(title).WithStyle("margin: 0;"));
        if (href != null)
            header = header.WithView(Controls.Markdown($"[{linkLabel ?? "More"} →]({href})")
                .WithStyle("font-size: 13px;"));
        return header;
    }

    /// <summary>
    /// Configuration summary on the Overview: the notable settings as read-only rows
    /// plus a link to the full Configuration area where they're edited.
    /// </summary>
    private static UiControl BuildConfigurationSection(object hubAddress, MeshNode node, NodeTypeDefinition? def)
    {
        var configHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader("Configuration", configHref, "Open configuration"));

        var rows = new List<(string Label, string? Value)>
        {
            ("Default Namespace", def?.DefaultNamespace),
            ("Children Query", def?.ChildrenQuery),
            ("Storage Table", def?.StorageTable),
            ("Owns Partition", def?.OwnsPartition == true ? "Yes" : null),
            ("Page Max Width", def?.PageMaxWidth),
            ("Dependencies", def?.Dependencies is { Count: > 0 } deps ? string.Join(", ", deps) : null),
            ("Configuration Lambda", string.IsNullOrWhiteSpace(def?.Configuration) ? null : "Defined"),
        };

        var hasAny = false;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            section = section.WithView(BuildInfoRow(label, value!));
            hasAny = true;
        }

        if (!hasAny)
            section = section.WithView(Controls.Body("All settings at their defaults.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        return section;
    }

    /// <summary>
    /// Lists the named source/test queries exactly as configured — one line per query,
    /// grouped label first (<c>src — `namespace:Source scope:subtree`</c>).
    /// </summary>
    private static UiControl BuildQueriesSection(string title, IReadOnlyList<CodeQueryGroup> groups)
    {
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader(title));

        if (groups.Count == 0)
        {
            return section.WithView(Controls.Body("None configured.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        }

        foreach (var group in groups)
            foreach (var raw in group.RawQueries)
                section = section.WithView(
                    Controls.Markdown($"**{group.Name}** — `{raw}`")
                        .WithStyle("padding: 2px 0;"));
        return section;
    }

    /// <summary>
    /// The latest three releases (newest first) with a link to the full Releases area.
    /// </summary>
    private static UiControl BuildLatestReleasesSection(object hubAddress, IReadOnlyList<MeshNode> releaseNodes, System.Text.Json.JsonSerializerOptions options)
    {
        var releasesHref = new LayoutAreaReference(ReleasesArea).ToHref(hubAddress);
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader("Latest releases", releasesHref, "All releases"));

        var latest = releaseNodes
            .Select(n => (Node: n, Release: n.ContentAs<NodeTypeRelease>(options)))
            .OrderByDescending(t => t.Release?.CreatedAt ?? t.Node.CreatedDate)
            .Take(3)
            .ToList();

        if (latest.Count == 0)
        {
            return section.WithView(Controls.Body(
                    "No releases yet — use the Compile button above to create the first one.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        }

        foreach (var (releaseNode, release) in latest)
            section = section.WithView(BuildReleaseRow(releaseNode, release));
        return section;
    }

    /// <summary>
    /// Renders the Configuration area for a NodeType: the shared <see cref="Shell"/>
    /// with the editable settings form (<see cref="BuildConfigurationPane"/>) as content.
    /// </summary>
    public static UiControl Configuration(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, (h, c) => GetNodeStream(host)
            .Select(definition => definition == null
                ? RenderLoading("Loading...")
                : BuildConfigurationPane(host, host.Hub.Address, definition)));

    /// <summary>
    /// Renders the instance search for this NodeType inside the shared <see cref="Shell"/> —
    /// the standard <see cref="MeshNodeLayoutAreas.Search"/> with NodeTypeCatalogMode lists
    /// and searches the type's instances.
    /// </summary>
    public static UiControl Search(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, MeshNodeLayoutAreas.Search);

    /// <summary>
    /// Renders a single source/test Code file inside the shared <see cref="Shell"/>.
    /// The area Id is the Code node's path; the content embeds that node's default
    /// (Content) area via <see cref="LayoutAreaControl"/>, so the file view stays
    /// live while the NodeType side menu stays put. This is where the Sources/Tests
    /// tree links point — previously they navigated to the Code node's own page,
    /// which swapped the NodeType menu for the code-sibling menu ("the menu keeps
    /// disappearing").
    /// </summary>
    public static UiControl Code(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, CodeContent);

    /// <summary>
    /// The NodeType's <c>$Model</c> area inside the shared <see cref="Shell"/>:
    /// the instance data model as a Mermaid class diagram with a tab to switch to
    /// the JSON schema and back; with an Id, the detail page for that type.
    /// </summary>
    public static UiControl NodeTypeDataModel(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, NodeTypeDataModelAreas.Content);

    private static IObservable<UiControl?> CodeContent(LayoutAreaHost host, RenderingContext ctx)
    {
        var codePath = host.Reference.Id?.ToString();
        if (string.IsNullOrEmpty(codePath))
            return Observable.Return<UiControl?>(Controls.Markdown(
                    "*No source file selected — pick one from the Sources or Tests tree.*")
                .WithStyle("padding: 24px;"));

        return Observable.Return<UiControl?>(
            new LayoutAreaControl(new Address(codePath!), new LayoutAreaReference(CodeLayoutAreas.ContentArea))
                .WithStyle("width: 100%;"));
    }

    /// <summary>
    /// Builds the NodeType side menu — the one navigation surface every NodeType area
    /// shares. Concerns, top to bottom: Overview (landing), Configuration, the source
    /// tree grouped by query name, the test tree grouped by query name, Releases,
    /// Instances (search for nodes of this type), and Related (declared dependencies,
    /// NodeTypes and Agents under this namespace).
    /// </summary>
    private static UiControl BuildSideMenu(
        object hubAddress,
        MeshNode node,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)> sourceGroups,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)> testGroups,
        IReadOnlyList<MeshNode> nodeTypes,
        IReadOnlyList<MeshNode> agents,
        System.Text.Json.JsonSerializerOptions options)
    {
        var content = node.ContentAs<NodeTypeDefinition>(options);
        // Scrolling comes from `.shell-splitter .navmenu` in standard-page-layout.css —
        // NavMenuView ignores inline Style on its root. Collapse/expand is the splitter
        // pane's affordance (chevrons on the bar), not the NavMenu hamburger.
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        navMenu = navMenu.WithView(new NavLinkControl("Overview", FluentIcons.Home(),
            new LayoutAreaReference(OverviewArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Configuration", FluentIcons.Settings(),
            new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Data model", FluentIcons.Database(),
            new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea).ToHref(hubAddress)));

        // Sources + Tests trees — the resolved outputs of the configured queries,
        // grouped by query name (default `src` / `test`), so the user sees exactly
        // what compiles — including shared code pulled in from other namespaces.
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Sources", FluentIcons.Code(), node.Path, sourceGroups, hubAddress));
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Tests", FluentIcons.Beaker(), node.Path, testGroups, hubAddress));

        navMenu = navMenu.WithView(new NavLinkControl("Releases", FluentIcons.Box(),
            new LayoutAreaReference(ReleasesArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Instances", FluentIcons.Search(),
            new LayoutAreaReference(SearchArea).ToHref(hubAddress)));

        // Related — navigation to related types: declared dependencies (e.g. the
        // dimensions a cube type references) plus NodeTypes and Agents under this
        // namespace. Only rendered when there's something to link.
        var related = new NavGroupControl("Related")
            .WithIcon(FluentIcons.Link())
            .WithSkin(s => s.WithExpanded(true));
        var hasRelated = false;

        if (content?.Dependencies is { Count: > 0 } deps)
        {
            foreach (var dep in deps)
            {
                related = related.WithView(new NavLinkControl(
                    dep.Split('/').LastOrDefault() ?? dep, FluentIcons.Document(), $"/{dep}"));
                hasRelated = true;
            }
        }

        foreach (var typeNode in nodeTypes.OrderBy(n => n.Order).ThenBy(n => n.Name))
        {
            related = related.WithView(new NavLinkControl(
                typeNode.Name ?? typeNode.Id, FluentIcons.DocumentText(), $"/{typeNode.Path}"));
            hasRelated = true;
        }

        foreach (var agentNode in agents.OrderBy(n => n.Order).ThenBy(n => n.Name))
        {
            related = related.WithView(new NavLinkControl(
                agentNode.Name ?? agentNode.Id, FluentIcons.Bot(), $"/{agentNode.Path}"));
            hasRelated = true;
        }

        if (hasRelated)
            navMenu = navMenu.WithNavGroup(related);

        return navMenu;
    }

    /// <summary>
    /// Builds the hierarchical navigation group for the resolved Sources or Tests:
    /// one sub-group per named query (the <c>name=</c> prefix; default <c>src</c>/<c>test</c>),
    /// each containing the file tree its queries resolved to. Files under the group's
    /// namespace root are displayed at their relative path (folders by namespace
    /// segment); files outside it — shared code pulled in via <c>@path</c> or
    /// cross-NodeType <c>namespace:</c> queries — go under a "(shared)" folder at their
    /// absolute path so their origin remains obvious.
    /// </summary>
    internal static NavGroupControl BuildCodeNavGroup(
        string groupLabel,
        Icon groupIcon,
        string rootPath,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)>? groups,
        object? hubAddress = null)
    {
        var root = new NavGroupControl(groupLabel)
            .WithIcon(groupIcon)
            .WithSkin(s => s.WithExpanded(true));

        if (groups == null || groups.Count == 0 || groups.All(g => g.Nodes.Count == 0))
        {
            return root.WithView(
                Controls.Body($"No {groupLabel.ToLowerInvariant()} yet")
                    .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
        }

        foreach (var (group, nodes) in groups)
        {
            var sub = new NavGroupControl(group.Name)
                .WithIcon(FluentIcons.Folder())
                .WithSkin(s => s.WithExpanded(true));

            if (nodes.Count == 0)
            {
                sub = sub.WithView(
                    Controls.Body("(empty)")
                        .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
            }
            else
            {
                // Relativise against the group's own namespace root when one is
                // determinable (so the default `src` group shows files directly,
                // not nested under a redundant "Source/" folder); otherwise fall
                // back to the NodeType's path.
                var basePath = group.BaseNamespace ?? rootPath;
                var tree = BuildCodeTreeForNavigation(basePath, nodes);
                foreach (var child in tree.OrderedChildren())
                    sub = AppendCodeTreeNode(sub, child, hubAddress);
            }

            root = root.WithGroup(sub);
        }

        return root;
    }

    /// <summary>
    /// Groups resolved code nodes into a tree: local files (paths starting with
    /// <c>{rootPath}/</c>) are relativised; foreign files go under a single
    /// "(shared)" folder with their absolute path preserved.
    /// </summary>
    internal static CodeTreeFolder BuildCodeTreeForNavigation(string rootPath, IReadOnlyCollection<MeshNode> nodes)
    {
        var rootPrefix = rootPath + "/";
        var tree = new CodeTreeFolder("");
        CodeTreeFolder? sharedFolder = null;

        foreach (var node in nodes.OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            if (node.Path.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                var relative = node.Path.Substring(rootPrefix.Length);
                tree.Insert(relative.Split('/'), 0, node);
            }
            else
            {
                if (sharedFolder == null)
                {
                    sharedFolder = new CodeTreeFolder("(shared)");
                    tree.AddFolder(sharedFolder);
                }
                // Preserve full absolute path so operators can tell which NodeType
                // owns this shared file. Split on '/' gives a nested folder tree.
                sharedFolder.Insert(node.Path.Split('/'), 0, node);
            }
        }
        return tree;
    }

    /// <summary>
    /// Runs a sequence of expanded queries via the LIVE <c>workspace.GetQuery</c> and returns the
    /// de-duplicated MeshNode results. Empty input → empty result, so the default "no sources/tests
    /// yet" state still renders cleanly.
    /// </summary>
    // 🚨 GetQuery, NOT IMeshService.Query(...).Take(1): the latter HANGS when a query never emits an
    // Initial (cold/unprovisioned scope), wedging the Shell's side-menu CombineLatest forever (the
    // FutuRe/LineOfBusiness render deadlock). GetQuery is live, shared (one upstream per id), carries
    // the subscriber's identity, and emits empty-on-absent — so the menu always renders. One shared
    // handle for the whole expanded set (params queries); fold to a deduped list.
    private static IObservable<IReadOnlyList<MeshNode>> RunQueries(
        LayoutAreaHost host,
        IEnumerable<string> queries)
    {
        var queryList = queries.ToList();
        if (queryList.Count == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        return host.Workspace.GetQuery("codegroup:" + string.Join("|", queryList), queryList.ToArray())
            .Select(nodes =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var results = new List<MeshNode>();
                foreach (var n in nodes)
                    if (n?.Path is { Length: > 0 } p && seen.Add(p))
                        results.Add(n);
                return (IReadOnlyList<MeshNode>)results;
            });
    }

    private static NavGroupControl AppendCodeTreeNode(NavGroupControl parent, CodeTreeNode node, object? hubAddress)
    {
        if (node is CodeTreeLeaf leaf)
        {
            // Inside a NodeType shell, open the file in the shell's own Code area so
            // the side menu stays. Without a hub address (no shell context), fall back
            // to the Code node's standalone page.
            var href = hubAddress != null
                ? new LayoutAreaReference(CodeArea) { Id = leaf.Node.Path }.ToHref(hubAddress)
                : new LayoutAreaReference(CodeLayoutAreas.OverviewArea).ToHref(leaf.Node.Path);
            return parent.WithView(new NavLinkControl(leaf.Node.Name ?? leaf.Node.Id, CustomIcons.CSharp(), href));
        }

        var folder = (CodeTreeFolder)node;
        var group = new NavGroupControl(folder.Name)
            .WithIcon(FluentIcons.Folder())
            .WithSkin(s => s.WithExpanded(true));
        foreach (var child in folder.OrderedChildren())
            group = AppendCodeTreeNode(group, child, hubAddress);
        return parent.WithGroup(group);
    }

    /// <summary>
    /// Testable pure helper: builds a <see cref="CodeTreeFolder"/> representing the
    /// hierarchy a caller would render as a <see cref="NavGroupControl"/>. Filters by
    /// <c>subPrefix</c> exactly like <see cref="BuildCodeNavGroup"/> so
    /// tests can assert the bucketing (outside-namespace files filtered, nested folders
    /// grouped, alphabetical order) without walking the UI control tree.
    /// </summary>
    internal static CodeTreeFolder BuildCodeTree(string rootPath, string subNamespace, IReadOnlyCollection<MeshNode> nodes)
    {
        var subPrefix = $"{rootPath}/{subNamespace}/";
        var tree = new CodeTreeFolder("");
        foreach (var node in nodes
                     .Where(n => n.Path.StartsWith(subPrefix, StringComparison.Ordinal))
                     .OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            var relative = node.Path.Substring(subPrefix.Length);
            tree.Insert(relative.Split('/'), 0, node);
        }
        return tree;
    }

    internal abstract class CodeTreeNode
    {
        public string Name { get; init; } = "";
    }

    internal sealed class CodeTreeLeaf : CodeTreeNode
    {
        public MeshNode Node { get; init; } = null!;
    }

    internal sealed class CodeTreeFolder : CodeTreeNode
    {
        private readonly Dictionary<string, CodeTreeFolder> _folders = new(StringComparer.Ordinal);
        private readonly List<CodeTreeLeaf> _leaves = new();

        public CodeTreeFolder(string name) { Name = name; }

        public IReadOnlyDictionary<string, CodeTreeFolder> Folders => _folders;
        public IReadOnlyList<CodeTreeLeaf> Leaves => _leaves;

        /// <summary>
        /// Splices an externally-built folder into this tree under its own name.
        /// Used to attach the synthetic "(shared)" folder for foreign code files.
        /// </summary>
        public void AddFolder(CodeTreeFolder folder) => _folders[folder.Name] = folder;

        public void Insert(string[] segments, int index, MeshNode node)
        {
            if (index == segments.Length - 1)
            {
                _leaves.Add(new CodeTreeLeaf { Name = segments[index], Node = node });
                return;
            }
            var folderName = segments[index];
            if (!_folders.TryGetValue(folderName, out var folder))
                _folders[folderName] = folder = new CodeTreeFolder(folderName);
            folder.Insert(segments, index + 1, node);
        }

        public IEnumerable<CodeTreeNode> OrderedChildren()
            => _folders.Values.OrderBy(f => f.Name, StringComparer.Ordinal)
                .Cast<CodeTreeNode>()
                .Concat(_leaves.OrderBy(l => l.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Renders the Releases area for a NodeType — the chronological list of
    /// <c>Release</c> MeshNodes at <c>{nodeTypePath}/Release/{version}</c>, written by
    /// the CompileWatcher on every successful compile. Each row shows the status,
    /// version, timestamp, source/test counts, and release-notes excerpt, and links to
    /// the release's own page (where the sources/tests as-of that version are
    /// navigable). The header carries the "Create Release" button — the canonical
    /// request-via-stream-update compile trigger.
    /// </summary>
    [Browsable(false)]
    public static UiControl Releases(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, ReleasesContent);

    private static IObservable<UiControl?> ReleasesContent(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var releasesStream = QueryNodesStream(host,
                $"namespace:{hubPath}/Release nodeType:{ReleaseNodeType.NodeType}")
            .StartWith((IReadOnlyList<MeshNode>)[]);

        return GetNodeStream(host)
            .CombineLatest(releasesStream)
            .Select(tuple =>
            {
                var (node, releases) = tuple;
                if (node == null)
                    return RenderLoading("Loading…");
                return BuildReleasesPane(host, node, releases);
            });
    }

    private static UiControl BuildReleasesPane(LayoutAreaHost host, MeshNode node, IReadOnlyList<MeshNode> releaseNodes)
    {
        var hubPath = host.Hub.Address.ToString();
        var def = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;");

        // Header row: title + Create Release (the compile trigger) + live status badge.
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; gap: 16px;")
            .WithView(Controls.H2("Releases").WithStyle("margin: 0;"));

        var statusStream = host.Workspace.GetMeshNodeStream()
            .Select(n => n.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions)?.CompilationStatus)
            .DistinctUntilChanged();
        var statusBadge = (LayoutAreaHost h, RenderingContext rc) => statusStream.Select(status =>
            (UiControl)Controls.Body(status switch
            {
                CompilationStatus.Pending => "Compiling…",
                CompilationStatus.Compiling => "Compiling…",
                CompilationStatus.Error => "Last compile: Error",
                _ => ""
            }).WithStyle("color: var(--neutral-foreground-hint); font-size: 13px;"));

        var createReleaseButton = Controls.Button("Create Release")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Play())
            .WithClickAction(clickCtx =>
            {
                // Canonical request-via-stream-update trigger (see
                // Doc/Architecture/RequestViaStreamUpdate.md): flip RequestedReleaseAt;
                // the per-NodeType hub's InstallReleaseRequestWatcher reacts and the
                // CompileWatcher runs Roslyn + writes the Release node.
                clickCtx.Host.Workspace.GetMeshNodeStream(hubPath).Update(curr =>
                {
                    if (curr?.Content is not NodeTypeDefinition cd) return curr!;
                    return curr with
                    {
                        Content = cd with
                        {
                            RequestedReleaseAt = DateTimeOffset.UtcNow,
                            RequestedReleaseForce = true
                        }
                    };
                }).Subscribe(
                    _ => { },
                    ex => clickCtx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                        .LogWarning(ex, "Release-request write failed for {Path}", hubPath));
                return Task.CompletedTask;
            });

        headerRow = headerRow.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center;")
            .WithView(statusBadge, "ReleasesStatusBadge")
            .WithView(createReleaseButton));
        stack = stack.WithView(headerRow);

        stack = stack.WithView(Controls.Body(
                "Every successful compile publishes an immutable release here. Open a " +
                "release to see its notes, the compile log, and the exact source and " +
                "test versions that went into it.")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

        if (!string.IsNullOrWhiteSpace(def?.ReleaseNotes))
            stack = stack.WithView(Controls.Body($"Pending release notes: {def!.ReleaseNotes}")
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.9rem; font-style: italic;"));

        var ordered = releaseNodes
            .Select(n => (Node: n, Release: n.ContentAs<NodeTypeRelease>(host.Hub.JsonSerializerOptions)))
            .OrderByDescending(t => t.Release?.CreatedAt ?? t.Node.CreatedDate)
            .ToList();

        if (ordered.Count == 0)
        {
            stack = stack.WithView(Controls.Body(
                    "No releases yet. Click 'Create Release' to compile this NodeType — " +
                    "the release will appear here.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
            return stack;
        }

        foreach (var (releaseNode, release) in ordered)
            stack = stack.WithView(BuildReleaseRow(releaseNode, release));

        return stack;
    }

    /// <summary>
    /// One release as a clickable card: status badge, version, timestamp,
    /// source/test counts, and notes excerpt. Links to the Release node's own page
    /// where the per-version sources/tests are navigable. Shared between the
    /// Overview's "Latest releases" section and the Releases area.
    /// </summary>
    private static UiControl BuildReleaseRow(MeshNode releaseNode, NodeTypeRelease? release)
    {
        var failed = string.Equals(release?.Status, "Failed", StringComparison.OrdinalIgnoreCase);
        var statusLabel = failed ? "Failed" : "Succeeded";
        var statusColor = failed ? "var(--error)" : "var(--accent-fill-rest)";
        var versionLabel = release?.Version ?? releaseNode.Name ?? releaseNode.Id;
        var createdAt = release?.CreatedAt ?? releaseNode.CreatedDate;

        var counts = "";
        if (release?.SourceVersions is { Count: > 0 } srcs)
            counts = $"{srcs.Count} source{(srcs.Count == 1 ? "" : "s")}";
        if (release?.TestVersions is { Count: > 0 } tsts)
            counts += $"{(counts.Length > 0 ? ", " : "")}{tsts.Count} test{(tsts.Count == 1 ? "" : "s")}";

        var notesExcerpt = release?.Notes?.Content;
        if (!string.IsNullOrWhiteSpace(notesExcerpt))
        {
            notesExcerpt = notesExcerpt!.Trim();
            var firstBreak = notesExcerpt.IndexOf('\n');
            if (firstBreak > 0) notesExcerpt = notesExcerpt[..firstBreak];
            if (notesExcerpt.Length > 200) notesExcerpt = notesExcerpt[..200] + "…";
        }

        var releaseHref = $"/{releaseNode.Path}";
        var rowHtml = $"<a href=\"{System.Net.WebUtility.HtmlEncode(releaseHref)}\" " +
            $"style=\"display: block; padding: 12px 16px; margin-bottom: 8px; " +
            $"background: var(--neutral-layer-2); border-radius: 4px; " +
            $"text-decoration: none; color: inherit; border-left: 3px solid {statusColor};\">" +
            $"<div style=\"display: flex; align-items: center; gap: 12px;\">" +
            $"<span style=\"font-weight: 600; padding: 2px 10px; border-radius: 12px; " +
            $"background: {statusColor}20; color: {statusColor}; font-size: 0.85rem;\">" +
            $"{System.Net.WebUtility.HtmlEncode(statusLabel)}</span>" +
            $"<span style=\"flex: 1; font-weight: 600;\">{System.Net.WebUtility.HtmlEncode(versionLabel)}</span>" +
            (counts.Length > 0
                ? $"<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">" +
                  $"{System.Net.WebUtility.HtmlEncode(counts)}</span>"
                : "") +
            $"<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">" +
            $"{createdAt:g}</span>" +
            $"</div>";

        if (!string.IsNullOrWhiteSpace(notesExcerpt))
        {
            rowHtml += $"<div style=\"margin-top: 6px; color: var(--neutral-foreground); " +
                $"font-size: 0.9rem; line-height: 1.4;\">" +
                $"{System.Net.WebUtility.HtmlEncode(notesExcerpt)}</div>";
        }

        rowHtml += "</a>";
        return Controls.Html(rowHtml);
    }

    /// <summary>
    /// Builds the main Configuration pane: an editable settings form for the NodeType
    /// (Name, Description, Icon, ChildrenQuery, DefaultNamespace, PageMaxWidth) with
    /// auto-save, plus a read-only preview of the Configuration lambda with an Edit
    /// button that opens the dedicated Monaco editor.
    /// </summary>
    private static UiControl BuildConfigurationPane(LayoutAreaHost host, object hubAddress, MeshNode node)
    {
        var definition = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 20px;");

        // Header row: title + Create Release + Run Tests actions.
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; gap: 16px;")
            .WithView(Controls.H2(node.Name ?? nodeId ?? "Unknown").WithStyle("margin: 0;"));

        // IsUpToDate: combines own-node stream (CompiledSources) with live sources query.
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        var nodeTypePath = host.Hub.Address.Path;
        var sourcesObs = meshService?.Query<MeshNode>(
            MeshQueryRequest.FromQuery($"namespace:{nodeTypePath}/Source nodeType:Code"))
            ?? Observable.Return(new QueryResultChange<MeshNode>());
        var isUpToDate = host.Workspace.GetMeshNodeStream()
            .CombineLatest(sourcesObs, (ownNode, sources) =>
                MeshDataSourceExtensions.IsSourcesUpToDate(ownNode.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions), sources.Items))
            .DistinctUntilChanged();

        var releaseButton = (LayoutAreaHost h, RenderingContext rc) => isUpToDate
            .Select(upToDate => (UiControl)Controls.Button("Create Release")
                // Appearance still signals the up-to-date state (Neutral = nothing changed
                // since the last release; Accent = actionable) without renaming the button —
                // it is THE "Create Release" entry point regardless of dirty state.
                .WithAppearance(upToDate ? Appearance.Neutral : Appearance.Accent)
                .WithIconStart(FluentIcons.Play())
                .WithClickAction(ctx =>
                {
                    // Creating a release is a privileged USER action gated by
                    // Permission.Compile. Route through the canonical, permission-checked
                    // entry point: hub.RequestNodeTypeRelease verifies the caller holds
                    // Compile on the target and refuses cleanly (status message, no release)
                    // when they don't. On success it flips RequestedReleaseAt +
                    // RequestedReleaseBy via stream.Update; the per-NodeType hub's
                    // InstallReleaseRequestWatcher promotes that to CompilationStatus=Pending
                    // and InstallCompileWatcher runs Roslyn UNDER SYSTEM (the pure compilation
                    // fills the cache; the resulting Release node is stamped to the caller).
                    // No bespoke CreateReleaseRequest. See RequestViaStreamUpdate.md +
                    // NodeTypeReleaseExtensions.
                    ctx.Host.Hub.RequestNodeTypeRelease(
                        nodeTypePath,
                        force: upToDate,
                        onError: msg => ctx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning("Create Release refused for {Path}: {Reason}", nodeTypePath, msg));
                    return Task.CompletedTask;
                }));

        var runTestsButton = Controls.Button("Run Tests")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Play())
            .WithClickAction(ctx =>
            {
                ctx.Host.Hub.Observe(new RunTestsRequest(),
                    o => o.WithTarget(ctx.Host.Hub.Address))
                    .Subscribe(
                        _ => { },
                        ex => ctx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning(ex, "RunTestsRequest failed on {Hub}", ctx.Host.Hub.Address));
                return Task.CompletedTask;
            });

        // Inline status badge so the user sees their click landed and where the
        // compile is in its lifecycle. Live observable — re-emits on every
        // status transition the watcher writes back.
        var statusStream = host.Workspace.GetMeshNodeStream()
            .Select(n => n.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions)?.CompilationStatus)
            .DistinctUntilChanged();
        var statusBadge = (LayoutAreaHost h, RenderingContext rc) => statusStream.Select(status =>
            (UiControl)Controls.Body(status switch
            {
                CompilationStatus.Pending => "Compiling…",
                CompilationStatus.Compiling => "Compiling…",
                CompilationStatus.Ok => "Last compile: Ok",
                CompilationStatus.Error => "Last compile: Error",
                _ => ""
            }).WithStyle("color: var(--neutral-foreground-hint); font-size: 13px;"));

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center;")
            .WithView(statusBadge, "CompileStatusBadge")
            .WithView(releaseButton, "CreateReleaseButton")
            .WithView(runTestsButton);

        headerRow = headerRow.WithView(actions);
        stack = stack.WithView(headerRow);

        // Live compile-activity panel: streams the messages from the most
        // recent compile activity (LastCompilationActivityPath) below the
        // header. Re-emits when the NodeType's own MeshNode ticks (status
        // changes, activity path updates). Empty when no compile has happened
        // yet. On success, also surface a "Latest release" link to the
        // freshly-created Release MeshNode at LatestReleasePath. Shape per
        // Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md → "Live
        // progress" UI requirement.
        var compileLogPanel = (LayoutAreaHost h, RenderingContext rc) =>
            host.Workspace.GetMeshNodeStream()
                .Select(n => n.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions))
                .Where(d => d is not null)
                .Select(d => BuildCompileLogPanel(d!));
        stack = stack.WithView(compileLogPanel, "CompileLogPanel");

        // Editable settings form — bound DIRECTLY to the node stream (IMeshNodeStreamCache), ONE
        // source of truth. Display Name / Icon are node TOP-LEVEL fields (fields-mode DataContext);
        // the NodeTypeDefinition settings live in node.Content (content-mode DataContext). Each
        // control's edit writes straight back to the matching field on the node — no /data replica,
        // no debounced save subscription. See Doc/GUI/DataBinding "edit node content by binding to
        // the node stream". (Pointer resolution against the node is case-insensitive, so the
        // camelCase node/Content JSON binds from these PascalCase pointers.)
        var nodeFieldsContext = LayoutAreaReference.GetMeshNodeDataContext(node.Path, bindContent: false);
        var contentContext = LayoutAreaReference.GetMeshNodeDataContext(node.Path, bindContent: true);

        var formGrid = Controls.Stack
            .WithStyle("display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px;");

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Name)))
        {
            Label = "Display Name",
            Immediate = true,
            DataContext = nodeFieldsContext
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Icon)))
        {
            Label = "Icon",
            Placeholder = "content:icon.svg, /static/…, <svg>…</svg>, or URL",
            Immediate = true,
            DataContext = nodeFieldsContext
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.ChildrenQuery)))
        {
            Label = "Children Query",
            Placeholder = "e.g. nodeType:Person scope:descendants",
            Immediate = true,
            DataContext = contentContext
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.DefaultNamespace)))
        {
            Label = "Default Namespace",
            Placeholder = "Pre-selected namespace in Create form",
            Immediate = true,
            DataContext = contentContext
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.PageMaxWidth)))
        {
            Label = "Page Max Width",
            Placeholder = "e.g. 1200px or 100%",
            Immediate = true,
            DataContext = contentContext
        });

        stack = stack.WithView(formGrid);

        stack = stack.WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeDefinition.Description)))
        {
            Label = "Description",
            Placeholder = "Long-form description shown in the Overview and Create dialog.",
            Immediate = true,
            DataContext = contentContext
        }.WithRows(4));

        // Release notes — what changed in the next compile. Bound straight to
        // NodeTypeDefinition.ReleaseNotes on the node (content-mode); the Create Release click reads
        // no data — it just flips CompilationStatus to Pending. Pure stream wiring, no Take(1).
        stack = stack.WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeDefinition.ReleaseNotes)))
        {
            Label = "Release notes",
            Placeholder = "What changed in the next compile? Shown on each row in the Releases pane.",
            Immediate = true,
            DataContext = contentContext
        }.WithRows(3));

        // Configuration lambda — read-only preview, with button to open the dedicated editor.
        var configHeader = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-top: 8px;")
            .WithView(Controls.H3("Configuration Lambda").WithStyle("margin: 0;"))
            .WithView(Controls.Button("Edit")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Edit())
                .WithNavigateToHref(editHref));

        stack = stack.WithView(configHeader);

        var configCode = definition?.Configuration ?? "";
        if (!string.IsNullOrEmpty(configCode))
        {
            var configDataId = Guid.NewGuid().AsString();
            host.UpdateData(configDataId, configCode);

            var configEditor = new CodeEditorControl()
                .WithLanguage("csharp")
                .WithHeight("280px")
                .WithLineNumbers(true)
                .WithMinimap(false)
                .WithWordWrap(true)
                .WithReadonly(true);

            configEditor = configEditor with
            {
                DataContext = LayoutAreaReference.GetDataPointer(configDataId),
                Value = new JsonPointerReference("")
            };

            stack = stack.WithView(configEditor);
        }
        else
        {
            stack = stack.WithView(Controls.Body("No configuration lambda defined.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the view for Configuration.
    /// Returns static structure with data-bound content.
    /// </summary>
    [Browsable(false)]
    public static UiControl HubConfigView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data stream
        host.SubscribeToDataStream(DefinitionDataId, GetNodeStream(host));

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<MeshNode>(DefinitionDataId)
                    .Select(node => node == null
                        ? RenderLoading("Loading...")
                        : BuildHubConfigViewContent(host, node)),
                "Content"
            );
    }

    private static UiControl BuildHubConfigViewContent(LayoutAreaHost host, MeshNode node)
    {
        var content = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        stack = stack.WithView(Controls.H2("Configuration").WithStyle("margin-bottom: 16px;"));
        stack = stack.WithView(Controls.Body("Lambda expression: Func<MessageHubConfiguration, MessageHubConfiguration>").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"));

        if (!string.IsNullOrEmpty(content?.Configuration))
        {
            stack = stack.WithView(Controls.Markdown($"```csharp\n{content.Configuration}\n```").WithStyle("max-height: 400px; overflow: auto;"));

            // Edit button
            var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.Edit())
                        .WithNavigateToHref(editHref))
            );
        }
        else
        {
            stack = stack.WithView(Controls.Body("No Configuration defined.").WithStyle("color: var(--neutral-foreground-hint);"));
        }

        // Back button
        var configBackHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithNavigateToHref(configBackHref));

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing Configuration.
    /// Returns static structure with data-bound editor.
    /// </summary>
    [Browsable(false)]
    public static UiControl HubConfigEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(DefinitionDataId, GetNodeStream(host));
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<MeshNode>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (node, codeFile) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        var allCode = codeFile?.Code ?? "";
                        return BuildHubConfigEditContent(host, node, allCode);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, MeshNode node, string allCodeForAutocomplete)
    {
        var content = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        // ID comes from hub address, not from content
        var nodeId = hubAddress.Segments.LastOrDefault() ?? "Unknown";

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var orderDataId = Guid.NewGuid().AsString();
        var childrenQueryDataId = Guid.NewGuid().AsString();
        var dependenciesDataId = Guid.NewGuid().AsString();
        var configurationDataId = Guid.NewGuid().AsString();

        // Initialize data streams
        host.UpdateData(displayNameDataId, node.Name ?? "");
        host.UpdateData(descriptionDataId, content?.Description ?? "");
        host.UpdateData(iconNameDataId, node.Icon ?? "");
        host.UpdateData(orderDataId, (node.Order ?? 0).ToString());
        host.UpdateData(childrenQueryDataId, content?.ChildrenQuery ?? "");
        host.UpdateData(dependenciesDataId, content?.Dependencies != null ? string.Join(", ", content.Dependencies) : "");
        host.UpdateData(configurationDataId, content?.Configuration ?? "config => config");

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {node.Name ?? nodeId}").WithStyle("margin-bottom: 16px;"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 150px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

        // Display Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Display Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) }));

        // Description
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Description:").WithStyle("font-weight: 500;"))
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter description...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Icon Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Document, Folder...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Display Order:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(orderDataId) }));

        // Children Query
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Children Query:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Query for children (e.g., nodeType:Person)")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(childrenQueryDataId) }));

        // Dependencies
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Dependencies:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Comma-separated node type paths...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(dependenciesDataId) }));

        // Configuration (code editor)
        stack = stack.WithView(Controls.H3("Configuration").WithStyle("margin: 24px 0 8px 0;"));
        stack = stack.WithView(Controls.Body("Lambda expression: config => config.AddData(...)").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("250px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithPlaceholder("config => config");

        if (!string.IsNullOrEmpty(allCodeForAutocomplete))
        {
            editor = editor.WithExtraTypeDefinitions(allCodeForAutocomplete);
        }

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(configurationDataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Cancel button
        var viewHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(viewHref));

        // Save button - sync click action; subscribes to combined form snapshot then posts.
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(actx =>
            {
                Observable.CombineLatest(
                    host.Stream.GetDataStream<string>(displayNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(descriptionDataId).Take(1),
                    host.Stream.GetDataStream<string>(iconNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(orderDataId).Take(1),
                    host.Stream.GetDataStream<string>(childrenQueryDataId).Take(1),
                    host.Stream.GetDataStream<string>(dependenciesDataId).Take(1),
                    host.Stream.GetDataStream<string>(configurationDataId).Take(1),
                    host.Workspace.GetMeshNodeStream().Take(1),
                    (displayName, description, iconName, orderStr, childrenQuery, dependenciesStr, configuration, currentNode) =>
                    {
                        if (!int.TryParse(orderStr, out var order)) order = 0;
                        List<string>? dependencies = null;
                        if (!string.IsNullOrWhiteSpace(dependenciesStr))
                        {
                            dependencies = dependenciesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                            if (dependencies.Count == 0) dependencies = null;
                        }
                        var updatedDefinition = (content ?? new NodeTypeDefinition()) with
                        {
                            Description = string.IsNullOrWhiteSpace(description) ? null : description,
                            ChildrenQuery = string.IsNullOrWhiteSpace(childrenQuery) ? null : childrenQuery,
                            Dependencies = dependencies,
                            Configuration = string.IsNullOrWhiteSpace(configuration) ? null : configuration
                        };
                        if (currentNode == null) return null;
                        return currentNode with
                        {
                            Name = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                            Icon = string.IsNullOrWhiteSpace(iconName) ? null : iconName,
                            Order = order,
                            Content = updatedDefinition
                        };
                    })
                    .Take(1)
                    .Subscribe(updatedNode =>
                    {
                        if (updatedNode == null)
                        {
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown("**Error:** Could not find MeshNode to update."),
                                "Save Failed"
                            ).WithSize("M");
                            actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }
                        var delivery = actx.Host.Hub.Post(
                            new DataChangeRequest { ChangedBy = actx.Host.Stream.ClientId }.WithUpdates(updatedNode),
                            o => o.WithTarget(hubAddress))!;
                        actx.Host.Hub.Observe(delivery).Subscribe(
                            callbackResponse =>
                            {
                                if (callbackResponse.Message is not DataChangeResponse responseMsg)
                                {
                                    var errorDialog = Controls.Dialog(
                                        Controls.Markdown($"**Error saving:** Unexpected response `{callbackResponse.Message?.GetType().Name ?? "null"}`."),
                                        "Save Failed"
                                    ).WithSize("M");
                                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                                    return;
                                }
                                if (responseMsg.Log.Status != ActivityStatus.Succeeded)
                                {
                                    var errorDialog = Controls.Dialog(
                                        Controls.Markdown($"**Error saving:**\n\n{responseMsg.Log}"),
                                        "Save Failed"
                                    ).WithSize("M");
                                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                                    return;
                                }
                                var configNavHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
                                actx.Host.UpdateArea(actx.Area, new RedirectControl(configNavHref));
                            },
                            ex =>
                            {
                                var errorDialog = Controls.Dialog(
                                    Controls.Markdown($"**Error saving:**\n\n{ex.Message}"),
                                    "Save Failed"
                                ).WithSize("M");
                                actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            });
                    });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.Label($"{label}:").WithStyle("width: 150px; flex-shrink: 0; font-weight: 600;"))
            .WithView(Controls.Body(value));
    }

    /// <summary>
    /// Compile-state panel rendered at the top of <see cref="Overview"/>.
    /// One panel; three visual states driven by the NodeType's persisted
    /// fields (<see cref="NodeTypeDefinition.IsDirty"/>,
    /// <see cref="NodeTypeDefinition.CompilationStatus"/>,
    /// <see cref="NodeTypeDefinition.CompilationError"/>):
    /// <list type="bullet">
    ///   <item><b>Dirty</b>: amber chip + "Compile" button that flips
    ///     <see cref="NodeTypeDefinition.RequestedReleaseAt"/> via
    ///     <c>workspace.GetMeshNodeStream(path).Update(...)</c> — the
    ///     per-NodeType hub's <c>InstallReleaseRequestWatcher</c> picks
    ///     up the trigger and runs Roslyn.</item>
    ///   <item><b>Compiling</b>: spinner with link to the live activity
    ///     log (<see cref="NodeTypeDefinition.LastCompilationActivityPath"/>).</item>
    ///   <item><b>Error</b>: red banner with the formatted diagnostics +
    ///     "Compile" button so the user can retry once they've edited.</item>
    ///   <item><b>Ok &amp; not dirty</b>: subtle "Up to date" chip with the
    ///     release path (<see cref="NodeTypeDefinition.LatestReleasePath"/>).</item>
    /// </list>
    /// <para>Empty (no panel) when the NodeType has no source code yet — a
    /// NodeType with neither <see cref="NodeTypeDefinition.Configuration"/>
    /// nor <see cref="NodeTypeDefinition.HubConfiguration"/> nor
    /// <see cref="NodeTypeDefinition.Sources"/> never participates in
    /// compilation; a panel would be noise.</para>
    /// </summary>
    private static UiControl BuildCompileStatusPanel(LayoutAreaHost host, NodeTypeDefinition? def)
    {
        if (def is null) return Controls.Stack;

        var hasCode = !string.IsNullOrWhiteSpace(def.Configuration)
            || !string.IsNullOrWhiteSpace(def.HubConfiguration)
            || (def.CurrentSourceVersions?.Count ?? 0) > 0;
        if (!hasCode) return Controls.Stack;

        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();
        var status = def.CompilationStatus;
        var isDirty = def.IsDirty;
        // 🚨 2026-05-21 — kickoff was deleted, so a never-compiled NodeType
        // no longer auto-compiles on activation. The Compile button is the
        // sole entry point; render the "Never compiled" state so the user
        // has a visible affordance to trigger the first build. "Never
        // compiled" = no assembly metadata persisted (no AssemblyPath /
        // AssemblyCollection). We deliberately do NOT compare framework
        // versions at the layout layer (that's HasUsableBuild's concern in
        // NodeTypeCompilationHelpers); if a build exists at all, treat the
        // state as "Up to date" until status flips Dirty/Error/Compiling.
        var hasBuild =
            !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath);
        var neverCompiled = !hasBuild
            && status != CompilationStatus.Compiling
            && status != CompilationStatus.Error;

        var panel = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px; padding: 12px 16px; margin: 16px 0; border-radius: 6px; border: 1px solid var(--neutral-stroke-rest);");

        UiControl chip;
        var compileButtonEnabled = true;
        string compileButtonLabel;
        string panelStyleSuffix;

        if (status == CompilationStatus.Compiling)
        {
            chip = Controls.Body("Compiling…").WithStyle("font-weight: 600;");
            compileButtonLabel = "Compile";
            compileButtonEnabled = false;
            panelStyleSuffix = "background: var(--neutral-fill-stealth-rest);";
        }
        else if (status == CompilationStatus.Error)
        {
            chip = Controls.Body("Compilation failed").WithStyle("font-weight: 600; color: var(--error-foreground);");
            compileButtonLabel = "Retry compile";
            panelStyleSuffix = "background: var(--error-fill-rest); border-color: var(--error-stroke-rest);";
        }
        else if (neverCompiled)
        {
            chip = Controls.Body("Never compiled — click Compile to build")
                .WithStyle("font-weight: 600; color: var(--warning-foreground);");
            compileButtonLabel = "Compile";
            panelStyleSuffix = "background: var(--warning-fill-rest); border-color: var(--warning-stroke-rest);";
        }
        else if (isDirty)
        {
            chip = Controls.Body("Source changed — needs compile")
                .WithStyle("font-weight: 600; color: var(--warning-foreground);");
            compileButtonLabel = "Compile";
            panelStyleSuffix = "background: var(--warning-fill-rest); border-color: var(--warning-stroke-rest);";
        }
        else
        {
            chip = Controls.Body("Up to date").WithStyle("font-weight: 600;");
            compileButtonLabel = "Recompile";
            panelStyleSuffix = "background: var(--neutral-fill-stealth-rest);";
        }

        panel = panel.WithStyle("align-items: center; gap: 12px; padding: 12px 16px; margin: 16px 0; border-radius: 6px; border: 1px solid var(--neutral-stroke-rest); " + panelStyleSuffix);
        panel = panel.WithView(chip);

        // "Compile" button — flips RequestedReleaseAt to trigger the watcher.
        // RequestedReleaseForce=true bypasses the "no source changes since last
        // compile" short-circuit so the button always at least retries.
        var compileButton = Controls.Button(compileButtonLabel)
            .WithAppearance(compileButtonEnabled ? Appearance.Accent : Appearance.Stealth)
            .WithClickAction(clickCtx =>
            {
                if (!compileButtonEnabled) return Task.CompletedTask;
                var triggerAt = DateTimeOffset.UtcNow;
                host.Hub.GetWorkspace()
                    .GetMeshNodeStream(hubPath)
                    .Update(curr =>
                    {
                        if (curr?.Content is not NodeTypeDefinition cd) return curr!;
                        return curr with
                        {
                            Content = cd with
                            {
                                RequestedReleaseAt = triggerAt,
                                RequestedReleaseForce = true
                            }
                        };
                    })
                    .Subscribe(
                        _ => { },
                        ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning(ex, "Compile-trigger write failed for {Path}", hubPath));
                return Task.CompletedTask;
            });
        panel = panel.WithView(compileButton);

        // Optional: a "View latest release" link when one exists. Helps the
        // user follow Release ↔ Activity for full build-detail traceability
        // without leaving the Overview.
        if (!string.IsNullOrEmpty(def.LatestReleasePath))
        {
            var releaseSegment = def.LatestReleasePath!.Split('/').LastOrDefault() ?? "latest";
            panel = panel.WithView(
                Controls.Markdown($"[{releaseSegment}](/{def.LatestReleasePath!})")
                    .WithStyle("margin-left: auto; font-size: 12px;"));
        }

        return panel;
    }

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));

    /// <summary>
    /// Compile-log panel beneath the Create-Release header. Shows:
    /// <list type="bullet">
    ///   <item>While compiling: a "Compiling…" banner.</item>
    ///   <item>After failure: the formatted diagnostics from
    ///     <see cref="NodeTypeDefinition.CompilationError"/>.</item>
    ///   <item>After success: a link to the freshly-created
    ///     <c>Release</c> MeshNode at <see cref="NodeTypeDefinition.LatestReleasePath"/>.</item>
    ///   <item>A clickable link to the most recent compile activity log
    ///     (<see cref="NodeTypeDefinition.LastCompilationActivityPath"/>) for full
    ///     Roslyn output / executed-source-queries trace.</item>
    /// </list>
    /// Empty when nothing has happened yet.
    /// </summary>
    private static UiControl BuildCompileLogPanel(NodeTypeDefinition def)
    {
        // Nothing meaningful to show until at least one compile has been
        // requested or recorded. Return an empty stack so the layout area
        // doesn't reserve space for an unused panel.
        var hasState = def.CompilationStatus is not null
            || !string.IsNullOrEmpty(def.LastCompilationActivityPath)
            || !string.IsNullOrEmpty(def.LatestReleasePath);
        if (!hasState) return Controls.Stack;

        var panel = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 12px 16px; background: var(--neutral-layer-2); border-radius: 4px; gap: 8px; margin-bottom: 8px;");

        if (def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling)
        {
            panel = panel.WithView(Controls.Body("Compiling…")
                .WithStyle("color: var(--accent-fill-rest); font-weight: 600;"));
        }
        else if (def.CompilationStatus == CompilationStatus.Error
                 && !string.IsNullOrEmpty(def.CompilationError))
        {
            panel = panel
                .WithView(Controls.Body("Compile failed")
                    .WithStyle("color: var(--error); font-weight: 600;"))
                .WithView(Controls.Html(
                    $"<pre style=\"white-space: pre-wrap; font-family: monospace; font-size: 12px; color: var(--error); margin: 0;\">{System.Net.WebUtility.HtmlEncode(def.CompilationError)}</pre>"));
        }
        else if (def.CompilationStatus == CompilationStatus.Ok
                 && !string.IsNullOrEmpty(def.LatestReleasePath))
        {
            var releaseHref = "/" + def.LatestReleasePath;
            panel = panel
                .WithView(Controls.Body("Release published")
                    .WithStyle("color: var(--accent-fill-rest); font-weight: 600;"))
                .WithView(Controls.Html(
                    $"<a href=\"{System.Net.WebUtility.HtmlEncode(releaseHref)}\" style=\"text-decoration: none; color: var(--accent-fill-rest);\">→ {System.Net.WebUtility.HtmlEncode(def.LatestReleasePath!)}</a>"));
        }

        if (!string.IsNullOrEmpty(def.LastCompilationActivityPath))
        {
            var activityHref = "/" + def.LastCompilationActivityPath;
            panel = panel.WithView(Controls.Html(
                $"<a href=\"{System.Net.WebUtility.HtmlEncode(activityHref)}\" style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">View full compile log →</a>"));
        }

        return panel;
    }
}

/// <summary>
/// Form DTO for the NodeType Configuration pane — carries the subset of MeshNode
/// and NodeTypeDefinition fields that the user can edit directly inline (Name, Icon,
/// Description, ChildrenQuery, DefaultNamespace, PageMaxWidth). The Configuration
/// lambda and Dependencies are edited in the dedicated HubConfigEdit Monaco view.
/// </summary>
public record NodeTypeConfigForm
{
    /// <summary>The display name of the NodeType.</summary>
    public string? Name { get; init; }
    /// <summary>The icon associated with the NodeType.</summary>
    public string? Icon { get; init; }
    /// <summary>The human-readable description of the NodeType.</summary>
    public string? Description { get; init; }
    /// <summary>The query used to list instances/children of this NodeType.</summary>
    public string? ChildrenQuery { get; init; }
    /// <summary>The default namespace applied to new instances of this NodeType.</summary>
    public string? DefaultNamespace { get; init; }
    /// <summary>The maximum page width used when rendering instances of this NodeType.</summary>
    public string? PageMaxWidth { get; init; }
    /// <summary>The pending release notes for the next NodeType release.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Builds a NodeTypeConfigForm from a mesh node and its optional NodeType definition.
    /// </summary>
    /// <param name="node">The mesh node to read values from.</param>
    /// <param name="def">The optional NodeType definition to read definition-level values from; may be null.</param>
    /// <returns>The populated configuration form.</returns>
    public static NodeTypeConfigForm FromNode(MeshNode node, NodeTypeDefinition? def) => new()
    {
        Name = node.Name,
        Icon = node.Icon,
        Description = def?.Description,
        ChildrenQuery = def?.ChildrenQuery,
        DefaultNamespace = def?.DefaultNamespace,
        PageMaxWidth = def?.PageMaxWidth,
        ReleaseNotes = def?.ReleaseNotes,
    };
}
