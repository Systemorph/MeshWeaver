using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds an overview layout for MeshNode content with read-only display and click-to-edit.
/// Shows read-only views by default, click switches to edit mode, blur auto-switches back.
/// Markdown properties are handled separately with full width and Done button.
/// Uses workspace stream pattern for automatic bidirectional data binding.
/// </summary>
public static class OverviewLayoutArea
{
    /// <summary>
    /// Gets the consistent data ID for a node path. Used by both header and property overview.
    /// </summary>
    public static string GetDataId(string nodePath) => $"content_{nodePath.Replace("/", "_")}";

    /// <summary>
    /// Builds the property overview for a MeshNode, showing read-only views with click-to-edit.
    /// </summary>
    public static UiControl BuildPropertyOverview(LayoutAreaHost host, MeshNode node)
    {
        var nodePath = node.Namespace ?? host.Hub.Address.ToString();

        if (node.Content is not JsonElement jsonContent)
            return Controls.Stack;

        // 1. Get $type from JsonElement
        if (!jsonContent.TryGetProperty("$type", out var typeProperty))
            return Controls.Stack;

        var typeName = typeProperty.GetString();
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var contentType = typeRegistry.GetType(typeName!);

        if (contentType == null)
            return Controls.Stack;

        // 2. Check access permissions
        var canEdit = CheckEditAccess(host, node);

        // 3. Set up workspace stream for bidirectional data binding (DomainDetails pattern)
        // Use consistent dataId so header and properties share the same data
        var dataId = GetDataId(nodePath);
        var collectionName = host.Workspace.DataContext.GetCollectionName(contentType);
        var entityId = GetEntityId(jsonContent, contentType, typeRegistry);

        if (!string.IsNullOrEmpty(collectionName) && entityId != null)
        {
            // Subscribe to workspace stream for bidirectional sync
            var stream = host.Workspace.GetStream(new EntityReference(collectionName, entityId));
            if (stream != null)
            {
                var typeDefinition = host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(contentType);
                if (typeDefinition != null)
                {
                    host.RegisterForDisposal(stream
                        .Where(e => e?.Value != null)
                        .Select(e => typeDefinition.SerializeEntityAndId(e!.Value!, host.Hub.JsonSerializerOptions))
                        .Where(e => e != null)
                        .Subscribe(e => host.UpdateData(dataId, e!))
                    );

                    // Set up auto-save: persist changes from local data back to workspace
                    if (canEdit)
                    {
                        SetupAutoSave(host, dataId, contentType, jsonContent);
                    }
                }
                else
                {
                    // Fallback: store JsonElement directly
                    host.UpdateData(dataId, jsonContent);
                }
            }
            else
            {
                // Fallback: store JsonElement directly
                host.UpdateData(dataId, jsonContent);
            }
        }
        else
        {
            // Fallback: store JsonElement directly
            host.UpdateData(dataId, jsonContent);
        }

        // 4. Get browsable properties (skip Title - shown in header)
        var properties = contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => !IsTitleProperty(p.Name))
            .ToList();

