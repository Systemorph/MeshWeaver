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
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
/// Page layout options that can be set per node type via hub configuration.
/// Use <c>configuration.Set(new PageLayoutOptions { MaxWidth = "960px" })</c>.
/// </summary>
public record PageLayoutOptions
{
    /// <summary>
    /// Maximum width for the page content area (e.g., "960px", "1200px").
    /// Applied as CSS max-width with centered margins.
    /// Default: null (no constraint, full width).
    /// </summary>
    public string? MaxWidth { get; init; }
}

/// <summary>
/// Layout areas for mesh node content.
/// - Overview: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, path)
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
    public const string GroupsArea = "Groups";
    public const string CreateNodeArea = "Create";
    public const string EditArea = "Edit";
    public const string DeleteArea = "Delete";
    public const string ThreadsArea = "Threads";
    public const string ChatArea = "Chat";
    public const string ImportMeshNodesArea = "ImportMeshNodes";
    public const string ExportArea = "Export";
    public const string CopyArea = "Copy";
    public const string MoveArea = "Move";
    public const string RecycleArea = "Recycle";
    public const string VersionsArea = "Versions";
    public const string VersionDiffArea = "VersionDiff";

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
            .AddDefaultMeshMenu()
            .AddDefaultSettingsMenuItems()
            .WithHandler<RollbackNodeRequest>(VersionLayoutArea.HandleRollbackNodeRequest)
            .WithHandler<UndoActivityRequest>(VersionLayoutArea.HandleUndoActivityRequest)
            .AddLayout(layout => layout.AddDefaultLayoutAreas());

    public static LayoutDefinition AddDefaultLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            .WithView(ThumbnailArea, Thumbnail)
            .WithView(SettingsArea, SettingsLayoutArea.Settings)
            .WithView(SearchArea, Search)
            .WithView(FilesArea, Files)
            .WithView(ChildrenArea, Children)
            .WithView(ThreadsArea, Threads)
            .WithView(ChatArea, Chat)
            .WithView(NodeTypesArea, NodeTypes)
            .WithView(AccessControlArea, AccessControl)
            .WithView(GroupsArea, Groups)
            .WithView(CreateNodeArea, CreateNode)
            .WithView(EditArea, EditNode)
            .WithView(ImportMeshNodesArea, ImportLayoutArea.ImportMeshNodes)
            .WithView(ExportArea, ExportLayoutArea.Export)
            .WithView(CopyArea, CopyLayoutArea.Copy)
            .WithView(MoveArea, MoveLayoutArea.Move)
            .WithView(RecycleArea, RecycleLayoutArea.Recycle)
            .WithView(VersionsArea, VersionLayoutArea.Versions)
            .WithView(VersionDiffArea, VersionLayoutArea.VersionDiff)
            .WithView(DeleteArea, DeleteLayoutArea.Delete)
            .WithView(PinLayoutArea.PinArea, PinLayoutArea.Pin)
            .WithView(PinLayoutArea.UnpinArea, PinLayoutArea.Unpin)
            .WithView(PinLayoutArea.PinnedThumbnailArea, PinLayoutArea.PinnedThumbnail)
            // UCR special areas
            .WithView(DataArea, Data)
            .WithView(SchemaArea, Schema)
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
        // CombineLatest with permission stream — pure observable composition, no await.
        return host.Workspace.GetMeshNodeStream().CombineLatest(
            PermissionHelper.GetEffectivePermissions(host.Hub, hubPath),
            (node, permissions) =>
            {
                if (!permissions.HasFlag(Permission.Read))
                    return (UiControl?)BuildAccessDenied(hubPath);
                var canEdit = permissions.HasFlag(Permission.Update);
                return (UiControl?)host.BuildDetailsContent(node, null, canEdit);
            });
    }

    private static UiControl BuildAccessDenied(string nodePath)
    {
        var nodeName = nodePath.Split('/').LastOrDefault() ?? nodePath;
        return Controls.Stack.WithWidth("100%").WithStyle("padding: 48px 24px; align-items: center; text-align: center;")
            .WithView(Controls.Icon(FluentIcons.ShieldKeyhole())
                .WithStyle("font-size: 64px; color: var(--neutral-foreground-hint); margin-bottom: 16px;"))
            .WithView(Controls.H2("Access Denied").WithStyle("margin: 0;"))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint); max-width: 480px;\">" +
                $"You do not have permission to view <strong>{System.Web.HttpUtility.HtmlEncode(nodeName)}</strong>. " +
                $"Contact the owner to request access.</p>"))
            .WithView(Controls.Button("Request Access")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.PersonAdd())
                .WithClickAction(ctx =>
                {
                    // Show a confirmation that the request was noted
                    var dialog = Controls.Dialog(
                        Controls.Markdown(
                            $"Access request for **{nodeName}** has been noted.\n\n" +
                            "The node owner will be notified."),
                        "Access Requested"
                    ).WithSize("S").WithClosable(true);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                    return Task.CompletedTask;
                }));
    }

    internal static string GetContainerStyle(LayoutAreaHost host, NodeTypeDefinition? typeDef = null)
    {
        var pageMaxWidth = typeDef?.PageMaxWidth
            ?? host.Hub.Configuration.Get<PageLayoutOptions>()?.MaxWidth
            ?? "1200px";
        return $"position: relative; max-width: {pageMaxWidth}; margin: 0 auto; padding: 0 24px;";
    }

    internal static UiControl BuildDetailsContent(this LayoutAreaHost host, MeshNode? node, NodeTypeDefinition? typeDef, bool canEdit = true)
    {
        // Outer wrapper at full page width
        var outer = Controls.Stack.WithWidth("100%");

        // Constrained content area (header + properties)
        var content = Controls.Stack.WithWidth("100%").WithStyle(GetContainerStyle(host, typeDef));

        // Header with title/icon
        content = content.WithView(BuildHeader(host, node, canEdit));

        // For built-in type nodes (Content is NodeTypeDefinition), show type info
        // instead of property editor which would expose internal NodeTypeDefinition fields.
        if (node?.Content is NodeTypeDefinition ntd)
        {
            content = content.WithView(BuildTypeInfoSection(node, ntd));
        }
        // Property overview (read-only with click-to-edit)
        else if (node != null)
        {
            content = content.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit));
        }

        outer = outer.WithView(content);

        // Children — full page width, outside the constrained container
        if (typeDef?.ShowChildrenInDetails ?? true)
        {
            outer = outer.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle("margin-top: 32px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);")
                    .WithView(LayoutAreaControl.Children(host.Hub)));
        }

        // Comments — back in constrained width
        if (host.Hub.Configuration.HasComments())
        {
            outer = outer.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle(GetContainerStyle(host, typeDef) + " margin-top: 32px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);")
                    .WithView(CommentsView.BuildInlineCommentsSection(host)));
        }

        return outer;
    }

    /// <summary>
    /// Builds a description section for built-in type nodes.
    /// Shows the type description from NodeTypeDefinition or a default message.
    /// </summary>
    private static UiControl BuildTypeInfoSection(MeshNode node, NodeTypeDefinition typeDef)
    {
        var description = typeDef.Description
            ?? $"Built-in type for managing {node.Name ?? node.NodeType ?? "content"} nodes.";

        return Controls.Markdown(description);
    }

    /// <summary>
    /// Builds the header: icon + title + identity action row (Move/Copy/Delete/Edit,
    /// plus Configuration on NodeType nodes), followed by a meta row with the node-type
    /// link and the Created/LastModified/LastModifiedBy timestamps.
    /// Clicking the icon opens an icon-picker dialog; clicking the title (when the
    /// content has a Title property and the user can edit) switches it to inline edit.
    /// </summary>
    internal static UiControl BuildHeader(LayoutAreaHost host, MeshNode? node, bool canEdit = true)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodePath = node?.Path ?? hubPath;
        var title = node?.Name ?? node?.Id ?? hubPath;
        var iconValue = MeshNodeImageHelper.ResolveNodeIcon(node);
        var rawIcon = node?.Icon;

        // Row 1 — icon + title (+ action buttons on the right)
        var identityRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 20px; margin-top: 16px;");

        identityRow = identityRow.WithView(BuildClickableIcon(host, node, iconValue, rawIcon, canEdit));

        // Title column takes remaining width so action buttons sit at the far right.
        var titleColumn = Controls.Stack.WithStyle("flex: 1; min-width: 0;");

        bool hasTitleProperty = false;
        if (node?.Content is JsonElement jsonElement && jsonElement.TryGetProperty("$type", out var typeProperty))
        {
            var typeName = typeProperty.GetString();
            var typeRegistry = host.Hub.ServiceProvider.GetService<ITypeRegistry>();
            var contentType = !string.IsNullOrEmpty(typeName) ? typeRegistry?.GetType(typeName) : null;
            hasTitleProperty = contentType?.GetProperty("Title") != null;
        }

        if (hasTitleProperty && node != null)
        {
            var dataId = EditLayoutArea.GetDataId(node.Namespace ?? hubPath);
            titleColumn = titleColumn.WithView(OverviewLayoutArea.BuildTitle(host, node, dataId, canEdit));
        }
        else
        {
            titleColumn = titleColumn.WithView(Controls.Html(
                $"<h1 style=\"margin: 0; font-size: 2rem; font-weight: 700; letter-spacing: -0.02em; line-height: 1.15;\">" +
                $"{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));
        }

        identityRow = identityRow.WithView(titleColumn);
        identityRow = identityRow.WithView(BuildHeaderActionRow(host, node, nodePath, canEdit));

        // Row 2 — node-type link + timestamps
        var metaRow = BuildHeaderMetaRow(host, node);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding-bottom: 20px; margin-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest); gap: 8px;")
            .WithView(identityRow)
            .WithView(metaRow);
    }

    /// <summary>
    /// Renders the node icon as a clickable tile that opens the icon-picker dialog when the
    /// user has edit rights. Falls back to a placeholder (dashed border) when no icon is set.
    /// </summary>
    private static UiControl BuildClickableIcon(
        LayoutAreaHost host, MeshNode? node, string? iconValue, string? rawIcon, bool canEdit)
    {
        const string tileStyle = "width: 56px; height: 56px; display: flex; align-items: center; justify-content: center; border-radius: 10px; background: var(--neutral-layer-2); flex-shrink: 0;";

        UiControl tile;
        if (!string.IsNullOrEmpty(iconValue))
        {
            if (iconValue.StartsWith("data:") || iconValue.StartsWith("http") || iconValue.StartsWith("/"))
            {
                var fit = iconValue.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";
                tile = Controls.Html(
                    $"<div style=\"{tileStyle}\"><img src=\"{iconValue}\" alt=\"\" style=\"width: 48px; height: 48px; border-radius: 8px; object-fit: {fit};\" /></div>");
            }
            else if (iconValue.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                tile = Controls.Html($"<div style=\"{tileStyle}\">{iconValue}</div>");
            }
            else if (rawIcon != null && MeshNodeImageHelper.IsFluentIconName(rawIcon))
            {
                tile = Controls.Stack.WithStyle(tileStyle)
                    .WithView(Controls.Icon(new Icon(FluentIcons.Provider, rawIcon))
                        .WithStyle("font-size: 36px; color: var(--accent-fill-rest);"));
            }
            else
            {
                tile = Controls.Html(
                    $"<div style=\"{tileStyle} font-size: 30px;\">{System.Web.HttpUtility.HtmlEncode(iconValue)}</div>");
            }
        }
        else
        {
            tile = Controls.Html(
                $"<div style=\"{tileStyle} border: 2px dashed var(--neutral-stroke-rest); background: transparent; color: var(--neutral-foreground-hint); font-size: 20px;\">+</div>");
        }

        if (!canEdit || node == null)
            return tile;

        // Wrap in a clickable stack that opens the icon-picker dialog.
        return Controls.Stack
            .WithStyle("cursor: pointer;")
            .WithView(tile)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateArea(DialogControl.DialogArea, NodeIconPickerDialog.Build(host, node));
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Builds the right-aligned button row: Edit, Move, Copy, Delete, plus Configuration on
    /// NodeType nodes and a node-type Configuration link on instance nodes.
    /// All buttons use anchor-style navigation (no <c>await</c>); Delete routes through the
    /// dedicated Delete area which uses Post + RegisterCallback with a progress indicator.
    /// </summary>
    private static UiControl BuildHeaderActionRow(
        LayoutAreaHost host, MeshNode? node, string nodePath, bool canEdit)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px; margin-left: auto; flex-wrap: wrap; justify-content: flex-end;");

        // Configuration button for NodeType definition nodes — points at their own Configuration area.
        if (node?.NodeType == MeshNode.NodeTypePath)
        {
            row = row.WithView(Controls.Button("Configuration")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Settings())
                .WithNavigateToHref(BuildUrl(nodePath, NodeTypeLayoutAreas.ConfigurationArea)));
        }

        if (canEdit)
        {
            row = row.WithView(Controls.Button("Edit")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Edit())
                .WithNavigateToHref(BuildUrl(nodePath, EditArea)));

            row = row.WithView(Controls.Button("Copy")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Copy())
                .WithNavigateToHref(BuildUrl(nodePath, CopyArea)));

            row = row.WithView(Controls.Button("Move")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.ArrowMove())
                .WithNavigateToHref(BuildUrl(nodePath, MoveArea)));

            row = row.WithView(Controls.Button("Delete")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("color: var(--error, #d32f2f);")
                .WithIconStart(FluentIcons.Delete())
                .WithNavigateToHref(BuildUrl(nodePath, DeleteArea)));
        }

        return row;
    }

    /// <summary>
    /// Meta row under the identity row: shows node-type as a link to the type's Configuration
    /// area and the Created / LastModified / LastModifiedBy timestamps when present.
    /// </summary>
    private static UiControl BuildHeaderMetaRow(LayoutAreaHost host, MeshNode? node)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 24px; flex-wrap: wrap; font-size: 0.85rem; color: var(--neutral-foreground-hint);");

        if (node != null && !string.IsNullOrEmpty(node.NodeType) && node.NodeType != MeshNode.NodeTypePath)
        {
            var typeHref = BuildUrl(node.NodeType, NodeTypeLayoutAreas.ConfigurationArea);
            var typeLabel = node.NodeType.Contains('/') ? node.NodeType.Split('/').Last() : node.NodeType;
            row = row.WithView(Controls.Html(
                "<span style=\"display: inline-flex; align-items: center; gap: 6px;\">" +
                "<span>Type:</span>" +
                $"<a href=\"{typeHref}\" style=\"color: var(--accent-fill-rest); font-weight: 500;\">{System.Web.HttpUtility.HtmlEncode(typeLabel)}</a>" +
                "</span>"));
        }

        if (node != null && node.CreatedDate != default)
        {
            var created = node.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var createdBy = string.IsNullOrEmpty(node.CreatedBy) ? "" : $" by {System.Web.HttpUtility.HtmlEncode(node.CreatedBy)}";
            row = row.WithView(Controls.Html($"<span><span style=\"color: var(--neutral-foreground-rest);\">Created:</span> {created}{createdBy}</span>"));
        }

        if (node != null && node.LastModified != default)
        {
            var modified = node.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var modifiedBy = string.IsNullOrEmpty(node.LastModifiedBy) ? "" : $" by {System.Web.HttpUtility.HtmlEncode(node.LastModifiedBy)}";
            row = row.WithView(Controls.Html($"<span><span style=\"color: var(--neutral-foreground-rest);\">Updated:</span> {modified}{modifiedBy}</span>"));
        }

        return row;
    }


    /// <summary>
    /// Builds a content URL for navigating to a specific layout area of a node.
    /// </summary>
    /// <param name="nodePath">The path of the node</param>
    /// <param name="area">The layout area to navigate to</param>
    /// <param name="queryString">Optional query string (without leading ?)</param>
    /// <returns>The full URL path</returns>
    public static string BuildUrl(string nodePath, string area, string? queryString = null)
    {
        var url = $"/{nodePath}/{area}";
        if (!string.IsNullOrEmpty(queryString))
            url += $"?{queryString}";
        return url;
    }

    /// <summary>
    /// Returns the Edit menu item if the user has Update permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetEditMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update))
            return null;
        return new("Edit", EditArea,
            RequiredPermission: Permission.Update, Order: -10, Href: BuildUrl(hubPath, EditArea));
    }

    /// <summary>
    /// Returns the Files menu item if the user has Read permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetFilesMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Files", FilesArea, Order: 25, Href: BuildUrl(hubPath, FilesArea));
    }

    /// <summary>
    /// Returns the Threads menu item (always visible).
    /// </summary>
    public static NodeMenuItemDefinition GetThreadsMenuItem(string hubPath)
        => new("Threads", ThreadsArea, Order: 50, Href: BuildUrl(hubPath, ThreadsArea));

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
    /// Renders the Metadata area showing node properties (name, type, path).
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
        var nodeName = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? "Overview";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithView(Controls.Html("<h2>Metadata</h2>"))
            .WithView(Controls.Button(nodeName)
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

        var parentPath = node.GetParentPath();
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parentHref = $"/{parentPath}/{OverviewArea}";
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithView(Controls.Html("<p><strong>Parent:</strong> </p>"))
                .WithView(Controls.Button(parentPath)
                    .WithNavigateToHref(parentHref)));
        }

        return stack;
    }


    private static string GetNodeContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle MarkdownContent (from MarkdownFileParser)
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        // Handle MarkdownDocument/MarkdownContent content (JSON with $type and content fields)
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if ((typeName == "MarkdownDocument" || typeName == "MarkdownContent") && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }

            // Fallback: try "content" property without $type check
            if (jsonElement.TryGetProperty("content", out var fallbackContent) && fallbackContent.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return fallbackContent.GetString() ?? string.Empty;
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

        return string.Empty;
    }


    /// <summary>
    /// Renders the Search view showing nodes as thumbnails with search.
    /// Uses MeshSearchControl for unified search and display.
    /// For NodeType nodes, shows instances of that type (nodeType:name scope:subtree).
    /// For instance nodes, uses CatalogQuery if set, otherwise defaults to namespace query.
    /// Excludes NodeType nodes from results (use NodeTypes area to view those).
    /// Render mode is determined by CatalogMode property (hierarchical or grouped).
    /// Reads search term from ?q= query parameter.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Search(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var configuredNodeTypeMode = host.Hub.Configuration.Get<NodeTypeCatalogMode>() != null;

        // Get search term from query string (if present)
        var searchTerm = host.GetQueryStringParamValue("q")?.Trim();

        return host.Workspace.GetMeshNodeStream().Select(node =>
        {
            // NodeType catalog mode is used when either:
            //  (a) the hub opts in via NodeTypeCatalogMode (e.g. AddNodeTypeView), or
            //  (b) the node itself is a NodeType instance (NodeType = "NodeType") —
            //      so types declared with only AddDefaultLayoutAreas still render as
            //      catalogs of their instances instead of falling through to the
            //      generic namespace search.
            var isNodeTypeMode = configuredNodeTypeMode
                || node?.NodeType == MeshNode.NodeTypePath;

            // For NodeType mode, query instances under this NodeType's namespace.
            // Uses the node's own path as namespace to correctly scope to local instances.
            // E.g., FutuRe/EuropeRe/LineOfBusiness → finds children under that namespace,
            // regardless of whether they reference the local or parent nodeType path.
            if (isNodeTypeMode && node != null)
            {
                var nodeTypePath = node.Path;
                var nodeTypeDefinition = node.Content as NodeTypeDefinition;

                // Build query: always restrict to this nodeType.
                // If DefaultNamespace is set, scope to that namespace; otherwise scope to current path descendants.
                var hiddenQuery = nodeTypeDefinition?.DefaultNamespace != null
                    ? $"nodeType:{nodeTypePath} namespace:{nodeTypeDefinition.DefaultNamespace}"
                    : $"nodeType:{nodeTypePath} namespace:{nodeTypePath} scope:descendants";
                var defaultNs = nodeTypeDefinition?.DefaultNamespace;
                var createNs = !string.IsNullOrEmpty(defaultNs) ? defaultNs : hubPath;

                var createQs = $"type={Uri.EscapeDataString(nodeTypePath)}";
                if (!string.IsNullOrEmpty(defaultNs))
                    createQs += $"&namespace={Uri.EscapeDataString(defaultNs)}";
                if (nodeTypeDefinition?.RestrictedToNamespaces is { Count: > 0 } nsRestrictions)
                    createQs += $"&namespaces={string.Join(",", nsRestrictions.Select(Uri.EscapeDataString))}";

                var createHref = $"/create?{createQs}";

                return (UiControl?)Controls.MeshSearch
                    .WithHiddenQuery(hiddenQuery)
                    .WithVisibleQuery(searchTerm ?? "")
                    .WithNamespace(hubPath)
                    .WithPlaceholder("Search... (use @ for references)")
                    .WithRenderMode(MeshSearchRenderMode.Hierarchical)
                    .WithMaxColumns(3)
                    .WithCreateHref(createHref);
            }

            // Instance node catalog
            var instanceHiddenQuery = $"namespace:{node?.Namespace ?? hubPath}";

            return Controls.MeshSearch
                .WithHiddenQuery(instanceHiddenQuery)
                .WithVisibleQuery(searchTerm ?? "")
                .WithNamespace(hubPath)
                .WithPlaceholder("Search... (use @ for references)")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical)
                .WithMaxColumns(3)
                // Pre-select self as the Location for the Create form (matches Associated catalog behavior).
                .WithCreateHref($"/{hubPath}/{CreateNodeArea}?namespace={Uri.EscapeDataString(hubPath)}");
        });
    }

    /// <summary>
    /// Renders the Children view showing child nodes as thumbnails without search.
    /// Groups children by NodeType (default) or Category if set, excludes NodeType nodes.
    /// Uses MeshSearchControl for unified search/catalog functionality.
    /// Includes a "Create Sub-Node" button when the user has Create permission.
    /// </summary>
    [Browsable(false)]
    public static UiControl Children(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return Controls.MeshSearch
            .WithTitle("Associated")
            // Exclude NodeType definitions — they belong to type admin, not the
            // Organization/instance catalog — and enable ReactiveMode so moves,
            // renames, and new children show up without an F5.
            .WithHiddenQuery($"namespace:{hubPath} is:main context:search -nodeType:NodeType")
            .WithReactiveMode(true)
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(false)
            .WithShowLoadingIndicator(false)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            // Group by Category first — falls back to NodeType inside the view
            // for nodes without an explicit category, so curated section names
            // ("Marketing", "Demo", etc.) drive the headings when set.
            .WithGroupBy("Category")
            .WithSectionCounts(true)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithCollapsibleSections(true)
            .WithCreateHref($"/{hubPath}/{CreateNodeArea}?namespace={Uri.EscapeDataString(hubPath)}");
    }

    /// <summary>
    /// Renders the Threads catalog showing child Thread nodes using MeshSearchControl.
    /// Includes a "Create Thread" button for starting new conversations.
    /// </summary>
    [Browsable(false)]
    public static UiControl Threads(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var createUrl = $"/{hubPath}/Create?type={Uri.EscapeDataString("Thread")}&namespace={Uri.EscapeDataString($"{hubPath}/_Thread")}";

        return Controls.Stack
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; padding: 0 0 12px 0;")
                .WithView(Controls.Button("Create Thread")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithNavigateToHref(createUrl)))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"nodeType:Thread namespace:{hubPath}/_Thread sort:lastModified-desc")
                .WithNamespace(hubPath)
                .WithRenderMode(MeshSearchRenderMode.Flat));
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: #888;\">Query service not available.</p>"));
        }

        return host.Workspace.GetMeshNodeStream().SelectMany(node =>
        {
            // NodeType children: ObserveQuery snapshot — listing observable, no await.
            var children = meshQuery.ObserveQuery<MeshNode>(
                    MeshQueryRequest.FromQuery($"namespace:{hubPath} nodeType:NodeType"))
                .Take(1)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items)
                .Catch<IReadOnlyList<MeshNode>, Exception>(_ => Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>()));

            // Own NodeType definition by path (known-path lookup): one-shot GetDataRequest
            // — true request/response, no SubscribeRequest+immediate-unsubscribe.
            var ownTypeStream = node != null && !string.IsNullOrEmpty(node.NodeType)
                ? host.Hub.GetMeshNode(node.NodeType)
                : Observable.Return<MeshNode?>(null);

            return children.CombineLatest(ownTypeStream, (nodeTypeChildren, ownType) =>
            {
                var hasOwnType = ownType != null;
                var hasNodeTypeChildren = nodeTypeChildren.Count > 0;
                return (node, ownType, nodeTypeChildren, hasOwnType, hasNodeTypeChildren);
            });
        }).Select(tuple =>
        {
            var (node, ownType, nodeTypeChildren, hasOwnType, hasNodeTypeChildren) = tuple;

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
                foreach (var typeNode in nodeTypeChildren.OrderBy(n => n.Order).ThenBy(n => n.Name))
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
    /// Reads ?collection= query parameter to select which collection to browse.
    /// </summary>
    [Browsable(false)]
    public static UiControl Files(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = BuildUrl(hubPath, OverviewArea);

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.Where(c => c.IsEditable).ToList();

        var stack = Controls.Stack
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref));

        if (collections is not { Count: > 0 })
            return stack;

        foreach (var col in collections)
        {
            if (collections.Count > 1)
                stack = stack.WithView(Controls.Title(col.DisplayName ?? col.Name, 3));

            var colConfig = col;
            stack = stack.WithView(new FileBrowserControl(colConfig.Name)
                .WithCollectionConfiguration(colConfig)
                .WithCollectionInfo(colConfig.SourceType, colConfig.BasePath, colConfig.Settings)
                .CreatePath());
        }

        return stack;
    }

    private static UiControl BuildFilesView(LayoutAreaHost host, string hubPath, bool readOnly)
    {
        var backHref = BuildUrl(hubPath, OverviewArea);

        var stack = Controls.Stack
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref));

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.Where(c => c.IsEditable).ToList();

        if (collections is not { Count: > 0 })
            return stack;

        if (collections.Count == 1)
        {
            stack = stack.WithView(new FileBrowserControl(collections[0].Name).WithReadOnly(readOnly));
            return stack;
        }

        // Multiple collections: show combobox for selection
        var initialCollection = host.GetQueryStringParamValue("collection") ?? collections[0].Name;

        var options = collections
            .Select(c => (Option)new Option<string>(c.Name, c.DisplayName ?? c.Name))
            .ToArray();

        var selectDataId = "filesCollectionSelect";
        var optionsDataId = "filesCollectionOptions";

        host.UpdateData(selectDataId, new Dictionary<string, object?> { ["collection"] = initialCollection });
        host.UpdateData(optionsDataId, options);

        stack = stack.WithView(new ComboboxControl(
            new JsonPointerReference("collection"),
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsDataId)))
        {
            Label = "Collection",
            Autocomplete = ComboboxAutocomplete.Both,
            DataContext = LayoutAreaReference.GetDataPointer(selectDataId)
        });

        stack = stack.WithView((h, _2) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(selectDataId)
                .Select(data =>
                {
                    var selected = data?.GetValueOrDefault("collection")?.ToString();
                    if (string.IsNullOrEmpty(selected))
                        return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Select a collection.</p>");
                    return (UiControl?)new FileBrowserControl(selected).WithReadOnly(readOnly);
                }));

        return stack;
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
            return host.Workspace.GetMeshNodeStream().Select(node =>
            {
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
        var iconUrl = !string.IsNullOrEmpty(imageUrl) ? imageUrl : "/static/NodeTypeIcons/document.svg";
        var name = node.Name ?? node.Id;

        var iconHtml = iconUrl.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            ? $"<div style=\"width: 24px; height: 24px; flex-shrink: 0; display: flex; align-items: center; justify-content: center;\">{iconUrl}</div>"
            : $"<img src=\"{iconUrl}\" alt=\"\" style=\"width: 24px; height: 24px; flex-shrink: 0; object-fit: contain;\" />";

        return Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px;"">
                {iconHtml}
                <span>{name}</span>
            </div>");
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
                .WithShowSearchBox(true)
                .WithPlaceholder($"Search {collectionName}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical)
                .WithMaxColumns(3));
        }

        // Show specific entity as navigation link
        var entityPath = $"{host.Hub.Address}/{collectionName}/{entityId}";
        return Observable.Return<UiControl?>(Controls.Markdown(
            $"[View {collectionName}: {entityId}](/{entityPath})"));
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

    private static UiControl RenderNodeSchema(MeshNode? node, string _, JsonSerializerOptions jsonOptions)
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

    private static string GenerateJsonSchema(Type type, JsonSerializerOptions jsonOptions)
    {
        // Use the built-in JsonSchemaExporter from System.Text.Json.Schema
        var options = jsonOptions;

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
    /// Delegates to AccessControlLayoutArea for the full management UI.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> AccessControl(LayoutAreaHost host, RenderingContext ctx)
        => AccessControlLayoutArea.AccessControl(host, ctx);

    /// <summary>
    /// Renders the Groups area for managing group memberships on this node.
    /// Delegates to GroupsLayoutArea for the full management UI.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Groups(LayoutAreaHost host, RenderingContext ctx)
        => GroupsLayoutArea.Groups(host, ctx);

    #endregion

    #region Chat

    /// <summary>
    /// Renders a standalone ThreadChatControl for the current node.
    /// Can be embedded in markdown via @@("path/Chat").
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Chat(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var nodeName = nodePath.Contains('/') ? nodePath[(nodePath.LastIndexOf('/') + 1)..] : nodePath;

        return Observable.Return<UiControl?>(new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName(nodeName));
    }

    #endregion

    #region Create Node

    /// <summary>
    /// Renders the Create Node area showing available types to create.
    /// Delegates to CreateLayoutArea.Create for the actual implementation.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> CreateNode(LayoutAreaHost host, RenderingContext ctx)
        => CreateLayoutArea.Create(host, ctx);

    /// <summary>
    /// Renders the Edit area showing all content type fields in pure edit mode with auto-save.
    /// Unlike Overview (which is toggleable click-to-edit), Edit shows all fields as editable immediately.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> EditNode(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.CombineLatest(
            PermissionHelper.GetEffectivePermissions(host.Hub, hubPath),
            (nodes, permissions) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);

                if (!permissions.HasFlag(Permission.Update))
                    return (UiControl?)BuildAccessDenied(hubPath);

                return (UiControl?)BuildEditNodeContent(host, node);
            });
    }

    private static UiControl BuildEditNodeContent(LayoutAreaHost host, MeshNode? node)
    {
        if (node == null)
            return Controls.Markdown("*Node not found*");

        var instance = node.Content;
        if (instance == null)
            return Controls.Stack.WithWidth("100%").WithStyle(GetContainerStyle(host))
                .WithView(BuildHeader(host, node, false))
                .WithView(Controls.Markdown("*No content type configured for this node.*")
                    .WithStyle("color: var(--neutral-foreground-hint);"));

        if (instance is JsonElement je)
            instance = JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions)!;

        // Skip edit form for NodeTypeDefinition content (type root nodes)
        if (instance is Configuration.NodeTypeDefinition)
            return Controls.Stack.WithWidth("100%").WithStyle(GetContainerStyle(host))
                .WithView(BuildHeader(host, node, false))
                .WithView(Controls.Markdown("*Built-in type nodes cannot be edited here.*")
                    .WithStyle("color: var(--neutral-foreground-hint);"));

        var contentType = instance.GetType();
        var nodePath = node.Path;
        var dataId = Layout.Domain.EditLayoutArea.GetDataId(nodePath);
        host.UpdateData(dataId, instance);

        // Setup auto-save (same mechanism as Overview)
        OverviewLayoutArea.SetupAutoSave(host, dataId, instance, node);

        var container = Controls.Stack.WithWidth("100%").WithStyle(GetContainerStyle(host));

        // Header with title
        container = container.WithView(BuildHeader(host, node, canEdit: true));

        // Property form in pure edit mode (not toggleable)
        container = container.WithView(Layout.Domain.EditLayoutArea.BuildPropertyForm(
            host, contentType, dataId, canEdit: true, isToggleable: false));

        return container;
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
