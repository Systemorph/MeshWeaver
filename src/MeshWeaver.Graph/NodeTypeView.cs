using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

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

    // Data keys for selection state
    private const string SelectedFileKey = "selectedFile";
    private const string SelectedSectionKey = "selectedSection";

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
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext ctx)
    {
        // Get NodeTypeDefinition from MeshNode.Content and CodeConfiguration from workspace stream
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();
        var codeConfigStream = host.Workspace.GetSingle<CodeConfiguration>();

        return definitionStream
            .CombineLatest(codeConfigStream)
            .Select(tuple =>
            {
                var content = tuple.First;
                var codeConfig = tuple.Second;

                if (content == null)
                    return RenderError("No NodeType definition found.");

                return BuildDetailsLayout(host, content, codeConfig);
            });
    }

    /// <summary>
    /// Builds the Details layout with overview and navigation.
    /// </summary>
    private static UiControl BuildDetailsLayout(
        LayoutAreaHost host,
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

        var hasCode = codeConfig?.Code != null || (codeConfig?.Files?.Count > 0);
        infoCard = infoCard.WithView(BuildInfoRow("Has Code", hasCode ? "Yes" : "No"));
        infoCard = infoCard.WithView(BuildInfoRow("Has HubConfiguration", !string.IsNullOrEmpty(content.HubConfiguration) ? "Yes" : "No"));

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
    /// </summary>
    public static IObservable<UiControl> CodeView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Get NodeTypeDefinition from MeshNode.Content and CodeConfiguration from workspace stream
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();
        var codeConfigStream = host.Workspace.GetSingle<CodeConfiguration>();

        return definitionStream
            .CombineLatest(codeConfigStream)
            .Select(tuple =>
            {
                var content = tuple.First;
                var codeConfig = tuple.Second;

                if (content == null)
                    return RenderError("NodeType not found.");

                return BuildSplitView(host, content, codeConfig);
            });
    }

    /// <summary>
    /// Builds the split view with left menu and main content pane.
    /// </summary>
    private static UiControl BuildSplitView(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeConfig)
    {
        var hubAddress = host.Hub.Address;

        // Initialize selection state - default to first file or "code"
        var defaultFile = codeConfig?.Files?.Keys.FirstOrDefault() ?? "code";
        var selectionDataId = $"nodeTypeSelection_{content.Id}";

        // Initialize selection if not already set
        host.UpdateData(selectionDataId, new NodeTypeViewSelection { SelectedFile = defaultFile, Section = "code" });

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithHeight("100%"))
            .WithView(
                BuildLeftMenu(host, content, codeConfig, selectionDataId),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                BuildMainPane(host, content, codeConfig, selectionDataId),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with collapsible sections.
    /// </summary>
    private static UiControl BuildLeftMenu(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeConfig,
        string selectionDataId)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280));

        // Code section
        var codeGroup = new NavGroupControl("Code")
            .WithIcon(FluentIcons.Code());

        if (codeConfig?.Files != null && codeConfig.Files.Count > 0)
        {
            foreach (var (fileName, file) in codeConfig.Files)
            {
                var displayName = file.DisplayName ?? fileName;
                var langLabel = file.Language != "csharp" ? $" ({file.Language})" : "";

                codeGroup = codeGroup.WithView(
                    Controls.MenuItem(displayName + langLabel, FluentIcons.Document())
                        .WithClickAction(actx =>
                        {
                            host.UpdateData(selectionDataId, new NodeTypeViewSelection { SelectedFile = fileName, Section = "code" });
                        })
                );
            }
        }
        else if (!string.IsNullOrEmpty(codeConfig?.Code))
        {
            // Legacy single-file mode
            codeGroup = codeGroup.WithView(
                Controls.MenuItem("Code", FluentIcons.Document())
                    .WithClickAction(actx =>
                    {
                        host.UpdateData(selectionDataId, new NodeTypeViewSelection { SelectedFile = "code", Section = "code" });
                    })
            );
        }
        else
        {
            codeGroup = codeGroup.WithView(
                Controls.Html("<span style=\"padding: 8px 16px; color: #888; font-style: italic;\">No code defined</span>")
            );
        }

        navMenu = navMenu.WithNavGroup(codeGroup);

        // HubConfiguration section
        var hubConfigGroup = new NavGroupControl("HubConfiguration")
            .WithIcon(FluentIcons.Settings());

        if (!string.IsNullOrEmpty(content.HubConfiguration))
        {
            hubConfigGroup = hubConfigGroup.WithView(
                Controls.MenuItem("Configuration Lambda", FluentIcons.CodeBlock())
                    .WithClickAction(actx =>
                    {
                        host.UpdateData(selectionDataId, new NodeTypeViewSelection { SelectedFile = null, Section = "hubconfig" });
                    })
            );
        }
        else
        {
            hubConfigGroup = hubConfigGroup.WithView(
                Controls.Html("<span style=\"padding: 8px 16px; color: #888; font-style: italic;\">No configuration defined</span>")
            );
        }

        navMenu = navMenu.WithNavGroup(hubConfigGroup);

        // Dependencies section (if any)
        if (codeConfig?.Dependencies != null && codeConfig.Dependencies.Count > 0)
        {
            var depsGroup = new NavGroupControl("Dependencies")
                .WithIcon(FluentIcons.Link())
                .WithSkin(s => s.WithExpanded(false));

            foreach (var dep in codeConfig.Dependencies)
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
    /// Builds the main content pane that reacts to menu selection.
    /// </summary>
    private static UiControl BuildMainPane(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeConfig,
        string selectionDataId)
    {
        // Create a reactive view that updates based on selection
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: 100%;")
            .WithView<UiControl>((h, ctx) =>
            {
                return h.Stream.GetDataStream<NodeTypeViewSelection>(selectionDataId)
                    .Select(selection => BuildMainPaneContent(h, content, codeConfig, selection));
            });
    }

    private static UiControl BuildMainPaneContent(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeConfig,
        NodeTypeViewSelection? selection)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; height: 100%; overflow: auto;");

        if (selection?.Section == "hubconfig")
        {
            // Show HubConfiguration
            stack = stack.WithView(Controls.Html("<h2 style=\"margin: 0 0 16px 0;\">HubConfiguration</h2>"));

            if (!string.IsNullOrEmpty(content.HubConfiguration))
            {
                stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Lambda expression for configuring the message hub.</p>"));

                var markdown = $"```csharp\n{content.HubConfiguration}\n```";
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
                stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No HubConfiguration defined.</p>"));
            }
        }
        else
        {
            // Show code file
            var fileName = selection?.SelectedFile ?? "code";
            string? code = null;
            string language = "csharp";
            string displayName = "Code";

            if (codeConfig?.Files != null && codeConfig.Files.TryGetValue(fileName, out var file))
            {
                code = file.Code;
                language = file.Language;
                displayName = file.DisplayName ?? fileName;
            }
            else if (fileName == "code" && !string.IsNullOrEmpty(codeConfig?.Code))
            {
                code = codeConfig.Code;
            }

            stack = stack.WithView(Controls.Html($"<h2 style=\"margin: 0 0 16px 0;\">{System.Web.HttpUtility.HtmlEncode(displayName)}</h2>"));

            if (!string.IsNullOrEmpty(code))
            {
                var markdown = $"```{language}\n{code}\n```";
                stack = stack.WithView(new MarkdownControl(markdown));

                // Edit button
                var editHref = new LayoutAreaReference(CodeEditArea) { Id = $"file={Uri.EscapeDataString(fileName)}" }.ToHref(hubAddress);
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
                stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No code defined.</p>"));
            }
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing code files.
    /// </summary>
    public static IObservable<UiControl> CodeEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        // Get CodeConfiguration from workspace stream
        var codeConfigStream = host.Workspace.GetSingle<CodeConfiguration>();

        // Parse file parameter from area reference
        var areaRef = new LayoutAreaReference(ctx.Area) { Id = ctx.Area };
        var fileName = areaRef.GetParameterValue("file") ?? "code";

        return codeConfigStream
            .SelectMany(async codeConfig =>
            {
                // Get dependency code for autocomplete (still need service for cross-type dependencies)
                var dependencyCode = "";
                if (nodeTypeService != null && codeConfig?.Dependencies != null && codeConfig.Dependencies.Count > 0)
                {
                    dependencyCode = await nodeTypeService.GetDependencyCodeAsync(codeConfig.Dependencies);
                }

                return BuildCodeEditContent(host, codeConfig, fileName, dependencyCode);
            });
    }

    private static UiControl BuildCodeEditContent(
        LayoutAreaHost host,
        CodeConfiguration? codeConfig,
        string fileName,
        string dependencyCode)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        // Get initial code and language
        string initialCode = "";
        string language = "csharp";
        string displayName = fileName;

        if (codeConfig?.Files != null && codeConfig.Files.TryGetValue(fileName, out var file))
        {
            initialCode = file.Code ?? "";
            language = file.Language;
            displayName = file.DisplayName ?? fileName;
        }
        else if (fileName == "code")
        {
            initialCode = codeConfig?.Code ?? "";
        }

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

        // Save button - update workspace stream which will sync via CodeConfigurationTypeSource
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(dataId).FirstAsync();

                // Update the code configuration
                var updatedConfig = UpdateCodeConfiguration(codeConfig, fileName, currentCode ?? "", language);

                // Update via workspace - CodeConfigurationTypeSource will sync to persistence
                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest().WithUpdates(updatedConfig),
                    o => o,
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
    /// Renders the view for HubConfiguration.
    /// </summary>
    public static IObservable<UiControl> HubConfigView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Get NodeTypeDefinition from MeshNode.Content
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();

        return definitionStream.Select(content =>
        {
            if (content == null)
                return RenderError("NodeType not found.");

            return BuildHubConfigViewContent(host, content);
        });
    }

    private static UiControl BuildHubConfigViewContent(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        stack = stack.WithView(Controls.Html("<h2 style=\"margin-bottom: 16px;\">HubConfiguration</h2>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Lambda expression: <code>Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;</code></p>"));

        if (!string.IsNullOrEmpty(content.HubConfiguration))
        {
            var markdown = $"```csharp\n{content.HubConfiguration}\n```";
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
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No HubConfiguration defined.</p>"));
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
    /// Renders the Monaco editor for editing HubConfiguration.
    /// </summary>
    public static IObservable<UiControl> HubConfigEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Get NodeTypeDefinition from MeshNode.Content
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();

        return definitionStream.Select(content =>
        {
            if (content == null)
                return RenderError("NodeType not found.");

            return BuildHubConfigEditContent(host, content);
        });
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var dataId = Guid.NewGuid().AsString();

        var initialValue = content.HubConfiguration ?? "config => config";
        host.UpdateData(dataId, initialValue);

        // Header
        stack = stack.WithView(Controls.Html("<h2 style=\"margin-bottom: 16px;\">Edit HubConfiguration</h2>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Enter a lambda expression: <code>config => config.AddData(...)</code></p>"));

        // Monaco editor
        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("300px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithPlaceholder("config => config")
            with
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
                var newHubConfig = await host.Stream.GetDataStream<string>(dataId).FirstAsync();

                // Update the NodeTypeDefinition with new HubConfiguration via workspace
                var updatedDefinition = content with { HubConfiguration = newHubConfig };
                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest().WithUpdates(updatedDefinition),
                    o => o,
                    cts.Token);

                if (response.Message.Log.Status != ActivityStatus.Succeeded)
                {
                    // Show error dialog
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving HubConfiguration:**\n\n{response.Message.Log}"),
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
    /// Updates the CodeConfiguration with new code for a specific file.
    /// </summary>
    private static CodeConfiguration UpdateCodeConfiguration(
        CodeConfiguration? current,
        string fileName,
        string newCode,
        string language)
    {
        if (fileName == "code" && (current?.Files == null || current.Files.Count == 0))
        {
            // Legacy single-file mode
            return new CodeConfiguration
            {
                Code = newCode,
                Dependencies = current?.Dependencies
            };
        }

        // Multi-file mode
        var files = current?.Files != null
            ? new Dictionary<string, CodeFile>(current.Files)
            : new Dictionary<string, CodeFile>();

        if (files.TryGetValue(fileName, out var existingFile))
        {
            files[fileName] = existingFile with { Code = newCode };
        }
        else
        {
            files[fileName] = new CodeFile { Code = newCode, Language = language };
        }

        return new CodeConfiguration
        {
            Code = current?.Code,
            Files = files,
            Dependencies = current?.Dependencies
        };
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

/// <summary>
/// Selection state for the NodeType view.
/// </summary>
internal record NodeTypeViewSelection
{
    public string? SelectedFile { get; init; }
    public string? Section { get; init; } = "code";
}
