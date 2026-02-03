using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for creating new mesh nodes.
/// Two flows:
/// 1. CreateChild (on parent): collect Name + Description, create transient node, redirect to node's Create area.
/// 2. Create (on transient node): show ContentType editor, Create/Cancel buttons.
/// </summary>
public static class CreateLayoutArea
{
    /// <summary>
    /// Main entry point for the Create layout area.
    /// - If current node is Transient: shows Create editor (own content type).
    /// - If ?type= query param is present: shows CreateChild form.
    /// - Otherwise: shows type selection grid for CreateChild.
    /// </summary>
    public static IObservable<UiControl?> Create(LayoutAreaHost host, RenderingContext ctx)
    {
        var currentPath = host.Hub.Address.ToString();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        // Check for type query parameter (CreateChild flow)
        var nodeTypePath = host.GetQueryStringParamValue("type");
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            return Observable.Return<UiControl?>(
                BuildCreateChildForm(host, currentPath, nodeTypePath)
            );
        }

        // Get current node to check if it's transient
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var currentNode = nodes.FirstOrDefault(n => n.Path == currentPath);

            // If current node is Transient, show Create editor (own node)
            if (currentNode?.State == MeshNodeState.Transient)
            {
                return (UiControl?)BuildCreateEditor(host, currentNode);
            }

