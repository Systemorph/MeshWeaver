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
/// Configuration for the catalog view query.
/// Set this in hub configuration to customize what query the standard catalog uses.
/// Use GitHub-style query syntax including exclusions (e.g., "-nodeType:NodeType" to exclude NodeType nodes).
/// </summary>
/// <param name="Query">The GitHub-style query to use for catalog (e.g., "scope:descendants -nodeType:NodeType")</param>
/// <param name="Title">Optional title for the catalog</param>
public record CatalogQueryConfig(string Query, string? Title = null);

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

    // Data keys for catalog
    private const string CatalogSearchDataId = "catalogSearch";
    private const string CatalogLimitDataId = "catalogLimit";
    private const int DefaultCatalogPageSize = 20;

    /// <summary>
    /// Adds the mesh node views (Details, Thumbnail, Metadata, Settings, Catalog) to the hub's layout.
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
                .WithView(CatalogArea, Catalog));

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
        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

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
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        // Get node from stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        // Load NodeType children
        var typesStream = Observable.FromAsync(async () =>
        {
            var types = new List<MeshNode>();
            if (persistence != null)
            {
                await foreach (var item in persistence.QueryAsync("nodeType:NodeType scope:descendants", hubPath))
                {
                    if (item is MeshNode mn)
                        types.Add(mn);
                }
            }
            return types.AsReadOnly() as IReadOnlyList<MeshNode>;
        });

        return nodeStream.CombineLatest(typesStream, (nodes, types) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildSettingsContent(host, node, types ?? Array.Empty<MeshNode>());
        }).StartWith(Controls.Markdown($"# Settings\n\n*Loading...*"));
    }

    private static UiControl BuildSettingsContent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> nodeTypes)
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
        sb.AppendLine($"| **IconName** | {node.IconName ?? "*not set*"} |");
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
    /// Renders the Catalog view showing nodes as thumbnails.
    /// Uses CatalogQueryConfig from hub configuration if available, otherwise defaults to showing descendants.
    /// If NodeTypeCatalogMode is set, reads NodeTypeDefinition to build query dynamically.
    /// Includes search bar for filtering and Load More for pagination.
    /// </summary>
    public static UiControl Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var catalogConfig = host.Hub.Configuration.Get<CatalogQueryConfig>();
        var isNodeTypeMode = host.Hub.Configuration.Get<NodeTypeCatalogMode>() != null;

        // Initialize catalog state
        host.UpdateData(CatalogSearchDataId, "");
        host.UpdateData(CatalogLimitDataId, DefaultCatalogPageSize.ToString());

        // If NodeType mode, build query from NodeTypeDefinition dynamically
        // Search from root to find all instances of this type regardless of location
        if (isNodeTypeMode)
        {
            var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();

            // Flag to track if we've initialized the search bar with the default query
            var queryInitialized = false;

            return Controls.Stack
                .WithWidth("100%")
                .WithView(
                    (h, c) => definitionStream
                        .CombineLatest(
                            h.GetDataStream<string>(CatalogSearchDataId),
                            h.GetDataStream<string>(CatalogLimitDataId))
                        .Throttle(TimeSpan.FromMilliseconds(300))
                        .SelectMany(async tuple =>
                        {
                            var (definition, currentSearch, limitStr) = tuple;
                            if (definition == null)
                                return Controls.Markdown("*Loading...*");

                            // Build default query from NodeTypeDefinition
                            // Use ChildrenQuery if defined, otherwise simple nodeType + scope query
                            var nodeTypePath = string.IsNullOrEmpty(definition.Namespace)
                                ? definition.Id
                                : $"{definition.Namespace}/{definition.Id}";
                            var defaultQuery = definition.ChildrenQuery
                                ?? $"nodeType:{nodeTypePath} scope:descendants";

                            // Pre-fill search bar with the default query on first load
                            if (!queryInitialized && string.IsNullOrEmpty(currentSearch))
                            {
                                queryInitialized = true;
                                host.UpdateData(CatalogSearchDataId, defaultQuery);
                                currentSearch = defaultQuery;
                            }

                            // Use the search bar content as the query (user can modify it)
                            var query = string.IsNullOrWhiteSpace(currentSearch) ? defaultQuery : currentSearch;
                            var dynamicConfig = new CatalogQueryConfig(query, definition.DisplayName ?? definition.Id);

                            var limit = int.TryParse(limitStr, out var l) ? l : DefaultCatalogPageSize;
                            // Search from root ("") to find all instances regardless of their location
                            // Use NodeType-specific catalog builder with activity fallback
                            return await BuildNodeTypeCatalogViewAsync(host, persistence, dynamicConfig, nodeTypePath, limit);
                        }),
                    "CatalogContent");
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<string>(CatalogSearchDataId)
                    .CombineLatest(h.GetDataStream<string>(CatalogLimitDataId))
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .SelectMany(async tuple =>
                    {
                        var (search, limitStr) = tuple;
                        var limit = int.TryParse(limitStr, out var l) ? l : DefaultCatalogPageSize;
                        return await BuildCatalogViewAsync(host, hubPath, persistence, catalogConfig, search, limit);
                    }),
                "CatalogContent");
    }

    private static async Task<UiControl> BuildCatalogViewAsync(
        LayoutAreaHost host,
        string basePath,
        IPersistenceService? persistence,
        CatalogQueryConfig? catalogConfig,
        string? searchFilter,
        int limit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Search bar - full width with Search button on same row
        var searchRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-bottom: 16px; align-items: center; width: 100%;")
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Search (e.g., name:*claims* status:Open)")
                .WithStyle("flex: 1;")
                .WithIconStart(FluentIcons.Search())
                .WithImmediate(true) with
            {
                DataContext = LayoutAreaReference.GetDataPointer(CatalogSearchDataId)
            })
            .WithView(Controls.Button("Search")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Search()));

        stack = stack.WithView(searchRow);

        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">Persistence service not available.</p>"));
            return stack;
        }

        // Build query - search for direct children using namespace filter
        // Direct children are nodes whose Namespace property equals basePath
        // Exclude NodeType nodes (they go to Settings)
        var queryLimit = limit + 1; // Request one more to detect if there are more
        var baseQuery = catalogConfig?.Query ?? $"namespace:{basePath} -nodeType:NodeType scope:descendants";
        var query = $"{baseQuery} limit:{queryLimit}";
        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            query = searchFilter.Trim() + " " + query;
        }

        var nodes = new List<MeshNode>();

        try
        {
            // Search from basePath with scope:descendants to find nodes with matching namespace
            await foreach (var item in persistence.QueryAsync(query, basePath))
            {
                if (item is MeshNode mn)
                {
                    nodes.Add(mn);
                }
            }
        }
        catch
        {
            // Query may fail - that's ok
        }

        // Check if there are more items
        var hasMore = nodes.Count > limit;
        if (hasMore)
        {
            nodes = nodes.Take(limit).ToList();
        }

        // Results info
        var subtitleText = catalogConfig?.Title ?? (string.IsNullOrWhiteSpace(searchFilter)
            ? "Showing children"
            : "Filtered results");
        stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin-bottom: 16px;\">{subtitleText}</p>"));

        // Group by nodeType and render
        if (nodes.Count == 0)
        {
            var noResultsMsg = string.IsNullOrWhiteSpace(searchFilter)
                ? "No items found."
                : "No items match your search.";
            stack = stack.WithView(Controls.Html($"<p style=\"color: #888;\">{noResultsMsg}</p>"));
        }
        else
        {
            // Results count
            var countText = hasMore
                ? $"Showing {nodes.Count}+ items"
                : $"Showing {nodes.Count} item{(nodes.Count != 1 ? "s" : "")}";
            stack = stack.WithView(Controls.Html($"<p style=\"color: #888; margin-bottom: 12px; font-size: 0.9em;\">{countText}</p>"));

            // Group nodes by nodeType
            var groupedNodes = nodes
                .GroupBy(n => n.NodeType ?? "Other")
                .OrderBy(g => g.Key);

            foreach (var group in groupedNodes)
            {
                // Group header
                var typeName = group.Key;
                stack = stack.WithView(Controls.Html($"<h3 style=\"margin: 16px 0 8px 0; font-size: 1.1em; color: #444;\">{System.Web.HttpUtility.HtmlEncode(typeName)}</h3>"));

                // Grid for this group
                var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
                foreach (var node in group)
                {
                    grid = grid.WithView(
                        MeshNodeThumbnailControl.FromNode(node, node.Path),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(6).WithLg(6));
                }
                stack = stack.WithView(grid);
            }

            // Load More button
            if (hasMore)
            {
                var newLimit = limit + DefaultCatalogPageSize;
                var loadMoreRow = Controls.Stack
                    .WithStyle("margin-top: 24px; display: flex; justify-content: center;")
                    .WithView(Controls.Button("Load More")
                        .WithAppearance(Appearance.Neutral)
                        .WithIconEnd(FluentIcons.ChevronDown())
                        .WithClickAction(actx =>
                        {
                            host.UpdateData(CatalogLimitDataId, newLimit.ToString());
                        }));
                stack = stack.WithView(loadMoreRow);
            }
        }

        return stack;
    }

    /// <summary>
    /// Builds the catalog view for NodeType mode with activity query fallback.
    /// If the activity query returns no results, falls back to regular node query.
    /// </summary>
    private static async Task<UiControl> BuildNodeTypeCatalogViewAsync(
        LayoutAreaHost host,
        IPersistenceService? persistence,
        CatalogQueryConfig catalogConfig,
        string nodeTypePath,
        int limit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Search bar - full width with Search button on same row
        var searchRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-bottom: 16px; align-items: center; width: 100%;")
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Query (e.g., nodeType:Person scope:descendants)")
                .WithStyle("flex: 1;")
                .WithIconStart(FluentIcons.Search())
                .WithImmediate(true) with
            {
                DataContext = LayoutAreaReference.GetDataPointer(CatalogSearchDataId)
            })
            .WithView(Controls.Button("Search")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Search()));

        stack = stack.WithView(searchRow);

        // Subtitle
        var title = catalogConfig.Title ?? nodeTypePath;
        stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin-bottom: 16px;\">Showing {System.Web.HttpUtility.HtmlEncode(title)}s</p>"));

        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">Persistence service not available.</p>"));
            return stack;
        }

        // Build query from catalogConfig.Query (which is the search bar content)
        var queryLimit = limit + 1; // Request one more to detect if there are more
        var query = catalogConfig.Query;

        // Remove any existing limit from query (we'll add our own)
        query = System.Text.RegularExpressions.Regex.Replace(
            query,
            @"limit:\d+\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Add our limit
        query = query + " limit:" + queryLimit;

        var nodes = new List<MeshNode>();

        try
        {
            // Search from root to find all instances matching the query
            await foreach (var item in persistence.QueryAsync(query, ""))
            {
                if (item is MeshNode mn)
                {
                    nodes.Add(mn);
                }
            }
        }
        catch
        {
            // Query may fail - that's ok
        }

        // Check if there are more items
        var hasMore = nodes.Count > limit;
        if (hasMore)
        {
            nodes = nodes.Take(limit).ToList();
        }

        // Thumbnail grid
        if (nodes.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No items match your query.</p>"));
        }
        else
        {
            // Results count
            var countText = hasMore
                ? $"Showing {nodes.Count}+ items"
                : $"Showing {nodes.Count} item{(nodes.Count != 1 ? "s" : "")}";
            stack = stack.WithView(Controls.Html($"<p style=\"color: #888; margin-bottom: 12px; font-size: 0.9em;\">{countText}</p>"));

            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
            foreach (var node in nodes)
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(node, node.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(6).WithLg(6));
            }
            stack = stack.WithView(grid);

            // Load More button
            if (hasMore)
            {
                var newLimit = limit + DefaultCatalogPageSize;
                var loadMoreRow = Controls.Stack
                    .WithStyle("margin-top: 24px; display: flex; justify-content: center;")
                    .WithView(Controls.Button("Load More")
                        .WithAppearance(Appearance.Neutral)
                        .WithIconEnd(FluentIcons.ChevronDown())
                        .WithClickAction(actx =>
                        {
                            host.UpdateData(CatalogLimitDataId, newLimit.ToString());
                        }));
                stack = stack.WithView(loadMoreRow);
            }
        }

        return stack;
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
