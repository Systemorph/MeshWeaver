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
/// - Details: Main configuration view
/// - CodeView: Read-only view of the Code
/// - CodeEdit: Monaco editor for Code
/// </summary>
public static class NodeTypeView
{
    public const string DetailsArea = "Details";
    public const string CodeViewArea = "Code";
    public const string CodeEditArea = "CodeEdit";

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(CodeViewArea, CodeView)
            .WithView(CodeEditArea, CodeEdit));

    /// <summary>
    /// Renders the main Details area for a NodeType.
    /// Shows an overview of the NodeType configuration.
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

            var codeConfig = await nodeTypeService.GetCodeConfigurationAsync(content.Id, hubPath, ct);

            return BuildMainLayout(host, node!, content, codeConfig);
        });
    }

    /// <summary>
    /// Builds the main layout with overview and navigation.
    /// </summary>
    private static UiControl BuildMainLayout(
        LayoutAreaHost host,
        MeshNode node,
        NodeTypeDefinition content,
        CodeConfiguration? codeConfig)
    {
        var hubAddress = host.Hub.Address;
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
        infoCard = infoCard.WithView(BuildInfoRow("Has Code", codeConfig?.Code != null ? "Yes" : "No"));
        infoCard = infoCard.WithView(BuildInfoRow("Has HubConfiguration", !string.IsNullOrEmpty(content.HubConfiguration) ? "Yes" : "No"));

        stack = stack.WithView(infoCard);

        // Navigation buttons
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("View Code")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Code())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(codeHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders a read-only view of the CodeConfiguration.
    /// </summary>
    public static IObservable<UiControl> CodeView(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (nodeTypeService == null || persistence == null)
            return Observable.Return(RenderError("Required services not available."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            var codeConfig = await nodeTypeService.GetCodeConfigurationAsync(content.Id, hubPath, ct);

            return BuildCodeViewContent(host, content, codeConfig);
        });
    }

    private static UiControl BuildCodeViewContent(LayoutAreaHost host, NodeTypeDefinition content, CodeConfiguration? codeConfig)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with Edit button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;");

        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0;\">Code: {System.Web.HttpUtility.HtmlEncode(content.DisplayName ?? content.Id)}</h2>"));

        var editHref = new LayoutAreaReference(CodeEditArea).ToHref(hubAddress);
        header = header.WithView(Controls.Button("Edit")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Edit())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref))));

        stack = stack.WithView(header);

        // Code as C# code block (read-only)
        stack = stack.WithView(Controls.Html("<h3>Code (C#)</h3>"));

        if (!string.IsNullOrEmpty(codeConfig?.Code))
        {
            var markdown = $"```csharp\n{codeConfig.Code}\n```";
            stack = stack.WithView(new MarkdownControl(markdown));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No Code defined.</p>"));
        }

        // HubConfiguration
        if (!string.IsNullOrEmpty(content.HubConfiguration))
        {
            stack = stack.WithView(Controls.Html("<h3>HubConfiguration</h3>"));
            var hubConfigMarkdown = $"```csharp\n{content.HubConfiguration}\n```";
            stack = stack.WithView(new MarkdownControl(hubConfigMarkdown));
        }

        // Back button
        var detailsHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(detailsHref))));

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing the CodeConfiguration.
    /// </summary>
    public static IObservable<UiControl> CodeEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (nodeTypeService == null || persistence == null)
            return Observable.Return(RenderError("Required services not available."));

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            var content = node?.Content as NodeTypeDefinition;
            if (content == null)
                return RenderError("NodeType not found.");

            var codeConfig = await nodeTypeService.GetCodeConfigurationAsync(content.Id, hubPath, ct);

            return BuildCodeEditContent(host, node!, content, codeConfig);
        });
    }

    private static UiControl BuildCodeEditContent(LayoutAreaHost host, MeshNode node, NodeTypeDefinition content, CodeConfiguration? codeConfig)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Store initial code in data stream
        host.UpdateData(dataId, codeConfig?.Code ?? "");

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Edit Code: {System.Web.HttpUtility.HtmlEncode(content.DisplayName ?? content.Id)}</h2>"));

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
                var updatedConfig = new CodeConfiguration { Code = currentCode ?? "" };

                // Save via NodeTypeService
                var nodeTypeSvc = actx.Host.Hub.ServiceProvider.GetService<INodeTypeService>();
                await nodeTypeSvc!.SaveCodeConfigurationAsync(node.Prefix, updatedConfig);

                // Navigate back to view
                var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref))));

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
