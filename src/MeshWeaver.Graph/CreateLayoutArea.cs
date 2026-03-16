using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for creating new mesh nodes.
/// Shows a unified "Create New" form with type autocomplete, namespace, name, and id fields.
/// For transient nodes, shows the content type editor with Create/Cancel buttons.
/// </summary>
public static class CreateLayoutArea
{
    /// <summary>
    /// Main entry point for the Create layout area.
    /// - If current node is Transient: shows Create editor (own content type).
    /// - Otherwise: shows unified Create New form with type autocomplete.
    /// </summary>
    public static IObservable<UiControl?> Create(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        // Check current node state once (Take(1)) to decide which view to show.
        // We must NOT react to every nodeStream emission, because BuildCreateEditor
        // calls host.UpdateData which resets form data — causing the editor to lose
        // user input and rebuild the UI on every stream emission.
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Take(1).SelectMany(async nodes =>
        {
            var currentNode = nodes.FirstOrDefault(n => n.Path == currentPath);

            // If current node is Transient, show Create editor (own node)
            if (currentNode?.State == MeshNodeState.Transient)
            {
                return (UiControl?)BuildCreateEditor(host, currentNode);
            }

            // Permission gate: check Create permission on current path
            var canCreate = await PermissionHelper.CanCreateAsync(host.Hub, currentPath);
            if (!canCreate)
            {
                // Fallback: in cross-hub layout rendering, ISecurityService may not have
                // the real user's context (ImpersonateAsHub sets hub identity instead).
                // Check if a DI-registered INodeTypeAccessRule supports Create for this type;
                // if so, show the form and let the backend (RlsNodeValidator) enforce actual security.
                var nodeType = currentNode?.NodeType;
                if (!string.IsNullOrEmpty(nodeType))
                {
                    var accessRules = host.Hub.ServiceProvider.GetServices<INodeTypeAccessRule>();
                    var rule = accessRules.FirstOrDefault(r =>
                        r.NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase));
                    if (rule != null && rule.SupportedOperations.Contains(NodeOperation.Create))
                        canCreate = true;
                }
            }
            if (!canCreate)
            {
                return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to create nodes here.</p>"));
            }

            // Show unified Create New form
            return (UiControl?)BuildCreateNewForm(host, nodes, currentPath);
        });
    }

    /// <summary>
    /// Builds the Create editor for a transient node (own node).
    /// 1. Resolves ContentType from MeshDataSource
    /// 2. Creates content instance using MeshDataSource.CreateContentInstance
    /// 3. Shows edit form for content type with editable Id field
    /// 4. Create button: If Id unchanged, confirm at same path. If Id changed, create new node + delete transient.
    /// 5. Cancel button: removes transient node and navigates back
    /// </summary>
    private static UiControl BuildCreateEditor(
        LayoutAreaHost host,
        MeshNode node)
    {
        var nodePath = node.Path;
        var parentPath = node.GetParentPath();
        var transientId = node.Id; // The GUID-based transient Id
        var desiredId = node.DesiredId ?? transientId; // User's intended Id (from dialog)

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

        var cancelUrl = !string.IsNullOrEmpty(parentPath)
            ? MeshNodeLayoutAreas.BuildUrl(parentPath, MeshNodeLayoutAreas.OverviewArea)
            : MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();

        // Set up metadata data binding for Name field
        var metadataDataId = $"create_metadata_{nodePath.Replace("/", "_")}";
        var metadataFormData = new Dictionary<string, object?>
        {
            ["name"] = node.Name ?? "",
            ["id"] = desiredId
        };
        host.UpdateData(metadataDataId, metadataFormData);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header: "Create {Name}" - data-bound to name field
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(metadataDataId)
                .Select(metadata =>
                {
                    var name = metadata?.GetValueOrDefault("name")?.ToString() ?? node.Name ?? node.Id;
                    return Controls.H2($"Create {name}").WithStyle("margin: 0 0 24px 0;");
                }));

        // Name field only (Id is hidden - computed from Name)
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 24px;")
            .WithView(Controls.Body("Name").WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new TextFieldControl(new JsonPointerReference("name"))
            {
                Placeholder = "Display name",
                Immediate = true,
                DataContext = LayoutAreaReference.GetDataPointer(metadataDataId)
            }.WithStyle("width: 100%;")));

        // Content type editor - use Overview with isToggleable=false for pure edit mode
        if (contentType != null && contentInstance != null)
        {
            var dataId = $"create_{nodePath.Replace("/", "_")}";
            host.UpdateData(dataId, contentInstance);

            var editor = Layout.Domain.EditLayoutArea.Overview(host, contentType, dataId, canEdit: true, isToggleable: false);
            stack = stack.WithView(editor);

            // Buttons: Cancel on left, Create on right
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithStyle("margin-top: 24px; justify-content: flex-start;")
                .WithView(Controls.Button("Cancel")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(async ctx =>
                    {
                        try { await nodeFactory.DeleteNodeAsync(nodePath); } catch { }
                        ctx.NavigateTo(cancelUrl);
                    }))
                .WithView(Controls.Button("Create")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithClickAction(ctx =>
                    {
                        // Get metadata and content from data streams
                        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(metadataDataId)
                            .Take(1)
                            .Subscribe(
                                metadata =>
                                {
                                    var currentName = metadata.GetValueOrDefault("name")?.ToString()?.Trim() ?? node.Name;

                                    ctx.Host.Stream.GetDataStream<JsonElement>(dataId)
                                        .Take(1)
                                        .Subscribe(
                                            jsonContent =>
                                            {
                                                try
                                                {
                                                    var currentContent = jsonContent.Deserialize(contentType!, ctx.Host.Hub.JsonSerializerOptions);
                                                    HandleConfirmCreate(ctx, host, logger, node, nodePath,
                                                        currentName, currentContent, contentType);
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
                                },
                                ex =>
                                {
                                    logger?.LogError(ex, "Error getting metadata from data stream for {NodePath}", nodePath);
                                    ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                });
                    })));
        }
        else if (node.NodeType == "Markdown")
        {
            // Markdown editor with auto-save (no Done button — Create/Cancel handles state)
            var rawContent = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
            var editor = new MarkdownEditorControl()
                .WithDocumentId(nodePath)
                .WithValue(rawContent ?? "")
                .WithHeight("400px")
                .WithPlaceholder("Start writing your markdown content...")
                .WithAutoSave(host.Hub.Address.ToString(), nodePath);
            stack = stack.WithView(editor);

            // Buttons: Cancel on left, Create on right
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithStyle("margin-top: 24px; justify-content: flex-start;")
                .WithView(Controls.Button("Cancel")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(async ctx =>
                    {
                        try { await nodeFactory.DeleteNodeAsync(nodePath); } catch { }
                        ctx.NavigateTo(cancelUrl);
                    }))
                .WithView(Controls.Button("Create")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(metadataDataId)
                            .Take(1)
                            .Subscribe(
                                metadata =>
                                {
                                    try
                                    {
                                        var currentName = metadata.GetValueOrDefault("name")?.ToString()?.Trim() ?? node.Name;
                                        HandleConfirmCreate(ctx, host, logger, node, nodePath,
                                            currentName, null, null);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogError(ex, "Error preparing CreateNodeRequest for {NodePath}", nodePath);
                                        ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                    }
                                },
                                ex =>
                                {
                                    logger?.LogError(ex, "Error getting metadata from data stream for {NodePath}", nodePath);
                                    ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                });
                    })));
        }
        else
        {
            // No content type editor — the metadata Name field above is sufficient.
            // Buttons: Cancel on left, Create on right
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithStyle("margin-top: 24px; justify-content: flex-start;")
                .WithView(Controls.Button("Cancel")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(async ctx =>
                    {
                        try { await nodeFactory.DeleteNodeAsync(nodePath); } catch { }
                        ctx.NavigateTo(cancelUrl);
                    }))
                .WithView(Controls.Button("Create")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(metadataDataId)
                            .Take(1)
                            .Subscribe(
                                metadata =>
                                {
                                    try
                                    {
                                        var currentName = metadata.GetValueOrDefault("name")?.ToString()?.Trim() ?? node.Name;
                                        HandleConfirmCreate(ctx, host, logger, node, nodePath,
                                            currentName, null, null);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogError(ex, "Error preparing CreateNodeRequest for {NodePath}", nodePath);
                                        ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                    }
                                },
                                ex =>
                                {
                                    logger?.LogError(ex, "Error getting metadata from data stream for {NodePath}", nodePath);
                                    ShowErrorDialog(ctx, "Creation Failed", ex.Message);
                                });
                    })));
        }

        return stack;
    }

    /// <summary>
    /// Handles Create when Id is unchanged - confirms transient node at same path.
    /// </summary>
    private static void HandleConfirmCreate(
        UiActionContext ctx,
        LayoutAreaHost host,
        ILogger? logger,
        MeshNode node,
        string nodePath,
        string? currentName,
        object? currentContent,
        Type? contentType)
    {
        // Update node with content and state=Active
        var updatedNode = node with
        {
            Name = currentName ?? node.Name,
            Content = currentContent ?? node.Content,
            State = MeshNodeState.Active
        };

        logger?.LogInformation("Confirming transient node at {NodePath} with content type {ContentType}",
            nodePath, contentType?.Name ?? "none");

        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        nodeFactory.CreateNodeAsync(updatedNode)
            .ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    logger?.LogInformation("Successfully confirmed node at {NodePath}", nodePath);
                    var overviewUrl = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);
                    ctx.NavigateTo(overviewUrl, replace: true);
                }
                else if (task.IsFaulted)
                {
                    var error = task.Exception?.InnerException?.Message ?? "Unknown error";
                    logger?.LogWarning("CreateNodeRequest failed for {NodePath}: {Error}", nodePath, error);
                    ShowErrorDialog(ctx, "Creation Failed", error);
                }
            });
    }

    /// <summary>
    /// Handles Create when Id is changed - creates new node at new path and deletes transient.
    /// </summary>
    private static void HandleIdChangeCreate(
        UiActionContext ctx,
        LayoutAreaHost host,
        IMeshService nodeFactory,
        ILogger? logger,
        MeshNode transientNode,
        string? currentName,
        string newId,
        object? currentContent,
        Type? contentType)
    {
        // Build new path with the changed Id
        var newPath = $"{transientNode.Namespace}/{newId}";
        var transientPath = transientNode.Path;

        // Create the new node at the new path
        var newNode = MeshNode.FromPath(newPath) with
        {
            Name = currentName ?? transientNode.Name,
            NodeType = transientNode.NodeType,
            Icon = transientNode.Icon,
            Category = transientNode.Category,
            Content = currentContent ?? transientNode.Content,
            State = MeshNodeState.Active
        };

        logger?.LogInformation("Creating new node at {NewPath} (Id changed from transient {TransientPath})",
            newPath, transientPath);

        nodeFactory.CreateNodeAsync(newNode)
            .ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    logger?.LogInformation("Successfully created node at {NewPath}", newPath);

                    // Delete the transient node asynchronously
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await nodeFactory.DeleteNodeAsync(transientPath);
                            logger?.LogInformation("Deleted transient node at {TransientPath}", transientPath);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to delete transient node at {TransientPath}", transientPath);
                        }
                    });

                    // Navigate to the new node
                    var overviewUrl = MeshNodeLayoutAreas.BuildUrl(newPath, MeshNodeLayoutAreas.OverviewArea);
                    ctx.NavigateTo(overviewUrl, replace: true);
                }
                else if (task.IsFaulted)
                {
                    var error = task.Exception?.InnerException?.Message ?? "Unknown error";
                    logger?.LogWarning("CreateNodeRequest failed for {NewPath}: {Error}", newPath, error);
                    ShowErrorDialog(ctx, "Creation Failed", error);
                }
            });
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
    /// Builds the unified "Create New" form:
    /// Namespace (MeshNodePicker), Type (MeshNodePicker with Items), Name, Id, Create/Cancel.
    /// Synchronous — defaults are resolved from the already-available nodes array.
    /// </summary>
    private static UiControl BuildCreateNewForm(
        LayoutAreaHost host,
        MeshNode[] nodes,
        string parentPath)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var meshConfiguration = host.Hub.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        stack = stack.WithView(Controls.H2("Create New").WithStyle("margin: 0 0 24px 0;"));

        // 1. Resolve defaults from the current node
        var currentNode = nodes.FirstOrDefault(n => n.Path == parentPath);

        var defaultNamespace = currentNode != null && currentNode.MainNode != currentNode.Path
            ? currentNode.MainNode
            : parentPath;

        var defaultType = currentNode?.NodeType == MeshNode.NodeTypePath
            ? parentPath
            : "Markdown";

        // Override from query string (e.g. Create?type=Organization)
        var typeOverride = host.GetQueryStringParamValue("type");
        if (!string.IsNullOrEmpty(typeOverride))
            defaultType = typeOverride;

        // Parse restriction query params (e.g. ?types=X,Y&namespaces=A,B)
        // Note: namespaces param uses != null to distinguish "absent" from "empty value" (root)
        var typesParam = host.GetQueryStringParamValue("types");
        var namespacesParam = host.GetQueryStringParamValue("namespaces");
        var restrictedTypes = !string.IsNullOrEmpty(typesParam)
            ? typesParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        string[]? restrictedNamespaces = namespacesParam != null
            ? namespacesParam.Split(',', StringSplitOptions.TrimEntries)
            : null;

        // If types restricted to single entry, use it as default
        if (restrictedTypes is { Length: 1 })
            defaultType = restrictedTypes[0];

        // When type is known, look up its NodeTypeDefinition for namespace restrictions
        var knownType = restrictedTypes is { Length: 1 } ? restrictedTypes[0] : defaultType;
        if (!string.IsNullOrEmpty(knownType))
        {
            var typeNode = meshConfiguration.Nodes.Values.FirstOrDefault(n => n.Path == knownType);
            var typeDef = typeNode?.Content as NodeTypeDefinition;

            // Apply DefaultNamespace from NodeTypeDefinition (pre-selects but doesn't restrict)
            if (typeDef?.DefaultNamespace != null)
                defaultNamespace = typeDef.DefaultNamespace;

            // Apply RestrictedToNamespaces if not already overridden by URL param
            if (restrictedNamespaces == null && typeDef?.RestrictedToNamespaces is { Count: > 0 })
                restrictedNamespaces = typeDef.RestrictedToNamespaces.ToArray();
        }

        // If namespaces restricted to single entry, use it as default
        if (restrictedNamespaces is { Length: 1 })
            defaultNamespace = restrictedNamespaces[0];

        // 2. Build fixed creatable type Items (alphabetical)
        var creatableTypeNodes = meshConfiguration.Nodes.Values
            .Where(n => n.ExcludeFromContext?.Contains("create") != true)
            .OrderBy(n => n.Name ?? n.Path)
            .ToArray();

        // 3. Form data
        var formId = $"create_form_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["namespace"] = defaultNamespace,
            ["type"] = defaultType,
            ["name"] = "",
            ["id"] = ""
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        // 4. Name field (required)
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Label = "Name *",
            Placeholder = "Enter a name...",
            Required = true,
            Immediate = true,
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 16px;"));

        // 5. Id field with auto-generation from Name
        var lastAutoId = "";
        var isAutoUpdating = false;
        host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Subscribe(data =>
            {
                if (isAutoUpdating || data == null) return;
                var name = data.GetValueOrDefault("name")?.ToString() ?? "";
                var currentId = data.GetValueOrDefault("id")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) return;

                var generatedId = GenerateIdFromName(name);
                if (string.IsNullOrEmpty(generatedId)) return;

                // Auto-update if id is empty or matches the last auto-generated value
                if (currentId != generatedId && (string.IsNullOrEmpty(currentId) || currentId == lastAutoId))
                {
                    lastAutoId = generatedId;
                    isAutoUpdating = true;
                    var updated = new Dictionary<string, object?>(data) { ["id"] = generatedId };
                    host.UpdateData(formId, updated);
                    isAutoUpdating = false;
                }
            });

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("id"))
        {
            Label = "Id",
            Placeholder = "Auto-generated from name...",
            Immediate = true,
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 4px;"));
        stack = stack.WithView(Controls.Body("This will be used as the node's identifier in the path")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; margin-bottom: 16px;"));

        // 6. Type picker (or readonly label if restricted to single value)
        if (restrictedTypes is { Length: 1 })
        {
            // Single type restriction — show readonly info
            var typeNode = creatableTypeNodes.FirstOrDefault(n => n.Path == restrictedTypes[0]);
            var typeLabel = typeNode?.Name ?? restrictedTypes[0];
            stack = stack.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-bottom: 16px;")
                .WithView(Controls.Body("Type").WithStyle("font-weight: 600; margin-bottom: 4px;"))
                .WithView(Controls.Body(typeLabel).WithStyle("color: var(--neutral-foreground-rest);")));
        }
        else if (restrictedTypes is { Length: > 1 })
        {
            // Multiple type restriction — filtered picker
            var filteredTypeNodes = creatableTypeNodes
                .Where(n => restrictedTypes.Contains(n.Path))
                .ToArray();
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("type"))
            {
                Label = "Type *",
                Required = true,
                Placeholder = "Select a type...",
                DataContext = dataContext
            }.WithItems(filteredTypeNodes)
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }
        else
        {
            // No restriction — full picker with Items and query
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("type"))
            {
                Label = "Type *",
                Required = true,
                Placeholder = "Select a type...",
                DataContext = dataContext
            }.WithItems(creatableTypeNodes)
             .WithQueries("nodeType:NodeType context:create")
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }

        // 7. Namespace picker (or readonly label if restricted to single value)
        if (restrictedNamespaces is { Length: 1 })
        {
            // Single namespace restriction — show readonly info
            var nsLabel = string.IsNullOrEmpty(restrictedNamespaces[0]) ? "Root (top-level)" : restrictedNamespaces[0];
            stack = stack.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-bottom: 16px;")
                .WithView(Controls.Body("Namespace").WithStyle("font-weight: 600; margin-bottom: 4px;"))
                .WithView(Controls.Body(nsLabel).WithStyle("color: var(--neutral-foreground-rest);")));
        }
        else if (restrictedNamespaces is { Length: > 1 })
        {
            // Multiple namespace restriction — filtered picker with synthetic root node
            var nsItems = restrictedNamespaces.Select(ns =>
                string.IsNullOrEmpty(ns)
                    ? new MeshNode("") { Name = "Root (top-level)", NodeType = "Namespace" }
                    : new MeshNode(ns) { Name = ns, NodeType = "Namespace" }
            ).ToArray();
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("namespace"))
            {
                Label = "Namespace",
                Placeholder = "Select namespace...",
                DataContext = dataContext
            }.WithItems(nsItems)
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }
        else
        {
            // No restriction — full picker with root option
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("namespace"))
            {
                Label = "Namespace",
                Placeholder = "Root (leave empty for top-level)...",
                DataContext = dataContext
            }.WithQueries("context:create").WithMaxResults(15)
             .WithEmptyOption("Root (top-level)")
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }

        // 8. Button row: Cancel on left, Create on right
        var cancelUrl = MeshNodeLayoutAreas.BuildUrl(parentPath, MeshNodeLayoutAreas.OverviewArea);
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 24px; justify-content: flex-start;");

        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(cancelUrl));

        buttonRow = buttonRow.WithView(Controls.Button("Create")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async actx =>
            {
                var formValues = await actx.Host.Stream
                    .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                var ns = formValues.GetValueOrDefault("namespace")?.ToString()?.Trim() ?? "";
                var selectedType = formValues.GetValueOrDefault("type")?.ToString()?.Trim();
                var name = formValues.GetValueOrDefault("name")?.ToString()?.Trim();
                var id = formValues.GetValueOrDefault("id")?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(selectedType))
                {
                    ShowErrorDialog(actx, "Validation Error", "Type is required.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowErrorDialog(actx, "Validation Error", "Name is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(id))
                    id = GenerateIdFromName(name);

                // For satellite types, inject _TypeName segment (e.g. MyProject/_Thread/MyThread)
                string nodePath;
                if (meshConfiguration.IsSatelliteNodeType(selectedType))
                {
                    var typeSegment = $"_{selectedType}";
                    nodePath = string.IsNullOrEmpty(ns) ? $"{typeSegment}/{id}" : $"{ns}/{typeSegment}/{id}";
                }
                else
                {
                    nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
                }

                try
                {
                    var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                    var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
                    MeshNode? existingNode = null;
                    if (meshQuery != null)
                    {
                        await foreach (var n in meshQuery.QueryAsync<MeshNode>($"path:{nodePath}"))
                        {
                            existingNode = n;
                            break;
                        }
                    }
                    if (existingNode != null && existingNode.State != MeshNodeState.Transient)
                    {
                        ShowErrorDialog(actx, "Node Already Exists",
                            $"A node already exists at path: {nodePath}. Please choose a different name or id.");
                        return;
                    }

                    var newNode = MeshNode.FromPath(nodePath) with
                    {
                        Name = name.Trim(),
                        NodeType = selectedType,
                        DesiredId = id,
                        State = MeshNodeState.Transient
                    };

                    logger?.LogInformation("Creating transient node at {NodePath} with type {NodeType}", nodePath, selectedType);
                    await nodeFactory.CreateTransientAsync(newNode, CancellationToken.None);
                    logger?.LogInformation("Successfully created transient node at {NodePath}", nodePath);

                    var createUrl = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.CreateNodeArea);
                    actx.NavigateTo(createUrl);
                }
                catch (Exception ex)
                {
                    var errorMsg = ex.Message.Contains("Access denied") || ex.Message.Contains("Unauthorized")
                        ? "You do not have permission to create nodes in this namespace."
                        : $"Failed to create node: {ex.Message}";
                    ShowErrorDialog(actx, "Creation Failed", errorMsg);
                }
            }));

        stack = stack.WithView(buttonRow);
        return stack;
    }

    /// <summary>
    /// Generates an Id from a Name by converting to PascalCase and removing special characters.
    /// E.g., "Build sales presentation deck" -> "BuildSalesPresentationDeck"
    /// </summary>
    private static string GenerateIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        // Split by spaces and other separators
        var words = Regex.Split(name, @"[\s\-_]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());

        var pascalCase = string.Join("", words);

        // Remove any remaining non-alphanumeric characters
        pascalCase = Regex.Replace(pascalCase, @"[^a-zA-Z0-9]", "");

        return pascalCase ?? "";
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
