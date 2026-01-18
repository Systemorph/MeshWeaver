using System.ComponentModel;
using System.Reactive.Linq;
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
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;

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
    public const string FilesArea = "Files";
    public const string ChildrenArea = "Children";
    public const string NodeTypesArea = "NodeTypes";
    public const string AccessControlArea = "AccessControl";

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
            .AddLayout(layout => layout.AddDefaultViews());

    public static LayoutDefinition AddDefaultViews(this LayoutDefinition layout)
        => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(ThumbnailArea, Thumbnail)
            .WithView(MetadataArea, Metadata)
            .WithView(SettingsArea, Settings)
            .WithView(CatalogArea, Catalog)
            .WithView(FilesArea, Files)
            .WithView(ChildrenArea, Children)
            .WithView(NodeTypesArea, NodeTypes)
            .WithView(AccessControlArea, AccessControl)
            // UCR special areas - $Content is registered by ContentCollectionsExtensions.AddContentCollections
            .WithView(DataArea, Data)
            .WithView(SchemaArea, Schema)
            .WithView(DefaultViews.EditArea, DefaultViews.Edit)
            .WithView(ModelArea, DataModelLayoutArea.DataModel);

    /// <summary>
    /// Renders the Details area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    /// Uses GetStream for node data. Children are loaded via ChildrenQuery from NodeTypeDefinition
    /// if set, otherwise via IMeshQuery with scope:children.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Get the NodeTypeDefinition from the workspace stream (for ChildrenQuery)
        var nodeTypeDefStream = host.Workspace.GetStream<NodeTypeDefinition>()?.Select(defs => defs?.FirstOrDefault())
            ?? Observable.Return<NodeTypeDefinition?>(null);

        // Combine streams to get both node and type definition
        var combinedStream = nodeStream.CombineLatest(nodeTypeDefStream, (nodes, typeDef) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (Node: node, TypeDef: typeDef);
        });

        // Load children based on ChildrenQuery or default IMeshQuery with scope:children
        return combinedStream.SelectMany(async data =>
        {
            var childrenQuery = data.TypeDef?.ChildrenQuery;
            IReadOnlyList<MeshNode> children;

            if (!string.IsNullOrEmpty(childrenQuery) && meshQuery != null)
            {
                // Use QueryAsync with ChildrenQuery
                // Replace {path} placeholder with actual path
                var query = childrenQuery.Replace("{path}", hubPath);
                try
                {
                    children = await meshQuery.QueryAsync<MeshNode>(query).ToListAsync();
                }
                catch
                {
                    children = Array.Empty<MeshNode>();
                }
            }
            else
            {
                // Default: use IMeshQuery with scope:children
                if (meshQuery != null)
                {
                    try
                    {
                        children = await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} scope:children -nodeType:NodeType").ToListAsync();
                    }
                    catch
                    {
                        children = Array.Empty<MeshNode>();
                    }
                }
                else
                {
                    children = Array.Empty<MeshNode>();
                }
            }

            return BuildDetailsContent(host, data.Node, children, data.TypeDef);
        });
    }

    private static UiControl BuildDetailsContent(this LayoutAreaHost host, MeshNode? node, IEnumerable<MeshNode> children, NodeTypeDefinition? typeDef)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("position: relative;");

        // Header: icon + title on left, menu button on right
        var title = node?.Name ?? host.Hub.Address.ToString();
        var imageUrl = node != null ? GetNodeImageUrl(node) : null;

        // Build title with icon
        var titleContent = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 16px;");

        // Add icon/image if available
        if (!string.IsNullOrEmpty(imageUrl))
        {
            titleContent = titleContent.WithView(Controls.Html(
                $"<img src=\"{imageUrl}\" alt=\"\" style=\"width: 48px; height: 48px; border-radius: 8px; object-fit: cover;\" />"));
        }
        else if (!string.IsNullOrEmpty(node?.Icon) && !node.Icon.StartsWith("data:") && !node.Icon.StartsWith("http"))
        {
            // FluentUI icon name
            titleContent = titleContent.WithView(Controls.Html(
                $"<fluent-icon name=\"{node.Icon}\" style=\"font-size: 48px; color: var(--accent-fill-rest);\"></fluent-icon>"));
        }

        titleContent = titleContent.WithView(Controls.Html($"<h1 style=\"margin: 0;\">{title}</h1>"));

        // Action menu positioned at top-right of content
        var actionMenu = Controls.Stack
            .WithStyle("position: absolute; top: 0; right: 0; z-index: 10;")
            .WithView(BuildActionMenu(host, node));

        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; padding-bottom: 24px; margin-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest);")
            .WithView(titleContent);

        stack = stack.WithView(actionMenu);  // Add action menu first (positioned absolutely)
        stack = stack.WithView(headerStack);

        // Main content based on node type
        // For Markdown type: renders content directly
        // For other types: generates markdown table from properties
        var content = GetNodeContentDisplay(node, host.Hub.JsonSerializerOptions);
        if (!string.IsNullOrWhiteSpace(content))
        {
            stack = stack.WithView(new MarkdownControl(content));
        }

        // Child node sections using a unified grid
        // Controlled by NodeTypeDefinition.ShowChildrenInDetails and DetailsChildrenLimit
        var showChildren = typeDef?.ShowChildrenInDetails ?? true;
        var childrenLimit = typeDef?.DetailsChildrenLimit ?? 10;

        if (showChildren)
        {
            // Group by Category if set, otherwise by NodeType
            var childGroups = children
                .Where(c => !string.IsNullOrEmpty(c.Category) || !string.IsNullOrEmpty(c.NodeType))
                .GroupBy(c => c.Category ?? c.NodeType!)
                .OrderBy(g => g.Key)
                .ToList();

            if (childGroups.Count > 0)
            {
                // Single unified grid for all child types
                var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

                foreach (var group in childGroups)
                {
                    var recentNodes = group.Take(childrenLimit).ToList();
                    var displayName = GetGroupDisplayName(group.Key, group.Count());

                    // Section header spans full width
                    grid = grid.WithView(
                        Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                        itemSkin => itemSkin.WithXs(12));

                    // Thumbnails in grid: xs=12, sm=6, md=4, lg=3
                    foreach (var child in recentNodes)
                    {
                        grid = grid.WithView(
                            BuildThumbnailContent(child, child.Namespace ?? ""),
                            itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
                    }

                    // "Show more" button if there are more than the limit
                    if (group.Count() > childrenLimit)
                    {
                        var showMoreHref = $"/{nodePath}/{MeshCatalogView.NodesArea}/{group.Key}";
                        grid = grid.WithView(
                            Controls.Button($"Show all {group.Count()}")
                                .WithAppearance(Appearance.Lightweight)
                                .WithNavigateToHref(showMoreHref),
                            itemSkin => itemSkin.WithXs(12));
                    }
                }

                stack = stack.WithView(grid);
            }
        }

        // Comments section at the bottom (only if comments are enabled)
        if (host.Hub.Configuration.HasComments())
        {
            stack = stack.WithView(CommentsView.BuildInlineCommentsSection(host));
        }

        return stack;
    }

    /// <summary>
    /// Builds a dropdown action menu with Edit, Comments, Files, Metadata, NodeType, Catalog, Settings.
    /// Uses icon-only mode to show just the ellipsis button without a chevron.
    /// Uses NavLinkControl for instant navigation via href.
    /// </summary>
    [Browsable(false)]
    public static UiControl BuildActionMenu(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();

        // Start with the trigger button (MoreHorizontal icon) - icon-only mode hides the chevron
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Edit option - goes to DefaultViews.Edit area
        if (node != null)
        {
            var editHref = $"/{nodePath}/{DefaultViews.EditArea}";
            menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));
        }

        // Comments option (only if comments are enabled)
        if (host.Hub.Configuration.HasComments())
        {
            var commentsHref = $"/{nodePath}/{CommentsArea}";
            menu = menu.WithView(new NavLinkControl("Comments", FluentIcons.Comment(IconSize.Size16), commentsHref));
        }

        // Files option (Content folder)
        var filesHref = $"/{nodePath}/{FilesArea}";
        menu = menu.WithView(new NavLinkControl("Files", FluentIcons.Folder(IconSize.Size16), filesHref));

        // Metadata option
        var metadataHref = $"/{nodePath}/{MetadataArea}";
        menu = menu.WithView(new NavLinkControl("Metadata", FluentIcons.Info(IconSize.Size16), metadataHref));


        // Catalog option
        var catalogHref = $"/{nodePath}/{CatalogArea}";
        menu = menu.WithView(new NavLinkControl("Catalog", FluentIcons.Grid(IconSize.Size16), catalogHref));

        // Node Types option
        var nodeTypesHref = $"/{nodePath}/{NodeTypesArea}";
        menu = menu.WithView(new NavLinkControl("Node Types", FluentIcons.Document(IconSize.Size16), nodeTypesHref));

        // Settings option
        var settingsHref = $"/{nodePath}/{SettingsArea}";
        menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), settingsHref));

        // Access Control option
        var accessControlHref = $"/{nodePath}/{AccessControlArea}";
        menu = menu.WithView(new NavLinkControl("Access Control", FluentIcons.Shield(IconSize.Size16), accessControlHref));

        return menu;
    }

    /// <summary>
    /// Builds a children section showing child nodes grouped by type in a responsive grid.
    /// Use this in custom views to display subnodes below your custom header.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="children">The list of child nodes to display.</param>
    /// <param name="nodePath">The parent node path (for "show more" links).</param>
    /// <param name="childrenLimit">Maximum children per type to show (default 10).</param>
    /// <returns>A LayoutGrid control with children, or null if no children.</returns>
    [Browsable(false)]
    public static UiControl? BuildChildrenSection(
        LayoutAreaHost host,
        IEnumerable<MeshNode> children,
        string nodePath,
        int childrenLimit = 10)
    {
        // Group by Category if set, otherwise by NodeType
        var childGroups = children
            .Where(c => !string.IsNullOrEmpty(c.Category) || !string.IsNullOrEmpty(c.NodeType))
            .GroupBy(c => c.Category ?? c.NodeType!)
            .OrderBy(g => g.Key)
            .ToList();

        if (childGroups.Count == 0)
            return null;

        // Single unified grid for all child types
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var group in childGroups)
        {
            var recentNodes = group.Take(childrenLimit).ToList();
            var displayName = GetGroupDisplayName(group.Key, group.Count());

            // Section header spans full width
            grid = grid.WithView(
                Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                itemSkin => itemSkin.WithXs(12));

            // Thumbnails in grid: xs=12, sm=6, md=4, lg=3
            foreach (var child in recentNodes)
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(child, child.Namespace ?? ""),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
            }

            // "Show more" button if there are more than the limit
            if (group.Count() > childrenLimit)
            {
                var showMoreHref = $"/{nodePath}/{MeshCatalogView.NodesArea}/{group.Key}";
                grid = grid.WithView(
                    Controls.Button($"Show all {group.Count()}")
                        .WithAppearance(Appearance.Lightweight)
                        .WithNavigateToHref(showMoreHref),
                    itemSkin => itemSkin.WithXs(12));
            }
        }

        return grid;
    }

    /// <summary>
    /// Gets the display name for a node type with count (e.g., "Project (5)").
    /// </summary>
    public static string GetGroupDisplayName(string nodeType, int count)
    {
        // Extract just the last segment if it's a path
        var typeName = nodeType.Contains('/') ? nodeType.Split('/').Last() : nodeType;
        // Capitalize first letter
        var display = char.ToUpper(typeName[0]) + typeName.Substring(1);
        return $"{display} ({count})";
    }

    /// <summary>
    /// Renders a compact thumbnail/card view of a node for use in catalogs and lists.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    [Browsable(false)]
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

    /// <summary>
    /// Renders the Metadata area showing node properties (name, type, description, path).
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    [Browsable(false)]
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
                .WithNavigateToHref(backHref)));

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
                    .WithNavigateToHref(parentHref)));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Settings area showing node properties and types catalog.
    /// Provides read-only view of node metadata with embedded catalog of NodeType children.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    [Browsable(false)]
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
        });
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
            stack = stack.WithView(Controls.Html($"<h3 style=\"margin: 32px 0 16px 0;\">Types</h3>"));

            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
            foreach (var typeNode in nodeTypes.OrderBy(t => t.Name))
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(typeNode, typeNode.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
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

        // Handle MarkdownContent (from MarkdownFileParser)
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

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
    /// Gets the content display for a node.
    /// For Markdown type: renders content directly as markdown.
    /// For other types: generates markdown table from properties.
    /// </summary>
    private static string GetNodeContentDisplay(MeshNode? node, JsonSerializerOptions jsonOptions)
    {
        if (node?.Content == null)
            return node?.Description ?? string.Empty;

        // Check for Markdown type - render content directly
        if (node.NodeType?.Equals("Markdown", StringComparison.OrdinalIgnoreCase) == true)
        {
            return GetNodeContent(node);
        }

        // Check if content is pure markdown text (MarkdownDocument type)
        if (node.Content is JsonElement jsonElement)
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

        // For other types, generate markdown table from properties
        return GenerateContentMarkdown(node.Content, jsonOptions);
    }

    /// <summary>
    /// Generates markdown representation of content (tables for properties).
    /// </summary>
    private static string GenerateContentMarkdown(object content, JsonSerializerOptions jsonOptions)
    {
        var sb = new System.Text.StringBuilder();

        if (content is JsonElement json)
        {
            GenerateJsonMarkdown(json, sb, 0);
        }
        else
        {
            // Use reflection to get properties
            var type = content.GetType();
            var properties = type.GetProperties()
                .Where(p => p.CanRead && p.Name != "$type")
                .OrderBy(p => p.Name);

            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");

            foreach (var prop in properties)
            {
                var value = prop.GetValue(content);
                var displayValue = FormatPropertyValue(value, jsonOptions);
                sb.AppendLine($"| **{prop.Name}** | {displayValue} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates markdown from a JsonElement, with sub-objects as separate tables.
    /// </summary>
    private static void GenerateJsonMarkdown(JsonElement json, System.Text.StringBuilder sb, int depth)
    {
        // Skip $type property and gather properties
        var properties = json.EnumerateObject()
            .Where(p => p.Name != "$type")
            .OrderBy(p => p.Name)
            .ToList();

        // Separate simple values from complex objects
        var simpleProps = properties.Where(p => p.Value.ValueKind != JsonValueKind.Object && p.Value.ValueKind != JsonValueKind.Array).ToList();
        var complexProps = properties.Where(p => p.Value.ValueKind == JsonValueKind.Object || p.Value.ValueKind == JsonValueKind.Array).ToList();

        // Render simple properties as table
        if (simpleProps.Count > 0)
        {
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");

            foreach (var prop in simpleProps)
            {
                var displayValue = FormatJsonValue(prop.Value);
                sb.AppendLine($"| **{prop.Name}** | {displayValue} |");
            }
        }

        // Render complex properties as sub-tables
        foreach (var prop in complexProps)
        {
            sb.AppendLine();
            sb.AppendLine($"### {prop.Name}");
            sb.AppendLine();

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                GenerateJsonMarkdown(prop.Value, sb, depth + 1);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                // For arrays, list items
                var items = prop.Value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    sb.AppendLine("*Empty array*");
                }
                else if (items.All(i => i.ValueKind == JsonValueKind.Object))
                {
                    // Array of objects - render each as a sub-table
                    for (int i = 0; i < items.Count; i++)
                    {
                        sb.AppendLine($"**Item {i + 1}:**");
                        sb.AppendLine();
                        GenerateJsonMarkdown(items[i], sb, depth + 1);
                        sb.AppendLine();
                    }
                }
                else
                {
                    // Array of simple values - render as list
                    foreach (var item in items)
                    {
                        sb.AppendLine($"- {FormatJsonValue(item)}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Formats a JSON value for display in markdown.
    /// </summary>
    private static string FormatJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "*null*",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null => "*null*",
            JsonValueKind.Object => "*object*",
            JsonValueKind.Array => $"*{value.GetArrayLength()} items*",
            _ => value.GetRawText()
        };
    }

    /// <summary>
    /// Formats a property value for display in markdown.
    /// </summary>
    private static string FormatPropertyValue(object? value, JsonSerializerOptions jsonOptions)
    {
        if (value == null)
            return "*null*";

        return value switch
        {
            string s => s,
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            IEnumerable<object> enumerable => $"*{enumerable.Count()} items*",
            _ when value.GetType().IsValueType => value.ToString() ?? "*null*",
            _ => "*complex object*"
        };
    }

    /// <summary>
    /// Renders the Catalog view showing nodes as thumbnails with search.
    /// Uses MeshSearchControl for unified search and display.
    /// For NodeType nodes, shows instances of that type (nodeType:name scope:subtree).
    /// For instance nodes, uses CatalogQuery if set, otherwise defaults to scope:children.
    /// Excludes NodeType nodes from results (use NodeTypes area to view those).
    /// Render mode is determined by CatalogMode property (hierarchical or grouped).
    /// Reads search term from ?q= query parameter.
    /// </summary>
    [Browsable(false)]
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
            if (isNodeTypeMode && node != null)
            {
                var nodeTypePath = node.Path;
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

            // Instance node catalog - excludes NodeType nodes
            var instanceHiddenQuery = node?.CatalogQuery ?? $"path:{node?.Namespace ?? hubPath} scope:children -nodeType:NodeType";

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
    /// Renders the Children view showing child nodes as thumbnails without search.
    /// Groups children by Category (or NodeType if no Category), excludes NodeType nodes.
    /// Use this for embedding children in custom views via LayoutAreaControl.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Children(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: #888;\">Query service not available.</p>"));
        }

        return Observable.FromAsync(async () =>
        {
            IReadOnlyList<MeshNode> children;
            try
            {
                children = await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} scope:children -nodeType:NodeType").ToListAsync();
            }
            catch
            {
                children = Array.Empty<MeshNode>();
            }

            if (children.Count == 0)
            {
                return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No children found.</p>");
            }

            // Group by Category if set, otherwise by NodeType
            var childGroups = children
                .GroupBy(c => c.Category ?? c.NodeType ?? "")
                .OrderBy(g => g.Key)
                .ToList();

            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var group in childGroups)
            {
                var groupKey = group.Key;
                var groupChildren = group.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name).ToList();

                // Add section header if there's a group key
                if (!string.IsNullOrEmpty(groupKey))
                {
                    var displayName = GetGroupDisplayName(groupKey, groupChildren.Count);
                    grid = grid.WithView(
                        Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                        itemSkin => itemSkin.WithXs(12));
                }

                foreach (var child in groupChildren)
                {
                    grid = grid.WithView(
                        MeshNodeThumbnailControl.FromNode(child, child.Path),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
                }
            }

            return (UiControl?)grid;
        });
    }

    /// <summary>
    /// Renders the NodeTypes view showing NodeType nodes defined at this level.
    /// Shows the node's own type (if any) and any NodeType children.
    /// Accessible from the menu as a separate page.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> NodeTypes(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: #888;\">Query service not available.</p>"));
        }

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);

            // Query for NodeType children at this level
            IReadOnlyList<MeshNode> nodeTypeChildren;
            try
            {
                nodeTypeChildren = await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:NodeType scope:children").ToListAsync();
            }
            catch
            {
                nodeTypeChildren = Array.Empty<MeshNode>();
            }

            // Query for the node's own NodeType definition (if it has one)
            MeshNode? ownType = null;
            if (node != null && !string.IsNullOrEmpty(node.NodeType))
            {
                try
                {
                    ownType = await meshQuery.QueryAsync<MeshNode>($"path:{node.NodeType} scope:exact").FirstOrDefaultAsync();
                }
                catch { }
            }

            var hasOwnType = ownType != null;
            var hasNodeTypeChildren = nodeTypeChildren.Count > 0;

            if (!hasOwnType && !hasNodeTypeChildren)
            {
                return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No node types defined at this level.</p>");
            }

            var stack = Controls.Stack.WithWidth("100%");

            // Own type section
            if (hasOwnType)
            {
                stack = stack.WithView(Controls.Html($"<h3 style=\"margin: 0 0 16px 0;\">Type of {node?.Name ?? "this node"}</h3>"));
                var ownTypeGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));
                ownTypeGrid = ownTypeGrid.WithView(
                    MeshNodeThumbnailControl.FromNode(ownType!, ownType!.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
                stack = stack.WithView(ownTypeGrid);
            }

            // NodeType children section
            if (hasNodeTypeChildren)
            {
                if (hasOwnType)
                {
                    stack = stack.WithView(Controls.Html("<div style=\"margin: 24px 0;\"></div>")); // Spacer
                }
                stack = stack.WithView(Controls.Html($"<h3 style=\"margin: 0 0 16px 0;\">Types in {node?.Namespace ?? hubPath}</h3>"));

                var typesGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));
                foreach (var typeNode in nodeTypeChildren.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name))
                {
                    // Skip if it's the same as own type
                    if (ownType != null && typeNode.Path == ownType.Path)
                        continue;

                    typesGrid = typesGrid.WithView(
                        MeshNodeThumbnailControl.FromNode(typeNode, typeNode.Path),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
                }
                stack = stack.WithView(typesGrid);
            }

            return (UiControl?)stack;
        });
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

    /// <summary>
    /// Renders a file browser for the node's content directory.
    /// Uses FileBrowserControl to display and manage files in the content collection.
    /// </summary>
    [Browsable(false)]
    public static UiControl Files(LayoutAreaHost host, RenderingContext _)
    {
        return new FileBrowserControl("content")
            .WithTopLevel(host.Hub.Address.ToString());

    }

    #region UCR Special Areas

    /// <summary>
    /// Renders content from the node's content collection.
    /// For images: renders inline. For markdown: renders the content.
    /// For other files: shows a download link.
    /// For self-reference (no path): shows the node's icon/logo.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext context)
    {
        var contentPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(contentPath))
        {
            // Self-reference: show the node's icon/logo
            var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
                ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

            return nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                if (node == null)
                    return (UiControl?)Controls.Markdown($"*Node not found: {hubPath}*");

                return (UiControl?)RenderNodeIcon(node, hubPath);
            });
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
    [Browsable(false)]
    public static IObservable<UiControl?> Data(LayoutAreaHost host, RenderingContext context)
    {
        var dataPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(dataPath))
        {
            // Self-reference: show the current MeshNode data as JSON
            var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
                ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

            return nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                if (node == null)
                    return (UiControl?)Controls.Markdown($"*Node not found: {hubPath}*");

                return (UiControl?)RenderMeshNodeData(node, host.Hub.JsonSerializerOptions);
            });
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
    [Browsable(false)]
    public static IObservable<UiControl?> Schema(LayoutAreaHost host, RenderingContext context)
    {
        var typeName = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(typeName))
        {
            // Self-reference: show MeshNode schema and content type schema
            var jsonOptions = host.Hub.JsonSerializerOptions;
            var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
                ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

            return nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return (UiControl?)RenderNodeSchema(node, hubPath, jsonOptions);
            });
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
        // Use the built-in JsonSchemaExporter from System.Text.Json.Schema
        var options = jsonOptions ?? JsonSerializerOptions.Default;

        var schema = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (ctx, node) =>
            {
                // Add documentation from XML docs using Namotion.Reflection
                if (ctx.TypeInfo.Type == type)
                {
                    // Add title for the main type
                    node["title"] = type.Name;

                    // Add description for the main type
                    var typeDescription = type.GetXmlDocsSummary();
                    if (!string.IsNullOrEmpty(typeDescription))
                    {
                        node["description"] = typeDescription;
                    }
                }

                // Add descriptions for properties
                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    // Get the actual PropertyInfo from the declaring type
                    var declaringType = ctx.PropertyInfo.DeclaringType;
                    var propertyName = ctx.PropertyInfo.Name;
                    var actualPropertyInfo = declaringType.GetProperty(propertyName.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = actualPropertyInfo.GetXmlDocsSummary();
                        if (!string.IsNullOrEmpty(propertyDescription))
                        {
                            jsonObj["description"] = propertyDescription;
                        }
                    }
                }

                return node;
            }
        });

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    #endregion

    #region Access Control

    /// <summary>
    /// Renders the Access Control area for managing user roles and permissions on this node.
    /// Shows current role assignments and allows adding/removing users with specific roles.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> AccessControl(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();

        if (securityService == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithView(
                    Controls.Html("<p style=\"color: var(--warning-color);\">Row-Level Security is not enabled. Add .AddRowLevelSecurity() to your mesh configuration.</p>")
                )
            );
        }

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Namespace == hubPath || n.Path == hubPath);
            return await BuildAccessControlContentAsync(host, securityService, node, hubPath);
        });
    }

    private static async Task<UiControl> BuildAccessControlContentAsync(
        LayoutAreaHost host,
        ISecurityService securityService,
        MeshNode? node,
        string nodePath)
    {
        var stack = Controls.Stack.WithStyle("padding: 24px; gap: 24px;");

        // Header
        var headerText = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? nodePath;
        stack = stack.WithView(Controls.H2($"Access Control - {headerText}"));

        // Get users with access to this namespace
        var usersWithAccess = new List<UserAccess>();
        await foreach (var userAccess in securityService.GetUsersWithAccessToNamespaceAsync(nodePath))
        {
            usersWithAccess.Add(userAccess);
        }

        // Users table section
        stack = stack.WithView(Controls.H3("Users with Access"));

        if (usersWithAccess.Count > 0)
        {
            // Build view models for the table
            // Note: In the simplified model, scope distinction requires additional tracking
            var viewModels = usersWithAccess
                .Select(u => new AccessControlViewModel(u))
                .OrderBy(vm => vm.UserId)
                .ToList();

            // Store data in workspace and create grid with reference
            var dataId = Guid.NewGuid().AsString();
            host.UpdateData(dataId, viewModels);

            var grid = new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId)))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(AccessControlViewModel.UserId) }.WithTitle("User"))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(AccessControlViewModel.DisplayName) }.WithTitle("Display Name"))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(AccessControlViewModel.RolesDisplay) }.WithTitle("Roles"))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(AccessControlViewModel.ScopeDisplay) }.WithTitle("Scope"));

            stack = stack.WithView(grid);
        }
        else
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">No users have access to this namespace. " +
                "Create Access data files in the Access partition to grant access.</p>"));
        }

        // Help section
        stack = stack.WithView(Controls.Html(
            "<div style=\"margin-top: 24px; padding: 16px; background: var(--neutral-layer-2); border-radius: 8px;\">" +
            "<h4 style=\"margin: 0 0 8px 0;\">Managing Access</h4>" +
            "<p style=\"margin: 0;\">Access is managed via JSON files in the <code>Access</code> partition. " +
            "Each user has a file with their roles:</p>" +
            "<pre style=\"margin: 8px 0; padding: 8px; background: var(--neutral-layer-1); border-radius: 4px;\">" +
            "{\n" +
            "  \"userId\": \"Alice\",\n" +
            "  \"displayName\": \"Alice Chen\",\n" +
            "  \"roles\": [\n" +
            "    { \"roleId\": \"Editor\", \"namespace\": \"ACME\" }\n" +
            "  ]\n" +
            "}</pre>" +
            "<p style=\"margin: 8px 0 0 0;\"><strong>Roles:</strong> Admin (full access), Editor (read/create/update), Viewer (read only)</p>" +
            "<p style=\"margin: 8px 0 0 0;\"><strong>Inheritance:</strong> Roles on a parent namespace apply to all children.</p>" +
            "</div>"));

        return stack;
    }

    #endregion
}

/// <summary>
/// View model for displaying user access in the Access Control DataGrid.
/// </summary>
public record AccessControlViewModel
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RolesDisplay { get; init; } = string.Empty;
    public string ScopeDisplay { get; init; } = string.Empty;

    public AccessControlViewModel() { }

    /// <summary>
    /// Creates a view model from a UserAccess record.
    /// In the simplified model, roles are stored per-namespace so the scope
    /// is determined by which partition the UserAccess was retrieved from.
    /// </summary>
    /// <param name="userAccess">The user access record</param>
    /// <param name="scope">The scope of this access: "Global", "Direct", or "Inherited from {namespace}"</param>
    public AccessControlViewModel(UserAccess userAccess, string scope = "Direct")
    {
        UserId = userAccess.UserId;
        DisplayName = userAccess.DisplayName ?? userAccess.UserId;
        RolesDisplay = string.Join(", ", userAccess.Roles.Select(r => r.RoleId).Distinct());
        ScopeDisplay = scope;
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
