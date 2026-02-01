using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for creating new mesh nodes.
/// Two-phase flow:
/// 1. On parent: collect Name + Description, create transient node, redirect to node's Create area.
/// 2. On transient node: show ContentType editor with Confirm/Cancel buttons.
/// </summary>
public static class CreateLayoutArea
{
    /// <summary>
    /// Main entry point for the Create layout area.
    /// - If current node is Transient: shows ContentType editor with Confirm button.
    /// - If ?type= query param is present: shows the name/description form.
    /// - Otherwise: shows a type selection grid.
    /// </summary>
    public static IObservable<UiControl?> Create(LayoutAreaHost host, RenderingContext ctx)
    {
        var currentPath = host.Hub.Address.ToString();
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        if (meshCatalog == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithView(
                    Controls.Html("<p style=\"color: var(--warning-color);\">Required services not available.</p>")
                )
            );
        }

        // Check for type query parameter
        var nodeTypePath = host.GetQueryStringParamValue("type");

        // Get current node to check if it's transient
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var currentNode = nodes.FirstOrDefault(n => n.Path == currentPath);

            // If current node is Transient, show ContentType editor with Confirm
            if (currentNode?.State == MeshNodeState.Transient)
            {
                return (UiControl?)BuildTransientNodeEditor(host, currentNode, meshCatalog);
            }

            // Not on a transient node - show type selection or create form
            if (string.IsNullOrEmpty(nodeTypePath))
            {
                // No type specified - show type selection grid
                if (nodeTypeService != null)
                {
                    return (UiControl?)await BuildTypeSelectionAsync(host, nodeTypeService, currentPath, CancellationToken.None);
                }
                return (UiControl?)Controls.Html("<p style=\"color: var(--warning-color);\">No type specified and type service not available.</p>");
            }

