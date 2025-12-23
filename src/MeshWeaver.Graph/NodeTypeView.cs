using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// - Details: Overview with DataModel summary and layout areas list
/// - DataModel: View/edit the TypeSource
/// - LayoutAreas: List all layout areas
/// - LayoutAreaEditor: Edit a specific layout area
/// </summary>
public static class NodeTypeView
{
    public const string DetailsArea = "Details";
    public const string DataModelArea = "DataModel";
    public const string LayoutAreasArea = "LayoutAreas";
    public const string LayoutAreaEditorArea = "LayoutAreaEditor";

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(DataModelArea, DataModel)
            .WithView(LayoutAreasArea, LayoutAreas)
            .WithView(LayoutAreaEditorArea, LayoutAreaEditor));

    /// <summary>
    /// Renders the Details/Overview area for a NodeType.
    /// Shows type metadata, DataModel summary, and list of layout areas.
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html("<h2>NodeType Details</h2>"))
                .WithView(Controls.Html("<p>Required services not available.</p>")));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
            {
                return Controls.Stack
                    .WithWidth("100%")
                    .WithView(Controls.Html("<h2>NodeType Not Found</h2>"))
                    .WithView(Controls.Html($"<p>No NodeType definition found at path: {hubPath}</p>"));
            }

            var dataModel = await nodeTypeService.GetDataModelAsync(content.Id, hubPath, ct);
            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);

            return BuildDetailsContent(host, node!, content, dataModel, layoutAreas);
        });
    }

    private static UiControl BuildDetailsContent(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        DataModel? dataModel,
        IReadOnlyList<LayoutAreaConfig> layoutAreas)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header
        var title = content.DisplayName ?? content.Id;
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{title}</h1>"))
            .WithView(Controls.Html($"<span style=\"color: #666;\">NodeType Definition</span>")));

        // Type info card
        var infoCard = Controls.Stack
            .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", content.Id));
        if (!string.IsNullOrEmpty(content.DisplayName))
            infoCard = infoCard.WithView(BuildInfoRow("Display Name", content.DisplayName));
        if (!string.IsNullOrEmpty(content.Description))
            infoCard = infoCard.WithView(BuildInfoRow("Description", content.Description));
        if (!string.IsNullOrEmpty(content.IconName))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", content.IconName));
        infoCard = infoCard.WithView(BuildInfoRow("Display Order", content.DisplayOrder.ToString()));

        stack = stack.WithView(infoCard);

        // DataModel section
        stack = stack.WithView(Controls.Html("<h2>Data Model</h2>"));

        if (dataModel != null)
        {
            var dataModelCard = Controls.Stack
                .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 20px; margin-bottom: 24px;");

            dataModelCard = dataModelCard.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Html($"<strong>Type ID:</strong> {dataModel.Id}"))
                .WithView(Controls.Button("View/Edit TypeSource")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        var href = $"/{node.Prefix}/{DataModelArea}";
                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(href));
                    })));

            // Show first few lines of TypeSource as preview
            if (!string.IsNullOrEmpty(dataModel.TypeSource))
            {
                var preview = GetTypeSourcePreview(dataModel.TypeSource);
                dataModelCard = dataModelCard.WithView(Controls.Html($"<pre style=\"background: #e9ecef; padding: 12px; border-radius: 4px; overflow: auto; max-height: 200px; font-size: 0.85em;\">{System.Web.HttpUtility.HtmlEncode(preview)}</pre>"));
            }

            stack = stack.WithView(dataModelCard);
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No DataModel defined.</p>"));
        }

        // Layout Areas section
        stack = stack.WithView(Controls.Html("<h2>Layout Areas</h2>"));

        if (layoutAreas.Count > 0)
        {
            var areasCard = Controls.Stack
                .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 20px; margin-bottom: 24px;");

            areasCard = areasCard.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Html($"<strong>{layoutAreas.Count} layout area(s) defined</strong>"))
                .WithView(Controls.Button("View All")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        var href = $"/{node.Prefix}/{LayoutAreasArea}";
                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(href));
                    })));

            // List areas
            foreach (var area in layoutAreas.OrderBy(a => a.Order))
            {
                areasCard = areasCard.WithView(Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("padding: 8px 0; border-bottom: 1px solid #e0e0e0; align-items: center; justify-content: space-between;")
                    .WithView(Controls.Html($"<span><strong>{area.Area}</strong> {(area.Title != null ? $"- {area.Title}" : "")}</span>"))
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Lightweight)
                        .WithClickAction(ctx =>
                        {
                            var href = $"/{node.Prefix}/{LayoutAreaEditorArea}/{area.Id}";
                            ctx.Host.UpdateArea(ctx.Area, new RedirectControl(href));
                        })));
            }

            stack = stack.WithView(areasCard);
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No layout areas defined.</p>"));
        }

        return stack;
    }

    private static string GetTypeSourcePreview(string typeSource)
    {
        var lines = typeSource.Split('\n').Take(15).ToArray();
        var preview = string.Join('\n', lines);
        if (typeSource.Split('\n').Length > 15)
            preview += "\n... (truncated)";
        return preview;
    }

    /// <summary>
    /// Renders the DataModel view/editor.
    /// </summary>
    public static IObservable<UiControl> DataModel(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
        {
            return Observable.Return(Controls.Html("<p>Required services not available.</p>"));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
            {
                return Controls.Html("<p>NodeType not found.</p>");
            }

            var dataModel = await nodeTypeService.GetDataModelAsync(content.Id, hubPath, ct);
            return BuildDataModelContent(host, node!, content, dataModel);
        });
    }

    private static UiControl BuildDataModelContent(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        DataModel? dataModel)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back button
        var backHref = $"/{node.Prefix}/{DetailsArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html($"<h2 style=\"margin: 0;\">Data Model: {content.Id}</h2>"))
            .WithView(Controls.Button("Back to Overview")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        if (dataModel == null)
        {
            stack = stack.WithView(Controls.Html("<p>No DataModel defined for this type.</p>"));
            return stack;
        }

        // DataModel metadata
        var metaCard = Controls.Stack
            .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 16px; margin-bottom: 24px;");
        metaCard = metaCard.WithView(BuildInfoRow("ID", dataModel.Id));
        if (!string.IsNullOrEmpty(dataModel.DisplayName))
            metaCard = metaCard.WithView(BuildInfoRow("Display Name", dataModel.DisplayName));
        if (!string.IsNullOrEmpty(dataModel.Description))
            metaCard = metaCard.WithView(BuildInfoRow("Description", dataModel.Description));
        stack = stack.WithView(metaCard);

        // TypeSource code display
        stack = stack.WithView(Controls.Html("<h3>Type Source (C#)</h3>"));

        if (!string.IsNullOrEmpty(dataModel.TypeSource))
        {
            // Use Monaco editor for code display (read-only for now)
            stack = stack.WithView(
                Controls.Html($"<pre style=\"background: #1e1e1e; color: #d4d4d4; padding: 16px; border-radius: 8px; overflow: auto; max-height: 600px; font-family: 'Consolas', 'Monaco', monospace; font-size: 0.9em;\">{System.Web.HttpUtility.HtmlEncode(dataModel.TypeSource)}</pre>"));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No TypeSource defined.</p>"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the list of Layout Areas.
    /// </summary>
    public static IObservable<UiControl> LayoutAreas(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
        {
            return Observable.Return(Controls.Html("<p>Required services not available.</p>"));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
            {
                return Controls.Html("<p>NodeType not found.</p>");
            }

            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);
            return BuildLayoutAreasContent(host, node!, content, layoutAreas);
        });
    }

    private static UiControl BuildLayoutAreasContent(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        IReadOnlyList<LayoutAreaConfig> layoutAreas)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back button
        var backHref = $"/{node.Prefix}/{DetailsArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html($"<h2 style=\"margin: 0;\">Layout Areas: {content.Id}</h2>"))
            .WithView(Controls.Button("Back to Overview")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        if (layoutAreas.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No layout areas defined for this type.</p>"));
            return stack;
        }

        // List all areas
        foreach (var area in layoutAreas.OrderBy(a => a.Order))
        {
            var areaCard = Controls.Stack
                .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 16px; margin-bottom: 16px;");

            areaCard = areaCard.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 12px;")
                .WithView(Controls.Html($"<h3 style=\"margin: 0;\">{area.Area}</h3>"))
                .WithView(Controls.Button("Edit")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        var href = $"/{node.Prefix}/{LayoutAreaEditorArea}/{area.Id}";
                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(href));
                    })));

            areaCard = areaCard.WithView(BuildInfoRow("ID", area.Id));
            if (!string.IsNullOrEmpty(area.Title))
                areaCard = areaCard.WithView(BuildInfoRow("Title", area.Title));
            if (!string.IsNullOrEmpty(area.Group))
                areaCard = areaCard.WithView(BuildInfoRow("Group", area.Group));
            areaCard = areaCard.WithView(BuildInfoRow("Order", area.Order.ToString()));
            areaCard = areaCard.WithView(BuildInfoRow("Invisible", area.IsInvisible ? "Yes" : "No"));
            areaCard = areaCard.WithView(BuildInfoRow("Has ViewSource", !string.IsNullOrEmpty(area.ViewSource) ? "Yes" : "No"));

            stack = stack.WithView(areaCard);
        }

        return stack;
    }

    /// <summary>
    /// Renders the Layout Area editor for a specific area.
    /// </summary>
    public static IObservable<UiControl> LayoutAreaEditor(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var layoutAreaId = host.Reference.Id?.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
        {
            return Observable.Return(Controls.Html("<p>Required services not available.</p>"));
        }

        if (string.IsNullOrEmpty(layoutAreaId))
        {
            return Observable.Return(Controls.Html("<p>No layout area ID specified.</p>"));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
            {
                return Controls.Html("<p>NodeType not found.</p>");
            }

            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);
            var layoutArea = layoutAreas.FirstOrDefault(a => a.Id == layoutAreaId);

            if (layoutArea == null)
            {
                return Controls.Html($"<p>Layout area '{layoutAreaId}' not found.</p>");
            }

            return BuildLayoutAreaEditorContent(host, node!, content, layoutArea);
        });
    }

    private static UiControl BuildLayoutAreaEditorContent(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        LayoutAreaConfig layoutArea)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back button
        var backHref = $"/{node.Prefix}/{LayoutAreasArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html($"<h2 style=\"margin: 0;\">Edit Layout Area: {layoutArea.Area}</h2>"))
            .WithView(Controls.Button("Back to Layout Areas")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        // Metadata card
        var metaCard = Controls.Stack
            .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 16px; margin-bottom: 24px;");
        metaCard = metaCard.WithView(BuildInfoRow("ID", layoutArea.Id));
        metaCard = metaCard.WithView(BuildInfoRow("Area Name", layoutArea.Area));
        if (!string.IsNullOrEmpty(layoutArea.Title))
            metaCard = metaCard.WithView(BuildInfoRow("Title", layoutArea.Title));
        if (!string.IsNullOrEmpty(layoutArea.Group))
            metaCard = metaCard.WithView(BuildInfoRow("Group", layoutArea.Group));
        metaCard = metaCard.WithView(BuildInfoRow("Order", layoutArea.Order.ToString()));
        metaCard = metaCard.WithView(BuildInfoRow("Invisible", layoutArea.IsInvisible ? "Yes" : "No"));
        stack = stack.WithView(metaCard);

        // ViewSource code display
        stack = stack.WithView(Controls.Html("<h3>View Source (C#)</h3>"));

        if (!string.IsNullOrEmpty(layoutArea.ViewSource))
        {
            stack = stack.WithView(
                Controls.Html($"<pre style=\"background: #1e1e1e; color: #d4d4d4; padding: 16px; border-radius: 8px; overflow: auto; max-height: 600px; font-family: 'Consolas', 'Monaco', monospace; font-size: 0.9em;\">{System.Web.HttpUtility.HtmlEncode(layoutArea.ViewSource)}</pre>"));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No ViewSource defined. This is a metadata-only area.</p>"));
        }

        return stack;
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid #e0e0e0;")
            .WithView(Controls.Html($"<strong style=\"width: 150px; flex-shrink: 0;\">{label}:</strong>"))
            .WithView(Controls.Html($"<span>{System.Web.HttpUtility.HtmlEncode(value)}</span>"));
    }
}