            // No type specified - show type selection grid for CreateChild
            if (nodeTypeService != null)
            {
                return (UiControl?)await BuildTypeSelectionAsync(host, nodeTypeService, currentPath, CancellationToken.None);
            }
            return (UiControl?)Controls.Html("<p style=\"color: var(--warning-color);\">No type specified and type service not available.</p>");
        });
    }

    /// <summary>
    /// Builds the Create editor for a transient node (own node).
    /// 1. Resolves ContentType from MeshDataSource
    /// 2. Creates content instance using MeshDataSource.CreateContentInstance
    /// 3. Shows edit form for content type
    /// 4. Create button: updates node with Content and state=Active
    /// 5. Cancel button: removes transient node and navigates back
    /// </summary>
    private static UiControl BuildCreateEditor(
        LayoutAreaHost host,
        MeshNode node)
    {
        var nodePath = node.Path;
        var parentPath = node.GetParentPath();

        // Get MeshDataSource from workspace to resolve ContentType
        var workspace = host.Workspace;
        var meshDataSource = workspace.DataContext.DataSources
            .OfType<MeshDataSource>()
            .FirstOrDefault(ds => ds.ContentType != null);
        var contentType = meshDataSource?.ContentType;

        // Create content instance using MeshDataSource
        object? contentInstance = node.Content;
        if (contentInstance == null && meshDataSource != null)
        {
            contentInstance = meshDataSource.CreateContentInstance(node);
        }

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header: "Create {Name}" with Cancel button on right
        var cancelUrl = !string.IsNullOrEmpty(parentPath)
            ? MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)
            : MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.H2($"Create {node.Name ?? node.Id}").WithStyle("margin: 0;"))
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Lightweight)
                .WithClickAction(async ctx =>
                {
                    // Delete transient node and navigate back
                    try
                    {
                        await meshCatalog.DeleteNodeAsync(nodePath);
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                    ctx.NavigateTo(cancelUrl);
                })));

        // Content type editor
        if (contentType != null && contentInstance != null)
        {
            var dataId = $"create_{nodePath.Replace("/", "_")}";
            host.UpdateData(dataId, contentInstance);

            var editor = host.Hub.ServiceProvider.Edit(contentType, dataId);
            stack = stack.WithView(editor);

            // Create button
            // Note: Uses Post + RegisterCallback instead of AwaitResponse to avoid deadlock.
            // Actors are single-threaded, and AwaitResponse blocks the actor's queue while waiting
            // for a response that would be processed by the same blocked queue - causing deadlock.
            stack = stack.WithView(Controls.Stack
                .WithStyle("margin-top: 24px;")
                .WithView(Controls.Button("Create")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithClickAction(ctx =>
                    {
                        // Get current content from data stream and process asynchronously
                        // Use JsonElement and deserialize with correct contentType to preserve typed properties
                        ctx.Host.Stream.GetDataStream<JsonElement>(dataId)
                            .Take(1)
                            .Subscribe(
                                jsonContent =>
                                {
                                    try
                                    {
                                        // Deserialize with the correct content type to preserve typed properties
                                        // This fixes the issue where GetDataStream<object> returns JsonElement
                                        // instead of the typed content, causing content fields to be empty
                                        var currentContent = jsonContent.Deserialize(contentType!, ctx.Host.Hub.JsonSerializerOptions);

                                        // Update node with content and state=Active
                                        var updatedNode = node with
                                        {
                                            Content = currentContent,
                                            State = MeshNodeState.Active
                                        };

                                        // Post without blocking - use callback for response handling
                                        var delivery = host.Hub.Post(
                                            new CreateNodeRequest(updatedNode),
                                            o => o.WithTarget(host.Hub.Address));

                                        if (delivery != null)
                                        {
                                            // Register callback to handle response asynchronously
                                            host.Hub.RegisterCallback(delivery, d =>
                                            {
                                                try
                                                {
                                                    if (d.Message is CreateNodeResponse response)
                                                    {
                                                        if (response.Success)
                                                        {
                                                            var overviewUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
                                                            ctx.NavigateTo(overviewUrl);
                                                        }
                                                        else
                                                        {
                                                            logger?.LogWarning("CreateNodeRequest failed for {NodePath}: {Error}", nodePath, response.Error);
                                                            ShowErrorDialog(ctx, "Creation Failed", response.Error ?? "Unknown error");
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    logger?.LogError(ex, "Error processing CreateNodeResponse for {NodePath}", nodePath);
                                                    ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                                }
                                                return d;
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogError(ex, "Error preparing CreateNodeRequest for {NodePath}", nodePath);
                                        ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                    }
                                },
                                ex =>
                                {
                                    logger?.LogError(ex, "Error getting content from data stream for {NodePath}", nodePath);
                                    ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                });
                    })));
        }
        else
        {
            // Fallback: basic properties form for nodes without ContentType
            stack = stack.WithView(BuildBasicPropertiesForm(host, node));

            // Create button for basic form
            // Note: Uses Post + RegisterCallback instead of AwaitResponse to avoid deadlock.
            // See architecture documentation for actor model patterns.
            stack = stack.WithView(Controls.Stack
                .WithStyle("margin-top: 24px;")
                .WithView(Controls.Button("Create")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithClickAction(ctx =>
                    {
                        try
                        {
                            // Update node state to Active
                            var updatedNode = node with { State = MeshNodeState.Active };

                            // Post without blocking - use callback for response handling
                            var delivery = host.Hub.Post(
                                new CreateNodeRequest(updatedNode),
                                o => o.WithTarget(host.Hub.Address));

                            if (delivery != null)
                            {
                                // Register callback to handle response asynchronously
                                host.Hub.RegisterCallback(delivery, d =>
                                {
                                    try
                                    {
                                        if (d.Message is CreateNodeResponse response)
                                        {
                                            if (response.Success)
                                            {
                                                var overviewUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
                                                ctx.NavigateTo(overviewUrl);
                                            }
                                            else
                                            {
                                                logger?.LogWarning("CreateNodeRequest failed for {NodePath}: {Error}", nodePath, response.Error);
                                                ShowErrorDialog(ctx, "Creation Failed", response.Error ?? "Unknown error");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogError(ex, "Error processing CreateNodeResponse for {NodePath}", nodePath);
                                        ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                    }
                                    return d;
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError(ex, "Error preparing CreateNodeRequest for {NodePath}", nodePath);
                            ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                        }
                    })));
        }

        return stack;
    }

    private static void ShowErrorDialog(UiActionContext ctx, string title, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }

    /// <summary>
    /// Builds basic properties form for nodes without ContentType.
    /// </summary>
    private static UiControl BuildBasicPropertiesForm(LayoutAreaHost host, MeshNode node)
    {
        var dataId = $"create_{node.Path.Replace("/", "_")}";
        var formData = new Dictionary<string, object?>
        {
            ["name"] = node.Name ?? "",
            ["description"] = node.Description ?? "",
            ["category"] = node.Category ?? "",
            ["icon"] = node.Icon ?? ""
        };
        host.UpdateData(dataId, formData);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;");

        // Name field
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 8px;")
            .WithView(Controls.Body("Name").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference("name"))
            {
                Placeholder = "Display name",
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("width: 100%;")));

        // Description field
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 8px;")
            .WithView(Controls.Body("Description").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new MarkdownEditorControl()
            {
                Value = new JsonPointerReference("description"),
                DocumentId = $"{dataId}_description",
                Height = "200px",
                Placeholder = "Enter a description (supports Markdown)",
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }));

        return stack;
    }

    /// <summary>
    /// Builds the type selection grid showing all creatable types as cards.
    /// </summary>
    private static async Task<UiControl> BuildTypeSelectionAsync(
        LayoutAreaHost host,
        INodeTypeService nodeTypeService,
        string parentPath,
        CancellationToken ct)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with back link
        var backHref = MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.OverviewArea);
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
        var creatableTypes = await nodeTypeService.GetCreatableTypesAsync(parentPath, ct).ToListAsync(ct);

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
            var typeCard = BuildTypeCard(parentPath, typeInfo);
            grid = grid.WithView(typeCard, itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
        }

        stack = stack.WithView(grid);
        return stack;
    }

    /// <summary>
    /// Builds a card for a creatable type that navigates to the create form.
    /// </summary>
    private static UiControl BuildTypeCard(string parentPath, CreatableTypeInfo typeInfo)
    {
        var displayName = typeInfo.DisplayName ?? GetLastPathSegment(typeInfo.NodeTypePath);
        var iconName = typeInfo.Icon ?? "Document";
        var description = string.IsNullOrEmpty(typeInfo.Description) ? "No description" : typeInfo.Description;

        // Navigate to create form with type query parameter
        var createUrl = MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.CreateNodeArea, $"type={Uri.EscapeDataString(typeInfo.NodeTypePath)}");

        return Controls.Stack
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-card-container); cursor: pointer;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithStyle("align-items: center; margin-bottom: 8px;")
                .WithView(Controls.Icon(iconName).WithStyle("font-size: 24px; color: var(--accent-fill-rest);"))
                .WithView(Controls.H4(displayName).WithStyle("margin: 0; font-weight: 600;")))
            .WithView(Controls.Body(description).WithStyle("color: var(--neutral-foreground-hint); font-size: 14px;"))
            .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(createUrl)));
    }

    /// <summary>
    /// Builds the CreateChild form (on parent node).
    /// Collects Name and Description, creates a transient node, redirects to new node's Create area.
    /// </summary>
    private static UiControl BuildCreateChildForm(
        LayoutAreaHost host,
        string parentPath,
        string nodeTypePath)
    {
        var typeName = GetLastPathSegment(nodeTypePath);

        // Compute the namespace: parentPath + last segment of nodeType
        var subPartition = GetLastPathSegment(nodeTypePath);
        var defaultNamespace = $"{parentPath}/{subPartition}";

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with cancel link
        var backHref = MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.CreateNodeArea);
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2($"Create {typeName}").WithStyle("margin: 0;")));

        // Show NodeType and Location info
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 16px; padding: 12px; background: var(--neutral-layer-card-container); border-radius: 4px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("align-items: center; margin-bottom: 8px;")
                .WithView(Controls.Body("Type:").WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); min-width: 80px;"))
                .WithView(Controls.Body(nodeTypePath).WithStyle("color: var(--accent-fill-rest);")))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("align-items: center;")
                .WithView(Controls.Body("Location:").WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); min-width: 80px;"))
                .WithView(Controls.Body(defaultNamespace).WithStyle("color: var(--neutral-foreground-rest);"))));

        // Set up data binding for Name and Description
        var dataId = $"create_{parentPath.Replace("/", "_")}_{Guid.NewGuid().AsString()}";
        var formData = new Dictionary<string, object?>
        {
            ["name"] = "",
            ["description"] = ""
        };
        host.UpdateData(dataId, formData);

        // Name field (required)
        stack = stack.WithView(BuildNameField(host, dataId));

        // Description field (optional)
        stack = stack.WithView(BuildDescriptionField(host, dataId));

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 24px;");

        // Next button - creates transient node and redirects to node's Create area
        buttonRow = buttonRow.WithView(Controls.Button("Next")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowRight())
            .WithClickAction(async actx =>
            {
                // Get form values from data stream
                var formValues = await actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(dataId).FirstAsync();

                var name = formValues.GetValueOrDefault("name")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Name is required.**"),
                        "Validation Error"
                    ).WithSize("S").WithClosable(true);
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                try
                {
                    // Generate Id from Name
                    var nodeId = GenerateIdFromName(name);

                    // Build the full path using the computed namespace
                    var nodePath = $"{defaultNamespace}/{nodeId}";

                    // Create the transient node with minimal properties
                    var newNode = MeshNode.FromPath(nodePath) with
                    {
                        Name = name,
                        NodeType = nodeTypePath,
                        Description = formValues.GetValueOrDefault("description")?.ToString()?.Trim(),
                        State = MeshNodeState.Transient
                    };
                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();

                    // Check if node already exists
                    var existingNode = await meshCatalog.GetNodeAsync(new Address(nodePath));
                    if (existingNode != null)
                    {
                        // Node already exists - if it's transient, redirect to its Create area
                        if (existingNode.State == MeshNodeState.Transient)
                        {
                            var createUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.CreateNodeArea);
                            actx.NavigateTo(createUrl);
                            return;
                        }
                        else
                        {
                            // Node exists and is not transient - show error
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown($"**A node already exists at this path:**\n\n`{nodePath}`\n\nPlease choose a different name."),
                                "Node Already Exists"
                            ).WithSize("M").WithClosable(true);
                            actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            return;
                        }
                    }

                    // Create the transient node via the catalog
                    await meshCatalog.CreateTransientNodeAsync(newNode, ct: CancellationToken.None);

                    // Navigate to the node's Create area for ContentType editing
                    var createUrl2 = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.CreateNodeArea);
                    actx.NavigateTo(createUrl2);
                }
                catch (Exception ex)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error creating node:**\n\n{ex.Message}"),
                        "Creation Failed"
                    ).WithSize("M").WithClosable(true);
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            }));

        // Cancel button
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)));

        stack = stack.WithView(buttonRow);
        return stack;
    }

    /// <summary>
    /// Builds the Name field (required).
    /// </summary>
    private static UiControl BuildNameField(LayoutAreaHost host, string dataId)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 16px;")
            .WithView(Controls.Body("Name *").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference("name"))
            {
                Placeholder = "Enter a name",
                Required = true,
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("width: 100%;"));
    }

    /// <summary>
    /// Builds the Description field as a markdown editor.
    /// </summary>
    private static UiControl BuildDescriptionField(LayoutAreaHost host, string dataId)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 8px;")
            .WithView(Controls.Body("Description").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new MarkdownEditorControl()
            {
                Value = new JsonPointerReference("description"),
                DocumentId = $"{dataId}_description",
                Height = "200px",
                Placeholder = "Enter a description (supports Markdown)",
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            });
    }

    /// <summary>
    /// Generates an Id from a Name by converting to PascalCase and removing special characters.
    /// E.g., "Build sales presentation deck" -> "BuildSalesPresentationDeck"
    /// </summary>
    private static string GenerateIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Guid.NewGuid().AsString();

        // Split by spaces and other separators
        var words = Regex.Split(name, @"[\s\-_]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());

        var pascalCase = string.Join("", words);

        // Remove any remaining non-alphanumeric characters
        pascalCase = Regex.Replace(pascalCase, @"[^a-zA-Z0-9]", "");

        // If empty after processing, use a GUID
        if (string.IsNullOrEmpty(pascalCase))
            return Guid.NewGuid().AsString();

        return pascalCase;
    }

    /// <summary>
    /// Gets the last segment of a path.
    /// </summary>
    private static string GetLastPathSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