            // Type specified - show create form (Name + Description)
            return (UiControl?)await BuildCreateFormAsync(host, currentPath, nodeTypePath, meshCatalog, CancellationToken.None);
        });
    }

    /// <summary>
    /// Builds the ContentType editor for a transient node.
    /// Resolves the ContentType from NodeType via INodeTypeService, creates an instance, and uses .Edit() for editing.
    /// </summary>
    private static UiControl BuildTransientNodeEditor(
        LayoutAreaHost host,
        MeshNode node,
        IMeshCatalog meshCatalog)
    {
        var nodePath = node.Path;
        var parentPath = node.GetParentPath();
        var nodeTypeService = host.Hub.ServiceProvider.GetService<INodeTypeService>();

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with cancel button
        var cancelHref = !string.IsNullOrEmpty(parentPath)
            ? MeshNodeLayoutAreas.BuildContentUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)
            : MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 24px; justify-content: space-between;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center;")
                .WithView(Controls.Button("Cancel")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(cancelHref))
                .WithView(Controls.H2($"Create {node.Name ?? node.Id}").WithStyle("margin: 0;")))
            .WithView(Controls.Html("<span style=\"padding: 4px 12px; background: var(--warning-color); color: white; border-radius: 4px; font-size: 12px;\">Draft</span>")));

        // Show node type info
        if (!string.IsNullOrEmpty(node.NodeType))
        {
            stack = stack.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-bottom: 16px; padding: 12px; background: var(--neutral-layer-card-container); border-radius: 4px;")
                .WithView(Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithHorizontalGap(8)
                    .WithStyle("align-items: center;")
                    .WithView(Controls.Body("Type:").WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); min-width: 80px;"))
                    .WithView(Controls.Body(node.NodeType).WithStyle("color: var(--accent-fill-rest);"))));
        }

        // ContentType editor - resolve type from NodeTypeService via INodeTypeService.GetContentType()
        var contentType = !string.IsNullOrEmpty(node.NodeType)
            ? nodeTypeService?.GetContentType(node.NodeType)
            : null;
        if (contentType != null)
        {
            // Create a data ID for the content editor
            var dataId = $"create_content_{nodePath.Replace("/", "_")}";

            // Create an instance of the ContentType
            var contentInstance = CreateContentInstance(contentType, node);

            // Store the content data for binding
            host.UpdateData(dataId, contentInstance);

            // Use .Edit() extension to create the editor
            var editor = host.Hub.ServiceProvider.Edit(contentType, dataId);
            stack = stack.WithView(editor);
        }
        else if (node.Content != null)
        {
            // Fallback to property overview if we have content but couldn't resolve type
            stack = stack.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node));
        }
        else
        {
            // No content type - show basic properties form
            stack = stack.WithView(BuildBasicPropertiesForm(host, node));
        }

        // Button row with Confirm and Delete Draft
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 24px;");

        // Confirm button - switches node from Transient to Active
        buttonRow = buttonRow.WithView(Controls.Button("Confirm")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Checkmark())
            .WithClickAction(async ctx =>
            {
                try
                {
                    await meshCatalog.ConfirmNodeAsync(nodePath);
                    // Navigate to Overview after confirmation
                    var overviewUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
                    ctx.NavigateTo(overviewUrl);
                }
                catch (Exception ex)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error confirming node:**\n\n{ex.Message}"),
                        "Confirmation Failed"
                    ).WithSize("M").WithClosable(true);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            }));

        // Delete Draft button
        buttonRow = buttonRow.WithView(Controls.Button("Delete Draft")
            .WithAppearance(Appearance.Neutral)
            .WithIconStart(FluentIcons.Delete())
            .WithClickAction(async ctx =>
            {
                try
                {
                    await meshCatalog.DeleteNodeAsync(nodePath);
                    // Navigate to parent's Overview after deletion
                    var redirectPath = !string.IsNullOrEmpty(parentPath) ? parentPath : nodePath;
                    var overviewUrl = MeshNodeLayoutAreas.BuildContentUrl(redirectPath, MeshNodeLayoutAreas.OverviewArea);
                    ctx.NavigateTo(overviewUrl);
                }
                catch (Exception ex)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error deleting draft:**\n\n{ex.Message}"),
                        "Delete Failed"
                    ).WithSize("M").WithClosable(true);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            }));

        stack = stack.WithView(buttonRow);
        return stack;
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
    /// Builds the initial create form with Name and Description fields.
    /// Creates a transient node and redirects to the node's Create area for ContentType editing.
    /// </summary>
    private static async Task<UiControl> BuildCreateFormAsync(
        LayoutAreaHost host,
        string parentPath,
        string nodeTypePath,
        IMeshCatalog meshCatalog,
        CancellationToken ct)
    {
        var typeName = GetLastPathSegment(nodeTypePath);

        // Compute the sub-partition namespace: parentPath + last segment of nodeType
        var subPartition = GetLastPathSegment(nodeTypePath);
        var defaultNamespace = $"{parentPath}/{subPartition}";

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with back/cancel link
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

        // Show NodeType and Namespace as readonly info
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

        // Set up data binding for the form - Name and Description only
        var dataId = $"create_{parentPath.Replace("/", "_")}_{Guid.NewGuid().AsString()}";
        var formData = new Dictionary<string, object?>
        {
            ["name"] = "",
            ["description"] = ""
        };
        host.UpdateData(dataId, formData);

        // Name field (required)
        stack = stack.WithView(BuildNameField(host, dataId));

        // Description field (markdown)
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
                Placeholder = "Enter a name for the new node",
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
        var documentId = $"{dataId}_description";
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 8px;")
            .WithView(Controls.Body("Description").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new MarkdownEditorControl()
            {
                Value = new JsonPointerReference("description"),
                DocumentId = documentId,
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

    /// <summary>
    /// Creates an instance of the ContentType, initializing from existing node content if available.
    /// </summary>
    private static object CreateContentInstance(Type contentType, MeshNode node)
    {
        // If node already has content, try to use it
        if (node.Content != null)
        {
            // If content is already the correct type, use it
            if (contentType.IsInstanceOfType(node.Content))
                return node.Content;

            // If content is JsonElement, deserialize it
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    };
                    var deserialized = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), contentType, options);
                    if (deserialized != null)
                        return deserialized;
                }
                catch
                {
                    // Fall through to create new instance
                }
            }
        }

        // Create a new instance
        try
        {
            return Activator.CreateInstance(contentType) ?? throw new InvalidOperationException($"Could not create instance of {contentType.Name}");
        }
        catch
        {
            // Try to create with required properties set to defaults
            throw new InvalidOperationException($"Could not create instance of {contentType.Name}. Ensure it has a parameterless constructor.");
        }
    }
}
