using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Marker record indicating that the catalog should operate in NodeType mode.
/// When set, the catalog reads NodeTypeDefinition from workspace to build the query dynamically.
/// </summary>
public record NodeTypeCatalogMode;

/// <summary>
/// Layout views for mesh node content.
/// - Details: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, description, path)
/// - Settings: Node settings with NodeType link navigation
/// - Comments: Comments section (Facebook-style)
/// </summary>
public static class MeshNodeView
{
    public const string DetailsArea = "Details";
    public const string ThumbnailArea = "Thumbnail";
    public const string MetadataArea = "Metadata";
    public const string SettingsArea = "Settings";
    public const string CommentsArea = "Comments";
    public const string CatalogArea = "Catalog";
    public const string CalendarArea = "Calendar";

    /// <summary>
    /// Adds the mesh node views (Details, Thumbnail, Metadata, Settings, Catalog, Calendar) to the hub's layout.
    /// Requires AddMeshDataSource() to be called first to enable GetStream&lt;MeshNode&gt;() in views.
    /// Catalog is set as the default area for browsing children with search.
    /// For comments support, call AddComments() after this method.
    /// </summary>
    public static MessageHubConfiguration AddDefaultViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(CatalogArea)
                .WithView(DetailsArea, Details)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(MetadataArea, Metadata)
                .WithView(SettingsArea, Settings)
                .WithView(CatalogArea, Catalog)
                .WithView(CalendarArea, Calendar));

    /// <summary>
    /// Renders the Details area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    /// Uses GetStream for node data and persistence for children.
    /// </summary>
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Load children from persistence asynchronously
        var childrenStream = Observable.FromAsync(async () =>
        {
            var children = new List<MeshNode>();
            if (persistence != null)
            {
                await foreach (var child in persistence.GetChildrenAsync(hubPath))
                {
                    children.Add(child);
                }
            }
            return children.AsReadOnly() as IReadOnlyList<MeshNode>;
        });

        // Combine node and children streams
        return nodeStream.CombineLatest(childrenStream, (nodes, children) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildDetailsContent(host, node, children ?? Array.Empty<MeshNode>());
        }).StartWith(Controls.Markdown($"# {hubPath}\n\n*Loading...*"));
    }

    private static UiControl BuildDetailsContent(this LayoutAreaHost host, MeshNode? node, IEnumerable<MeshNode> children)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%");

        // Header: title on left, icon buttons on right
        var title = node?.Name ?? host.Hub.Address.ToString();
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{title}</h1>"))
            .WithView(BuildActionButtons(host, node));

        stack = stack.WithView(headerStack);

        // Main content based on node type
        var content = GetNodeContent(node);
        if (!string.IsNullOrWhiteSpace(content))
        {
            stack = stack.WithView(new MarkdownControl(content));
        }

        // Child node sections using a unified grid
        var childTypes = children
            .Where(c => !string.IsNullOrEmpty(c.NodeType))
            .GroupBy(c => c.NodeType!)
            .OrderBy(g => g.Key)
            .ToList();

        if (childTypes.Count > 0)
        {
            // Single unified grid for all child types
            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var group in childTypes)
            {
                var recentNodes = group.Take(10).ToList();
                var displayName = GetNodeTypeDisplayName(group.Key, group.Count());

                // Section header spans full width
                grid = grid.WithView(
                    Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                    itemSkin => itemSkin.WithXs(12));

                // Thumbnails in grid: xs=12, sm=6, md=4, lg=3
                foreach (var child in recentNodes)
                {
                    grid = grid.WithView(
                        BuildThumbnailContent(child, child.Namespace ?? ""),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
                }

                // "Show more" button if there are more than 10
                if (group.Count() > 10)
                {
                    var showMoreHref = $"/{nodePath}/{MeshCatalogView.NodesArea}/{group.Key}";
                    grid = grid.WithView(
                        Controls.Button($"Show all {group.Count()}")
                            .WithAppearance(Appearance.Lightweight)
                            .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(showMoreHref))),
                        itemSkin => itemSkin.WithXs(12));
                }
            }

            stack = stack.WithView(grid);
        }

        // Comments section at the bottom (only if comments are enabled)
        if (host.Hub.Configuration.HasComments())
        {
            stack = stack.WithView(CommentsView.BuildInlineCommentsSection(host));
        }

        return stack;
    }

    /// <summary>
    /// Builds icon-only action buttons for Edit, Metadata, and Settings.
    /// </summary>
    private static UiControl BuildActionButtons(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var buttons = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        // Edit button (icon only)
        if (node != null)
        {
            var editHref = $"/{node.Namespace}/{MeshCatalogView.EditorArea}";
            buttons = buttons.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                    .WithAppearance(Appearance.Stealth)
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(editHref))));
        }

        // Settings button (icon only) - navigates to node settings with NodeType link
        var settingsHref = $"/{nodePath}/{SettingsArea}";
        buttons = buttons.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.Settings(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(settingsHref))));

        // Metadata button (icon only)
        var metadataHref = $"/{nodePath}/{MetadataArea}";
        buttons = buttons.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.Info(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(metadataHref))));

        return buttons;
    }

    /// <summary>
    /// Renders a compact thumbnail/card view of a node for use in catalogs and lists.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, _) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildThumbnailContent(node, hubPath);
            },
            hubPath);
    }

    private static UiControl BuildThumbnailContent(MeshNode? node, string hubPath)
    {
        return MeshNodeThumbnailControl.FromNode(node, hubPath);
    }

    private static string GetNodeTypeDisplayName(string nodeType, int count)
    {
        // Capitalize first letter and add count
        var display = char.ToUpper(nodeType[0]) + nodeType.Substring(1);
        return $"{display}s ({count})";
    }

    /// <summary>
    /// Renders the Metadata area showing node properties (name, type, description, path).
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Metadata(LayoutAreaHost host, RenderingContext _1)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildMetadataContent(h, node);
            },
            "Metadata");
    }

    private static UiControl BuildMetadataContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back link
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var backHref = $"/{nodePath}/{DetailsArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithView(Controls.Html("<h2>Metadata</h2>"))
            .WithView(Controls.Button("Back to Content")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Display metadata fields
        stack = stack.WithView(Controls.Html($"<p><strong>Name:</strong> {node.Name}</p>"));
        stack = stack.WithView(Controls.Html($"<p><strong>Path:</strong> {node.Namespace}</p>"));

        if (!string.IsNullOrEmpty(node.NodeType))
        {
            stack = stack.WithView(Controls.Html($"<p><strong>Type:</strong> {node.NodeType}</p>"));
        }

        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            stack = stack.WithView(Controls.Html($"<p><strong>Description:</strong> {node.Description}</p>"));
        }

        if (!string.IsNullOrEmpty(node.ParentPath))
        {
            var parentHref = $"/{node.ParentPath}/{DetailsArea}";
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithView(Controls.Html("<p><strong>Parent:</strong> </p>"))
                .WithView(Controls.Button(node.ParentPath)
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(parentHref)))));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Settings area showing node properties and types catalog.
    /// Provides read-only view of node metadata with embedded catalog of NodeType children.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Settings(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // Get node from stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Load NodeType children
        var typesStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;

            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:NodeType scope:descendants").ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>();
            }
        });

        return nodeStream.CombineLatest(typesStream, (nodes, types) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildSettingsContent(host, node, types ?? Array.Empty<MeshNode>());
        }).StartWith(Controls.Markdown($"# Settings\n\n*Loading...*"));
    }

    private static UiControl BuildSettingsContent(LayoutAreaHost _, MeshNode? node, IReadOnlyList<MeshNode> nodeTypes)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header
        stack = stack.WithView(Controls.Html("<h2 style=\"margin: 0 0 24px 0;\">Settings</h2>"));

        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Build markdown representation of MeshNode (excluding content)
        var markdown = BuildNodeMarkdown(node);
        stack = stack.WithView(new MarkdownControl(markdown));

        // Types catalog - show NodeType children using standard grid
        if (nodeTypes.Count > 0)
        {
            stack = stack.WithView(Controls.Html("<h3 style=\"margin: 32px 0 16px 0;\">Types</h3>"));

            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
            foreach (var typeNode in nodeTypes.OrderBy(t => t.Name))
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(typeNode, typeNode.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
            }
            stack = stack.WithView(grid);
        }

        return stack;
    }

    private static string BuildNodeMarkdown(MeshNode node)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## Node Properties");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| **Id** | `{node.Id}` |");
        sb.AppendLine($"| **Name** | {node.Name ?? "*not set*"} |");
        sb.AppendLine($"| **Path** | `{node.Path}` |");
        sb.AppendLine($"| **Namespace** | `{node.Namespace ?? ""}` |");
        sb.AppendLine($"| **NodeType** | {(string.IsNullOrEmpty(node.NodeType) ? "*not set*" : $"[{node.NodeType}](/{node.NodeType})")} |");
        sb.AppendLine($"| **Description** | {node.Description ?? "*not set*"} |");
        sb.AppendLine($"| **Icon** | {node.Icon ?? "*not set*"} |");
        sb.AppendLine($"| **DisplayOrder** | {node.DisplayOrder} |");
        sb.AppendLine($"| **IsPersistent** | {node.IsPersistent} |");
        sb.AppendLine($"| **State** | {node.State} |");
        sb.AppendLine($"| **LastModified** | {node.LastModified:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| **Version** | {node.Version} |");

        if (node.AddressSegments > 0)
            sb.AppendLine($"| **AddressSegments** | {node.AddressSegments} |");

        if (!string.IsNullOrEmpty(node.StreamProvider))
            sb.AppendLine($"| **StreamProvider** | {node.StreamProvider} |");

        if (!string.IsNullOrEmpty(node.AssemblyLocation))
            sb.AppendLine($"| **AssemblyLocation** | `{node.AssemblyLocation}` |");

        return sb.ToString();
    }

    private static UiControl BuildSettingsRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 12px 0; border-bottom: 1px solid #e0e0e0;")
            .WithView(Controls.Html($"<strong style=\"width: 150px; flex-shrink: 0;\">{label}:</strong>"))
            .WithView(Controls.Html($"<span>{value}</span>"));
    }

    private static string GetNodeContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle Article content
        if (node.Content is Article article)
            return article.Content ?? string.Empty;

        // Handle MarkdownDocument content (JSON with $type and content fields)
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if (typeName == "MarkdownDocument" && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }
        }

        // Handle Story content using reflection to avoid circular dependency
        var nodeType = node.NodeType?.ToLowerInvariant();
        if (nodeType == "story")
        {
            // Try to get the Text property via reflection
            var textProperty = node.Content.GetType().GetProperty("Text");
            if (textProperty != null)
            {
                var textValue = textProperty.GetValue(node.Content) as string;
                if (!string.IsNullOrEmpty(textValue))
                    return textValue;
            }
        }

        // Check for NodeDescription
        if (node.Content is NodeDescription nd)
            return nd.Description;

        // Fall back to Description field
        return node.Description ?? string.Empty;
    }

    /// <summary>
    /// Renders the Catalog view showing nodes as thumbnails in a LayoutGrid.
    /// Includes a search bar that combines user query with background filters.
    /// For NodeTypes, shows instances of that type.
    /// For instance nodes, shows children grouped by Category.
    /// </summary>
    public static UiControl Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var isNodeTypeMode = host.Hub.Configuration.Get<NodeTypeCatalogMode>() != null;

        if (meshQuery == null)
        {
            return Controls.Html("<p style=\"color: #888;\">Query service not available.</p>");
        }

        // For NodeType mode, get the definition to build the nodeType filter
        if (isNodeTypeMode)
        {
            var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();

            return Controls.Stack
                .WithWidth("100%")
                .WithView(BuildSearchBar(hubPath), "catalogSearch")
                .WithView(
                    (h, _) => definitionStream
                        .SelectMany(async definition =>
                        {
                            if (definition == null)
                                return Controls.Markdown("*Loading...*");

                            // Build nodeType filter path
                            var nodeTypePath = string.IsNullOrEmpty(definition.Namespace)
                                ? definition.Id
                                : $"{definition.Namespace}/{definition.Id}";

                            // Query: nodeType filter only
                            var query = $"nodeType:{nodeTypePath}";
                            return await BuildCatalogGridAsync(meshQuery, query);
                        }),
                    "CatalogContent");
        }

        // Instance node catalog - check for hierarchical mode
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return Controls.Stack
            .WithWidth("100%")
            .WithView(BuildSearchBar(hubPath), "catalogSearch")
            .WithView(
                (h, _) => nodeStream.SelectMany(async nodes =>
                {
                    var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                    var catalogMode = node?.CatalogMode?.ToLowerInvariant();

                    if (catalogMode == "hierarchical")
                    {
                        // Hierarchical mode: scope:descendants with tree structure
                        var query = $"namespace:{hubPath} scope:descendants";
                        return await BuildCatalogGridHierarchicalAsync(meshQuery, query, hubPath);
                    }
                    else
                    {
                        // Default: grouped by category
                        var query = $"namespace:{hubPath}";
                        return await BuildCatalogGridWithCategoriesAsync(meshQuery, query);
                    }
                }),
                "CatalogContent");
    }

    /// <summary>
    /// Builds the search bar with text input and Search button.
    /// Search navigates to search page with query parameter.
    /// </summary>
    private static UiControl BuildSearchBar(string hubPath)
    {
        var searchInputId = $"search_{hubPath.Replace("/", "_")}";

        return Controls.Html($@"
            <div style=""display: flex; gap: 8px; margin-bottom: 16px; align-items: center; width: 100%;"">
                <input type=""text"" id=""{searchInputId}"" placeholder=""Search...""
                    style=""flex: 1; padding: 8px 12px; border: 1px solid #d1d1d1; border-radius: 4px; font-size: 14px;""
                    onkeydown=""if(event.key==='Enter'){{var q=this.value;if(q)window.location.href='/search?q='+encodeURIComponent('namespace:{hubPath} scope:descendants '+q);}}"" />
                <button onclick=""var q=document.getElementById('{searchInputId}').value;if(q)window.location.href='/search?q='+encodeURIComponent('namespace:{hubPath} scope:descendants '+q);""
                    style=""padding: 8px 16px; background: #0078d4; color: white; border: none; border-radius: 4px; cursor: pointer; font-size: 14px;"">
                    Search
                </button>
            </div>");
    }

    /// <summary>
    /// Builds a LayoutGrid with thumbnail cards for each query result.
    /// Renders icon, title, and description directly from node properties.
    /// </summary>
    private static async Task<UiControl> BuildCatalogGridAsync(IMeshQuery meshQuery, string query)
    {
        List<MeshNode> nodes;
        try
        {
            nodes = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch
        {
            nodes = [];
        }

        if (nodes.Count == 0)
        {
            return Controls.Html("<p style=\"color: #888;\">No items found.</p>");
        }

        // Build LayoutGrid with thumbnail cards rendered directly
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));

        foreach (var node in nodes)
        {
            // Render thumbnail directly from node properties (icon, title, description)
            // Use wider columns: 1 per row on mobile, 2 on tablet, 2 on desktop, 3 on large screens
            grid = grid.WithView(
                MeshNodeThumbnailControl.FromNode(node, node.Path),
                itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(6).WithLg(4));
        }

        return grid;
    }

    /// <summary>
    /// Builds a hierarchical LayoutGrid showing parent-child relationships with indentation.
    /// Groups items by their immediate parent, showing tree structure.
    /// </summary>
    private static async Task<UiControl> BuildCatalogGridHierarchicalAsync(IMeshQuery meshQuery, string query, string basePath)
    {
        List<MeshNode> nodes;
        try
        {
            nodes = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch
        {
            nodes = [];
        }

        if (nodes.Count == 0)
        {
            return Controls.Html("<p style=\"color: #888;\">No items found.</p>");
        }

        // Build a tree structure from the flat list
        var nodesByPath = nodes.ToDictionary(n => n.Path, n => n);
        var basePathNormalized = basePath.Trim('/');
        var baseDepth = string.IsNullOrEmpty(basePathNormalized) ? 0 : basePathNormalized.Split('/').Length;

        // Find root-level nodes (immediate children of basePath)
        var rootNodes = nodes
            .Where(n => GetParentPath(n.Path, basePathNormalized) == basePathNormalized)
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name)
            .ToList();

        // Build the hierarchical grid
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var rootNode in rootNodes)
        {
            grid = AddNodeToGrid(grid, rootNode, nodes, baseDepth, 0);
        }

        return grid;
    }

    private static string GetParentPath(string path, string basePath)
    {
        var pathNormalized = path.Trim('/');
        var lastSlash = pathNormalized.LastIndexOf('/');
        if (lastSlash < 0)
            return "";
        return pathNormalized.Substring(0, lastSlash);
    }

    private static LayoutGridControl AddNodeToGrid(LayoutGridControl grid, MeshNode node, List<MeshNode> allNodes, int baseDepth, int indentLevel)
    {
        // Calculate left margin for indentation
        var marginLeft = indentLevel * 32;

        // Add the node with indentation
        grid = grid.WithView(
            Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle($"margin-left: {marginLeft}px; width: calc(100% - {marginLeft}px);")
                .WithView(MeshNodeThumbnailControl.FromNode(node, node.Path)),
            itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(6).WithLg(4));

        // Find and add children
        var nodePath = node.Path;
        var children = allNodes
            .Where(n => GetParentPath(n.Path, "") == nodePath)
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name)
            .ToList();

        foreach (var child in children)
        {
            grid = AddNodeToGrid(grid, child, allNodes, baseDepth, indentLevel + 1);
        }

        return grid;
    }

    /// <summary>
    /// Builds a LayoutGrid with thumbnail cards grouped by Category.
    /// Each category gets a heading followed by its nodes.
    /// </summary>
    private static async Task<UiControl> BuildCatalogGridWithCategoriesAsync(IMeshQuery meshQuery, string query)
    {
        List<MeshNode> nodes;
        try
        {
            nodes = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch
        {
            nodes = [];
        }

        if (nodes.Count == 0)
        {
            return Controls.Html("<p style=\"color: #888;\">No items found.</p>");
        }

        // Group nodes by Category
        var groups = nodes
            .GroupBy(n => n.Category ?? "Uncategorized")
            .OrderBy(g => g.Key)
            .ToList();

        // Build LayoutGrid with category headings
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));

        foreach (var group in groups)
        {
            // Category heading spans full width
            grid = grid.WithView(
                Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{group.Key}</h3>"),
                itemSkin => itemSkin.WithXs(12));

            // Nodes in this category
            foreach (var node in group.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name))
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(node, node.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(6).WithLg(4));
            }
        }

        return grid;
    }

    /// <summary>
    /// Renders a Calendar view showing scheduled items by publish date.
    /// Groups items by week and displays them in a timeline format.
    /// </summary>
    public static UiControl Calendar(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            return Controls.Html("<p style=\"color: #888;\">Query service not available.</p>");
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithView(Controls.Html("<h2 style=\"margin: 0 0 16px 0;\">Publishing Calendar</h2>"))
            .WithView(
                (h, _) => Observable.FromAsync(async () =>
                {
                    // Query all descendants to find items with publish dates
                    var query = $"namespace:{hubPath} scope:descendants";
                    return await BuildCalendarViewAsync(meshQuery, query);
                }),
                "CalendarContent");
    }

    /// <summary>
    /// Builds a calendar view showing items grouped by week.
    /// </summary>
    private static async Task<UiControl> BuildCalendarViewAsync(IMeshQuery meshQuery, string query)
    {
        List<MeshNode> nodes;
        try
        {
            nodes = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
        }
        catch
        {
            nodes = [];
        }

        // Filter nodes that have publishable content (Posts with PublishDate)
        var scheduledItems = new List<(MeshNode Node, DateTime PublishDate, string? Status)>();

        foreach (var node in nodes)
        {
            if (node.Content is System.Text.Json.JsonElement json)
            {
                DateTime? publishDate = null;
                string? status = null;

                if (json.TryGetProperty("publishDate", out var dateProp) && dateProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    if (DateTime.TryParse(dateProp.GetString(), out var date))
                        publishDate = date;
                }

                if (json.TryGetProperty("status", out var statusProp))
                {
                    status = statusProp.GetString();
                }

                if (publishDate.HasValue)
                {
                    scheduledItems.Add((node, publishDate.Value, status));
                }
            }
        }

        if (scheduledItems.Count == 0)
        {
            return Controls.Html("<p style=\"color: #888;\">No scheduled items found.</p>");
        }

        // Group by week
        var groupedByWeek = scheduledItems
            .OrderBy(x => x.PublishDate)
            .GroupBy(x => GetWeekStart(x.PublishDate))
            .ToList();

        var stack = Controls.Stack.WithWidth("100%");

        foreach (var week in groupedByWeek)
        {
            var weekStart = week.Key;
            var weekEnd = weekStart.AddDays(6);
            var weekHeader = $"{weekStart:MMM d} - {weekEnd:MMM d, yyyy}";

            stack = stack.WithView(Controls.Html($"<h3 style=\"margin: 24px 0 12px 0; padding-bottom: 8px; border-bottom: 2px solid #0078d4;\">{weekHeader}</h3>"));

            // Items in this week, grouped by day
            var byDay = week.GroupBy(x => x.PublishDate.Date).OrderBy(x => x.Key);

            foreach (var day in byDay)
            {
                var dayLabel = day.Key.ToString("ddd, MMM d");
                stack = stack.WithView(Controls.Html($"<div style=\"font-weight: 600; margin: 12px 0 8px 0; color: #666;\">{dayLabel}</div>"));

                foreach (var (node, publishDate, status) in day.OrderBy(x => x.PublishDate))
                {
                    var time = publishDate.ToString("HH:mm");
                    var statusBadge = GetStatusBadge(status);
                    var platforms = GetPlatforms(node);

                    var itemHtml = $@"
                        <div style=""display: flex; align-items: center; gap: 12px; padding: 12px; margin: 4px 0; background: #f5f5f5; border-radius: 8px; cursor: pointer;"" onclick=""window.location.href='/{node.Path}'"">
                            <div style=""font-weight: 600; color: #0078d4; min-width: 50px;"">{time}</div>
                            <div style=""flex: 1;"">
                                <div style=""font-weight: 500;"">{node.Name}</div>
                                <div style=""font-size: 12px; color: #666;"">{platforms}</div>
                            </div>
                            {statusBadge}
                        </div>";

                    stack = stack.WithView(Controls.Html(itemHtml));
                }
            }
        }

        return stack;
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }

    private static string GetStatusBadge(string? status)
    {
        var (color, bg) = status?.ToLowerInvariant() switch
        {
            "scheduled" => ("#0078d4", "#e6f2ff"),
            "published" => ("#107c10", "#e6f7e6"),
            "draft" => ("#797979", "#f0f0f0"),
            "archived" => ("#a80000", "#ffe6e6"),
            _ => ("#797979", "#f0f0f0")
        };

        return $"<span style=\"padding: 4px 8px; border-radius: 4px; font-size: 11px; font-weight: 500; background: {bg}; color: {color};\">{status ?? "Draft"}</span>";
    }

    private static string GetPlatforms(MeshNode node)
    {
        if (node.Content is System.Text.Json.JsonElement json && json.TryGetProperty("platforms", out var platformsProp))
        {
            if (platformsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var platforms = new List<string>();
                foreach (var p in platformsProp.EnumerateArray())
                {
                    if (p.GetString() is string platform)
                        platforms.Add(platform);
                }
                return string.Join(" • ", platforms);
            }
        }
        return "";
    }

}

/// <summary>
/// View model for displaying comments in the DataGrid.
/// </summary>
public record CommentViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;

    public CommentViewModel() { }

    public CommentViewModel(Comment comment)
    {
        Id = comment.Id;
        Author = comment.Author;
        Text = comment.Text;
        CreatedAt = comment.CreatedAt.ToString("g");
    }
}
