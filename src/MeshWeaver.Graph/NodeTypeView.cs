using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// - Details: Overview of the NodeType
/// - CodeView: Split view with left menu and code display
/// - CodeEdit: Monaco editor for code editing
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeView
{
    public const string DetailsArea = "Details";
    public const string CodeViewArea = "Code";
    public const string CodeEditArea = "CodeEdit";
    public const string HubConfigViewArea = "HubConfig";
    public const string HubConfigEditArea = "HubConfigEdit";

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFilesDataId = "codeFiles";
    private const string CodeFileDataId = "codeFile";
    private const string SelectionDataId = "selection";

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(CodeViewArea)
            .WithView(DetailsArea, Details)
            .WithView(CodeViewArea, CodeView)
            .WithView(CodeEditArea, CodeEdit)
            .WithView(HubConfigViewArea, HubConfigView)
            .WithView(HubConfigEditArea, HubConfigEdit));

    /// <summary>
    /// Renders the main Details area for a NodeType.
    /// Shows an overview of the NodeType configuration.
    /// Returns static structure with data-bound dynamic parts.
    /// </summary>
    public static UiControl Details(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams - data will be stored in data section
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return static structure with nested observable view for content
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (definition, codeFile) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading NodeType definition...");
                        return BuildDetailsLayout(host, definition, codeFile);
                    }),
                "Content"
            );
    }

    /// <summary>
    /// Builds the Details layout with overview and navigation.
    /// </summary>
    private static UiControl BuildDetailsLayout(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeFile)
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

        var hasCode = !string.IsNullOrEmpty(codeFile?.Code);
        infoCard = infoCard.WithView(BuildInfoRow("Has Code", hasCode ? "Yes" : "No"));
        infoCard = infoCard.WithView(BuildInfoRow("Has Configuration", !string.IsNullOrEmpty(content.Configuration) ? "Yes" : "No"));

        stack = stack.WithView(infoCard);

        // Navigation buttons
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("View Code & Configuration")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Code())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(codeHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the split view with left menu and code/config display.
    /// Returns static Splitter structure with data-bound content panes.
    /// </summary>
    public static UiControl CodeView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams - data will be stored in data section
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());
        host.SubscribeToDataStream(CodeFilesDataId, host.Workspace.GetStream<CodeConfiguration>()!);

        // Initialize selection state
        host.UpdateData(SelectionDataId, new NodeTypeViewSelection { Section = "configuration" });

        // Return static Splitter structure with observable nested views
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left menu - observable, updates when definition loads
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .Select(definition => definition == null
                        ? RenderLoading("Loading...")
                        : BuildLeftMenu(host, definition)),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Main pane - observable, updates when definition loads
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .Select(definition => definition == null
                        ? RenderLoading("Loading configuration...")
                        : BuildMainPaneContent(host, definition)),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with Configuration entry.
    /// </summary>
    private static UiControl BuildLeftMenu(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Configuration entry
        navMenu = navMenu.WithView(
            new NavLinkControl("Configuration", FluentIcons.Settings(), null)
                .WithClickAction(actx =>
                {
                    host.UpdateData(SelectionDataId, new NodeTypeViewSelection { Section = "configuration" });
                })
        );

        // Dependencies section (if any)
        if (content.Dependencies != null && content.Dependencies.Count > 0)
        {
            var depsGroup = new NavGroupControl("Dependencies")
                .WithIcon(FluentIcons.Link())
                .WithSkin(s => s.WithExpanded(false));

            foreach (var dep in content.Dependencies)
            {
                depsGroup = depsGroup.WithView(
                    Controls.Html($"<span style=\"padding: 4px 16px; display: block;\">{System.Web.HttpUtility.HtmlEncode(dep)}</span>")
                );
            }

            navMenu = navMenu.WithNavGroup(depsGroup);
        }

        return navMenu;
    }

    /// <summary>
    /// Builds the main content pane showing configuration.
    /// </summary>
    private static UiControl BuildMainPaneContent(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; min-height: 100%; overflow: auto;");

        // Show Configuration (the lambda expression for hub configuration)
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px; width: 100%;")
            .WithView(Controls.Html("<h2 style=\"margin: 0;\">Configuration</h2>"));

        if (!string.IsNullOrEmpty(content.Configuration))
        {
            headerRow = headerRow.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref)))
            );
        }

        stack = stack.WithView(headerRow);

        if (!string.IsNullOrEmpty(content.Configuration))
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Lambda expression for configuring the message hub.</p>"));

            var markdown = $"```csharp\n{content.Configuration}\n```";
            stack = stack.WithView(new MarkdownControl(markdown).WithStyle("width: 100%;"));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No configuration defined.</p>"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing code files.
    /// Returns static structure with data-bound editor.
    /// </summary>
    public static UiControl CodeEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());

        // Return structure with nested observable view for editor
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<CodeConfiguration>(CodeFileDataId)
                    .Select(codeFile => BuildCodeEditContent(host, codeFile, "")),
                "Editor"
            );
    }

    private static UiControl BuildCodeEditContent(
        LayoutAreaHost host,
        CodeConfiguration? codeFile,
        string dependencyCode)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Get initial code and language
        string initialCode = codeFile?.Code ?? "";
        string language = codeFile?.Language ?? "csharp";
        string displayName = codeFile?.DisplayName ?? "Code";

        host.UpdateData(dataId, initialCode);

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Edit: {System.Web.HttpUtility.HtmlEncode(displayName)}</h2>"));

        // Monaco editor bound to the data stream with autocomplete support
        var editor = new CodeEditorControl()
            .WithLanguage(language)
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true);

        if (!string.IsNullOrEmpty(dependencyCode))
        {
            editor = editor.WithExtraTypeDefinitions(dependencyCode);
        }

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button - update workspace stream which will sync to persistence
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
                {
                    var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();

                    // Update the CodeConfiguration
                    var updatedCodeConfiguration = (codeFile ?? new CodeConfiguration()) with
                    {
                        Code = currentCode,
                        Language = language
                    };

                    // Update via workspace - will sync to persistence
                    using var cts = new CancellationTokenSource(10.Seconds());
                    var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(updatedCodeConfiguration),
                        o => o.WithTarget(hubAddress),
                        cts.Token);

                    if (response.Message.Log.Status != ActivityStatus.Succeeded)
                    {
                        // Show error dialog
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown($"**Error saving code:**\n\n{response.Message.Log}"),
                            "Save Failed"
                        ).WithSize("M");
                        actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

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

    /// <summary>
    /// Renders the view for Configuration.
    /// Returns static structure with data-bound content.
    /// </summary>
    public static UiControl HubConfigView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data stream
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .Select(content => content == null
                        ? RenderLoading("Loading...")
                        : BuildHubConfigViewContent(host, content)),
                "Content"
            );
    }

    private static UiControl BuildHubConfigViewContent(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        stack = stack.WithView(Controls.Html("<h2 style=\"margin-bottom: 16px;\">Configuration</h2>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Lambda expression: <code>Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;</code></p>"));

        if (!string.IsNullOrEmpty(content.Configuration))
        {
            var markdown = $"```csharp\n{content.Configuration}\n```";
            stack = stack.WithView(new MarkdownControl(markdown));

            // Edit button
            var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.Edit())
                        .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref))))
            );
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No Configuration defined.</p>"));
        }

        // Back button
        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(codeHref))));

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing Configuration.
    /// Returns static structure with data-bound editor.
    /// </summary>
    public static UiControl HubConfigEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (content, codeFile) = tuple;
                        if (content == null)
                            return RenderLoading("Loading...");
                        var allCode = codeFile?.Code ?? "";
                        return BuildHubConfigEditContent(host, content, allCode);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, NodeTypeDefinition content, string allCodeForAutocomplete)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        var initialValue = content.Configuration ?? "config => config";
        host.UpdateData(dataId, initialValue);

        // Header
        stack = stack.WithView(Controls.Html("<h2 style=\"margin-bottom: 16px;\">Edit Configuration</h2>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Enter a lambda expression: <code>config => config.AddData(...)</code></p>"));

        // Monaco editor with all code files for autocomplete
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("300px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithPlaceholder("config => config");

        if (!string.IsNullOrEmpty(allCodeForAutocomplete))
        {
            editor = editor.WithExtraTypeDefinitions(allCodeForAutocomplete);
        }

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button - update workspace stream
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var newConfiguration = await host.Stream.GetDataStream<string>(dataId).FirstAsync();

                // Update the NodeTypeDefinition with new Configuration via workspace
                var updatedDefinition = content with { Configuration = newConfiguration };
                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest().WithUpdates(updatedDefinition),
                    o => o.WithTarget(hubAddress),
                    cts.Token);

                if (response.Message.Log.Status != ActivityStatus.Succeeded)
                {
                    // Show error dialog
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving Configuration:**\n\n{response.Message.Log}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

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

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));

    private static UiControl RenderError(string message)
        => new MarkdownControl($"> [!CAUTION]\n> {message}\n");
}

/// <summary>
/// Selection state for the NodeType view.
/// </summary>
internal record NodeTypeViewSelection
{
    public string? SelectedFile { get; init; }
    public string? Section { get; init; } = "code";
}