        // 5. Separate properties into regular vs markdown (SeparateEditView)
        var regularProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView != true)
            .ToList();

        var markdownProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView == true)
            .ToList();

        var stack = Controls.Stack.WithWidth("100%");

        // 6. Build grid for regular properties (read-only with click-to-edit)
        if (regularProps.Count > 0)
        {
            var propsGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var prop in regularProps)
            {
                var propCell = BuildPropertyCell(host, prop, dataId, nodePath, node, canEdit);
                propsGrid = propsGrid.WithView(propCell, s => s.WithXs(12).WithMd(6).WithLg(4));
            }

            stack = stack.WithView(propsGrid);
        }

        // 7. Build markdown sections (full width, title + MarkdownControl)
        foreach (var prop in markdownProps)
        {
            var markdownSection = BuildMarkdownSection(host, prop, dataId, nodePath, node, canEdit);
            stack = stack.WithView(markdownSection);
        }

        return stack;
    }

    /// <summary>
    /// Builds a clickable title that switches to edit mode on click.
    /// </summary>
    public static UiControl BuildTitle(LayoutAreaHost host, MeshNode node, string dataId, bool canEdit)
    {
        var editStateId = $"editState_{dataId}_title";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        return Controls.Stack
            .WithView((h, ctx) =>
                editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && canEdit
                            ? BuildTitleEditView(h, dataId, editStateId)
                            : BuildTitleReadView(h, node, dataId, editStateId, canEdit)));
    }

    private static UiControl BuildTitleReadView(
        LayoutAreaHost host,
        MeshNode node,
        string dataId,
        string editStateId,
        bool canEdit)
    {
        var title = node.Name ?? node.Id ?? "";

        // Use StackControl which supports click actions (HtmlControl doesn't have click handling in Blazor)
        var titleStack = Controls.Stack
            .WithStyle($"cursor: {(canEdit ? "pointer" : "default")};")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));

        if (canEdit)
        {
            titleStack = titleStack.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });
        }

        return titleStack;
    }

    private static UiControl BuildTitleEditView(
        LayoutAreaHost host,
        string dataId,
        string editStateId)
    {
        var titleField = new TextFieldControl(new JsonPointerReference("title"))
        {
            Immediate = true,
            AutoFocus = true,
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        }
        .WithStyle("font-size: 2rem; font-weight: bold; border: none; background: transparent; min-width: 300px;")
        .WithBlurAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, false);
            return Task.CompletedTask;
        });

        return titleField;
    }

    private static object? GetEntityId(JsonElement jsonContent, Type contentType, ITypeRegistry typeRegistry)
    {
        // Try to get the key property from the type
        var keyProperty = contentType.GetProperties()
            .FirstOrDefault(p => p.HasAttribute<KeyAttribute>());

        if (keyProperty == null)
        {
            // Try common ID property names
            keyProperty = contentType.GetProperty("Id") ?? contentType.GetProperty("ID");
        }

        if (keyProperty == null)
            return null;

        var propName = keyProperty.Name.ToCamelCase();
        if (propName != null && jsonContent.TryGetProperty(propName, out var idElement))
        {
            return idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.TryGetInt64(out var longVal) ? longVal : idElement.GetDouble(),
                _ => null
            };
        }

        return null;
    }

    private static bool CheckEditAccess(LayoutAreaHost host, MeshNode node)
    {
        // Base editability - check if security service denies access
        var baseEditable = true;

        // Check user permissions via security service
        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService != null)
        {
            var nodePath = node.Path;
            try
            {
                // Use synchronous check (cached permissions should be fast)
                var permissions = securityService.GetEffectivePermissionsAsync(nodePath).GetAwaiter().GetResult();
                return permissions.HasFlag(Permission.Update);
            }
            catch
            {
                // If security check fails, fall back to base editability
                return baseEditable;
            }
        }

        return baseEditable;
    }

    private static bool IsTitleProperty(string name) =>
        name.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets up auto-save: when local data changes, persist via DataChangeRequest.
    /// </summary>
    private static void SetupAutoSave(
        LayoutAreaHost host,
        string dataId,
        Type contentType,
        JsonElement initialContent)
    {
        var initialJson = initialContent.GetRawText();
        var autoSaveId = $"autoSave_{dataId}";

        host.RegisterForDisposal(autoSaveId,
            host.Stream.GetDataStream<object>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(300))
                .Subscribe(async updatedContent =>
                {
                    if (updatedContent == null)
                        return;

                    // Serialize current content to JSON for comparison
                    string currentJson;
                    if (updatedContent is JsonElement je)
                        currentJson = je.GetRawText();
                    else if (updatedContent is JsonNode jn)
                        currentJson = jn.ToJsonString(host.Hub.JsonSerializerOptions);
                    else
                        currentJson = JsonSerializer.Serialize(updatedContent, host.Hub.JsonSerializerOptions);

                    // Skip if no changes
                    if (currentJson == initialJson)
                        return;

                    // Update initial to prevent re-sending same change
                    initialJson = currentJson;

                    // Deserialize to the correct type for DataChangeRequest
                    object? typedContent;
                    if (updatedContent.GetType() == contentType)
                    {
                        typedContent = updatedContent;
                    }
                    else
                    {
                        typedContent = JsonSerializer.Deserialize(currentJson, contentType, host.Hub.JsonSerializerOptions);
                    }

                    if (typedContent != null)
                    {
                        // Send DataChangeRequest to persist the change
                        await host.Hub.AwaitResponse<DataChangeResponse>(
                            new DataChangeRequest().WithUpdates(typedContent),
                            o => o.WithTarget(host.Hub.Address));
                    }
                }));
    }

    /// <summary>
    /// Builds a property cell with label and reactive read/edit view.
    /// Uses state-based switching and blur action for auto-save.
    /// </summary>
    private static UiControl BuildPropertyCell(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string nodePath,
        MeshNode node,
        bool canEdit)
    {
        var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? prop.Name.Wordify();

        var propName = prop.Name.ToCamelCase()!;
        var editStateId = $"editState_{dataId}_{propName}";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        var isEditable = canEdit
                         && prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                         && !prop.HasAttribute<KeyAttribute>();

        var stack = Controls.Stack.WithStyle("padding: 4px 8px;");

        // Label
        stack = stack.WithView(Controls.Label(displayName)
            .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem;"));

        // Reactive view that switches between read-only and edit mode
        stack = stack.WithView((h, ctx) =>
            editStateStream
                .StartWith(false)
                .DistinctUntilChanged()
                .Select(isEditing =>
                    isEditing && isEditable
                        ? BuildPropertyEditView(h, prop, dataId, editStateId)
                        : BuildPropertyReadView(h, prop, dataId, editStateId, isEditable)));

        return stack;
    }

    /// <summary>
    /// Builds the read-only view for a property.
    /// For select/dimension fields, we resolve the display name and show it in a label.
    /// </summary>
    private static UiControl BuildPropertyReadView(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string editStateId,
        bool isEditable)
    {
        var propName = prop.Name.ToCamelCase()!;
        var propType = prop.PropertyType;

        // Check for DimensionAttribute or UiControlAttribute with options
        var dimAttr = prop.GetCustomAttribute<DimensionAttribute>();
        var uiAttr = prop.GetCustomAttribute<UiControlAttribute>();
        var displayFormatAttr = prop.GetCustomAttribute<DisplayFormatAttribute>();

        UiControl readOnlyControl;

        if (dimAttr != null)
        {
            // For dimensions, resolve the display name from the dimension collection
            readOnlyControl = BuildDimensionReadOnlyLabel(host, propName, dataId, dimAttr);
        }
        else if (uiAttr?.Options != null)
        {
            // For select with static options, resolve the display name
            readOnlyControl = BuildOptionsReadOnlyLabel(host, propName, dataId, uiAttr.Options);
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            // Format DateTime using DisplayFormatAttribute or default to date only
            var format = displayFormatAttr?.DataFormatString ?? "{0:d}"; // Short date by default
            readOnlyControl = BuildFormattedDateLabel(host, propName, dataId, format);
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
            {
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("padding: 8px; min-height: 32px;");
        }
        else
        {
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
            {
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("padding: 8px; min-height: 32px; background: var(--neutral-fill-rest); border-radius: 4px;");
        }

        if (isEditable)
        {
            // Wrap in a StackControl for click handling
            var clickableStack = Controls.Stack
                .WithStyle("cursor: pointer;")
                .WithView(readOnlyControl)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, true);
                    return Task.CompletedTask;
                });
            return clickableStack;
        }

        return readOnlyControl;
    }

    private static UiControl BuildDimensionReadOnlyLabel(
        LayoutAreaHost host,
        string propName,
        string dataId,
        DimensionAttribute dimensionAttr)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);

        if (string.IsNullOrEmpty(collectionName))
        {
            return new LabelControl(new JsonPointerReference(propName))
            {
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("padding: 8px; min-height: 32px;");
        }

        // Create a computed label that resolves the dimension display name
        var displayLabelId = $"displayLabel_{dataId}_{propName}";

        // Subscribe to both the data and the dimension collection to resolve display names
        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        var collectionStream = host.Workspace.GetStream(new CollectionReference(collectionName));

        if (collectionStream != null)
        {
            host.RegisterForDisposal(displayLabelId,
                dataStream.CombineLatest(collectionStream, (data, collection) =>
                {
                    if (data.ValueKind == JsonValueKind.Undefined || collection?.Value == null)
                        return "";

                    // Get the property value
                    if (!data.TryGetProperty(propName, out var valueElement))
                        return "";

                    var keyValue = valueElement.ValueKind switch
                    {
                        JsonValueKind.String => valueElement.GetString(),
                        JsonValueKind.Number => valueElement.TryGetInt64(out var l) ? (object)l : valueElement.GetDouble(),
                        _ => null
                    };

                    if (keyValue == null)
                        return "";

                    // Find the matching instance and get its display name
                    if (collection.Value.Instances.TryGetValue(keyValue, out var instance))
                    {
                        if (instance is INamed named)
                            return named.DisplayName;
                        return instance.ToString() ?? "";
                    }

                    return keyValue.ToString() ?? "";
                }).Subscribe(displayName => host.UpdateData(displayLabelId, displayName)));
        }
        else
        {
            host.UpdateData(displayLabelId, "");
        }

        return new LabelControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(displayLabelId)))
            .WithStyle("padding: 8px; min-height: 32px;");
    }

    private static UiControl BuildOptionsReadOnlyLabel(
        LayoutAreaHost host,
        string propName,
        string dataId,
        object options)
    {
        var displayLabelId = $"displayLabel_{dataId}_{propName}";
        var optionsList = ConvertOptions(options);

        // Subscribe to data changes and resolve display name from options
        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        host.RegisterForDisposal(displayLabelId,
            dataStream.Select(data =>
            {
                if (data.ValueKind == JsonValueKind.Undefined)
                    return "";

                if (!data.TryGetProperty(propName, out var valueElement))
                    return "";

                var keyValue = valueElement.ValueKind == JsonValueKind.String
                    ? valueElement.GetString()
                    : valueElement.ToString();

                // Find matching option and return its display text
                var option = optionsList.FirstOrDefault(o => o.GetItem()?.ToString() == keyValue);
                return option?.Text ?? keyValue ?? "";
            }).Subscribe(displayName => host.UpdateData(displayLabelId, displayName)));

        return new LabelControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(displayLabelId)))
            .WithStyle("padding: 8px; min-height: 32px;");
    }

    private static UiControl BuildFormattedDateLabel(
        LayoutAreaHost host,
        string propName,
        string dataId,
        string format)
    {
        var displayLabelId = $"displayLabel_{dataId}_{propName}";

        // Subscribe to data changes and format the date
        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        host.RegisterForDisposal(displayLabelId,
            dataStream.Select(data =>
            {
                if (data.ValueKind == JsonValueKind.Undefined)
                    return "";

                if (!data.TryGetProperty(propName, out var valueElement))
                    return "";

                if (valueElement.ValueKind == JsonValueKind.Null)
                    return "";

                if (valueElement.TryGetDateTime(out var dateTime))
                {
                    try
                    {
                        return string.Format(format, dateTime);
                    }
                    catch
                    {
                        return dateTime.ToShortDateString();
                    }
                }

                return valueElement.ToString();
            }).Subscribe(formattedDate => host.UpdateData(displayLabelId, formattedDate)));

        return new LabelControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(displayLabelId)))
            .WithStyle("padding: 8px; min-height: 32px;");
    }

    /// <summary>
    /// Builds the edit view for a property with blur action for auto-switching back.
    /// No Save/Cancel buttons - changes are saved automatically via Immediate binding.
    /// </summary>
    private static UiControl BuildPropertyEditView(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string editStateId)
    {
        var propName = prop.Name.ToCamelCase()!;
        var jsonPointer = new JsonPointerReference(propName);
        var propType = prop.PropertyType;
        var isRequired = prop.HasAttribute<RequiredMemberAttribute>() || prop.HasAttribute<RequiredAttribute>();
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        UiControl editCtrl;

        // Check for UiControlAttribute
        var uiAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiAttr != null && uiAttr.SeparateEditView != true)
        {
            editCtrl = CreateFromUiControlAttribute(host, uiAttr, prop, jsonPointer, isRequired, dataId, editStateId);
        }
        // Check for DimensionAttribute
        else if (prop.GetCustomAttribute<DimensionAttribute>() is { } dimAttr)
        {
            editCtrl = CreateDimensionSelect(host, jsonPointer, dimAttr, isRequired, dataId, editStateId);
        }
        // Type-based control
        else if (propType.IsNumber())
        {
            editCtrl = new NumberFieldControl(jsonPointer, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            editCtrl = new DateTimeControl(jsonPointer)
            {
                Required = isRequired
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            // Checkbox doesn't really need blur - immediate is fine
            editCtrl = new CheckBoxControl(jsonPointer) { Required = isRequired };
        }
        else
        {
            editCtrl = new TextFieldControl(jsonPointer)
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
        }

        editCtrl = editCtrl with
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId),
            Style = "width: 100%; max-width: 300px;"
        };

        return editCtrl;
    }

    private static Task SwitchToReadOnly(UiActionContext ctx, string editStateId)
    {
        ctx.Host.UpdateData(editStateId, false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a markdown section with title, full width, and Done button.
    /// </summary>
    private static UiControl BuildMarkdownSection(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string nodePath,
        MeshNode node,
        bool canEdit)
    {
        var propName = prop.Name.ToCamelCase()!;
        var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? prop.Name.Wordify();
        var editStateId = $"editState_{dataId}_{propName}";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        var isEditable = canEdit
                         && prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                         && !prop.HasAttribute<KeyAttribute>();

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 24px;")
            .WithView((h, ctx) =>
                editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && isEditable
                            ? BuildMarkdownEditView(h, prop, dataId, nodePath, editStateId)
                            : BuildMarkdownReadView(h, prop, dataId, displayName, editStateId, isEditable)));
    }

    /// <summary>
    /// Builds the read-only view for a markdown property.
    /// </summary>
    private static UiControl BuildMarkdownReadView(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string displayName,
        string editStateId,
        bool isEditable)
    {
        var propName = prop.Name.ToCamelCase()!;

        var markdownControl = new MarkdownControl(new JsonPointerReference(propName))
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        };

        var contentStack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("background: var(--neutral-fill-rest); border-radius: 8px; padding: 16px 20px;" + (isEditable ? " cursor: pointer;" : ""))
            .WithView(Controls.H3(displayName).WithStyle("margin-bottom: 12px;"))
            .WithView(markdownControl);

        if (isEditable)
        {
            contentStack = contentStack.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });
        }

        return contentStack;
    }

    /// <summary>
    /// Builds the edit view for a markdown property with Done button.
    /// Binds directly to the main data - AutoSave handles persistence.
    /// </summary>
    private static UiControl BuildMarkdownEditView(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string nodePath,
        string editStateId)
    {
        var propName = prop.Name.ToCamelCase()!;
        var hubAddress = host.Hub.Address.ToString();
        var markdownAttr = prop.GetCustomAttribute<MarkdownAttribute>();

        var editor = new MarkdownEditorControl()
            .WithDocumentId($"{nodePath}/{prop.Name}")
            .WithHeight(markdownAttr?.EditorHeight ?? "400px")
            .WithMaxHeight("none")
            .WithTrackChanges(markdownAttr?.TrackChanges ?? false)
            .WithPlaceholder(markdownAttr?.Placeholder ?? "Enter content...") with
        {
            // Bind directly to the main data via JsonPointer
            Value = new JsonPointerReference(propName),
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        };

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("background: var(--neutral-fill-rest); border-radius: 8px; padding: 16px 20px;")
            .WithView(editor)
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 12px;")
                .WithView(Controls.Button("Done")
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(editStateId, false);
                        return Task.CompletedTask;
                    })));
    }

    private static UiControl CreateFromUiControlAttribute(
        LayoutAreaHost host,
        UiControlAttribute attr,
        PropertyInfo prop,
        JsonPointerReference jsonPointer,
        bool isRequired,
        string dataId,
        string editStateId)
    {
        if (attr.ControlType == typeof(TextAreaControl))
            return new TextAreaControl(jsonPointer)
            {
                Required = isRequired,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));

        if (attr.ControlType == typeof(SelectControl) && attr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptions(attr.Options));
            return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired,
                Style = "width: 100%; max-width: 300px;"
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
        }

        return new TextFieldControl(jsonPointer)
        {
            Required = isRequired,
            Immediate = true,
            AutoFocus = true
        }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
    }

    private static UiControl CreateDimensionSelect(
        LayoutAreaHost host,
        JsonPointerReference jsonPointer,
        DimensionAttribute dimensionAttr,
        bool isRequired,
        string dataId,
        string editStateId)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);
        if (string.IsNullOrEmpty(collectionName))
            return new TextFieldControl(jsonPointer)
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));

        var optionsId = Guid.NewGuid().AsString();
        host.RegisterForDisposal(dataId,
            host.Workspace.GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptions(x.Value!,
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(opts => host.UpdateData(optionsId, opts)));

        return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired,
            Style = "width: 100%; max-width: 300px;"
        }.WithBlurAction(ctx => SwitchToReadOnly(ctx, editStateId));
    }

    private static IReadOnlyCollection<Option> ConvertOptions(object options)
    {
        if (options is string[] strings)
            // Keep original text (may contain emojis)
            return strings.Select(s => (Option)new Option<string>(s, s)).ToArray();
        if (options is IEnumerable<Option> opts)
            return opts.ToArray();
        return Array.Empty<Option>();
    }

    private static IReadOnlyCollection<Option> ConvertDimensionToOptions(InstanceCollection instances, ITypeDefinition dimType)
    {
        var displayName = typeof(INamed).IsAssignableFrom(dimType.Type)
            ? (Func<object, string>)(x => ((INamed)x).DisplayName)
            : o => o.ToString()!;
        var keyType = dimType.GetKeyType();
        var optionType = typeof(Option<>).MakeGenericType(keyType);

        return instances.Instances
            .Select(kvp => (Option)Activator.CreateInstance(optionType, kvp.Key, displayName(kvp.Value))!)
            .ToArray();
    }
}
