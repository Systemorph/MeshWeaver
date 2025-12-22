using System.Reactive.Linq;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Views for displaying and editing DataModel and LayoutAreaConfig configurations.
/// - Read-only views show code in markdown C# code blocks
/// - Editor views use Monaco editor with C# syntax highlighting
/// </summary>
public static class ConfigurationViews
{
    // Area names for views
    public const string DataModelSource = nameof(DataModelSource);
    public const string DataModelEditor = nameof(DataModelEditor);
    public const string LayoutAreaSource = nameof(LayoutAreaSource);
    public const string LayoutAreaEditor = nameof(LayoutAreaEditor);

    /// <summary>
    /// Adds configuration views (DataModel and LayoutArea source/editor views) to the layout.
    /// </summary>
    public static LayoutDefinition AddConfigurationViews(this LayoutDefinition layout)
        => layout
            .WithView(DataModelSource, RenderDataModelSource)
            .WithView(DataModelEditor, RenderDataModelEditor)
            .WithView(LayoutAreaSource, RenderLayoutAreaSource)
            .WithView(LayoutAreaEditor, RenderLayoutAreaEditor);

    /// <summary>
    /// Read-only view showing DataModel.TypeSource as a C# markdown code block.
    /// Reference: DataModelSource/{dataModelId}
    /// </summary>
    private static IObservable<UiControl> RenderDataModelSource(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataModelId = host.Reference.Id as string;
        if (string.IsNullOrEmpty(dataModelId))
            return Observable.Return(RenderError("DataModel ID not specified. Use: DataModelSource/{id}"));

        var configStorage = host.Hub.ServiceProvider.GetService<IConfigurationStorageService>();
        if (configStorage == null)
            return Observable.Return(RenderError("Configuration storage service not available."));

        return Observable.FromAsync(async ct =>
        {
            var dataModel = await configStorage.LoadByIdAsync<DataModel>(dataModelId, ct);
            if (dataModel == null)
                return RenderError($"DataModel '{dataModelId}' not found.");

            return RenderCodeBlock(
                dataModel.TypeSource,
                $"DataModel: {dataModel.DisplayName ?? dataModel.Id}",
                host.Hub.Address,
                dataModelId,
                DataModelEditor);
        });
    }

    /// <summary>
    /// Editor view for DataModel.TypeSource using Monaco editor with C# syntax.
    /// Reference: DataModelEditor/{dataModelId}
    /// </summary>
    private static IObservable<UiControl> RenderDataModelEditor(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataModelId = host.Reference.Id as string;
        if (string.IsNullOrEmpty(dataModelId))
            return Observable.Return(RenderError("DataModel ID not specified. Use: DataModelEditor/{id}"));

        var configStorage = host.Hub.ServiceProvider.GetService<IConfigurationStorageService>();
        if (configStorage == null)
            return Observable.Return(RenderError("Configuration storage service not available."));

        return Observable.FromAsync(async ct =>
        {
            var dataModel = await configStorage.LoadByIdAsync<DataModel>(dataModelId, ct);
            if (dataModel == null)
                return RenderError($"DataModel '{dataModelId}' not found.");

            return RenderCodeEditor(
                host,
                ctx,
                dataModel.TypeSource,
                $"Edit DataModel: {dataModel.DisplayName ?? dataModel.Id}",
                host.Hub.Address,
                dataModelId,
                DataModelSource,
                (source, dm) => dm with { TypeSource = source },
                dataModel);
        });
    }

    /// <summary>
    /// Read-only view showing LayoutAreaConfig.ViewSource as a C# markdown code block.
    /// Reference: LayoutAreaSource/{layoutAreaId}
    /// </summary>
    private static IObservable<UiControl> RenderLayoutAreaSource(LayoutAreaHost host, RenderingContext ctx)
    {
        var layoutAreaId = host.Reference.Id as string;
        if (string.IsNullOrEmpty(layoutAreaId))
            return Observable.Return(RenderError("LayoutArea ID not specified. Use: LayoutAreaSource/{id}"));

        var configStorage = host.Hub.ServiceProvider.GetService<IConfigurationStorageService>();
        if (configStorage == null)
            return Observable.Return(RenderError("Configuration storage service not available."));

        return Observable.FromAsync(async ct =>
        {
            var layoutArea = await configStorage.LoadByIdAsync<LayoutAreaConfig>(layoutAreaId, ct);
            if (layoutArea == null)
                return RenderError($"LayoutAreaConfig '{layoutAreaId}' not found.");

            var source = layoutArea.ViewSource ?? "// No view source defined";
            return RenderCodeBlock(
                source,
                $"Layout Area: {layoutArea.Title ?? layoutArea.Area}",
                host.Hub.Address,
                layoutAreaId,
                LayoutAreaEditor);
        });
    }

