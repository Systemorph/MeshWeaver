using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Json.More;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Reflection;

namespace MeshWeaver.Graph;

/// <summary>
/// Marker record indicating that the catalog should operate in NodeType mode.
/// When set, the catalog reads NodeTypeDefinition from workspace to build the query dynamically.
/// </summary>
public record NodeTypeCatalogMode;

/// <summary>
/// Layout areas for mesh node content.
/// - Overview: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, description, path)
/// - Settings: Node settings with NodeType link navigation
/// - Children: Child nodes grouped by type
/// </summary>
public static class MeshNodeLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string ThumbnailArea = "Thumbnail";
    public const string MetadataArea = "Metadata";
    public const string SettingsArea = "Settings";
    public const string CommentsArea = "Comments";
    public const string SearchArea = "Search";
    public const string FilesArea = "Files";
    public const string ChildrenArea = "Children";
    public const string NodeTypesArea = "NodeTypes";
    public const string AccessControlArea = "AccessControl";
    public const string CreateNodeArea = "Create";
    public const string EditArea = "Edit";

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
    public static MessageHubConfiguration AddDefaultLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout.AddDefaultLayoutAreas());

    public static LayoutDefinition AddDefaultLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            .WithView(ThumbnailArea, Thumbnail)
            .WithView(MetadataArea, Metadata)
            .WithView(SettingsArea, Settings)
            .WithView(SearchArea, Search)
            .WithView(FilesArea, Files)
            .WithView(ChildrenArea, Children)
            .WithView(NodeTypesArea, NodeTypes)
            .WithView(AccessControlArea, AccessControl)
            .WithView(CreateNodeArea, CreateNode)
            // UCR special areas
            .WithView(DataArea, Data)
            .WithView(SchemaArea, Schema)
            .WithView(EditArea, Edit)
            .WithView(ModelArea, DataModelLayoutArea.DataModel)
            .AddDomainLayoutAreas();

    /// <summary>
    /// Renders the Overview area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    /// Uses GetStream for node data. Children are displayed via LayoutAreaControl.Children.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Get the NodeTypeDefinition from the workspace stream (for ShowChildrenInDetails)
        var nodeTypeDefStream = host.Workspace.GetStream<NodeTypeDefinition>()?.Select(defs => defs?.FirstOrDefault())
            ?? Observable.Return<NodeTypeDefinition?>(null);

        // Combine streams to get both node and type definition
        return nodeStream.CombineLatest(nodeTypeDefStream, (nodes, typeDef) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildDetailsContent(host, node, typeDef);
        });
    }

    private static UiControl BuildDetailsContent(this LayoutAreaHost host, MeshNode? node, NodeTypeDefinition? typeDef)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("position: relative;");

        // Header: icon + title on left, menu button on right
        var title = node?.Name ?? host.Hub.Address.ToString();
        var iconValue = node?.Icon;

        // Build title with icon
        var titleContent = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 16px;");

        // Add icon/image if available
        if (!string.IsNullOrEmpty(iconValue))
        {
            if (iconValue.StartsWith("data:") || iconValue.StartsWith("http") || iconValue.StartsWith("/"))
            {
                // Data URI, HTTP URL, or relative path - use as image
                titleContent = titleContent.WithView(Controls.Html(
                    $"<img src=\"{iconValue}\" alt=\"\" style=\"width: 48px; height: 48px; border-radius: 8px; object-fit: cover;\" />"));
            }
            else if (iconValue.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                // Inline SVG - render directly with size constraints
                titleContent = titleContent.WithView(Controls.Html(
                    $"<div style=\"width: 48px; height: 48px; display: flex; align-items: center; justify-content: center;\">{iconValue}</div>"));
            }
            else
            {
                // FluentUI icon name - use Controls.Icon
                titleContent = titleContent.WithView(
                    Controls.Icon(iconValue).WithStyle("font-size: 48px; color: var(--accent-fill-rest);"));
            }
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
        // Resolve content type from the actual content using $type property
        Type? contentType = null;
        var typeRegistry = host.Hub.ServiceProvider.GetService<ITypeRegistry>();

        if (node?.Content != null)
        {
            if (node.Content is JsonElement jsonElement)
            {
                // Get $type from JSON and resolve via TypeRegistry
                if (jsonElement.TryGetProperty("$type", out var typeProperty))
                {
                    var typeName = typeProperty.GetString();
                    if (!string.IsNullOrEmpty(typeName) && typeRegistry != null)
                    {
                        contentType = typeRegistry.GetType(typeName);
                    }
                }
            }
            else
            {
                // Content is already deserialized - use its runtime type
                contentType = node.Content.GetType();
            }
        }

        // Render content display if we have a valid content type (not MeshNode itself)
        if (contentType != null && contentType != typeof(MeshNode) && node?.Content != null)
        {
            var contentDisplay = BuildContentTypeDisplay(host, node, contentType);
            stack = stack.WithView(contentDisplay);
        }
        else
        {
            // Fall back to markdown display for Markdown nodes or plain content
            var content = GetNodeContentDisplay(node, host.Hub.JsonSerializerOptions);
            if (!string.IsNullOrWhiteSpace(content))
            {
                stack = stack.WithView(new MarkdownControl(content));
            }
        }

        // Child node sections using LayoutAreaControl.Children
        // Controlled by NodeTypeDefinition.ShowChildrenInDetails
        var showChildren = typeDef?.ShowChildrenInDetails ?? true;

        if (showChildren)
        {
            stack = stack.WithView(LayoutAreaControl.Children(host.Hub));
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

        // Create option - goes to CreateNode area (first item in menu)
        var createHref = $"/{nodePath}/{CreateNodeArea}";
        menu = menu.WithView(new NavLinkControl("Create", FluentIcons.Add(IconSize.Size16), createHref));

        // Edit option - goes to Edit area
        if (node != null)
        {
            var editHref = $"/{nodePath}/{EditArea}";
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


        // Search option
        var searchHref = $"/{nodePath}/{SearchArea}";
        menu = menu.WithView(new NavLinkControl("Search", FluentIcons.Grid(IconSize.Size16), searchHref));

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
        var backHref = $"/{nodePath}/{OverviewArea}";
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
            var parentHref = $"/{node.ParentPath}/{OverviewArea}";
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
    /// Builds a UI display for content type properties with inline editing support.
    /// Regular properties use inline click-to-edit pattern with auto-save on blur.
    /// Markdown/SeparateEditView properties use click-to-edit (MarkdownControl -> MarkdownEditorControl).
    /// </summary>
    private static UiControl BuildContentTypeDisplay(LayoutAreaHost host, MeshNode node, Type contentType)
    {
        var nodePath = node.Namespace ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%");

        // Deserialize content to the actual type if it's a JsonElement
        object? content = node.Content;
        if (content is JsonElement jsonElement)
        {
            try
            {
                content = jsonElement.Deserialize(contentType, host.Hub.JsonSerializerOptions);
            }
            catch
            {
                // If deserialization fails, keep as JsonElement
            }
        }

        // Get all browsable properties
        var properties = contentType.GetProperties()
            .Where(p => p.CanRead && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .ToList();

        // Separate properties into regular and markdown with SeparateEditView
        // Skip "Title" and "Name" properties since they're already shown in the header
        var titlePropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title", "Name", "DisplayName" };
        var regularProperties = new List<PropertyInfo>();
        var separateViewProperties = new List<PropertyInfo>();

        foreach (var prop in properties)
        {
            // Skip title-like properties (already shown in header)
            if (titlePropertyNames.Contains(prop.Name))
                continue;

            var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();
            if (uiControlAttr?.SeparateEditView == true)
            {
                separateViewProperties.Add(prop);
            }
            else
            {
                regularProperties.Add(prop);
            }
        }

        // Render regular properties with inline editing
        // Use a responsive grid layout: 3 columns on large screens, 2 on medium, 1 on small
        if (regularProperties.Any() && content != null)
        {
            var dataId = $"content_{node.Path?.Replace("/", "_") ?? Guid.NewGuid().ToString("N")}";

            // Initialize data stream with current content
            host.UpdateData(dataId, content);

            // Create a responsive grid for properties (label + control stacked within each cell)
            // Using LayoutGrid with responsive columns: xs=12 (1 per row), md=6 (2 per row), lg=4 (3 per row)
            var propsGrid = Controls.LayoutGrid
                .WithSkin(s => s.WithSpacing(2))
                .WithStyle(s => s
                    .WithPadding("12px 0")
                    .WithWidth("100%")) with { DataContext = LayoutAreaReference.GetDataPointer(dataId) };

            foreach (var prop in regularProperties)
            {
                var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
                    ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                    ?? prop.Name.Wordify();

                // Each property gets a cell with label on top, control below
                var propCell = Controls.Stack
                    .WithStyle(s => s.WithPadding("4px 8px"));

                // Label
                propCell = propCell.WithView(
                    Controls.Label($"{displayName}")
                        .WithStyle(s => s
                            .WithFontWeight("600")
                            .WithColor("var(--neutral-foreground-hint)")
                            .WithFontSize("0.875rem")
                            .WithPadding("0 0 4px 0")));

                // Form control - starts in readonly mode, click enables editing
                var propControl = CreateInlineEditableFormControl(host, prop, content, contentType, nodePath, node, dataId);
                propCell = propCell.WithView(propControl);

                // Responsive: 1 column on xs, 2 on md, 3 on lg
                propsGrid = propsGrid.WithView(propCell, itemSkin => itemSkin.WithXs(12).WithMd(6).WithLg(4));
            }

            stack = stack.WithView(propsGrid);

            // Set up auto-save when data stream changes (debounced)
            SetupAutoSave(host, dataId, node, nodePath, contentType);
        }

        // Render markdown/separate view fields - click-to-edit pattern (control type changes)
        foreach (var prop in separateViewProperties)
        {
            var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
                ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                ?? prop.Name.Wordify();

            var value = GetPropertyValue(content, prop);
            var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();

            // Section header
            var headerLabel = Controls.Label(displayName)
                .WithStyle("font-size: 16px; font-weight: 600; margin-bottom: 12px; padding-bottom: 8px; border-bottom: 1px solid var(--neutral-stroke-rest); display: block;");

            // Render read control directly with click action
            var readControl = CreateSeparatePropertyReadControl(prop, value, uiControlAttr, content, contentType, nodePath);

            stack = stack.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle("background: var(--neutral-fill-rest); border-radius: 8px; padding: 16px 20px; margin-bottom: 16px;")
                    .WithView(headerLabel)
                    .WithView(readControl));
        }

        return stack;
    }

    /// <summary>
    /// Creates an inline editable form control for a property.
    /// The control starts in readonly mode and becomes editable on click.
    /// This provides a consistent look (always shows the form control) and avoids visual switching.
    /// </summary>
    private static UiControl CreateInlineEditableFormControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        object? content,
        Type contentType,
        string nodePath,
        MeshNode node,
        string dataId)
    {
        // Check if property is editable
        var isReadonly = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false
                         || prop.HasAttribute<KeyAttribute>();

        var propName = prop.Name.ToCamelCase()!;
        var jsonPointerReference = new JsonPointerReference(propName);
        var isRequired = prop.HasAttribute<RequiredMemberAttribute>() || prop.HasAttribute<RequiredAttribute>();
        var propType = prop.PropertyType;

        // Create the form control based on type
        UiControl formControl;

        // Check for UiControlAttribute first
        var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttr != null)
        {
            formControl = CreateControlFromUiControlAttribute(host, uiControlAttr, prop, jsonPointerReference, isRequired);
        }
        // Check for DimensionAttribute
        else if (prop.GetCustomAttribute<DimensionAttribute>() is { } dimensionAttr)
        {
            formControl = CreateDimensionSelectControl(host, prop, jsonPointerReference, dimensionAttr, isRequired, dataId);
        }
        // Handle based on property type
        else if (propType.IsNumber())
        {
            var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            formControl = new NumberFieldControl(jsonPointerReference, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Readonly = isReadonly, // Start in readonly mode
                Immediate = true
            };
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            formControl = new DateTimeControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = isReadonly
            };
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            formControl = new CheckBoxControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = isReadonly
            };
        }
        else
        {
            // Default to TextField for strings and other types
            formControl = new TextFieldControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = isReadonly, // Start in readonly mode
                Immediate = true
            };
        }

        // If truly readonly, just return the control
        if (isReadonly)
        {
            return formControl.WithStyle(s => s.WithWidth("100%"));
        }

        // For editable controls, add click action to enable editing
        // The form control starts readonly and becomes editable on click
        return formControl
            .WithStyle(s => s.WithWidth("100%"))
            .WithClickAction(ctx =>
            {
                // Switch to editable mode - replace with the same control type but editable
                var editableControl = CreateEditableFormControl(host, prop, jsonPointerReference, propType, isRequired, dataId);
                ctx.Host.UpdateArea(ctx.Area, editableControl);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Creates an editable form control (readonly = false) for inline editing.
    /// </summary>
    private static UiControl CreateEditableFormControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        JsonPointerReference jsonPointerReference,
        Type propType,
        bool isRequired,
        string dataId)
    {
        UiControl editControl;

        // Check for UiControlAttribute first
        var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttr != null)
        {
            editControl = CreateControlFromUiControlAttribute(host, uiControlAttr, prop, jsonPointerReference, isRequired);
            // Make it editable
            if (editControl is TextAreaControl ta)
                editControl = ta with { Readonly = false };
            else if (editControl is SelectControl sc)
                editControl = sc with { Readonly = false };
        }
        // Check for DimensionAttribute
        else if (prop.GetCustomAttribute<DimensionAttribute>() is { } dimensionAttr)
        {
            editControl = CreateDimensionSelectControl(host, prop, jsonPointerReference, dimensionAttr, isRequired, dataId);
        }
        // Handle based on property type
        else if (propType.IsNumber())
        {
            var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            editControl = new NumberFieldControl(jsonPointerReference, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Readonly = false,
                AutoFocus = true,
                Immediate = true
            };
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            editControl = new DateTimeControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = false
            };
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            editControl = new CheckBoxControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = false
            };
        }
        else
        {
            // Default to TextField for strings and other types
            editControl = new TextFieldControl(jsonPointerReference)
            {
                Required = isRequired,
                Readonly = false,
                AutoFocus = true,
                Immediate = true
            };
        }

        return editControl.WithStyle(s => s.WithWidth("100%"));
    }

    /// <summary>
    /// Creates a control based on UiControlAttribute.
    /// </summary>
    private static UiControl CreateControlFromUiControlAttribute(
        LayoutAreaHost host,
        UiControlAttribute uiControlAttr,
        PropertyInfo prop,
        JsonPointerReference jsonPointerReference,
        bool isRequired)
    {
        var controlType = uiControlAttr.ControlType;

        if (controlType == typeof(TextAreaControl))
        {
            return new TextAreaControl(jsonPointerReference) { Required = isRequired, Readonly = false };
        }

        if (controlType == typeof(SelectControl) && uiControlAttr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptionsToCollection(uiControlAttr.Options));
            return new SelectControl(jsonPointerReference, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired,
                Readonly = false
            };
        }

        if (controlType == typeof(RadioGroupControl) && uiControlAttr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptionsToCollection(uiControlAttr.Options));
            return Controls.RadioGroup(jsonPointerReference, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)), prop.Name);
        }

        // Default to TextField
        return new TextFieldControl(jsonPointerReference) { Required = isRequired, Readonly = false, AutoFocus = true };
    }

    /// <summary>
    /// Creates a SelectControl for dimension properties.
    /// </summary>
    private static UiControl CreateDimensionSelectControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        JsonPointerReference jsonPointerReference,
        DimensionAttribute dimensionAttr,
        bool isRequired,
        string parentDataId)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);

        // Dimension type must be registered in DataContext
        if (string.IsNullOrEmpty(collectionName))
        {
            throw new InvalidOperationException(
                $"Dimension type '{dimensionAttr.Type.FullName}' used on property '{prop.DeclaringType?.Name}.{prop.Name}' " +
                $"is not registered in the DataContext. Please register it using AddData(data => data.AddSource(source => source.WithType<{dimensionAttr.Type.Name}>(...))).");
        }

        var optionsId = Guid.NewGuid().AsString();
        host.RegisterForDisposal(parentDataId,
            host.Workspace
                .GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptions(x.Value!, host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(options => host.UpdateData(optionsId, options)));

        return new SelectControl(jsonPointerReference, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired,
            Readonly = false
        };
    }

    /// <summary>
    /// Converts options array to a collection of Option objects.
    /// </summary>
    private static IReadOnlyCollection<Option> ConvertOptionsToCollection(object options)
    {
        if (options is IEnumerable<Option> optionCollection)
        {
            return optionCollection.ToArray();
        }

        if (options is string[] stringOptions)
        {
            return stringOptions.Select(s => (Option)new Option<string>(s, s.Wordify())).ToArray();
        }

        if (options is IEnumerable enumerable)
        {
            return enumerable.Cast<object>().Select(o => (Option)new Option<string>(o.ToString()!, o.ToString()!.Wordify())).ToArray();
        }

        return Array.Empty<Option>();
    }

    /// <summary>
    /// Sets up auto-save when the data stream changes.
    /// Debounces changes by 500ms before persisting.
    /// </summary>
    private static void SetupAutoSave(LayoutAreaHost host, string dataId, MeshNode node, string nodePath, Type contentType)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(MeshNodeLayoutAreas));

        // Keep track of the initial serialized value to detect real changes
        var initialJson = JsonSerializer.Serialize(node.Content, host.Hub.JsonSerializerOptions);
        logger?.LogDebug("SetupAutoSave for {DataId}, initial content hash: {HashCode}", dataId, initialJson.GetHashCode());

        host.RegisterForDisposal(dataId,
            host.Stream.GetDataStream<object>(dataId)
                .Do(content => logger?.LogDebug("Data stream emitted for {DataId}: {ContentType}", dataId, content?.GetType().Name ?? "null"))
                .Debounce(TimeSpan.FromMilliseconds(500)) // Wait 500ms after last change
                .Subscribe(async updatedContent =>
                {
                    if (updatedContent == null)
                    {
                        logger?.LogDebug("Auto-save skipped for {DataId}: null content", dataId);
                        return;
                    }

                    // Serialize to compare - skip if no actual change
                    var currentJson = JsonSerializer.Serialize(updatedContent, host.Hub.JsonSerializerOptions);
                    logger?.LogDebug("Auto-save comparing for {DataId}: current hash {CurrentHash}, initial hash {InitialHash}",
                        dataId, currentJson.GetHashCode(), initialJson.GetHashCode());

                    if (currentJson == initialJson)
                    {
                        logger?.LogDebug("Auto-save skipped for {DataId}: no change detected", dataId);
                        return;
                    }

                    // Update the initial value to prevent re-saving
                    initialJson = currentJson;
                    logger?.LogInformation("Auto-save triggered for {NodePath}", node.Path);

                    // Persist via DataChangeRequest
                    // Target the node's own hub address (where MeshNodeTypeSource is registered)
                    var updatedNode = node with { Content = updatedContent };
                    var targetAddress = new Address(node.Path);
                    logger?.LogDebug("Auto-save targeting {TargetAddress} for node {NodePath}", targetAddress, node.Path);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        var response = await host.Hub.AwaitResponse<DataChangeResponse>(
                            new DataChangeRequest().WithUpdates(updatedNode),
                            o => o.WithTarget(targetAddress),
                            cts.Token);
                        logger?.LogInformation("Auto-save completed for {NodePath}, status: {Status}", node.Path, response.Message.Status);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to auto-save node {NodePath}", node.Path);
                    }
                }));
    }

    /// <summary>
    /// Creates a read control for markdown/separate view fields with click action to switch to edit mode.
    /// </summary>
    private static UiControl CreateSeparatePropertyReadControl(
        PropertyInfo prop,
        object? value,
        UiControlAttribute? uiControlAttr,
        object? content,
        Type contentType,
        string nodePath)
    {
        UiControl readControl;

        if (uiControlAttr?.DisplayControlType == typeof(MarkdownControl) && value is string markdownText)
        {
            if (string.IsNullOrEmpty(markdownText))
            {
                readControl = Controls.Body("No content provided")
                    .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 16px 0; cursor: pointer;");
            }
            else
            {
                readControl = Controls.Stack
                    .WithStyle("cursor: pointer;")
                    .WithView(new MarkdownControl(markdownText));
            }
        }
        else
        {
            var displayValue = value?.ToString() ?? string.Empty;
            readControl = string.IsNullOrEmpty(displayValue)
                ? Controls.Body("No content provided")
                    .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 16px 0; cursor: pointer;")
                : Controls.Body(displayValue)
                    .WithStyle("cursor: pointer;");
        }

        // Check if property is editable
        var isReadonly = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false
                         || prop.HasAttribute<KeyAttribute>();

        if (isReadonly)
        {
            return readControl;
        }

        // Add click action to switch to edit mode
        return readControl.WithClickAction(ctx =>
        {
            var dataId = $"edit_{prop.Name}_{Guid.NewGuid():N}";
            var currentValue = content != null ? prop.GetValue(content) : null;

            // Set initial data (null is valid for empty fields)
            ctx.Host.UpdateData(dataId, currentValue!);

            // Create editor control
            var editorControl = CreatePropertyEditor(ctx.Host, prop, dataId);

            // Create save button
            var saveButton = Controls.Button("Save")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async saveCtx =>
                {
                    var stream = saveCtx.Host.Stream.GetDataStream<object>(dataId);
                    var newValue = await stream.FirstAsync();
                    await SavePropertyChange(saveCtx, prop, content, contentType, nodePath, newValue);

                    // Update read control with new value
                    var newReadControl = CreateSeparatePropertyReadControl(prop, newValue, uiControlAttr, content, contentType, nodePath);
                    saveCtx.Host.UpdateArea(ctx.Area, newReadControl);
                });

            // Create cancel button
            var cancelButton = Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("margin-left: 8px;")
                .WithClickAction(cancelCtx =>
                {
                    // Revert to read control
                    var originalReadControl = CreateSeparatePropertyReadControl(prop, currentValue, uiControlAttr, content, contentType, nodePath);
                    cancelCtx.Host.UpdateArea(ctx.Area, originalReadControl);
                    return Task.CompletedTask;
                });

            // Build edit view - for markdown, stack vertically
            UiControl editView;
            if (uiControlAttr?.ControlType == typeof(MarkdownEditorControl))
            {
                editView = Controls.Stack
                    .WithStyle("width: 100%;")
                    .WithView(editorControl)
                    .WithView(Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("gap: 8px; margin-top: 12px;")
                        .WithView(saveButton)
                        .WithView(cancelButton));
            }
            else
            {
                editView = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px; width: 100%;")
                    .WithView(editorControl)
                    .WithView(saveButton)
                    .WithView(cancelButton);
            }

            ctx.Host.UpdateArea(ctx.Area, editView);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Saves a property change to the node's content using DataChangeRequest via workspace stream.
    /// </summary>
    private static async Task SavePropertyChange(
        UiActionContext actx,
        PropertyInfo prop,
        object? content,
        Type contentType,
        string nodePath,
        object? newValue)
    {
        // Get the node from workspace stream (not IPersistenceService)
        var nodeStream = actx.Host.Workspace.GetStream<MeshNode>();
        if (nodeStream == null) return;

        var nodes = await nodeStream.FirstAsync();
        var node = nodes?.FirstOrDefault(n => n.Path == nodePath || n.Namespace == nodePath);
        if (node == null) return;

        // Create updated content with the new property value
        object? updatedContent;
        if (content != null && contentType.IsClass)
        {
            // For records/classes, try to create a copy with the updated property
            // This works for records with 'with' expressions
            updatedContent = CreateUpdatedContent(content, prop, newValue);
        }
        else
        {
            updatedContent = content;
        }

        // Update the node via DataChangeRequest
        var updatedNode = node with { Content = updatedContent };
        var hubAddress = actx.Host.Hub.Configuration.ParentHub?.Address ?? actx.Host.Hub.Address;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                new DataChangeRequest().WithUpdates(updatedNode),
                o => o.WithTarget(hubAddress),
                cts.Token);
        }
        catch
        {
            // Log or handle error - for now silently fail
        }
    }

    /// <summary>
    /// Creates an updated copy of the content with a changed property value.
    /// </summary>
    private static object? CreateUpdatedContent(object content, PropertyInfo prop, object? newValue)
    {
        var contentType = content.GetType();

        // Check if the type is a record (has a copy constructor pattern)
        // Try to find a with-style cloning approach
        if (prop.CanWrite)
        {
            // For mutable properties, just set the value
            prop.SetValue(content, newValue);
            return content;
        }

        // For immutable records, we need to create a new instance
        // Use reflection to find the constructor and create a copy
        var constructor = contentType.GetConstructors().FirstOrDefault();
        if (constructor == null) return content;

        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var matchingProp = contentType.GetProperty(param.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (matchingProp != null)
            {
                args[i] = matchingProp.Name == prop.Name ? newValue : matchingProp.GetValue(content);
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
            }
        }

        return constructor.Invoke(args);
    }

    private static object? GetPropertyValue(object? content, PropertyInfo prop)
    {
        if (content == null) return null;

        // Handle JsonElement content
        if (content is JsonElement jsonElement)
        {
            var propName = prop.Name.ToCamelCase();
            if (propName != null && jsonElement.TryGetProperty(propName, out var propValue))
            {
                return propValue.ValueKind switch
                {
                    JsonValueKind.String => propValue.GetString(),
                    JsonValueKind.Number => propValue.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => propValue.ToString()
                };
            }
            return null;
        }

        // Handle direct property access
        try
        {
            return prop.GetValue(content);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatDisplayValue(object? value, PropertyInfo prop)
    {
        if (value == null) return "";

        // Format DateTime
        if (value is DateTime dt)
            return dt.ToString("MMM dd, yyyy");

        // Format enum
        if (value.GetType().IsEnum)
            return value.ToString()!.Wordify();

        // Format boolean
        if (value is bool b)
            return b ? "Yes" : "No";

        return value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Creates an editor control for a property based on its type, using data binding.
    /// Follows the pattern from EditorExtensions.cs.
    /// </summary>
    private static UiControl CreatePropertyEditor(LayoutAreaHost host, PropertyInfo prop, string dataId)
    {
        var jsonPointerReference = new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId));
        var isReadonly = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false
                         || prop.HasAttribute<KeyAttribute>();
        var isRequired = prop.HasAttribute<RequiredMemberAttribute>() || prop.HasAttribute<RequiredAttribute>();

        // Check for UiControlAttribute
        var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttr != null)
        {
            // Handle markdown editor
            if (uiControlAttr.ControlType == typeof(MarkdownEditorControl))
            {
                var markdownAttr = prop.GetCustomAttribute<MarkdownAttribute>();
                return new MarkdownEditorControl
                {
                    Value = jsonPointerReference,
                    Height = markdownAttr?.EditorHeight ?? "200px",
                    Placeholder = markdownAttr?.Placeholder ?? "Enter content (supports Markdown formatting)",
                    ShowPreview = markdownAttr?.ShowPreview ?? false,
                    TrackChangesEnabled = markdownAttr?.TrackChanges ?? false,
                    Readonly = isReadonly
                };
            }

            // Handle TextArea
            if (uiControlAttr.ControlType == typeof(TextAreaControl))
            {
                return new TextAreaControl(jsonPointerReference) { Required = isRequired, Readonly = isReadonly };
            }
        }

        // Check for DimensionAttribute
        var dimensionAttr = prop.GetCustomAttribute<DimensionAttribute>();
        if (dimensionAttr != null)
        {
            var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);

            // Dimension type must be registered in DataContext
            if (string.IsNullOrEmpty(collectionName))
            {
                throw new InvalidOperationException(
                    $"Dimension type '{dimensionAttr.Type.FullName}' used on property '{prop.DeclaringType?.Name}.{prop.Name}' " +
                    $"is not registered in the DataContext. Please register it using AddData(data => data.AddSource(source => source.WithType<{dimensionAttr.Type.Name}>(...))).");
            }

            var optionsId = Guid.NewGuid().AsString();
            host.RegisterForDisposal(dataId,
                host.Workspace
                    .GetStream(new CollectionReference(collectionName))!
                    .Select(x => ConvertDimensionToOptions(x.Value!, host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                    .Subscribe(options => host.UpdateData(optionsId, options)));

            return new SelectControl(jsonPointerReference, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired,
                Readonly = isReadonly
            };
        }

        // Handle based on property type
        var propType = prop.PropertyType;

        // Number types
        if (propType.IsNumber())
        {
            var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            return new NumberFieldControl(jsonPointerReference, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Readonly = isReadonly
            };
        }

        // DateTime
        if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            return new DateTimeControl(jsonPointerReference) { Required = isRequired, Readonly = isReadonly };
        }

        // Boolean
        if (propType == typeof(bool) || propType == typeof(bool?))
        {
            return new CheckBoxControl(jsonPointerReference) { Required = isRequired, Readonly = isReadonly };
        }

        // Default to TextField for strings and other types
        return new TextFieldControl(jsonPointerReference) { Required = isRequired, Readonly = isReadonly };
    }

    /// <summary>
    /// Converts dimension instances to options for select controls.
    /// </summary>
    private static IReadOnlyCollection<Option> ConvertDimensionToOptions(InstanceCollection instances, ITypeDefinition dimensionType)
    {
        var displayNameSelector =
            typeof(INamed).IsAssignableFrom(dimensionType.Type)
                ? (Func<object, string>)(x => ((INamed)x).DisplayName)
                : o => o.ToString()!;

        var keyType = dimensionType.GetKeyType();
        var optionType = typeof(Option<>).MakeGenericType(keyType);

        return instances.Instances
            .Select(kvp => (Option)Activator.CreateInstance(optionType, [kvp.Key, displayNameSelector(kvp.Value)])!)
            .ToArray();
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
    private static string FormatPropertyValue(object? value, JsonSerializerOptions _)
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
    /// Renders the Search view showing nodes as thumbnails with search.
    /// Uses MeshSearchControl for unified search and display.
    /// For NodeType nodes, shows instances of that type (nodeType:name scope:subtree).
    /// For instance nodes, uses CatalogQuery if set, otherwise defaults to scope:children.
    /// Excludes NodeType nodes from results (use NodeTypes area to view those).
    /// Render mode is determined by CatalogMode property (hierarchical or grouped).
    /// Reads search term from ?q= query parameter.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Search(LayoutAreaHost host, RenderingContext ctx)
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

            return Controls.MeshSearch
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
    /// Groups children by NodeType (default) or Category if set, excludes NodeType nodes.
    /// Uses MeshSearchControl for unified search/catalog functionality.
    /// </summary>
    [Browsable(false)]
    public static UiControl Children(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return Controls.MeshSearch
            .WithHiddenQuery($"path:{hubPath} scope:children -nodeType:NodeType")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            // No explicit grouping - defaults to NodeType which gives meaningful labels
            .WithSectionCounts(true)
            .WithItemLimit(10)
            .WithCollapsibleSections(true);
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

    /// <summary>
    /// Renders the Edit area using EditorExtensions for the ContentType.
    /// If a ContentType is registered, uses EditorExtensions to generate a form.
    /// Falls back to MeshNodeEditorControl if no ContentType is found.
    /// Includes a back button to return to Overview.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var dataContext = host.Workspace.DataContext;

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence?.GetNodeAsync(nodePath, ct)!;
            var stack = Controls.Stack.WithWidth("100%");

            // Back button
            var overviewHref = $"/{nodePath}/{OverviewArea}";
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-bottom: 16px;")
                .WithView(Controls.Button("← Back to Overview").WithNavigateToHref(overviewHref)));

            // Get ContentType from DataContext's TypeSources (first non-MeshNode type)
            var contentTypeSource = dataContext.TypeSources.Values
                .FirstOrDefault(ts => ts.TypeDefinition.Type != typeof(MeshNode));

            if (contentTypeSource != null && node?.Content != null)
            {
                var contentType = contentTypeSource.TypeDefinition.Type;
                var dataId = Guid.NewGuid().AsString();

                // Deserialize content to the actual type if it's a JsonElement
                object content = node.Content;
                if (content is JsonElement jsonElement)
                {
                    try
                    {
                        content = jsonElement.Deserialize(contentType, host.Hub.JsonSerializerOptions) ?? content;
                    }
                    catch
                    {
                        // If deserialization fails, keep as JsonElement
                    }
                }

                // Use EditorExtensions to generate editor for ContentType
                var editor = host.Hub.ServiceProvider.Edit(contentType, dataId);
                host.UpdateData(dataId, content);
                stack = stack.WithView(editor);

                // Save button with DataChangeRequest
                stack = stack.WithView(BuildSaveButton(host, dataId, contentType, nodePath));
            }
            else
            {
                // Fallback to MeshNodeEditorControl
                stack = stack.WithView(new MeshNodeEditorControl { NodePath = nodePath, NodeType = node?.NodeType });
            }

            return (UiControl)stack;
        });
    }

    /// <summary>
    /// Builds a save button that updates the node's Content via DataChangeRequest.
    /// </summary>
    private static UiControl BuildSaveButton(LayoutAreaHost host, string dataId, Type contentType, string nodePath)
    {
        var hubAddress = host.Hub.Configuration.ParentHub?.Address ?? host.Hub.Address;

        return Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithStyle("margin-top: 16px;")
            .WithClickAction(async actx =>
            {
                // Get the updated content from the workspace data stream
                var stream = actx.Host.Stream.GetDataStream<object>(dataId);
                var updatedContent = await stream.FirstAsync();

                if (updatedContent == null)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**No changes to save.**"),
                        "Info"
                    ).WithSize("S");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Get the current node from workspace stream (not IPersistenceService)
                var nodeStream = actx.Host.Workspace.GetStream<MeshNode>();
                if (nodeStream == null)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Node stream not available.**"),
                        "Error"
                    ).WithSize("S");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                var nodes = await nodeStream.FirstAsync();
                var node = nodes?.FirstOrDefault(n => n.Path == nodePath || n.Namespace == nodePath);

                if (node == null)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Node not found.**"),
                        "Error"
                    ).WithSize("S");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Update the node with new content
                var updatedNode = node with { Content = updatedContent };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(hubAddress),
                        cts.Token);

                    if (response.Message.Log.Status != ActivityStatus.Succeeded)
                    {
                        var errorMsg = response.Message.Log.Messages.LastOrDefault()?.Message ?? "Save failed";
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown($"**Error saving:**\n\n{errorMsg}"),
                            "Save Failed"
                        ).WithSize("M");
                        actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    }
                    else
                    {
                        // Navigate back to overview on success
                        actx.Host.UpdateArea(actx.Area, new RedirectControl($"/{nodePath}/{OverviewArea}"));
                    }
                }
                catch (Exception ex)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving:**\n\n{ex.Message}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            });
    }

    #region UCR Special Areas

    /// <summary>
    /// Renders content from the node's content collection.
    /// For images: renders inline. For markdown: renders the content.
    /// For other files: shows a download link.
    /// For self-reference (no path): shows the node's icon/logo.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
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
    private static UiControl RenderNodeIcon(MeshNode node, string _)
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
    /// </summary>
    private static string? GetNodeImageUrl(MeshNode node)
    {
        return node.Icon;
    }

    private static IObservable<UiControl?> RenderImageAsync(LayoutAreaHost host, string contentPath, string _)
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

    private static UiControl RenderDownloadLink(LayoutAreaHost host, string contentPath, string _1)
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

    private static UiControl RenderNodeSchema(MeshNode? node, string _, JsonSerializerOptions? jsonOptions = null)
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

    #region Create Node

    /// <summary>
    /// Renders the Create Node area showing available types to create.
    /// If a type is selected via ?type= query param, shows a form for creating the node.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> CreateNode(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (nodeTypeService == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithView(
                    Controls.Html("<p style=\"color: var(--warning-color);\">NodeTypeService is not available.</p>")
                )
            );
        }

        // Check if a type is selected via query parameter
        var selectedType = host.GetQueryStringParamValue("type")?.Trim();

        return Observable.FromAsync(async ct =>
        {
            if (!string.IsNullOrEmpty(selectedType))
            {
                // Type is selected - show create form
                return (UiControl?)await BuildCreateFormAsync(host, hubPath, selectedType, ct);
            }
            else
            {
                // No type selected - show type selection grid
                return (UiControl?)await BuildTypeSelectionAsync(host, nodeTypeService, hubPath, ct);
            }
        });
    }

    /// <summary>
    /// Builds the type selection grid showing all creatable types as cards.
    /// </summary>
    private static async Task<UiControl> BuildTypeSelectionAsync(
        LayoutAreaHost host,
        INodeTypeService nodeTypeService,
        string nodePath,
        CancellationToken ct)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with back link
        var backHref = $"/{nodePath}/{OverviewArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2("Create New").WithStyle("margin: 0;")));

        // Get creatable types
        var creatableTypes = await nodeTypeService.GetCreatableTypesAsync(nodePath, ct).ToListAsync(ct);

        if (creatableTypes.Count == 0)
        {
            stack = stack.WithView(Controls.Body("No types available for creation.")
                .WithStyle("color: var(--neutral-foreground-hint);"));
            return stack;
        }

        // Grid of type cards
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));

        foreach (var typeInfo in creatableTypes)
        {
            var typeCard = BuildTypeCard(nodePath, typeInfo);
            grid = grid.WithView(typeCard, itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
        }

        stack = stack.WithView(grid);
        return stack;
    }

    /// <summary>
    /// Builds a card for a creatable type that navigates to the create form.
    /// </summary>
    private static UiControl BuildTypeCard(string nodePath, CreatableTypeInfo typeInfo)
    {
        var createHref = $"/{nodePath}/{CreateNodeArea}?type={Uri.EscapeDataString(typeInfo.NodeTypePath)}";
        var displayName = typeInfo.DisplayName ?? GetLastPathSegment(typeInfo.NodeTypePath);
        var iconName = typeInfo.Icon ?? "Document";
        var description = string.IsNullOrEmpty(typeInfo.Description) ? "No description" : typeInfo.Description;

        // Use NavLinkControl for navigation, wrapped in a styled stack for card appearance
        return Controls.Stack
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-card-container);")
            .WithView(new NavLinkControl(
                Controls.Stack
                    .WithView(Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithHorizontalGap(12)
                        .WithStyle("align-items: center; margin-bottom: 8px;")
                        .WithView(Controls.Icon(iconName).WithStyle("font-size: 24px; color: var(--accent-fill-rest);"))
                        .WithView(Controls.H4(displayName).WithStyle("margin: 0; font-weight: 600;")))
                    .WithView(Controls.Body(description).WithStyle("color: var(--neutral-foreground-hint); font-size: 14px;")),
                null,
                createHref));
    }

    /// <summary>
    /// Builds the create form for a selected type with Name and Description fields.
    /// Uses a view model and EditorExtensions for proper data binding.
    /// For Markdown types, creates a MarkdownContent with a title heading.
    /// </summary>
    private static async Task<UiControl> BuildCreateFormAsync(
        LayoutAreaHost host,
        string parentPath,
        string nodeTypePath,
        CancellationToken ct)
    {
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with back link
        var backHref = $"/{parentPath}/{CreateNodeArea}";
        var typeName = GetLastPathSegment(nodeTypePath);
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2($"Create {typeName}").WithStyle("margin: 0;")));

        // Show NodeType being created
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("margin-bottom: 16px; align-items: center;")
            .WithView(Controls.Body("Node Type:").WithStyle("font-weight: 600; color: var(--neutral-foreground-hint);"))
            .WithView(Controls.Body(nodeTypePath).WithStyle("color: var(--accent-fill-rest);")));

        // Get type info for display
        string? typeDescription = null;
        if (persistence != null)
        {
            var typeNode = await persistence.GetNodeAsync(nodeTypePath, ct);
            if (typeNode?.Content is NodeTypeDefinition typeDef)
            {
                typeDescription = typeDef.Description;
            }
            else
            {
                typeDescription = typeNode?.Description;
            }
        }

        if (!string.IsNullOrEmpty(typeDescription))
        {
            stack = stack.WithView(Controls.Body(typeDescription)
                .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"));
        }

        // Create data bindings for form fields
        var nameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = $"{nameDataId}-description";
        host.UpdateData(nameDataId, "");
        host.UpdateData(descriptionDataId, "");

        // Name field - using TextFieldControl
        stack = stack.WithView(Controls.Stack
            .WithStyle("margin-bottom: 16px;")
            .WithView(Controls.Body("Name *").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter a name for the new node")
                .WithImmediate(true)
                .WithStyle("width: 100%;")
                with
            { DataContext = LayoutAreaReference.GetDataPointer(nameDataId) }));

        // Description field - using MarkdownEditorControl with proper data binding
        stack = stack.WithView(Controls.Stack
            .WithStyle("margin-bottom: 16px; width: 100%;")
            .WithView(Controls.Body("Description").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new MarkdownEditorControl()
            {
                Value = new JsonPointerReference(""),
                DocumentId = descriptionDataId,
                Height = "200px",
                Placeholder = "Enter a description (supports Markdown formatting)",
                DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId)
            }));

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 24px;");

        // Create button
        buttonRow = buttonRow.WithView(Controls.Button("Create")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Add())
            .WithClickAction(async actx =>
            {
                // Get form values from workspace
                var name = await host.Stream.GetDataStream<string>(nameDataId).FirstAsync();
                var description = await host.Stream.GetDataStream<string>(descriptionDataId).FirstAsync();

                if (string.IsNullOrWhiteSpace(name))
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Name is required.**"),
                        "Validation Error"
                    ).WithSize("S");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Sanitize name for use as ID
                var trimmedName = name.Trim();
                var nodeId = trimmedName
                    .Replace(" ", "-")
                    .Replace("/", "-")
                    .ToLowerInvariant();

                var nodePath = string.IsNullOrEmpty(parentPath) ? nodeId : $"{parentPath}/{nodeId}";

                // Determine content based on node type
                object? content = null;
                if (nodeTypePath == GraphConfigurationExtensions.MarkdownNodeType)
                {
                    // For Markdown types, create MarkdownContent with title heading
                    var markdownText = $"# {trimmedName}\n\n{description ?? ""}";
                    content = MarkdownContent.Parse(markdownText, nodePath);
                }

                // Create the node
                var node = MeshNode.FromPath(nodePath) with
                {
                    Name = trimmedName,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    NodeType = nodeTypePath,
                    IsPersistent = true,
                    Content = content
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var meshAddress = host.Hub.Configuration.ParentHub?.Address ?? host.Hub.Address;
                var response = await actx.Host.Hub.AwaitResponse(
                    new CreateNodeRequest(node),
                    o => o.WithTarget(meshAddress),
                    cts.Token);

                if (response.Message.Success)
                {
                    // Navigate to the new node's edit view
                    var editHref = $"/{nodePath}/{EditArea}";
                    actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref));
                }
                else
                {
                    var errorMsg = response.Message.Error ?? "Failed to create node";
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error creating node:**\n\n{errorMsg}"),
                        "Creation Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            }));

        // Cancel button
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref($"/{parentPath}/{OverviewArea}"));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Gets the last segment of a path.
    /// </summary>
    private static string GetLastPathSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
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
