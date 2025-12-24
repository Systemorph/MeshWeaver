using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// - Details: Main configuration view with left nav menu and content area
/// - DataModelView: Read-only view of a DataModel
/// - DataModelEdit: Monaco editor for DataModel TypeSource
/// - LayoutAreaView: Read-only view of a LayoutArea source
/// - LayoutAreaEdit: Monaco editor for LayoutArea ViewSource
/// </summary>
public static class NodeTypeView
{
    public const string DetailsArea = "Details";
    public const string DataModelViewArea = "DataModel";
    public const string DataModelEditArea = "DataModelEdit";
    public const string LayoutAreaViewArea = "LayoutArea";
    public const string LayoutAreaEditArea = "LayoutAreaEdit";
    public const string AddDataModelArea = "AddDataModel";
    public const string AddLayoutAreaArea = "AddLayoutArea";

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(DataModelViewArea, DataModelView)
            .WithView(DataModelEditArea, DataModelEdit)
            .WithView(LayoutAreaViewArea, LayoutAreaView)
            .WithView(LayoutAreaEditArea, LayoutAreaEdit)
            .WithView(AddDataModelArea, AddDataModel)
            .WithView(AddLayoutAreaArea, AddLayoutArea));

    /// <summary>
    /// Renders the main Details area for a NodeType.
    /// Shows a left navigation menu with Data Model and Layout Areas sections,
    /// and a content area that displays the selected item.
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
        {
            return Observable.Return(RenderError("Required services not available."));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
            {
                return RenderError($"No NodeType definition found at path: {hubPath}");
            }

            var dataModel = await nodeTypeService.GetDataModelAsync(content.Id, hubPath, ct);
            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);

            return BuildMainLayout(host, node!, content, dataModel, layoutAreas);
        });
    }

    /// <summary>
    /// Builds the main layout with left nav menu and content area.
    /// </summary>
    private static UiControl BuildMainLayout(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        DataModel? dataModel,
        IReadOnlyList<LayoutAreaConfig> layoutAreas)
    {
        var hubAddress = host.Hub.Address;
        var nodePrefix = node.Prefix;

        // Build left navigation menu
        var navMenu = Controls.NavMenu
            .WithSkin(s => s.WithWidth(280));

        // Data Model section with "+" button
        var dataModelGroup = Controls.NavGroup("Data Model")
            .WithIcon(FluentIcons.Database());

        // Add existing data model entry
        if (dataModel != null)
        {
            var dataModelHref = new LayoutAreaReference(DataModelViewArea) { Id = dataModel.Id }.ToHref(hubAddress);
            dataModelGroup = dataModelGroup.WithNavLink(dataModel.DisplayName ?? dataModel.Id, dataModelHref, FluentIcons.Code());
        }

        // Add "+" button for new data model
        var addDataModelHref = new LayoutAreaReference(AddDataModelArea).ToHref(hubAddress);
        dataModelGroup = dataModelGroup.WithNavLink("+ Add Data Model", addDataModelHref, FluentIcons.Add());

        navMenu = navMenu.WithNavGroup(dataModelGroup);

        // Layout Areas section with "+" button
        var layoutAreasGroup = Controls.NavGroup("Layout Areas")
            .WithIcon(FluentIcons.LayoutCellFour());

        // Add existing layout area entries
        foreach (var area in layoutAreas.OrderBy(a => a.Order))
        {
            var areaHref = new LayoutAreaReference(LayoutAreaViewArea) { Id = area.Id }.ToHref(hubAddress);
            layoutAreasGroup = layoutAreasGroup.WithNavLink(area.Title ?? area.Area, areaHref, FluentIcons.Code());
        }

        // Add "+" button for new layout area
        var addLayoutAreaHref = new LayoutAreaReference(AddLayoutAreaArea).ToHref(hubAddress);
        layoutAreasGroup = layoutAreasGroup.WithNavLink("+ Add Layout Area", addLayoutAreaHref, FluentIcons.Add());

        navMenu = navMenu.WithNavGroup(layoutAreasGroup);

        // Build content area - default shows overview
        var contentArea = BuildOverviewContent(content, dataModel, layoutAreas);

        // Create splitter layout with nav menu on left and content on right
        var splitter = Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal))
            .WithView(navMenu, s => s.WithSize("280px").WithResizable(false))
            .WithView(contentArea, s => s.WithSize("1fr"));

        return splitter;
    }

    /// <summary>
    /// Builds the overview content showing type metadata.
    /// </summary>
    private static UiControl BuildOverviewContent(
        NodeTypeDefinition content,
        DataModel? dataModel,
        IReadOnlyList<LayoutAreaConfig> layoutAreas)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header
        var title = content.DisplayName ?? content.Id;
        stack = stack.WithView(Controls.Html($"<h1 style=\"margin: 0 0 8px 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));
        stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin: 0 0 24px 0;\">NodeType Configuration</p>"));

        // Type info card
        var infoCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", content.Id));
        if (!string.IsNullOrEmpty(content.DisplayName))
            infoCard = infoCard.WithView(BuildInfoRow("Display Name", content.DisplayName));
        if (!string.IsNullOrEmpty(content.Description))
            infoCard = infoCard.WithView(BuildInfoRow("Description", content.Description));
        if (!string.IsNullOrEmpty(content.IconName))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", content.IconName));
        infoCard = infoCard.WithView(BuildInfoRow("Display Order", content.DisplayOrder.ToString()));

        stack = stack.WithView(infoCard);

        // Summary section
        stack = stack.WithView(Controls.Html("<h2>Summary</h2>"));

        var summaryCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px;");

        summaryCard = summaryCard.WithView(BuildInfoRow("Data Models", dataModel != null ? "1" : "0"));
        summaryCard = summaryCard.WithView(BuildInfoRow("Layout Areas", layoutAreas.Count.ToString()));

        stack = stack.WithView(summaryCard);

        return stack;
    }

    /// <summary>
    /// Renders a read-only view of a DataModel's TypeSource.
    /// Shows the type definition diagram from DomainViews and the C# source.
    /// </summary>
    public static IObservable<UiControl> DataModelView(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var dataModelId = host.Reference.Id as string;
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (nodeTypeService == null)
            return Observable.Return(RenderError("NodeType service not available."));

        if (string.IsNullOrEmpty(dataModelId))
            return Observable.Return(RenderError("DataModel ID not specified."));

        return Observable.FromAsync(async ct =>
        {
            var dataModel = await nodeTypeService.GetDataModelAsync(dataModelId, hubPath, ct);
            if (dataModel == null)
                return RenderError($"DataModel '{dataModelId}' not found.");

            return BuildDataModelViewContent(host, dataModel);
        });
    }

    private static UiControl BuildDataModelViewContent(LayoutAreaHost host, DataModel dataModel)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with Edit button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;");

        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0;\">Data Model: {System.Web.HttpUtility.HtmlEncode(dataModel.DisplayName ?? dataModel.Id)}</h2>"));

        var editHref = new LayoutAreaReference(DataModelEditArea) { Id = dataModel.Id }.ToHref(hubAddress);
        header = header.WithView(Controls.Button("Edit")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Edit())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref))));

        stack = stack.WithView(header);

        // Metadata card
        var metaCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 16px; margin-bottom: 24px;");
        metaCard = metaCard.WithView(BuildInfoRow("ID", dataModel.Id));
        if (!string.IsNullOrEmpty(dataModel.DisplayName))
            metaCard = metaCard.WithView(BuildInfoRow("Display Name", dataModel.DisplayName));
        if (!string.IsNullOrEmpty(dataModel.Description))
            metaCard = metaCard.WithView(BuildInfoRow("Description", dataModel.Description));
        stack = stack.WithView(metaCard);

        // TypeSource as C# code block (read-only)
        stack = stack.WithView(Controls.Html("<h3>Type Source (C#)</h3>"));

        if (!string.IsNullOrEmpty(dataModel.TypeSource))
        {
            var markdown = $"```csharp\n{dataModel.TypeSource}\n```";
            stack = stack.WithView(new MarkdownControl(markdown));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No TypeSource defined.</p>"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing a DataModel's TypeSource.
    /// </summary>
    public static IObservable<UiControl> DataModelEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var dataModelId = host.Reference.Id as string;
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (nodeTypeService == null || persistence == null)
            return Observable.Return(RenderError("Required services not available."));

        if (string.IsNullOrEmpty(dataModelId))
            return Observable.Return(RenderError("DataModel ID not specified."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var dataModel = await nodeTypeService.GetDataModelAsync(dataModelId, hubPath, ct);
            if (dataModel == null)
                return RenderError($"DataModel '{dataModelId}' not found.");

            return BuildDataModelEditContent(host, node!, dataModel);
        });
    }

    private static UiControl BuildDataModelEditContent(LayoutAreaHost host, MeshNode node, DataModel dataModel)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Store initial code in data stream
        host.UpdateData(dataId, dataModel.TypeSource ?? "");

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Edit Data Model: {System.Web.HttpUtility.HtmlEncode(dataModel.DisplayName ?? dataModel.Id)}</h2>"));

        // Monaco editor bound to the data stream
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            with
        { DataContext = LayoutAreaReference.GetDataPointer(dataId), Value = new JsonPointerReference("") };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();
                var updatedDataModel = dataModel with { TypeSource = currentCode ?? "" };

                // Save via NodeTypeService
                var nodeTypeService = actx.Host.Hub.ServiceProvider.GetService<INodeTypeService>();
                await nodeTypeService!.SaveDataModelAsync(node.Prefix, updatedDataModel);

                // Navigate back to view
                var viewHref = new LayoutAreaReference(DataModelViewArea) { Id = dataModel.Id }.ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var viewHref = new LayoutAreaReference(DataModelViewArea) { Id = dataModel.Id }.ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders a read-only view of a LayoutArea's ViewSource as C# code.
    /// </summary>
    public static IObservable<UiControl> LayoutAreaView(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var layoutAreaId = host.Reference.Id as string;
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (nodeTypeService == null || persistence == null)
            return Observable.Return(RenderError("Required services not available."));

        if (string.IsNullOrEmpty(layoutAreaId))
            return Observable.Return(RenderError("LayoutArea ID not specified."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);
            var layoutArea = layoutAreas.FirstOrDefault(a => a.Id == layoutAreaId);

            if (layoutArea == null)
                return RenderError($"LayoutArea '{layoutAreaId}' not found.");

            return BuildLayoutAreaViewContent(host, layoutArea);
        });
    }

    private static UiControl BuildLayoutAreaViewContent(LayoutAreaHost host, LayoutAreaConfig layoutArea)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with Edit button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;");

        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0;\">Layout Area: {System.Web.HttpUtility.HtmlEncode(layoutArea.Title ?? layoutArea.Area)}</h2>"));

        var editHref = new LayoutAreaReference(LayoutAreaEditArea) { Id = layoutArea.Id }.ToHref(hubAddress);
        header = header.WithView(Controls.Button("Edit")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Edit())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref))));

        stack = stack.WithView(header);

        // Metadata card
        var metaCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 16px; margin-bottom: 24px;");
        metaCard = metaCard.WithView(BuildInfoRow("ID", layoutArea.Id));
        metaCard = metaCard.WithView(BuildInfoRow("Area", layoutArea.Area));
        if (!string.IsNullOrEmpty(layoutArea.Title))
            metaCard = metaCard.WithView(BuildInfoRow("Title", layoutArea.Title));
        if (!string.IsNullOrEmpty(layoutArea.Group))
            metaCard = metaCard.WithView(BuildInfoRow("Group", layoutArea.Group));
        metaCard = metaCard.WithView(BuildInfoRow("Order", layoutArea.Order.ToString()));
        metaCard = metaCard.WithView(BuildInfoRow("Invisible", layoutArea.IsInvisible ? "Yes" : "No"));
        stack = stack.WithView(metaCard);

        // ViewSource as C# code block (read-only)
        stack = stack.WithView(Controls.Html("<h3>View Source (C#)</h3>"));

        if (!string.IsNullOrEmpty(layoutArea.ViewSource))
        {
            var markdown = $"```csharp\n{layoutArea.ViewSource}\n```";
            stack = stack.WithView(new MarkdownControl(markdown));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No ViewSource defined.</p>"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing a LayoutArea's ViewSource.
    /// </summary>
    public static IObservable<UiControl> LayoutAreaEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var layoutAreaId = host.Reference.Id as string;
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (nodeTypeService == null || persistence == null)
            return Observable.Return(RenderError("Required services not available."));

        if (string.IsNullOrEmpty(layoutAreaId))
            return Observable.Return(RenderError("LayoutArea ID not specified."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            var layoutAreas = await nodeTypeService.GetLayoutAreasAsync(content.Id, hubPath, ct);
            var layoutArea = layoutAreas.FirstOrDefault(a => a.Id == layoutAreaId);

            if (layoutArea == null)
                return RenderError($"LayoutArea '{layoutAreaId}' not found.");

            return BuildLayoutAreaEditContent(host, node!, layoutArea);
        });
    }

    private static UiControl BuildLayoutAreaEditContent(LayoutAreaHost host, MeshNode node, LayoutAreaConfig layoutArea)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Store initial code in data stream
        host.UpdateData(dataId, layoutArea.ViewSource ?? "");

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Edit Layout Area: {System.Web.HttpUtility.HtmlEncode(layoutArea.Title ?? layoutArea.Area)}</h2>"));

        // Monaco editor bound to the data stream
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            with
        { DataContext = LayoutAreaReference.GetDataPointer(dataId), Value = new JsonPointerReference("") };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();
                var updatedLayoutArea = layoutArea with { ViewSource = currentCode ?? "" };

                // Save via NodeTypeService
                var nodeTypeService = actx.Host.Hub.ServiceProvider.GetService<INodeTypeService>();
                await nodeTypeService!.SaveLayoutAreaAsync(node.Prefix, updatedLayoutArea);

                // Navigate back to view
                var viewHref = new LayoutAreaReference(LayoutAreaViewArea) { Id = layoutArea.Id }.ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var viewHref = new LayoutAreaReference(LayoutAreaViewArea) { Id = layoutArea.Id }.ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the form for adding a new DataModel.
    /// </summary>
    public static IObservable<UiControl> AddDataModel(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
            return Observable.Return(RenderError("Required services not available."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            return BuildAddDataModelContent(host, node!, content);
        });
    }

    private static UiControl BuildAddDataModelContent(LayoutAreaHost host, MeshNode node, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Default template for new data model
        var defaultTemplate = $@"public record {content.Id}
{{
    [Key]
    public string Id {{ get; init; }} = string.Empty;

    public string Name {{ get; init; }} = string.Empty;
}}";

        // Store initial code in data stream
        host.UpdateData(dataId, defaultTemplate);

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Add Data Model for: {System.Web.HttpUtility.HtmlEncode(content.DisplayName ?? content.Id)}</h2>"));

        // Monaco editor
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            with
        { DataContext = LayoutAreaReference.GetDataPointer(dataId), Value = new JsonPointerReference("") };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Create")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();
                var newDataModel = new DataModel
                {
                    Id = content.Id,
                    DisplayName = content.DisplayName,
                    Description = content.Description,
                    IconName = content.IconName,
                    DisplayOrder = content.DisplayOrder,
                    TypeSource = currentCode ?? ""
                };

                var nodeTypeService = actx.Host.Hub.ServiceProvider.GetService<INodeTypeService>();
                await nodeTypeService!.SaveDataModelAsync(node.Prefix, newDataModel);

                // Navigate to the view
                var viewHref = new LayoutAreaReference(DataModelViewArea) { Id = content.Id }.ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var detailsHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(detailsHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the form for adding a new LayoutArea.
    /// </summary>
    public static IObservable<UiControl> AddLayoutArea(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (persistence == null || nodeTypeService == null)
            return Observable.Return(RenderError("Required services not available."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            return BuildAddLayoutAreaContent(host, node!, content);
        });
    }

    private static UiControl BuildAddLayoutAreaContent(LayoutAreaHost host, MeshNode node, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();
        var areaNameId = Guid.NewGuid().AsString();

        // Default template for new layout area
        var defaultTemplate = @"public static UiControl Render(LayoutAreaHost host, RenderingContext ctx)
{
    return Controls.Stack
        .WithView(Controls.Html(""<h1>Hello World</h1>""));
}";

        // Store initial values in data streams
        host.UpdateData(dataId, defaultTemplate);
        host.UpdateData(areaNameId, "NewArea");

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Add Layout Area for: {System.Web.HttpUtility.HtmlEncode(content.DisplayName ?? content.Id)}</h2>"));

        // Area name input
        stack = stack.WithView(Controls.Html("<label style=\"font-weight: bold; display: block; margin-bottom: 8px;\">Area Name:</label>"));
        stack = stack.WithView(Controls.Text(new JsonPointerReference(""))
            with
        { DataContext = LayoutAreaReference.GetDataPointer(areaNameId) });

        stack = stack.WithView(Controls.Html("<div style=\"height: 16px;\"></div>"));

        // Monaco editor
        stack = stack.WithView(Controls.Html("<label style=\"font-weight: bold; display: block; margin-bottom: 8px;\">View Source (C#):</label>"));
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("400px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            with
        { DataContext = LayoutAreaReference.GetDataPointer(dataId), Value = new JsonPointerReference("") };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Create")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();
                var areaName = await host.Stream.GetDataStream<string>(areaNameId).FirstAsync();

                var newLayoutArea = new LayoutAreaConfig
                {
                    Id = $"{content.Id}-{areaName}",
                    Area = areaName ?? "NewArea",
                    Title = areaName,
                    Order = 0,
                    ViewSource = currentCode ?? ""
                };

                var nodeTypeService = actx.Host.Hub.ServiceProvider.GetService<INodeTypeService>();
                await nodeTypeService!.SaveLayoutAreaAsync(node.Prefix, newLayoutArea);

                // Navigate to the view
                var viewHref = new LayoutAreaReference(LayoutAreaViewArea) { Id = newLayoutArea.Id }.ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var detailsHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(detailsHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.Html($"<strong style=\"width: 150px; flex-shrink: 0;\">{System.Web.HttpUtility.HtmlEncode(label)}:</strong>"))
            .WithView(Controls.Html($"<span>{System.Web.HttpUtility.HtmlEncode(value)}</span>"));
    }

    private static UiControl RenderError(string message)
        => new MarkdownControl($"> [!CAUTION]\n> {message}\n");
}
