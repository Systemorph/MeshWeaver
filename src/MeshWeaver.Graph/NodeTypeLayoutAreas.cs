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
    public const string SearchArea = "Search";
    public const string OverviewArea = "Overview";
    public const string ConfigurationArea = "Configuration";
    public const string HubConfigViewArea = "HubConfig";
    public const string HubConfigEditArea = "HubConfigEdit";
    public const string ReleasesArea = "Releases";

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
                .WithView(ProgressArea, Progress)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                .WithView(MeshNodeLayoutAreas.ModelArea, DataModelLayoutArea.DataModel));

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
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not NodeTypeDefinition def)
                    return (UiControl?)Controls.Markdown(
                        "*This node has no NodeTypeDefinition — nothing to compile.*");

                var (icon, header, body) = RenderProgressLines(def);
                var stack = Controls.Stack
                    .WithStyle("padding: 12px; gap: 8px;")
                    .WithView(Controls.Markdown($"### {icon} {header}"));
                if (!string.IsNullOrEmpty(body))
                    stack = stack.WithView(Controls.Markdown(body));

                // Live activity log — embed the activity hub's existing
                // ProgressArea so Roslyn diagnostics stream in line by line.
                // Only render while we have an activity path AND the compile
                // hasn't terminally settled to Ok (post-success the activity
                // is history, served via the Releases tab).
                if (!string.IsNullOrEmpty(def.LastCompilationActivityPath)
                    && def.CompilationStatus != CompilationStatus.Ok)
                {
                    stack = stack.WithView(new LayoutAreaControl(
                            new Address(def.LastCompilationActivityPath!),
                            new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                        .WithStyle("margin-top: 8px; padding: 12px; background: var(--neutral-layer-3); border-radius: 4px; min-height: 48px;"));
                }
                return (UiControl?)stack;
            });
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

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                (h, c) => GetNodeStream(host)
                    .CombineLatest(sourceGroupsStream, testGroupsStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (node, sourceGroups, testGroups, nodeTypes, agents) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        return BuildSideMenu(hubAddress, node, sourceGroups, testGroups, nodeTypes, agents);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                (h, c) => mainContent(h, c),
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        return GetNodeStream(host)
            .Select(node =>
            {
                if (node == null || meshQuery == null)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var def = node.Content as NodeTypeDefinition;
                var groups = tests
                    ? CodeQueryResolver.GroupAll(def?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)
                    : CodeQueryResolver.GroupAll(def?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName);
                if (groups.Count == 0)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var streams = groups.Select(g =>
                    RunQueries(meshQuery, g.ExpandedQueries).Select(nodes => (Group: g, Nodes: nodes)));
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NodeTypeLayoutAreas));
        return meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Select(change => (IReadOnlyList<MeshNode>)change.Items)
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
                var typeDef = node.Content as NodeTypeDefinition;

                var content = Controls.Stack.WithWidth("100%")
                    .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, typeDef)
                        + " padding-top: 8px; padding-bottom: 32px; gap: 8px; overflow-y: auto; height: 100%;");
                content = content.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

                // Compile-state banner + Compile button — the "ability to compile"
                // affordance on the landing page. Bound to IsDirty / CompilationStatus.
                content = content.WithView(BuildCompileStatusPanel(host, typeDef));

                // Markdown description from the mesh node's NodeTypeDefinition.
                if (!string.IsNullOrEmpty(typeDef?.Description))
                    content = content.WithView(Controls.Markdown(typeDef.Description));

                content = content.WithView(BuildConfigurationSection(hubAddress, node, typeDef));
                content = content.WithView(BuildQueriesSection("Source queries",
                    CodeQueryResolver.GroupAll(typeDef?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName)));
                content = content.WithView(BuildQueriesSection("Test queries",
                    CodeQueryResolver.GroupAll(typeDef?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)));
                content = content.WithView(BuildLatestReleasesSection(hubAddress, releases));

                return (UiControl?)content;
            });
    }

    private static UiControl BuildSectionHeader(string title, string? href = null, string? linkLabel = null)
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

        var hasAny = false;
        void AddRow(ref LayoutStackControl s, string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            s = (LayoutStackControl)s.WithView(BuildInfoRow(label, value!));
            hasAny = true;
        }

        var stack = section;
        AddRow(ref stack, "Default Namespace", def?.DefaultNamespace);
        AddRow(ref stack, "Children Query", def?.ChildrenQuery);
        AddRow(ref stack, "Storage Table", def?.StorageTable);
        AddRow(ref stack, "Owns Partition", def?.OwnsPartition == true ? "Yes" : null);
        AddRow(ref stack, "Page Max Width", def?.PageMaxWidth);
        AddRow(ref stack, "Dependencies", def?.Dependencies is { Count: > 0 } deps ? string.Join(", ", deps) : null);
        AddRow(ref stack, "Configuration Lambda", string.IsNullOrWhiteSpace(def?.Configuration) ? null : "Defined");

        if (!hasAny)
            stack = (LayoutStackControl)stack.WithView(Controls.Body("All settings at their defaults.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        return stack;
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
    private static UiControl BuildLatestReleasesSection(object hubAddress, IReadOnlyList<MeshNode> releaseNodes)
    {
        var releasesHref = new LayoutAreaReference(ReleasesArea).ToHref(hubAddress);
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader("Latest releases", releasesHref, "All releases"));

        var latest = releaseNodes
            .Select(n => (Node: n, Release: n.Content as NodeTypeRelease))
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
    /// Renders the Configuration area for a NodeType.
    /// Split view with left navigation menu and main configuration pane.
    /// Code files are listed as navigation links to their own Code node addresses.
    /// </summary>
    public static UiControl Configuration(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        var definitionStream = GetNodeStream(host);

        // Resolve Sources / Tests via CodeQueryResolver — the same queries the
        // compiler runs — so the side-menu listing is guaranteed to match what
        // actually compiles. Each emission re-resolves in case the NodeType's
        // definition changed.
        var sourcesNodesStream = definitionStream
            .Select(node =>
            {
                if (node == null || meshQuery == null)
                    return Observable.Return(Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>);
                var def = node.Content as NodeTypeDefinition;
                return RunQueries(meshQuery,
                    CodeQueryResolver.ExpandAll(def?.Sources, CodeQueryResolver.DefaultSources, node.Path));
            })
            .Switch();

        var testsNodesStream = definitionStream
            .Select(node =>
            {
                if (node == null || meshQuery == null)
                    return Observable.Return(Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>);
                var def = node.Content as NodeTypeDefinition;
                return RunQueries(meshQuery,
                    CodeQueryResolver.ExpandAll(def?.Tests, CodeQueryResolver.DefaultTests, node.Path));
            })
            .Switch();

        var nodeTypeLogger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeLayoutAreas");

        // Live observable of NodeType nodes under this namespace.
        // Catch logs and re-emits empty so a transient query failure degrades
        // the menu to "no nodeTypes" rather than blanking the whole layout —
        // BUT it logs at Warning so silent timeouts (which previously hid CI
        // hangs) surface in test output instead of disappearing.
        var nodeTypesStream = meshQuery == null
            ? Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())
            : meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{hubPath} nodeType:NodeType scope:descendants"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(ex =>
                {
                    nodeTypeLogger?.LogWarning(ex,
                        "NodeType query failed for path={Path}; falling back to empty list",
                        hubPath);
                    return Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>());
                });

        // Live observable of Agent nodes under this namespace.
        var agentsStream = meshQuery == null
            ? Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())
            : meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{hubPath} nodeType:Agent scope:descendants"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(ex =>
                {
                    nodeTypeLogger?.LogWarning(ex,
                        "Agent query failed for path={Path}; falling back to empty list",
                        hubPath);
                    return Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>());
                });

        // Return static Splitter structure with observable nested views
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left menu - observable, updates when definition or resolved code lists change
                (h, c) => definitionStream
                    .CombineLatest(sourcesNodesStream, testsNodesStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (definition, sources, tests, nodeTypes, agents) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildLeftMenu(host, hubAddress, definition, sources, tests, nodeTypes, agents);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Main pane - shows configuration
                (h, c) => definitionStream
                    .Select(definition =>
                    {
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildConfigurationPane(host, hubAddress, definition);
                    }),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with Configuration, Code files, Node Types, and Agents entries.
    /// Sources / Tests lists are the fully resolved outputs of the NodeType's source queries (or the
    /// defaults), so the user sees exactly what compiles — including shared code pulled in from
    /// other namespaces via <c>@path</c> shorthand or foreign <c>namespace:</c> queries.
    /// Search link is placed at the bottom so the MeshSearch children listing is reachable from there
    /// without visually competing with the configuration-focused entries at the top.
    /// </summary>
    private static UiControl BuildLeftMenu(
        LayoutAreaHost host,
        object hubAddress,
        MeshNode node,
        IReadOnlyCollection<MeshNode>? sources,
        IReadOnlyCollection<MeshNode>? tests,
        IReadOnlyCollection<MeshNode>? nodeTypes = null,
        IReadOnlyCollection<MeshNode>? agents = null)
    {
        var content = node.Content as NodeTypeDefinition;
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false))
            .WithStyle("overflow-y: auto; height: 100%;");

        // Configuration link at top — it's the landing area for this view.
        var configHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Configuration", FluentIcons.Settings(), configHref)
        );

        // Sources + Tests sections — hierarchical trees of whatever the configured
        // source/test queries resolved to. Each file is displayed at its relative
        // path under {node.Path}; foreign files (shared code from other namespaces)
        // are shown with their full absolute path so their origin is visible.
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Sources", FluentIcons.Code(), node.Path, sources));
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Tests", FluentIcons.Beaker(), node.Path, tests));

        // Node Types section (if any NodeType nodes exist under this namespace)
        if (nodeTypes != null && nodeTypes.Count > 0)
        {
            var typesGroup = new NavGroupControl("Node Types")
                .WithIcon(FluentIcons.Document())
                .WithSkin(s => s.WithExpanded(true));

            foreach (var typeNode in nodeTypes.OrderBy(n => n.Order).ThenBy(n => n.Name))
            {
                var typeHref = $"/{typeNode.Path}";
                typesGroup = typesGroup.WithView(
                    new NavLinkControl(typeNode.Name ?? typeNode.Id, FluentIcons.DocumentText(), typeHref)
                );
            }

            navMenu = navMenu.WithNavGroup(typesGroup);
        }

        // Agents section (if any Agent nodes exist under this namespace)
        if (agents != null && agents.Count > 0)
        {
            var agentsGroup = new NavGroupControl("Agents")
                .WithIcon(FluentIcons.Bot())
                .WithSkin(s => s.WithExpanded(true));

            foreach (var agentNode in agents.OrderBy(n => n.Order).ThenBy(n => n.Name))
            {
                var agentHref = $"/{agentNode.Path}";
                agentsGroup = agentsGroup.WithView(
                    new NavLinkControl(agentNode.Name ?? agentNode.Id, FluentIcons.Bot(), agentHref)
                );
            }

            navMenu = navMenu.WithNavGroup(agentsGroup);
        }

        // Dependencies section (if any)
        if (content?.Dependencies != null && content.Dependencies.Count > 0)
        {
            var depsGroup = new NavGroupControl("Dependencies")
                .WithIcon(FluentIcons.Link())
                .WithSkin(s => s.WithExpanded(false));

            foreach (var dep in content.Dependencies)
            {
                depsGroup = depsGroup.WithView(
                    Controls.Body(dep).WithStyle("padding: 4px 16px; display: block;")
                );
            }

            navMenu = navMenu.WithNavGroup(depsGroup);
        }

        // Releases — list of compile activities for this NodeType. Each compile
        // (success or failure) writes an ActivityLog under {nodeTypePath}/_activity/{logId};
        // the Releases pane lists them with status + timestamp + a link to drill in.
        var releasesHref = new LayoutAreaReference(ReleasesArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Releases", FluentIcons.History(), releasesHref)
        );

        // Search at the bottom — inlines with the MeshSearch children listing.
        var searchHref = new LayoutAreaReference(SearchArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Search", FluentIcons.Search(), searchHref)
        );

        return navMenu;
    }

    /// <summary>
    /// Builds a hierarchical navigation group for a resolved Sources or Tests list. Files
    /// whose path starts with <c>{rootPath}/</c> are displayed at their relative path
    /// (folders by namespace segment); files OUTSIDE <c>{rootPath}</c> — shared code pulled
    /// in via <c>@path</c> or cross-NodeType <c>namespace:</c> queries — are displayed under
    /// a "(shared)" folder at their absolute path so their origin remains obvious.
    /// </summary>
    internal static NavGroupControl BuildCodeNavGroup(
        string groupLabel,
        Icon groupIcon,
        string rootPath,
        IReadOnlyCollection<MeshNode>? codeNodes)
    {
        var root = new NavGroupControl(groupLabel)
            .WithIcon(groupIcon)
            .WithSkin(s => s.WithExpanded(true));

        if (codeNodes == null || codeNodes.Count == 0)
        {
            return root.WithView(
                Controls.Body($"No {groupLabel.ToLowerInvariant()} yet")
                    .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
        }

        var tree = BuildCodeTreeForNavigation(rootPath, codeNodes);
        foreach (var child in tree.OrderedChildren())
            root = AppendCodeTreeNode(root, child);

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
    /// Runs a sequence of expanded queries via <see cref="IMeshService"/> and returns
    /// the de-duplicated MeshNode results. Empty input → empty result, so the default
    /// "no sources/tests yet" state still renders cleanly.
    /// </summary>
    // Reactive (no async/FromAsync): each query is already an IObservable; compose
    // them subscribe-all-upfront via CombineLatest (per AsynchronousCalls.md) and
    // fold the positional results into one deduped list. Each per-query stream
    // Take(1)s its initial set and Catches to empty, so one bad query can't blank
    // the list and CombineLatest still fires + completes once every query emitted.
    private static IObservable<IReadOnlyList<MeshNode>> RunQueries(
        IMeshService meshQuery,
        IEnumerable<string> queries)
    {
        var queryList = queries.ToList();
        if (queryList.Count == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        var streams = queryList.Select(q =>
            meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(q))
                .Take(1)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items)
                // A stray query syntax error in one entry shouldn't empty the whole list.
                .Catch<IReadOnlyList<MeshNode>, Exception>(
                    _ => Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())));

        return Observable.CombineLatest(streams)
            .Select(lists =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var results = new List<MeshNode>();
                foreach (var list in lists)
                    foreach (var n in list)
                        if (n?.Path is { Length: > 0 } p && seen.Add(p))
                            results.Add(n);
                return (IReadOnlyList<MeshNode>)results;
            });
    }

    private static NavGroupControl AppendCodeTreeNode(NavGroupControl parent, CodeTreeNode node)
    {
        if (node is CodeTreeLeaf leaf)
        {
            var href = new LayoutAreaReference(CodeLayoutAreas.OverviewArea).ToHref(leaf.Node.Path);
            return parent.WithView(new NavLinkControl(leaf.Node.Name ?? leaf.Node.Id, CustomIcons.CSharp(), href));
        }

        var folder = (CodeTreeFolder)node;
        var group = new NavGroupControl(folder.Name)
            .WithIcon(FluentIcons.Folder())
            .WithSkin(s => s.WithExpanded(true));
        foreach (var child in folder.OrderedChildren())
            group = AppendCodeTreeNode(group, child);
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
    /// Builds the main Configuration pane: an editable settings form for the NodeType
    /// (Name, Description, Icon, ChildrenQuery, DefaultNamespace, PageMaxWidth) with
    /// auto-save, plus a read-only preview of the Configuration lambda with an Edit
    /// button that opens the dedicated Monaco editor.
    /// </summary>
    /// <summary>
    /// Renders the Releases area for a NodeType — the chronological list of
    /// compile activities (one per "Create Release" click). Each entry shows
    /// the release status, timestamp, the user who triggered it, and any
    /// release notes captured on the activity log. Compile state lives at
    /// <c>{nodeTypePath}/_activity/{logId}</c> and is written by the
    /// CompileWatcher in <c>MeshDataSource.InstallCompileWatcher</c> on every
    /// success-or-failure transition.
    /// </summary>
    [Browsable(false)]
    public static UiControl Releases(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Live observable of activity nodes under {nodeTypePath}/_activity. We
        // filter to category=Compilation client-side because the activity store
        // keeps every category (compile, save, delete) in the same satellite
        // table — releases are only the compile entries.
        var activityStream = meshQuery == null
            ? Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())
            : meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{hubPath}/_activity nodeType:Activity"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(_ =>
                    Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal)
                .WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                (h, c) => GetNodeStream(host)
                    .CombineLatest(activityStream, (def, list) => (def, list))
                    .Select(t => t.def == null
                        ? RenderLoading("Loading…")
                        : BuildLeftMenu(host, hubAddress, t.def, null, null, null, null)),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                (h, c) => activityStream.Select(items => BuildReleasesPane(hubAddress, items)),
                skin => skin.WithSize("*")
            );
    }

    private static UiControl BuildReleasesPane(object hubAddress, IReadOnlyList<MeshNode> activities)
    {
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 16px;");

        stack = stack.WithView(Controls.H2("Releases").WithStyle("margin: 0;"));
        stack = stack.WithView(Controls.Body(
            "Every Create Release click writes an activity log here — successes, " +
            "failures, and the executed-source-queries trace. Click any row to " +
            "drill into the full log.")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

        // Filter to compile activities and order newest-first. Other categories
        // (e.g. data-change activities) live in the same _activity namespace but
        // aren't releases. ActivityLog.Category is the only reliable filter —
        // path/id naming isn't enforced.
        var compileActivities = activities
            .Where(n => n.Content is ActivityLog log && log.Category == ActivityCategory.Compilation)
            .OrderByDescending(n => (n.Content as ActivityLog)?.Start ?? DateTime.MinValue)
            .ToList();

        if (compileActivities.Count == 0)
        {
            stack = stack.WithView(Controls.Body(
                "No releases yet. Click 'Create Release' on the Configuration view " +
                "to compile this NodeType — the activity will appear here.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
            return stack;
        }

        // One row per release: status badge, user, timestamp, notes excerpt,
        // navigate-to link to the full activity log.
        foreach (var activityNode in compileActivities)
        {
            if (activityNode.Content is not ActivityLog log)
                continue;

            var statusLabel = log.Status switch
            {
                ActivityStatus.Failed => "Error",
                ActivityStatus.Warning => "Warning",
                _ => "Ok"
            };
            var statusColor = log.Status switch
            {
                ActivityStatus.Failed => "var(--error)",
                ActivityStatus.Warning => "var(--warning)",
                _ => "var(--accent-fill-rest)"
            };
            var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";

            // Release-notes messages: every Info message prefixed "Release
            // notes: " written by the Create Release click. We render every
            // matching line so a multi-line note is preserved verbatim.
            const string notesPrefix = "Release notes:";
            var notesLines = log.Messages
                .Where(m => m.Message.StartsWith(notesPrefix, StringComparison.Ordinal))
                .Select(m => m.Message[notesPrefix.Length..].Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            var notes = string.Join("\n", notesLines);

            var activityHref = $"/{activityNode.Path}";
            var rowHtml = $"<a href=\"{System.Net.WebUtility.HtmlEncode(activityHref)}\" " +
                $"style=\"display: block; padding: 12px 16px; margin-bottom: 8px; " +
                $"background: var(--neutral-layer-2); border-radius: 4px; " +
                $"text-decoration: none; color: inherit; border-left: 3px solid {statusColor};\">" +
                $"<div style=\"display: flex; align-items: center; gap: 12px;\">" +
                $"<span style=\"font-weight: 600; padding: 2px 10px; border-radius: 12px; " +
                $"background: {statusColor}20; color: {statusColor}; font-size: 0.85rem;\">" +
                $"{System.Net.WebUtility.HtmlEncode(statusLabel)}</span>" +
                $"<span style=\"flex: 1;\">{System.Net.WebUtility.HtmlEncode(userName)}</span>" +
                $"<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">" +
                $"{log.Start:g}</span>" +
                $"</div>";

            if (!string.IsNullOrWhiteSpace(notes))
            {
                rowHtml += $"<div style=\"margin-top: 6px; color: var(--neutral-foreground); " +
                    $"font-size: 0.9rem; line-height: 1.4;\">" +
                    $"{System.Net.WebUtility.HtmlEncode(notes)}</div>";
            }

            rowHtml += "</a>";
            stack = stack.WithView(Controls.Html(rowHtml));
        }

        return stack;
    }

    private static UiControl BuildConfigurationPane(LayoutAreaHost host, object hubAddress, MeshNode node)
    {
        var definition = node.Content as NodeTypeDefinition;
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto; gap: 20px;");

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
                MeshDataSourceExtensions.IsSourcesUpToDate(ownNode?.Content as NodeTypeDefinition, sources.Items))
            .DistinctUntilChanged();

        var releaseButton = (LayoutAreaHost h, RenderingContext rc) => isUpToDate
            .Select(upToDate => (UiControl)Controls.Button(upToDate ? "Up to Date" : "Create Release")
                .WithAppearance(upToDate ? Appearance.Neutral : Appearance.Accent)
                .WithIconStart(FluentIcons.Play())
                .WithClickAction(ctx =>
                {
                    // Canonical request-via-stream-update trigger — flips
                    // `RequestedReleaseAt` + `RequestedReleaseForce` on the
                    // NodeType's own MeshNode. The per-NodeType hub's
                    // `InstallReleaseRequestWatcher` observes the timestamp
                    // moving past `LastReleaseRequestHandledAt` and flips
                    // `CompilationStatus = Pending`; `InstallCompileWatcher`
                    // then runs Roslyn. No bespoke `CreateReleaseRequest`
                    // round-trip — the trigger and observation flow through
                    // the same node state every other subscriber already
                    // watches. See `Doc/Architecture/RequestViaStreamUpdate.md`.
                    ctx.Host.Workspace.GetMeshNodeStream(nodeTypePath).Update(curr =>
                    {
                        if (curr?.Content is not NodeTypeDefinition def) return curr!;
                        return curr with
                        {
                            Content = def with
                            {
                                RequestedReleaseAt = DateTimeOffset.UtcNow,
                                RequestedReleaseForce = upToDate,
                            }
                        };
                    }).Subscribe(
                        _ => { },
                        ex => ctx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning(ex, "Release-request write failed for {Path}", nodeTypePath));
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
            .Select(n => (n?.Content as NodeTypeDefinition)?.CompilationStatus)
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
                .Select(n => n?.Content as NodeTypeDefinition)
                .Where(d => d is not null)
                .Select(d => BuildCompileLogPanel(d!));
        stack = stack.WithView(compileLogPanel, "CompileLogPanel");

        // Editable settings form — auto-saves to MeshNode.Content as NodeTypeDefinition.
        var dataId = $"nodeTypeConfig_{node.Path.Replace('/', '_')}";
        var form = NodeTypeConfigForm.FromNode(node, definition);
        host.UpdateData(dataId, form);
        SetupNodeTypeConfigAutoSave(host, dataId, form, node, definition);

        var dataPointer = LayoutAreaReference.GetDataPointer(dataId);
        var formGrid = Controls.Stack
            .WithStyle("display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px;");

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeConfigForm.Name)))
        {
            Label = "Display Name",
            Immediate = true,
            DataContext = dataPointer
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeConfigForm.Icon)))
        {
            Label = "Icon",
            Placeholder = "content:icon.svg, /static/…, <svg>…</svg>, or URL",
            Immediate = true,
            DataContext = dataPointer
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeConfigForm.ChildrenQuery)))
        {
            Label = "Children Query",
            Placeholder = "e.g. nodeType:Person scope:descendants",
            Immediate = true,
            DataContext = dataPointer
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeConfigForm.DefaultNamespace)))
        {
            Label = "Default Namespace",
            Placeholder = "Pre-selected namespace in Create form",
            Immediate = true,
            DataContext = dataPointer
        });

        formGrid = formGrid.WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeConfigForm.PageMaxWidth)))
        {
            Label = "Page Max Width",
            Placeholder = "e.g. 1200px or 100%",
            Immediate = true,
            DataContext = dataPointer
        });

        stack = stack.WithView(formGrid);

        stack = stack.WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeConfigForm.Description)))
        {
            Label = "Description",
            Placeholder = "Long-form description shown in the Overview and Create dialog.",
            Immediate = true,
            DataContext = dataPointer
        }.WithRows(4));

        // Release notes — what changed in the next compile. The form-debounce
        // (SetupNodeTypeConfigAutoSave) writes this onto NodeTypeDefinition.
        // ReleaseNotes via stream.UpdateMeshNode the moment the user stops
        // typing; the Create Release click reads no data — it just flips
        // CompilationStatus to Pending. Pure stream wiring, no Take(1).
        stack = stack.WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeConfigForm.ReleaseNotes)))
        {
            Label = "Release notes",
            Placeholder = "What changed in the next compile? Shown on each row in the Releases pane.",
            Immediate = true,
            DataContext = dataPointer
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
    /// Debounced autosave for the NodeType Configuration form. On changes, writes
    /// the form values back through <c>UpdateMeshNode</c>
    /// so the edits flow through the live MeshNode stream (GetStream on <see cref="MeshNodeReference"/>)
    /// — the standard reactive write path. Using <c>UpdateNodeRequest</c> targeted at the local hub
    /// address would skip the stream patch and leave subscribed views showing stale state.
    /// No <c>await</c>: composed via Subscribe.
    /// </summary>
    private static void SetupNodeTypeConfigAutoSave(
        LayoutAreaHost host,
        string dataId,
        NodeTypeConfigForm initial,
        MeshNode node,
        NodeTypeDefinition? originalDefinition)
    {
        var current = (object)initial;

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<object>(dataId)
                .Throttle(TimeSpan.FromMilliseconds(400))
                .Subscribe(updated =>
                {
                    if (Equals(current, updated)) return;
                    current = updated;
                    if (updated is not NodeTypeConfigForm form) return;

                    // Write through the live stream — this is what GetStream(new MeshNodeReference())
                    // subscribers observe. UpdateMeshNode reads the latest node, applies the lambda,
                    // and emits a patch on the MeshNode data-source stream.
                    var saveLogger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Graph.NodeTypeLayoutAreas");
                    host.Workspace.GetMeshNodeStream(node.Path).Update(liveNode =>
                    {
                        var baseDef = (liveNode.Content as NodeTypeDefinition)
                            ?? originalDefinition
                            ?? new NodeTypeDefinition();
                        var nextDefinition = baseDef with
                        {
                            Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description,
                            ChildrenQuery = string.IsNullOrWhiteSpace(form.ChildrenQuery) ? null : form.ChildrenQuery,
                            DefaultNamespace = string.IsNullOrWhiteSpace(form.DefaultNamespace) ? null : form.DefaultNamespace,
                            PageMaxWidth = string.IsNullOrWhiteSpace(form.PageMaxWidth) ? null : form.PageMaxWidth,
                            ReleaseNotes = string.IsNullOrWhiteSpace(form.ReleaseNotes) ? null : form.ReleaseNotes,
                        };
                        return liveNode with
                        {
                            Name = string.IsNullOrWhiteSpace(form.Name) ? liveNode.Name : form.Name,
                            Icon = string.IsNullOrWhiteSpace(form.Icon) ? null : form.Icon,
                            Content = nextDefinition
                        };
                    }).Subscribe(
                        _ => { },
                        ex => saveLogger?.LogWarning(ex,
                            "SetupNodeTypeConfigAutoSave: UpdateMeshNode failed for {NodePath}", node.Path));
                }));
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
        var content = node.Content as NodeTypeDefinition;
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
        var content = node.Content as NodeTypeDefinition;
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

    /// <summary>
    /// Builds the Create href for a NodeType page.
    /// Type defaults to the hub's own type path.
    /// Namespace uses DefaultNamespace if explicitly set; omitted otherwise
    /// so the Create form uses the current browsing context.
    /// </summary>
    private static string BuildCreateHref(string hubPath, NodeTypeDefinition? typeDef)
    {
        var qs = $"type={Uri.EscapeDataString(hubPath)}";
        if (typeDef?.DefaultNamespace != null)
            qs += $"&namespace={Uri.EscapeDataString(typeDef.DefaultNamespace)}";
        if (typeDef?.RestrictedToNamespaces is { Count: > 0 } nsRestrictions)
            qs += $"&namespaces={string.Join(",", nsRestrictions.Select(Uri.EscapeDataString))}";
        return $"/create?{qs}";
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
    public string? Name { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public string? ChildrenQuery { get; init; }
    public string? DefaultNamespace { get; init; }
    public string? PageMaxWidth { get; init; }
    public string? ReleaseNotes { get; init; }

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
