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
/// Simplified flow: collect Name + Description, create transient node, redirect to Edit.
/// </summary>
public static class CreateLayoutArea
{
    /// <summary>
    /// Main entry point for the Create layout area.
    /// If ?type= query param is present, shows the create form.
    /// Otherwise shows a type selection grid.
    /// </summary>
    public static IObservable<UiControl?> Create(LayoutAreaHost host, RenderingContext ctx)
    {
        var parentPath = host.Hub.Address.ToString();
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

        return Observable.FromAsync(async ct =>
        {
            if (string.IsNullOrEmpty(nodeTypePath))
            {
                // No type specified - show type selection grid
                if (nodeTypeService != null)
                {
                    return (UiControl?)await BuildTypeSelectionAsync(host, nodeTypeService, parentPath, ct);
                }
                return (UiControl?)Controls.Html("<p style=\"color: var(--warning-color);\">No type specified and type service not available.</p>");
            }

            // Type specified - show create form
            return (UiControl?)await BuildCreateFormAsync(host, parentPath, nodeTypePath, meshCatalog, ct);
        });
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
    /// Builds a simplified create form with Name and Description fields.
    /// Creates a transient node and redirects to Edit for full property editing.
    /// The namespace is computed as parentPath + sub-partition (last segment of nodeType).
    /// The Id is generated from the Name.
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
        // E.g., if parentPath is "ACME/ProductLaunch" and nodeType is "ACME/Project/Todo"
        // then namespace is "ACME/ProductLaunch/Todo"
        var subPartition = GetLastPathSegment(nodeTypePath);
        var defaultNamespace = $"{parentPath}/{subPartition}";

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; max-width: 800px;");

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

        // Next button - creates transient node and redirects to Edit for full property editing
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
                        // Node already exists - if it's transient, just redirect to Edit
                        if (existingNode.State == MeshNodeState.Transient)
                        {
                            var editUrl = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.EditArea);
                            actx.NavigateTo(editUrl);
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

                    // Navigate to the Edit layout area for full property editing
                    var editUrl2 = MeshNodeLayoutAreas.BuildContentUrl(nodePath, MeshNodeLayoutAreas.EditArea);
                    actx.NavigateTo(editUrl2);
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
            .WithStyle("margin-bottom: 8px; width: 100%;")
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
}
