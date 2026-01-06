using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
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

    // UCR (Unified Content Reference) special areas
    public const string ContentArea = "$Content";
    public const string DataArea = "$Data";
    public const string SchemaArea = "$Schema";
    public const string ModelArea = "$Model";

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
                .WithView(CalendarArea, Calendar)
                // UCR special areas - $Content is registered by ContentCollectionsExtensions.AddContentCollections
                .WithView(DataArea, Data)
                .WithView(SchemaArea, Schema)
                .WithView(ModelArea, DataModelLayoutArea.DataModel));

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
    /// Uses MeshSearchControl for unified search and display.
    /// For NodeType nodes, shows instances of that type (nodeType:name scope:subtree).
    /// For instance nodes, uses CatalogQuery if set, otherwise defaults to namespace:path (direct children).
    /// Render mode is determined by CatalogMode property (hierarchical or grouped).
    /// Reads search term from ?q= query parameter.
    /// </summary>
    public static IObservable<UiControl?> Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var isNodeTypeMode = host.Hub.Configuration.Get<NodeTypeCatalogMode>() != null;

        // Get search term from query string (if present)
        var searchTerm = host.GetQueryStringParamValue("q")?.Trim();

        // Get node stream to access node properties
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);

            // For NodeType mode, query by the node's full path as the nodeType filter
            // Also limit to the node's namespace
            if (isNodeTypeMode && node != null)
            {
                var nodeTypePath = node.Path; // Full path like "Type/Person" or "Systemorph/Type/Project"
                var nodeTypeNamespace = node.Namespace ?? "";
                var hiddenQuery = string.IsNullOrEmpty(nodeTypeNamespace)
                    ? $"nodeType:{nodeTypePath} scope:subtree"
                    : $"namespace:{nodeTypeNamespace} nodeType:{nodeTypePath} scope:subtree";
                return (UiControl?)Controls.MeshSearch
                    .WithHiddenQuery(hiddenQuery)
                    .WithVisibleQuery(searchTerm ?? "")
                    .WithNamespace(hubPath)
                    .WithPlaceholder("Search... (use @ for references)")
                    .WithRenderMode(MeshSearchRenderMode.Hierarchical)
                    .WithMaxColumns(3);
            }

            // Instance node catalog
            // Use CatalogQuery if set, otherwise default to namespace:node.Namespace (direct children only)
            var instanceHiddenQuery = node?.CatalogQuery ?? $"namespace:{node?.Namespace ?? hubPath}";

            // Determine render mode from CatalogMode property (default: hierarchical)
            var catalogMode = node?.CatalogMode?.ToLowerInvariant();
            var renderMode = catalogMode == "grouped"
                ? MeshSearchRenderMode.Grouped
                : MeshSearchRenderMode.Hierarchical;

            return (UiControl?)Controls.MeshSearch
                .WithHiddenQuery(instanceHiddenQuery)
                .WithVisibleQuery(searchTerm ?? "")
                .WithNamespace(hubPath)
                .WithPlaceholder("Search... (use @ for references)")
                .WithRenderMode(renderMode)
                .WithMaxColumns(3);
        });
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

    #region UCR Special Areas

    /// <summary>
    /// Renders content from the node's content collection.
    /// For images: renders inline. For markdown: renders the content.
    /// For other files: shows a download link.
    /// For self-reference (no path): shows the node's icon/logo.
    /// </summary>
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext context)
    {
        var contentPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(contentPath))
        {
            // Self-reference: show the node's icon/logo
            return host.StreamView<MeshNode>(
                (nodes, h) =>
                {
                    var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                    if (node == null)
                        return Controls.Markdown($"*Node not found: {hubPath}*");

                    return RenderNodeIcon(node, hubPath);
                },
                hubPath);
        }

        // Determine content type from extension
        var extension = Path.GetExtension(contentPath)?.ToLowerInvariant() ?? "";

        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" =>
                RenderImageAsync(host, contentPath, extension),
            ".md" or ".markdown" =>
                Observable.Return<UiControl?>(RenderMarkdownContent(host, contentPath)),
            ".pdf" =>
                Observable.Return<UiControl?>(RenderPdf(host, contentPath)),
            ".json" =>
                Observable.Return<UiControl?>(RenderJsonContent(host, contentPath)),
            _ => Observable.Return<UiControl?>(RenderDownloadLink(host, contentPath, extension))
        };
    }

    /// <summary>
    /// Renders the node's icon/logo for content self-reference.
    /// Priority: content.avatar > content.logo > node.Icon
    /// </summary>
    private static UiControl RenderNodeIcon(MeshNode node, string hubPath)
    {
        var imageUrl = GetNodeImageUrl(node);

        if (string.IsNullOrEmpty(imageUrl))
        {
            // No image - show a placeholder or the node type icon
            var iconName = !string.IsNullOrEmpty(node.Icon) ? node.Icon : "Document";
            return Controls.Html($@"
                <div style=""display: flex; align-items: center; gap: 8px;"">
                    <fluent-icon name=""{iconName}"" size=""24""></fluent-icon>
                    <span>{node.Name ?? node.Id}</span>
                </div>");
        }

        // Check if it's a data URI (inline SVG or base64 image)
        if (imageUrl.StartsWith("data:"))
        {
            return Controls.Html($@"<img src=""{imageUrl}"" alt=""{node.Name ?? node.Id}"" style=""max-width: 100%; max-height: 200px; height: auto;"" />");
        }

        // External URL
        return Controls.Html($@"<img src=""{imageUrl}"" alt=""{node.Name ?? node.Id}"" style=""max-width: 100%; max-height: 200px; height: auto;"" />");
    }

    /// <summary>
    /// Gets the image URL for a node.
    /// Priority: content.avatar > content.logo > node.Icon (if URL/data URI)
    /// </summary>
    private static string? GetNodeImageUrl(MeshNode node)
    {
        // Check content properties (avatar, logo)
        if (node.Content != null)
        {
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                // Try avatar
                if (jsonElement.TryGetProperty("avatar", out var avatarProp) && avatarProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var avatar = avatarProp.GetString();
                    if (!string.IsNullOrEmpty(avatar))
                        return avatar;
                }
                if (jsonElement.TryGetProperty("Avatar", out var avatarPascalProp) && avatarPascalProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var avatar = avatarPascalProp.GetString();
                    if (!string.IsNullOrEmpty(avatar))
                        return avatar;
                }
                // Try logo
                if (jsonElement.TryGetProperty("logo", out var logoProp) && logoProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var logo = logoProp.GetString();
                    if (!string.IsNullOrEmpty(logo))
                        return logo;
                }
                if (jsonElement.TryGetProperty("Logo", out var logoPascalProp) && logoPascalProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var logo = logoPascalProp.GetString();
                    if (!string.IsNullOrEmpty(logo))
                        return logo;
                }
            }
            else if (node.Content is IDictionary<string, object> dict)
            {
                if (dict.TryGetValue("avatar", out var avatar) || dict.TryGetValue("Avatar", out avatar))
                {
                    var avatarStr = avatar?.ToString();
                    if (!string.IsNullOrEmpty(avatarStr))
                        return avatarStr;
                }
                if (dict.TryGetValue("logo", out var logo) || dict.TryGetValue("Logo", out logo))
                {
                    var logoStr = logo?.ToString();
                    if (!string.IsNullOrEmpty(logoStr))
                        return logoStr;
                }
            }
            else
            {
                // Reflection for typed objects
                var avatarProperty = node.Content.GetType().GetProperty("Avatar");
                if (avatarProperty != null)
                {
                    var avatarValue = avatarProperty.GetValue(node.Content) as string;
                    if (!string.IsNullOrEmpty(avatarValue))
                        return avatarValue;
                }
                var logoProperty = node.Content.GetType().GetProperty("Logo");
                if (logoProperty != null)
                {
                    var logoValue = logoProperty.GetValue(node.Content) as string;
                    if (!string.IsNullOrEmpty(logoValue))
                        return logoValue;
                }
            }
        }

        // Fall back to node.Icon if it's a URL or data URI
        if (!string.IsNullOrEmpty(node.Icon) && (node.Icon.StartsWith("data:") || node.Icon.StartsWith("http")))
            return node.Icon;

        return null;
    }

    private static IObservable<UiControl?> RenderImageAsync(LayoutAreaHost host, string contentPath, string extension)
    {
        // Build static content URL: /static/{address}/content/{filePath}
        var address = host.Hub.Address.ToString();
        var staticUrl = $"/static/{address}/content/{contentPath}";

        return Observable.Return<UiControl?>(
            Controls.Html($"<img src='{staticUrl}' alt='{Path.GetFileName(contentPath)}' style='max-width: 100%;' />"));
    }

    private static UiControl RenderMarkdownContent(LayoutAreaHost host, string contentPath)
    {
        // For markdown files, show text indicating content is inserted and provide navigation link
        var address = host.Hub.Address.ToString();
        var fileName = Path.GetFileName(contentPath);

        // Create a message with link to navigate to the content
        var markdown = $"*This is text inserted from @{address}/content:{contentPath}*\n\n" +
                       $"[Navigate to {fileName}](/{address}/$Content/{contentPath})";

        return Controls.Markdown(markdown);
    }

    private static UiControl RenderPdf(LayoutAreaHost host, string contentPath)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        return Controls.Html($@"
            <div style=""width: 100%; min-height: 500px;"">
                <iframe src=""{contentUrl}"" style=""width: 100%; height: 600px; border: 1px solid #ccc; border-radius: 4px;"" title=""{Path.GetFileName(contentPath)}""></iframe>
                <div style=""margin-top: 8px;"">
                    <a href=""{contentUrl}"" download=""{Path.GetFileName(contentPath)}"" style=""color: #0078d4;"">Download PDF</a>
                </div>
            </div>");
    }

    private static UiControl RenderJsonContent(LayoutAreaHost host, string contentPath)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        return Controls.Markdown($"```json\n// Loading {contentPath}...\n```");
    }

    private static UiControl RenderDownloadLink(LayoutAreaHost host, string contentPath, string extension)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        var fileName = Path.GetFileName(contentPath);
        return Controls.Html($@"
            <div style=""padding: 16px; background: #f5f5f5; border-radius: 8px; display: inline-flex; align-items: center; gap: 12px;"">
                <span style=""font-size: 24px;"">📄</span>
                <div>
                    <div style=""font-weight: 500;"">{fileName}</div>
                    <a href=""{contentUrl}"" download=""{fileName}"" style=""color: #0078d4; font-size: 14px;"">Download</a>
                </div>
            </div>");
    }

    /// <summary>
    /// Renders data entities from the node's data context.
    /// If Id is specified, renders that specific entity/collection/type.
    /// If no Id (self-reference), shows the current MeshNode data.
    /// </summary>
    public static IObservable<UiControl?> Data(LayoutAreaHost host, RenderingContext context)
    {
        var dataPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(dataPath))
        {
            // Self-reference: show the current MeshNode data as JSON
            return host.StreamView<MeshNode>(
                (nodes, h) =>
                {
                    var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                    if (node == null)
                        return Controls.Markdown($"*Node not found: {hubPath}*");

                    return RenderMeshNodeData(node, host.Hub.JsonSerializerOptions);
                },
                hubPath);
        }

        // Check if dataPath is a collection name or a type name
        if (host.Workspace.DataContext.TypeSources.TryGetValue(dataPath, out var typeSource))
        {
            // It's a collection name - show catalog for this collection
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} type:{dataPath}")
                .WithPlaceholder($"Search {dataPath}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical));
        }

        // Render specific collection or entity
        // The dataPath could be "CollectionName/entityId"
        var parts = dataPath.Split('/', 2);
        var collectionName = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        if (!host.Workspace.DataContext.TypeSources.TryGetValue(collectionName, out typeSource))
        {
            // Not a known collection - might be a type name, search for it
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} {dataPath}")
                .WithPlaceholder($"Search {dataPath}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical));
        }

        if (string.IsNullOrEmpty(entityId))
        {
            // Show catalog for this collection
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} type:{collectionName}")
                .WithPlaceholder($"Search {collectionName}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical));
        }

        // Show specific entity - delegate to standard entity view
        return Observable.Return<UiControl?>(Controls.Markdown($"*Loading entity {entityId} from {collectionName}...*"));
    }

    private static UiControl RenderMeshNodeData(MeshNode node, JsonSerializerOptions jsonOptions)
    {
        // Serialize the MeshNode as JSON
        var json = JsonSerializer.Serialize(node, new JsonSerializerOptions(jsonOptions)
        {
            WriteIndented = true
        });

        return new MarkdownControl($"```json\n{json}\n```");
    }

    /// <summary>
    /// Renders JSON schema for a type.
    /// If Id is specified, shows schema for that type name.
    /// If no Id (self-reference), shows schema for MeshNode and content type.
    /// </summary>
    public static IObservable<UiControl?> Schema(LayoutAreaHost host, RenderingContext context)
    {
        var typeName = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(typeName))
        {
            // Self-reference: show MeshNode schema and content type schema
            var jsonOptions = host.Hub.JsonSerializerOptions;
            return host.StreamView<MeshNode>(
                (nodes, h) =>
                {
                    var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                    return RenderNodeSchema(node, hubPath, jsonOptions);
                },
                hubPath);
        }

        // Try to get the type from the registry
        var typeRegistry = host.Hub.ServiceProvider.GetService<ITypeRegistry>();
        if (typeRegistry == null)
            return Observable.Return<UiControl?>(Controls.Markdown($"*Type registry not available.*"));

        var typeDef = typeRegistry.GetTypeDefinition(typeName);
        if (typeDef == null)
            return Observable.Return<UiControl?>(Controls.Markdown($"*Type '{typeName}' not found.*"));

        // Generate JSON schema for the type using hub's JSON options
        var schema = GenerateJsonSchema(typeDef.Type, host.Hub.JsonSerializerOptions);
        return Observable.Return<UiControl?>(new MarkdownControl($"## JSON Schema: {typeName}\n\n```json\n{schema}\n```"));
    }

    private static UiControl RenderNodeSchema(MeshNode? node, string hubPath, JsonSerializerOptions? jsonOptions = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Schema");
        sb.AppendLine();

        // MeshNode schema
        sb.AppendLine("### MeshNode");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(GenerateJsonSchema(typeof(MeshNode), jsonOptions));
        sb.AppendLine("```");

        // Content type schema if available
        if (node?.Content != null)
        {
            var contentType = node.Content.GetType();

            // Handle JsonElement specially
            if (contentType == typeof(JsonElement))
            {
                var jsonElement = (JsonElement)node.Content;
                if (jsonElement.TryGetProperty("$type", out var typeProperty))
                {
                    var contentTypeName = typeProperty.GetString();
                    sb.AppendLine();
                    sb.AppendLine($"### Content Type: {contentTypeName}");
                    sb.AppendLine();
                    sb.AppendLine("Content is a `JsonElement` with type indicator.");
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("### Content Type");
                    sb.AppendLine();
                    sb.AppendLine("Content is a `JsonElement` (dynamic content).");
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine($"### Content Type: {contentType.Name}");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(GenerateJsonSchema(contentType, jsonOptions));
                sb.AppendLine("```");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("### Content Type");
            sb.AppendLine();
            sb.AppendLine("*No content defined for this node.*");
        }

        return new MarkdownControl(sb.ToString());
    }

    private static string GenerateJsonSchema(Type type, JsonSerializerOptions? jsonOptions = null)
    {
        // Use the built-in JsonSchemaExporter from System.Text.Json.Schema (.NET 9+)
        var options = jsonOptions ?? JsonSerializerOptions.Default;

        // Configure schema exporter options with custom transformer to add descriptions
        var exporterOptions = new System.Text.Json.Schema.JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = false,
            TransformSchemaNode = (context, node) =>
            {
                // Add description from attributes or XML documentation
                MemberInfo? member = context.PropertyInfo?.PropertyType != null
                    ? context.PropertyInfo.AttributeProvider as MemberInfo
                    : context.TypeInfo.Type;

                if (member != null)
                {
                    var description = GetDescription(member);
                    if (!string.IsNullOrEmpty(description) && node is JsonObject obj && !obj.ContainsKey("description"))
                    {
                        obj["description"] = description;
                    }
                }

                return node;
            }
        };

        var schemaNode = options.GetJsonSchemaAsNode(type, exporterOptions);

        // Convert to indented JSON string
        return schemaNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? GetDescription(MemberInfo member)
    {
        // Try DescriptionAttribute
        var descAttr = member.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        if (descAttr != null)
            return descAttr.Description;

        // Try DisplayAttribute
        var displayAttr = member.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>();
        if (displayAttr != null)
            return displayAttr.Description ?? displayAttr.Name;

        // Try XML documentation
        var xmlDoc = GetXmlDocumentation(member);
        if (!string.IsNullOrEmpty(xmlDoc))
            return xmlDoc;

        return null;
    }

    // Cache for XML documentation per assembly
    private static readonly Dictionary<string, System.Xml.Linq.XDocument?> XmlDocCache = new();

    private static string? GetXmlDocumentation(MemberInfo member)
    {
        var assembly = member.DeclaringType?.Assembly;
        if (assembly == null)
            return null;

        // Try to load the XML documentation file
        var xmlDoc = GetXmlDocumentForAssembly(assembly);
        if (xmlDoc == null)
            return null;

        // Build the member name in XML documentation format
        var memberName = GetXmlMemberName(member);
        if (string.IsNullOrEmpty(memberName))
            return null;

        try
        {
            // Find the member element
            var memberElement = xmlDoc.Descendants("member")
                .FirstOrDefault(m => m.Attribute("name")?.Value == memberName);

            if (memberElement != null)
            {
                // Get the summary element
                var summary = memberElement.Element("summary")?.Value;
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    // Clean up whitespace
                    return System.Text.RegularExpressions.Regex.Replace(summary.Trim(), @"\s+", " ");
                }
            }
        }
        catch
        {
            // Ignore XML parsing errors
        }

        return null;
    }

    private static System.Xml.Linq.XDocument? GetXmlDocumentForAssembly(System.Reflection.Assembly assembly)
    {
        var assemblyLocation = assembly.Location;
        if (string.IsNullOrEmpty(assemblyLocation))
            return null;

        lock (XmlDocCache)
        {
            if (XmlDocCache.TryGetValue(assemblyLocation, out var cached))
                return cached;

            try
            {
                var xmlPath = Path.ChangeExtension(assemblyLocation, ".xml");
                if (File.Exists(xmlPath))
                {
                    var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                    XmlDocCache[assemblyLocation] = doc;
                    return doc;
                }
            }
            catch
            {
                // Ignore file loading errors
            }

            XmlDocCache[assemblyLocation] = null;
            return null;
        }
    }

    private static string? GetXmlMemberName(MemberInfo member)
    {
        var declaringType = member.DeclaringType;
        if (declaringType == null)
            return null;

        var typeName = declaringType.FullName?.Replace('+', '.');
        if (typeName == null)
            return null;

        return member switch
        {
            PropertyInfo prop => $"P:{typeName}.{prop.Name}",
            FieldInfo field => $"F:{typeName}.{field.Name}",
            MethodInfo method => $"M:{typeName}.{method.Name}",
            Type type => $"T:{type.FullName?.Replace('+', '.')}",
            _ => null
        };
    }

    private static string EscapeJsonString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion

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