    /// <summary>
    /// Editor view for LayoutAreaConfig.ViewSource using Monaco editor with C# syntax.
    /// Reference: LayoutAreaEditor/{layoutAreaId}
    /// </summary>
    private static IObservable<UiControl> RenderLayoutAreaEditor(LayoutAreaHost host, RenderingContext ctx)
    {
        var layoutAreaId = host.Reference.Id as string;
        if (string.IsNullOrEmpty(layoutAreaId))
            return Observable.Return(RenderError("LayoutArea ID not specified. Use: LayoutAreaEditor/{id}"));

        var configStorage = host.Hub.ServiceProvider.GetService<IConfigurationStorageService>();
        if (configStorage == null)
            return Observable.Return(RenderError("Configuration storage service not available."));

        return Observable.FromAsync(async ct =>
        {
            var layoutArea = await configStorage.LoadByIdAsync<LayoutAreaConfig>(layoutAreaId, ct);
            if (layoutArea == null)
                return RenderError($"LayoutAreaConfig '{layoutAreaId}' not found.");

            var source = layoutArea.ViewSource ?? "";
            return RenderCodeEditor(
                host,
                ctx,
                source,
                $"Edit Layout Area: {layoutArea.Title ?? layoutArea.Area}",
                host.Hub.Address,
                layoutAreaId,
                LayoutAreaSource,
                (src, area) => area with { ViewSource = src },
                layoutArea);
        });
    }

    /// <summary>
    /// Renders a read-only code block with header and edit button.
    /// </summary>
    private static UiControl RenderCodeBlock(
        string code,
        string title,
        Address hubAddress,
        string configId,
        string editorArea)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with title and Edit button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 8px;")
            .WithView(Controls.Html($"<h3 style=\"margin: 0;\">{title}</h3>"))
            .WithView(Controls.Button("Edit")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(actionCtx =>
                {
                    var editHref = new LayoutAreaReference(editorArea) { Id = configId }.ToHref(hubAddress);
                    actionCtx.Host.UpdateArea(actionCtx.Area, new RedirectControl(editHref));
                }));

        stack = stack.WithView(header);

        // Code block in markdown
        var markdown = $"```csharp\n{code}\n```";
        stack = stack.WithView(new MarkdownControl(markdown));

        return stack;
    }

    /// <summary>
    /// Renders a Monaco code editor with header, Save and Cancel buttons.
    /// </summary>
    private static UiControl RenderCodeEditor<TConfig>(
        LayoutAreaHost host,
        RenderingContext ctx,
        string initialCode,
        string title,
        Address hubAddress,
        string configId,
        string sourceArea,
        Func<string, TConfig, TConfig> updateSource,
        TConfig config)
    {
        var stack = Controls.Stack.WithWidth("100%");
        var dataId = Guid.NewGuid().AsString();

        // Store initial code in data stream
        host.UpdateData(dataId, initialCode);

        // Header with title
        stack = stack.WithView(Controls.Html($"<h3 style=\"margin-bottom: 8px;\">{title}</h3>"));

        // Monaco editor bound to the data stream
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("400px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            with { DataContext = LayoutAreaReference.GetDataPointer(dataId), Value = new JsonPointerReference("") };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 8px;");

        // Save button - saves via DataChangeRequest
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async actionCtx =>
            {
                // Get the current code from the data stream
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();

                // Update the config
                var updatedConfig = updateSource(currentCode ?? "", config);

                // Post DataChangeRequest - the TypeSource will handle persistence
                actionCtx.Host.Hub.Post(
                    DataChangeRequest.Update(new object[] { updatedConfig! }),
                    o => o.WithTarget(actionCtx.Host.Hub.Address));

                // Navigate back to source view
                var sourceHref = new LayoutAreaReference(sourceArea) { Id = configId }.ToHref(hubAddress);
                actionCtx.Host.UpdateArea(actionCtx.Area, new RedirectControl(sourceHref));
            }));

        // Cancel button
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actionCtx =>
            {
                var sourceHref = new LayoutAreaReference(sourceArea) { Id = configId }.ToHref(hubAddress);
                actionCtx.Host.UpdateArea(actionCtx.Area, new RedirectControl(sourceHref));
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders an error message.
    /// </summary>
    private static UiControl RenderError(string message)
        => new MarkdownControl($"> [!CAUTION]\n> {message}\n");
}
