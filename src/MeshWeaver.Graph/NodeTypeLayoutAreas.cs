using System.ComponentModel;
using System.Reactive.Linq;
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

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFileDataId = "codeFile";
    private const string CodeNodesDataId = "codeNodes";

    /// <summary>
    /// Gets the current MeshNode from the workspace stream.
    /// </summary>
    private static IObservable<MeshNode?> GetNodeStream(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());
        return nodeStream.Select(nodes => nodes.FirstOrDefault(n => n.Path == hubPath));
    }

    /// <summary>
    /// Adds NodeType catalog views plus all standard node views (Settings, Files, Threads, Chat,
    /// AccessControl, etc.). Use this for NodeType definitions that should also support
    /// the full node management experience (e.g., Organization, custom domain types).
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddNodeTypeView()
            .AddLayout(layout => layout.WithDefaultArea(SearchArea));

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// Uses the standard MeshNodeLayoutAreas.Search with NodeTypeCatalogMode to dynamically query instances.
    /// Includes UCR areas ($Data, $Schema, $Model) for unified content references.
    /// Note: $Content is registered by ContentCollectionsExtensions.AddContentCollections.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration
            .Set(new NodeTypeCatalogMode())  // Enable NodeType catalog mode
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(MeshNodeLayoutAreas.OverviewArea, ListOverview)  // Override default Overview for listings
                .WithView(SearchArea, MeshNodeLayoutAreas.Search)  // Use standard search
                .WithView(OverviewArea, Overview)
                .WithView(ConfigurationArea, Configuration)
                .WithView(HubConfigViewArea, HubConfigView)
                .WithView(HubConfigEditArea, HubConfigEdit)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                .WithView(MeshNodeLayoutAreas.ModelArea, DataModelLayoutArea.DataModel));

    /// <summary>
    /// List overview for NodeType nodes - used in search results and listings.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> ListOverview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        // Map nodes and extract NodeTypeDefinition from Content
        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);

            // Extract NodeTypeDefinition from MeshNode.Content
            NodeTypeDefinition? typeDef = null;
            if (node?.Content != null)
            {
                typeDef = node.Content as NodeTypeDefinition;
            }

            return host.BuildNodeTypeDetailsContent(node, typeDef);
        });
    }

    /// <summary>
    /// Builds details content for NodeType nodes with ShowChildrenInDetails support.
    /// </summary>
    private static UiControl BuildNodeTypeDetailsContent(this LayoutAreaHost host, MeshNode? node, NodeTypeDefinition? typeDef)
    {
        // Delegate to the shared BuildDetailsContent which now uses a gear icon
        return host.BuildDetailsContent(node, typeDef);
    }

    /// <summary>
    /// Renders the Overview area for a NodeType.
    /// Shows the markdown Description from NodeTypeDefinition, followed by
    /// the children (instances) of this type with a search bar.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var definitionStream = GetNodeStream(host);

        return definitionStream.Select(node =>
        {
            var typeDef = node?.Content as NodeTypeDefinition;

            var outer = Controls.Stack.WithWidth("100%");

            // Header
            var content = Controls.Stack.WithWidth("100%")
                .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, typeDef));
            content = content.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

            // Markdown Description
            if (!string.IsNullOrEmpty(typeDef?.Description))
            {
                content = content.WithView(Controls.Markdown(typeDef.Description));
            }

            outer = outer.WithView(content);

            // Children — use MeshSearch directly (ChildrenArea is not registered for NodeType hubs)
            var hubPath = host.Hub.Address.ToString();
            outer = outer.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, typeDef) + " margin-top: 32px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);")
                    .WithView(Controls.MeshSearch
                        .WithHiddenQuery(typeDef?.DefaultNamespace != null
                            ? $"nodeType:{hubPath} namespace:{typeDef.DefaultNamespace}"
                            : $"nodeType:{hubPath} namespace:{hubPath} scope:descendants")
                        .WithShowSearchBox(false)
                        .WithShowEmptyMessage(false)
                        .WithShowLoadingIndicator(false)
                        .WithRenderMode(MeshSearchRenderMode.Grouped)
                        .WithSectionCounts(true)
                        .WithItemLimit(50)
                        .WithMaxRows(3)
                        .WithCollapsibleSections(true)
                        .WithCreateHref(BuildCreateHref(hubPath, typeDef))));

            return (UiControl?)outer;
        });
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
                return Observable.FromAsync(token => RunQueriesAsync(meshQuery,
                    CodeQueryResolver.ExpandAll(def?.Sources, CodeQueryResolver.DefaultSources, node.Path),
                    token));
            })
            .Switch();

        var testsNodesStream = definitionStream
            .Select(node =>
            {
                if (node == null || meshQuery == null)
                    return Observable.Return(Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>);
                var def = node.Content as NodeTypeDefinition;
                return Observable.FromAsync(token => RunQueriesAsync(meshQuery,
                    CodeQueryResolver.ExpandAll(def?.Tests, CodeQueryResolver.DefaultTests, node.Path),
                    token));
            })
            .Switch();

        // Live observable of NodeType nodes under this namespace.
        var nodeTypesStream = meshQuery == null
            ? Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())
            : meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{hubPath} nodeType:NodeType scope:descendants"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(_ => Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));

        // Live observable of Agent nodes under this namespace.
        var agentsStream = meshQuery == null
            ? Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>())
            : meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{hubPath} nodeType:Agent scope:descendants"))
                .Select(change => (IReadOnlyList<MeshNode>)change.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(_ => Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));

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
    private static async Task<IReadOnlyList<MeshNode>> RunQueriesAsync(
        IMeshService meshQuery,
        IEnumerable<string> queries,
        CancellationToken ct)
    {
        var results = new List<MeshNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in queries)
        {
            try
            {
                await foreach (var n in meshQuery.QueryAsync<MeshNode>(q, ct: ct).WithCancellation(ct))
                {
                    if (n?.Path is { Length: > 0 } p && seen.Add(p))
                        results.Add(n);
                }
            }
            catch
            {
                // A stray query syntax error in one entry shouldn't empty the whole list.
            }
        }
        return results;
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
    /// <paramref name="subPrefix"/> exactly like <see cref="BuildCodeNavGroup"/> so
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
    private static UiControl BuildConfigurationPane(LayoutAreaHost host, object hubAddress, MeshNode node)
    {
        var definition = node.Content as NodeTypeDefinition;
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto; gap: 20px;");

        stack = stack.WithView(Controls.H2(node.Name ?? nodeId ?? "Unknown").WithStyle("margin: 0;"));

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
    /// the form values back through <see cref="MeshNodeExtensions.UpdateMeshNode(IWorkspace, Func{MeshNode, MeshNode}, Address?, string?)"/>
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
                    host.Workspace.UpdateMeshNode(liveNode =>
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
                        };
                        return liveNode with
                        {
                            Name = string.IsNullOrWhiteSpace(form.Name) ? liveNode.Name : form.Name,
                            Icon = string.IsNullOrWhiteSpace(form.Icon) ? null : form.Icon,
                            Content = nextDefinition
                        };
                    }, nodePath: node.Path);
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
                    (host.Workspace.GetStream<MeshNode>() ?? Observable.Return<MeshNode[]?>(null)).Take(1),
                    (displayName, description, iconName, orderStr, childrenQuery, dependenciesStr, configuration, currentNodes) =>
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
                        var hubPath = host.Hub.Address.ToString();
                        var currentNode = currentNodes?.FirstOrDefault(n => n.Path == hubPath);
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

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));
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

    public static NodeTypeConfigForm FromNode(MeshNode node, NodeTypeDefinition? def) => new()
    {
        Name = node.Name,
        Icon = node.Icon,
        Description = def?.Description,
        ChildrenQuery = def?.ChildrenQuery,
        DefaultNamespace = def?.DefaultNamespace,
        PageMaxWidth = def?.PageMaxWidth,
    };
}
