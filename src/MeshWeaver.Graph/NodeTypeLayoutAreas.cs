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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// Uses standard MeshNodeView.Search with NodeTypeCatalogMode for showing instances.
/// - Overview: Split view with left menu and configuration/code display (default)
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeLayoutAreas
{
    public const string SearchArea = "Search";
    public const string OverviewArea = "Overview";
    public const string HubConfigViewArea = "HubConfig";
    public const string HubConfigEditArea = "HubConfigEdit";

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFileDataId = "codeFile";
    private const string CodeNodesDataId = "codeNodes";

    /// <summary>
    /// Gets the current MeshNode from the workspace stream.
    /// </summary>
    private static IObservable<MeshNode?> GetNodeStream(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());
        return nodeStream.Select(nodes => nodes.FirstOrDefault(n => n.Path == hubPath));
    }

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
                .WithDefaultArea(OverviewArea)
                .WithView(MeshNodeLayoutAreas.OverviewArea, ListOverview)  // Override default Overview for listings
                .WithView(SearchArea, MeshNodeLayoutAreas.Search)  // Use standard search
                .WithView(OverviewArea, Overview)
                .WithView(HubConfigViewArea, HubConfigView)
                .WithView(HubConfigEditArea, HubConfigEdit)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                .WithView(MeshNodeLayoutAreas.ModelArea, DataModelLayoutArea.DataModel));

    /// <summary>
    /// List overview for NodeType nodes - used in search results and listings.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> ListOverview(LayoutAreaHost host, RenderingContext _)
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
        // Delegate to the shared BuildDetailsContent which now uses a gear icon
        return host.BuildDetailsContent(node, typeDef);
    }

    /// <summary>
    /// Renders the Overview area for a NodeType.
    /// Split view with left navigation menu and main configuration pane.
    /// Code files are listed as navigation links to their own Code node addresses.
    /// </summary>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        var definitionStream = GetNodeStream(host);

        // Observe child Code nodes reactively via ObserveQuery
        host.UpdateData(CodeNodesDataId, Array.Empty<MeshNode>());

        if (meshQuery != null)
        {
            meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{hubPath} nodeType:{CodeNodeType.NodeType} scope:descendants"))
                .Scan(new List<MeshNode>(), (list, change) =>
                {
                    if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                        return change.Items.ToList();
                    foreach (var item in change.Items)
                    {
                        if (change.ChangeType == QueryChangeType.Added)
                            list.Add(item);
                        else if (change.ChangeType == QueryChangeType.Removed)
                            list.RemoveAll(n => n.Path == item.Path);
                        else if (change.ChangeType == QueryChangeType.Updated)
                        {
                            list.RemoveAll(n => n.Path == item.Path);
                            list.Add(item);
                        }
                    }
                    return list;
                })
                .Subscribe(codeNodes => host.UpdateData(CodeNodesDataId, codeNodes.ToArray()));
        }

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

        var codeNodesStream = host.Stream.GetDataStream<MeshNode[]>(CodeNodesDataId);

        // Return static Splitter structure with observable nested views
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left menu - observable, updates when definition or code nodes load
                (h, c) => definitionStream
                    .CombineLatest(codeNodesStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (definition, codeNodes, nodeTypes, agents) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildLeftMenu(host, hubAddress, definition, codeNodes, nodeTypes, agents);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Main pane - shows configuration
                (h, c) => definitionStream
                    .Select(definition =>
                    {
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildConfigurationPane(host, hubAddress, definition);
                    }),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with Configuration, Code files, Node Types, and Agents entries.
    /// Code files are shown as links that navigate to the Code node's own address.
    /// </summary>
    private static UiControl BuildLeftMenu(
        LayoutAreaHost host,
        object hubAddress,
        MeshNode node,
        IReadOnlyCollection<MeshNode>? codeNodes,
        IReadOnlyCollection<MeshNode>? nodeTypes = null,
        IReadOnlyCollection<MeshNode>? agents = null)
    {
        var content = node.Content as NodeTypeDefinition;
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Search link
        var searchHref = new LayoutAreaReference(SearchArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Search", FluentIcons.Search(), searchHref)
        );

        // Node type definition entry
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";
        var configHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl(node.Name ?? nodeId, FluentIcons.Settings(), configHref)
        );

        // Code section - entries navigate to each Code node's own address
        var codeGroup = new NavGroupControl("Code")
            .WithIcon(FluentIcons.Code())
            .WithSkin(s => s.WithExpanded(true));

        if (codeNodes != null && codeNodes.Count > 0)
        {
            foreach (var codeNode in codeNodes)
            {
                var codeConfig = codeNode.Content as CodeConfiguration;
                var codeHref = new LayoutAreaReference(CodeLayoutAreas.OverviewArea).ToHref(codeNode.Path);
                codeGroup = codeGroup.WithView(
                    new NavLinkControl(codeNode.Name ?? codeConfig?.DisplayName ?? codeNode.Id, CustomIcons.CSharp(), codeHref)
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

            foreach (var typeNode in nodeTypes.OrderBy(n => n.Order).ThenBy(n => n.Name))
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

            foreach (var agentNode in agents.OrderBy(n => n.Order).ThenBy(n => n.Name))
            {
                var agentHref = $"/{agentNode.Path}";
                agentsGroup = agentsGroup.WithView(
                    new NavLinkControl(agentNode.Name ?? agentNode.Id, FluentIcons.Bot(), agentHref)
                );
            }

            navMenu = navMenu.WithNavGroup(agentsGroup);
        }

        // Dependencies section (if any)
        if (content?.Dependencies != null && content.Dependencies.Count > 0)
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
    /// Builds the main pane showing the node name as title
    /// and the Configuration property in a read-only CodeEditorControl.
    /// </summary>
    private static UiControl BuildConfigurationPane(LayoutAreaHost host, object hubAddress, MeshNode node)
    {
        var definition = node.Content as NodeTypeDefinition;
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
        var nodeId = hubAddress is Address addr ? addr.Segments.LastOrDefault() : (hubAddress.ToString() ?? "Unknown").Split('/').LastOrDefault() ?? "Unknown";

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto;");

        // Title with edit button
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.H2(node.Name ?? nodeId ?? "Unknown"))
            .WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithNavigateToHref(editHref)
            );

        stack = stack.WithView(headerRow);

        // Configuration in CodeEditorControl
        var configCode = definition?.Configuration ?? "";
        if (!string.IsNullOrEmpty(configCode))
        {
            var configDataId = Guid.NewGuid().AsString();
            host.UpdateData(configDataId, configCode);

            var configEditor = new CodeEditorControl()
                .WithLanguage("csharp")
                .WithHeight("calc(100vh - 220px)")
                .WithLineNumbers(true)
                .WithMinimap(false)
                .WithWordWrap(true)
                .WithReadonly(true);

            configEditor = configEditor with
            {
                DataContext = LayoutAreaReference.GetDataPointer(configDataId),
                Value = new JsonPointerReference("")
            };

            stack = stack.WithView(configEditor);
        }
        else
        {
            stack = stack.WithView(Controls.Body("No configuration defined.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
        }

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
        host.SubscribeToDataStream(DefinitionDataId, GetNodeStream(host));

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<MeshNode>(DefinitionDataId)
                    .Select(node => node == null
                        ? RenderLoading("Loading...")
                        : BuildHubConfigViewContent(host, node)),
                "Content"
            );
    }

    private static UiControl BuildHubConfigViewContent(LayoutAreaHost host, MeshNode node)
    {
        var content = node.Content as NodeTypeDefinition;
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        stack = stack.WithView(Controls.H2("Configuration").WithStyle("margin-bottom: 16px;"));
        stack = stack.WithView(Controls.Body("Lambda expression: Func<MessageHubConfiguration, MessageHubConfiguration>").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"));

        if (!string.IsNullOrEmpty(content?.Configuration))
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
        var overviewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithNavigateToHref(overviewHref));

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
        host.SubscribeToDataStream(DefinitionDataId, GetNodeStream(host));
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<MeshNode>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (node, codeFile) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        var allCode = codeFile?.Code ?? "";
                        return BuildHubConfigEditContent(host, node, allCode);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, MeshNode node, string allCodeForAutocomplete)
    {
        var content = node.Content as NodeTypeDefinition;
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
        host.UpdateData(displayNameDataId, node.Name ?? "");
        host.UpdateData(descriptionDataId, content?.Description ?? "");
        host.UpdateData(iconNameDataId, node.Icon ?? "");
        host.UpdateData(displayOrderDataId, (node.Order ?? 0).ToString());
        host.UpdateData(childrenQueryDataId, content?.ChildrenQuery ?? "");
        host.UpdateData(dependenciesDataId, content?.Dependencies != null ? string.Join(", ", content.Dependencies) : "");
        host.UpdateData(configurationDataId, content?.Configuration ?? "config => config");

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {node.Name ?? nodeId}").WithStyle("margin-bottom: 16px;"));

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

        // Cancel button
        var viewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(viewHref));

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

                // Update the NodeTypeDefinition with content-only properties
                var updatedDefinition = (content ?? new NodeTypeDefinition()) with
                {
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    ChildrenQuery = string.IsNullOrWhiteSpace(childrenQuery) ? null : childrenQuery,
                    Dependencies = dependencies,
                    Configuration = string.IsNullOrWhiteSpace(configuration) ? null : configuration
                };

                // Get current MeshNode and update both node properties and content
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

                // Update MeshNode with new content and node-level properties
                var updatedNode = currentNode with
                {
                    Name = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                    Icon = string.IsNullOrWhiteSpace(iconName) ? null : iconName,
                    Order = displayOrder,
                    Content = updatedDefinition
                };

                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest { ChangedBy = actx.Host.Stream.ClientId }.WithUpdates(updatedNode),
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

                // Navigate back to overview
                var overviewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(overviewHref));
            }));

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
}
