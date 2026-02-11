using System.ComponentModel;
using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// Uses standard MeshNodeView.Search with NodeTypeCatalogMode for showing instances.
/// - Details: Overview of the NodeType
/// - CodeView: Split view with left menu and code display
/// - CodeEdit: Monaco editor for code editing
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeView
{
    public const string SearchArea = "Search";
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
    /// Uses the standard MeshNodeLayoutAreas.Search with NodeTypeCatalogMode to dynamically query instances.
    /// Includes UCR areas ($Data, $Schema, $Model) for unified content references.
    /// Note: $Content is registered by ContentCollectionsExtensions.AddContentCollections.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration
            .Set(new NodeTypeCatalogMode())  // Enable NodeType catalog mode
            .AddLayout(layout => layout
                .WithDefaultArea(CodeViewArea)
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)  // Override default Overview
                .WithView(SearchArea, MeshNodeLayoutAreas.Search)  // Use standard search
                .WithView(DetailsArea, Details)
                .WithView(CodeViewArea, CodeView)
                .WithView(CodeEditArea, CodeEdit)
                .WithView(HubConfigViewArea, HubConfigView)
                .WithView(HubConfigEditArea, HubConfigEdit)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                .WithView(MeshNodeLayoutAreas.ModelArea, DataModelLayoutArea.DataModel));

    /// <summary>
    /// Overview for NodeType nodes - extracts ShowChildrenInDetails from NodeTypeDefinition content.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        // Map nodes and extract NodeTypeDefinition from Content
        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);

            // Extract NodeTypeDefinition from MeshNode.Content
            NodeTypeDefinition? typeDef = null;
            if (node?.Content != null)
            {
                typeDef = node.Content as NodeTypeDefinition;
            }

            return host.BuildNodeTypeDetailsContent(node, typeDef);
        });
    }

    /// <summary>
    /// Builds details content for NodeType nodes with ShowChildrenInDetails support.
    /// </summary>
    private static UiControl BuildNodeTypeDetailsContent(this LayoutAreaHost host, MeshNode? node, NodeTypeDefinition? typeDef)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("position: relative;");

        // Action menu (top-right)
        stack = stack.WithView(Controls.Stack
            .WithStyle("position: absolute; top: 0; right: 0; z-index: 10;")
            .WithView(MeshNodeLayoutAreas.BuildActionMenu(host, node)));

        // Header with title/icon
        stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node));

        // Property overview (read-only with click-to-edit)
        if (node != null)
        {
            stack = stack.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node));
        }

        // Children - controlled by NodeTypeDefinition.ShowChildrenInDetails
        if (typeDef?.ShowChildrenInDetails ?? true)
        {
            stack = stack.WithView(LayoutAreaControl.Children(host.Hub));
        }

        // Comments
        if (host.Hub.Configuration.HasComments())
        {
            stack = stack.WithView(CommentsView.BuildInlineCommentsSection(host));
        }

        return stack;
    }

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

        // Header - ID comes from hub address, not from content
        var nodeId = hubAddress.Segments.LastOrDefault() ?? "Unknown";
        var title = content.DisplayName ?? nodeId;
        stack = stack.WithView(Controls.H1(title));
        stack = stack.WithView(Controls.Body("NodeType Configuration").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 24px;"));

        // Type info card
        var infoCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", nodeId));
        if (!string.IsNullOrEmpty(content.DisplayName))
            infoCard = infoCard.WithView(BuildInfoRow("Display Name", content.DisplayName));
        if (!string.IsNullOrEmpty(content.Description))
            infoCard = infoCard.WithView(BuildInfoRow("Description", content.Description));
        if (!string.IsNullOrEmpty(content.Icon))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", content.Icon));
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
            .WithNavigateToHref(codeHref));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the split view with left menu and code/config display.
    /// Returns static Splitter structure with data-bound content panes.
    /// </summary>
    public static UiControl CodeView(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // Get data streams directly
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var codeFilesStream = Observable.FromAsync(async () =>
        {
            if (persistence == null)
                return Array.Empty<CodeConfiguration>() as IReadOnlyCollection<CodeConfiguration>;
            var codeParentPath = $"{hubPath}/Code";
            var codeFiles = new List<CodeConfiguration>();
            await foreach (var child in persistence.GetChildrenAsync(codeParentPath))
            {
                if (child.Content is CodeConfiguration cf)
                    codeFiles.Add(cf);
            }
            return codeFiles as IReadOnlyCollection<CodeConfiguration>;
        });
        var selectionStream = host.Stream.GetDataStream<string?>(SelectionDataId);

        // Query for NodeType nodes under this namespace
        var nodeTypesStream = Observable.FromAsync(async () =>
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

        // Query for Agent nodes under this namespace
        var agentsStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:Agent scope:descendants").ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>();
            }
        });

        // Initialize selection to "configuration" (show node type definition by default)
        host.UpdateData(SelectionDataId, "configuration");

        // Return static Splitter structure with observable nested views
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left menu - observable, updates when definition or code files load
                (h, c) => definitionStream
                    .CombineLatest(codeFilesStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (definition, codeFiles, nodeTypes, agents) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildLeftMenu(host, hubAddress, definition, codeFiles, nodeTypes, agents);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Main pane - reacts to selection
                (h, c) => definitionStream
                    .CombineLatest(codeFilesStream, selectionStream)
                    .Select(tuple =>
                    {
                        var (definition, codeFiles, selection) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildMainPane(host, hubAddress, definition, codeFiles, selection);
                    }),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with Configuration, Code files, Node Types, and Agents entries.
    /// </summary>
    private static UiControl BuildLeftMenu(
        LayoutAreaHost host,
        object hubAddress,
        NodeTypeDefinition content,
        IReadOnlyCollection<CodeConfiguration>? codeFiles,
        IReadOnlyCollection<MeshNode>? nodeTypes = null,
        IReadOnlyCollection<MeshNode>? agents = null)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to Search link
        var searchHref = new LayoutAreaReference(SearchArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("← Back to Search", FluentIcons.ArrowLeft(), searchHref)
        );

        // Node type definition entry - switches main view to configuration
        // ID comes from hub address, not from content
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";
        navMenu = navMenu.WithView(
            new NavLinkControl(content.DisplayName ?? nodeId, FluentIcons.Settings(), null)
                .WithClickAction(actx => host.UpdateData(SelectionDataId, "configuration"))
        );

        // Code section
        var codeGroup = new NavGroupControl("Code")
            .WithIcon(FluentIcons.Code())
            .WithSkin(s => s.WithExpanded(true));

        if (codeFiles != null && codeFiles.Count > 0)
        {
            foreach (var file in codeFiles)
            {
                var fileId = file.Id;
                codeGroup = codeGroup.WithView(
                    new NavLinkControl(file.DisplayName ?? file.Id, CustomIcons.CSharp(), null)
                        .WithClickAction(actx => host.UpdateData(SelectionDataId, fileId))
                );
            }
        }
        else
        {
            codeGroup = codeGroup.WithView(
                Controls.Body("No code files").WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);")
            );
        }

        navMenu = navMenu.WithNavGroup(codeGroup);

        // Node Types section (if any NodeType nodes exist under this namespace)
        if (nodeTypes != null && nodeTypes.Count > 0)
        {
            var typesGroup = new NavGroupControl("Node Types")
                .WithIcon(FluentIcons.Document())
                .WithSkin(s => s.WithExpanded(true));

            foreach (var typeNode in nodeTypes.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name))
            {
                var typeHref = $"/{typeNode.Path}";
                typesGroup = typesGroup.WithView(
                    new NavLinkControl(typeNode.Name ?? typeNode.Id, FluentIcons.DocumentText(), typeHref)
                );
            }

            navMenu = navMenu.WithNavGroup(typesGroup);
        }

        // Agents section (if any Agent nodes exist under this namespace)
        if (agents != null && agents.Count > 0)
        {
            var agentsGroup = new NavGroupControl("Agents")
                .WithIcon(FluentIcons.Bot())
                .WithSkin(s => s.WithExpanded(true));

            foreach (var agentNode in agents.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name))
            {
                var agentHref = $"/{agentNode.Path}";
                agentsGroup = agentsGroup.WithView(
                    new NavLinkControl(agentNode.Name ?? agentNode.Id, FluentIcons.Bot(), agentHref)
                );
            }

            navMenu = navMenu.WithNavGroup(agentsGroup);
        }

        // Dependencies section (if any)
        if (content.Dependencies != null && content.Dependencies.Count > 0)
        {
            var depsGroup = new NavGroupControl("Dependencies")
                .WithIcon(FluentIcons.Link())
                .WithSkin(s => s.WithExpanded(false));

            foreach (var dep in content.Dependencies)
            {
                depsGroup = depsGroup.WithView(
                    Controls.Body(dep).WithStyle("padding: 4px 16px; display: block;")
                );
            }

            navMenu = navMenu.WithNavGroup(depsGroup);
        }

        return navMenu;
    }

    /// <summary>
    /// Builds the main content pane based on selection.
    /// Shows either configuration or a code file.
    /// </summary>
    private static UiControl BuildMainPane(
        LayoutAreaHost _,
        object hubAddress,
        NodeTypeDefinition definition,
        IReadOnlyCollection<CodeConfiguration>? codeFiles,
        string? selection)
    {
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto;");

        // Show configuration
        if (selection == "configuration")
        {
            return BuildConfigurationPane(stack, hubAddress, definition);
        }

        // Show selected code file or first one
        var codeFile = codeFiles?.FirstOrDefault(f => f.Id == selection)
            ?? codeFiles?.FirstOrDefault();

        if (codeFile == null)
        {
            return stack.WithView(Controls.Body("No code files available.").WithStyle("color: var(--neutral-foreground-hint);"));
        }

        return BuildCodeFilePane(stack, hubAddress, codeFile);
    }

    /// <summary>
    /// Builds the read-only view of NodeTypeDefinition in the main pane.
    /// Shows all properties with Configuration as one of them.
    /// </summary>
    private static UiControl BuildConfigurationPane(StackControl stack, object hubAddress, NodeTypeDefinition definition)
    {
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        // ID comes from hub address, not from content
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";

        // Header with edit button
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.H2(definition.DisplayName ?? nodeId ?? "Unknown"))
            .WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithNavigateToHref(editHref)
            );

        stack = stack.WithView(headerRow);

        // Properties card
        var propsCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        propsCard = propsCard.WithView(BuildInfoRow("ID", nodeId ?? "Unknown"));
        propsCard = propsCard.WithView(BuildInfoRow("Namespace", definition.Namespace));

        if (!string.IsNullOrEmpty(definition.DisplayName))
            propsCard = propsCard.WithView(BuildInfoRow("Display Name", definition.DisplayName));

        if (!string.IsNullOrEmpty(definition.Description))
            propsCard = propsCard.WithView(BuildInfoRow("Description", definition.Description));

        if (!string.IsNullOrEmpty(definition.Icon))
            propsCard = propsCard.WithView(BuildInfoRow("Icon", definition.Icon));

        propsCard = propsCard.WithView(BuildInfoRow("Display Order", definition.DisplayOrder.ToString()));

        if (!string.IsNullOrEmpty(definition.ChildrenQuery))
            propsCard = propsCard.WithView(BuildInfoRow("Children Query", definition.ChildrenQuery));

        if (definition.Dependencies != null && definition.Dependencies.Count > 0)
            propsCard = propsCard.WithView(BuildInfoRow("Dependencies", string.Join(", ", definition.Dependencies)));

        stack = stack.WithView(propsCard);

        // Configuration section (lambda expression)
        if (!string.IsNullOrEmpty(definition.Configuration))
        {
            stack = stack.WithView(Controls.H3("Configuration").WithStyle("margin: 16px 0 8px 0;"));
            stack = stack.WithView(Controls.Body("Lambda expression for configuring the message hub:").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));
            stack = stack.WithView(Controls.Markdown($"```csharp\n{definition.Configuration}\n```").WithStyle("width: 100%; max-height: 400px; overflow: auto;"));
        }

        return stack;
    }

    /// <summary>
    /// Builds a code file view in the main pane.
    /// </summary>
    private static UiControl BuildCodeFilePane(StackControl stack, object hubAddress, CodeConfiguration codeFile)
    {
        var editHref = new LayoutAreaReference(CodeEditArea).ToHref(hubAddress);

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.H2(codeFile.DisplayName ?? codeFile.Id));

        if (!string.IsNullOrEmpty(codeFile.Code))
        {
            headerRow = headerRow.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithNavigateToHref(editHref)
            );
        }

        stack = stack.WithView(headerRow);

        if (!string.IsNullOrEmpty(codeFile.Code))
        {
            stack = stack.WithView(Controls.Markdown($"```{codeFile.Language ?? "csharp"}\n{codeFile.Code}\n```").WithStyle("width: 100%; flex: 1; min-height: 0; overflow: auto;"));
        }
        else
        {
            stack = stack.WithView(Controls.Body("No code defined.").WithStyle("color: var(--neutral-foreground-hint);"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing code files.
    /// Returns static structure with data-bound editor.
    /// </summary>
    [Browsable(false)]
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
        var codeDataId = Guid.NewGuid().AsString();
        var displayNameDataId = Guid.NewGuid().AsString();

        // Get initial code and language
        string initialCode = codeFile?.Code ?? "";
        string language = codeFile?.Language ?? "csharp";
        string displayName = codeFile?.DisplayName ?? "";

        host.UpdateData(codeDataId, initialCode);
        host.UpdateData(displayNameDataId, displayName);

        // DisplayName editor at top
        var displayNameRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Label("Display Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithStyle("flex: 1; max-width: 400px;")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) });

        stack = stack.WithView(displayNameRow);

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
            DataContext = LayoutAreaReference.GetDataPointer(codeDataId),
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
                    var currentCode = await host.Stream.GetDataStream<string>(codeDataId).FirstAsync();
                    var currentDisplayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();

                    // Update the CodeConfiguration
                    var updatedCodeConfiguration = (codeFile ?? new CodeConfiguration()) with
                    {
                        Code = currentCode,
                        Language = language,
                        DisplayName = string.IsNullOrWhiteSpace(currentDisplayName) ? null : currentDisplayName
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
            .WithNavigateToHref(viewHref));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the view for Configuration.
    /// Returns static structure with data-bound content.
    /// </summary>
    [Browsable(false)]
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

        stack = stack.WithView(Controls.H2("Configuration").WithStyle("margin-bottom: 16px;"));
        stack = stack.WithView(Controls.Body("Lambda expression: Func<MessageHubConfiguration, MessageHubConfiguration>").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"));

        if (!string.IsNullOrEmpty(content.Configuration))
        {
            stack = stack.WithView(Controls.Markdown($"```csharp\n{content.Configuration}\n```").WithStyle("max-height: 400px; overflow: auto;"));

            // Edit button
            var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.Edit())
                        .WithNavigateToHref(editHref))
            );
        }
        else
        {
            stack = stack.WithView(Controls.Body("No Configuration defined.").WithStyle("color: var(--neutral-foreground-hint);"));
        }

        // Back button
        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithNavigateToHref(codeHref));

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing Configuration.
    /// Returns static structure with data-bound editor.
    /// </summary>
    [Browsable(false)]
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
        // ID comes from hub address, not from content
        var nodeId = hubAddress.Segments.LastOrDefault() ?? "Unknown";

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var displayOrderDataId = Guid.NewGuid().AsString();
        var childrenQueryDataId = Guid.NewGuid().AsString();
        var dependenciesDataId = Guid.NewGuid().AsString();
        var configurationDataId = Guid.NewGuid().AsString();

        // Initialize data streams
        host.UpdateData(displayNameDataId, content.DisplayName ?? "");
        host.UpdateData(descriptionDataId, content.Description ?? "");
        host.UpdateData(iconNameDataId, content.Icon ?? "");
        host.UpdateData(displayOrderDataId, content.DisplayOrder.ToString());
        host.UpdateData(childrenQueryDataId, content.ChildrenQuery ?? "");
        host.UpdateData(dependenciesDataId, content.Dependencies != null ? string.Join(", ", content.Dependencies) : "");
        host.UpdateData(configurationDataId, content.Configuration ?? "config => config");

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {content.DisplayName ?? nodeId}").WithStyle("margin-bottom: 16px;"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 150px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

        // Display Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Display Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) }));

        // Description
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Description:").WithStyle("font-weight: 500;"))
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter description...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Icon Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Document, Folder...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Display Order:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayOrderDataId) }));

        // Children Query
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Children Query:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Query for children (e.g., nodeType:Person)")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(childrenQueryDataId) }));

        // Dependencies
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label("Dependencies:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Comma-separated node type paths...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(dependenciesDataId) }));

        // Configuration (code editor)
        stack = stack.WithView(Controls.H3("Configuration").WithStyle("margin: 24px 0 8px 0;"));
        stack = stack.WithView(Controls.Body("Lambda expression: config => config.AddData(...)").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("250px")
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
            DataContext = LayoutAreaReference.GetDataPointer(configurationDataId),
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
                // Get all field values
                var displayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();
                var description = await host.Stream.GetDataStream<string>(descriptionDataId).FirstAsync();
                var iconName = await host.Stream.GetDataStream<string>(iconNameDataId).FirstAsync();
                var displayOrderStr = await host.Stream.GetDataStream<string>(displayOrderDataId).FirstAsync();
                var childrenQuery = await host.Stream.GetDataStream<string>(childrenQueryDataId).FirstAsync();
                var dependenciesStr = await host.Stream.GetDataStream<string>(dependenciesDataId).FirstAsync();
                var configuration = await host.Stream.GetDataStream<string>(configurationDataId).FirstAsync();

                // Parse display order
                if (!int.TryParse(displayOrderStr, out var displayOrder))
                    displayOrder = 0;

                // Parse dependencies
                List<string>? dependencies = null;
                if (!string.IsNullOrWhiteSpace(dependenciesStr))
                {
                    dependencies = dependenciesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (dependencies.Count == 0)
                        dependencies = null;
                }

                // Update the NodeTypeDefinition with all properties
                var updatedDefinition = content with
                {
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    Icon = string.IsNullOrWhiteSpace(iconName) ? null : iconName,
                    DisplayOrder = displayOrder,
                    ChildrenQuery = string.IsNullOrWhiteSpace(childrenQuery) ? null : childrenQuery,
                    Dependencies = dependencies,
                    Configuration = string.IsNullOrWhiteSpace(configuration) ? null : configuration
                };

                // Get current MeshNode and update its content
                var hubPath = host.Hub.Address.ToString();
                var currentNodes = await host.Workspace.GetStream<MeshNode>()!.FirstAsync();
                var currentNode = currentNodes?.FirstOrDefault(n => n.Path == hubPath);
                if (currentNode == null)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Error:** Could not find MeshNode to update."),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Update MeshNode with new content
                var updatedNode = currentNode with { Content = updatedDefinition };

                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest().WithUpdates(updatedNode),
                    o => o.WithTarget(hubAddress),
                    cts.Token);

                if (response.Message.Log.Status != ActivityStatus.Succeeded)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving:**\n\n{response.Message.Log}"),
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
            .WithNavigateToHref(viewHref));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.Label($"{label}:").WithStyle("width: 150px; flex-shrink: 0; font-weight: 600;"))
            .WithView(Controls.Body(value));
    }

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));

    private static UiControl RenderError(string message)
        => new MarkdownControl($"> [!CAUTION]\n> {message}\n");
}
