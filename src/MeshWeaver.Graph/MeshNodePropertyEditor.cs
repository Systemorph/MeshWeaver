using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds inline property editors for MeshNode content.
/// Follows EditorExtensions patterns - no deserialization, uses JsonPointerReference.
/// </summary>
public static class MeshNodePropertyEditor
{
    /// <summary>
    /// Builds inline property editors for node content.
    /// Uses editable controls bound to the data stream with Immediate for real-time updates.
    /// </summary>
    public static UiControl BuildPropertyEditors(LayoutAreaHost host, MeshNode node)
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

        // 2. Store JsonElement in data stream (NO deserialization)
        var dataId = $"content_{nodePath.Replace("/", "_")}";
        host.UpdateData(dataId, jsonContent);

        // 3. Create grid layout
        var propsGrid = Controls.LayoutGrid
            .WithSkin(s => s.WithSpacing(2));

        // 4. Get browsable properties (skip Title - shown in header)
        var properties = contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => !EditLayoutArea.IsTitleProperty(p.Name))
            .ToList();

        // 5. For each property, create an editable control
        foreach (var prop in properties)
        {
            var propCell = BuildPropertyCell(host, prop, dataId, nodePath, node);
            propsGrid = propsGrid.WithView(propCell, s => s.WithXs(12).WithMd(6).WithLg(4));
        }

        return propsGrid;
    }

    private static UiControl BuildPropertyCell(LayoutAreaHost host, PropertyInfo prop, string dataId, string nodePath, MeshNode node)
    {
        var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? prop.Name.Wordify();

        var stack = Controls.Stack.WithStyle("padding: 4px 8px;");

        // Label
        stack = stack.WithView(Controls.Label(displayName)
            .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem;"));

        // Check for SeparateEditView - these get special treatment (markdown, etc.)
        var uiControlAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttr?.SeparateEditView == true)
        {
            var separateControl = BuildSeparateEditViewProperty(host, prop, uiControlAttr, dataId, nodePath, node);
            stack = stack.WithView(separateControl);
        }
        else
        {
            // Create editable control for inline editing
            var editControl = CreateEditableControl(host, prop, dataId, nodePath);
            stack = stack.WithView(editControl);
        }

        return stack;
    }

    /// <summary>
    /// Builds a separate edit view property (markdown content, etc.).
    /// Uses reactive view to preserve edit state during re-renders.
    /// </summary>
    private static UiControl BuildSeparateEditViewProperty(
        LayoutAreaHost host,
        PropertyInfo prop,
        UiControlAttribute uiControlAttr,
        string dataId,
        string nodePath,
        MeshNode node)
    {
        var editStateId = $"editState_{nodePath.Replace("/", "_")}_{prop.Name}";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("background: var(--neutral-fill-rest); border-radius: 8px; padding: 16px 20px; margin-bottom: 16px;")
            .WithView((h, ctx) =>
            {
                return editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                    {
                        if (isEditing)
                        {
                            return CreateSeparateEditControl(h, prop, uiControlAttr, dataId, nodePath, node, editStateId);
                        }
                        else
                        {
                            return CreateSeparateReadControl(h, prop, uiControlAttr, dataId, editStateId);
                        }
                    });
            });
    }

    /// <summary>
    /// Creates an editable control for a property.
    /// Uses Immediate=true for real-time data binding.
    /// </summary>
    private static UiControl CreateEditableControl(LayoutAreaHost host, PropertyInfo prop, string dataId, string nodePath)
    {
        var propName = prop.Name.ToCamelCase()!;
        var jsonPointer = new JsonPointerReference(propName);
        var isRequired = prop.HasAttribute<RequiredMemberAttribute>() || prop.HasAttribute<RequiredAttribute>();
        var isReadonly = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false
                         || prop.HasAttribute<KeyAttribute>();
        var propType = prop.PropertyType;

        UiControl editCtrl;

        // Check for UiControlAttribute
        var uiAttr = prop.GetCustomAttribute<UiControlAttribute>();
        if (uiAttr != null && uiAttr.SeparateEditView != true)
        {
            editCtrl = CreateFromUiControlAttribute(host, uiAttr, prop, jsonPointer, isRequired, isReadonly, dataId);
        }
        // Check for DimensionAttribute
        else if (prop.GetCustomAttribute<DimensionAttribute>() is { } dimAttr)
        {
            editCtrl = CreateDimensionSelect(host, jsonPointer, dimAttr, isRequired, isReadonly, dataId);
        }
        // Check for MeshNodeAttribute (scalar string properties only, not collections)
        else if (prop.GetCustomAttribute<MeshNodeAttribute>() is { } meshNodeAttr
                 && propType == typeof(string))
        {
            editCtrl = new MeshNodePickerControl(jsonPointer)
            {
                Queries = MeshNodeAttribute.ResolveQueries(meshNodeAttr.Queries, nodePath, nodePath),
                Required = isRequired,
                Readonly = isReadonly
            };
        }
        // Type-based control
        else if (propType.IsNumber())
        {
            var typeReg = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            editCtrl = new NumberFieldControl(jsonPointer, typeReg.GetOrAddType(propType))
            {
                Required = isRequired, Readonly = isReadonly, Immediate = true
            };
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            editCtrl = new DateTimeControl(jsonPointer) { Required = isRequired, Readonly = isReadonly };
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            editCtrl = new CheckBoxControl(jsonPointer) { Required = isRequired, Readonly = isReadonly };
        }
        else
        {
            editCtrl = new TextFieldControl(jsonPointer)
            {
                Required = isRequired, Readonly = isReadonly, Immediate = true
            };
        }

        // Set DataContext for data binding
        return editCtrl with { DataContext = LayoutAreaReference.GetDataPointer(dataId) };
    }

    private static UiControl CreateFromUiControlAttribute(
        LayoutAreaHost host, UiControlAttribute attr, PropertyInfo prop,
        JsonPointerReference jsonPointer, bool isRequired, bool isReadonly, string dataId)
    {
        if (attr.ControlType == typeof(TextAreaControl))
            return new TextAreaControl(jsonPointer) { Required = isRequired, Readonly = isReadonly };

        if (attr.ControlType == typeof(SelectControl) && attr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptions(attr.Options));
            return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired, Readonly = isReadonly
            };
        }

        if (attr.ControlType == typeof(MarkdownEditorControl))
        {
            var mdAttr = prop.GetCustomAttribute<MarkdownAttribute>();
            return new MarkdownEditorControl
            {
                Value = jsonPointer,
                Height = mdAttr?.EditorHeight ?? "200px",
                Placeholder = mdAttr?.Placeholder ?? "Enter content...",
                Readonly = isReadonly
            };
        }

        return new TextFieldControl(jsonPointer) { Required = isRequired, Readonly = isReadonly };
    }

    private static UiControl CreateDimensionSelect(
        LayoutAreaHost host, JsonPointerReference jsonPointer,
        DimensionAttribute dimensionAttr, bool isRequired, bool isReadonly, string dataId)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);
        if (string.IsNullOrEmpty(collectionName))
            return new TextFieldControl(jsonPointer) { Required = isRequired, Readonly = isReadonly };

        var optionsId = Guid.NewGuid().AsString();
        host.RegisterForDisposal(dataId,
            host.Workspace.GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptions(x.Value!,
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(opts => host.UpdateData(optionsId, opts)));

        return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired, Readonly = isReadonly
        };
    }

    private static UiControl CreateSeparateReadControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        UiControlAttribute uiControlAttr,
        string dataId,
        string editStateId)
    {
        var propName = prop.Name.ToCamelCase()!;
        var isEditable = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                         && !prop.HasAttribute<KeyAttribute>();

        // For markdown display, render the content
        if (uiControlAttr.DisplayControlType == typeof(MarkdownControl))
        {
            var readControl = Controls.Stack
                .WithStyle("cursor: pointer;")
                .WithView(new MarkdownControl(new JsonPointerReference(propName))
                {
                    DataContext = LayoutAreaReference.GetDataPointer(dataId)
                });

            if (!isEditable)
                return readControl;

            return readControl.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });
        }

        // Default text display using Label with data binding
        var textControl = new LabelControl(new JsonPointerReference(propName))
            .WithStyle("cursor: pointer;") with
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        };

        if (!isEditable)
            return textControl;

        return textControl.WithClickAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, true);
            return Task.CompletedTask;
        });
    }

    private static UiControl CreateSeparateEditControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        UiControlAttribute uiControlAttr,
        string dataId,
        string nodePath,
        MeshNode node,
        string editStateId)
    {
        var propName = prop.Name.ToCamelCase()!;
        var hubAddress = host.Hub.Address.ToString();

        // For markdown properties, use MarkdownEditorControl with auto-save
        if (uiControlAttr.ControlType == typeof(MarkdownEditorControl))
        {
            var markdownAttr = prop.GetCustomAttribute<MarkdownAttribute>();

            var editor = new MarkdownEditorControl()
                .WithDocumentId($"{nodePath}/{prop.Name}")
                .WithHeight(markdownAttr?.EditorHeight ?? "400px")
                .WithMaxHeight("none")
                .WithTrackChanges(markdownAttr?.TrackChanges ?? false)
                .WithPlaceholder(markdownAttr?.Placeholder ?? "Enter content (supports Markdown formatting)")
                .WithAutoSave(hubAddress, nodePath) with
            {
                Value = new JsonPointerReference(propName),
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            };

            // Done button to close edit mode
            var doneButton = Controls.Button("Done")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, false);
                    return Task.CompletedTask;
                });

            return Controls.Stack
                .WithStyle("width: 100%;")
                .WithView(editor)
                .WithView(Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("gap: 8px; margin-top: 12px;")
                    .WithView(doneButton));
        }

        // For other properties, use standard edit control
        var jsonPointer = new JsonPointerReference(propName);
        var isRequired = prop.HasAttribute<RequiredMemberAttribute>() || prop.HasAttribute<RequiredAttribute>();
        var editControl = new TextAreaControl(jsonPointer) { Required = isRequired, Readonly = false };

        var saveButton = Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async ctx =>
            {
                // Get updated content and save
                var updatedContent = await ctx.Host.Stream.GetDataStream<JsonElement>(dataId).FirstAsync();
                var updatedNode = node with { Content = updatedContent };
                var targetAddress = new Address(nodePath);

                try
                {
                    await ctx.Host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest { ChangedBy = ctx.Host.Stream.ClientId }.WithUpdates(updatedNode),
                        o => o.WithTarget(targetAddress));
                }
                catch { }

                ctx.Host.UpdateData(editStateId, false);
            });

        var cancelButton = Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-left: 8px;")
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, false);
                return Task.CompletedTask;
            });

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px; width: 100%;")
            .WithView(editControl)
            .WithView(saveButton)
            .WithView(cancelButton);
    }

    private static IReadOnlyCollection<Option> ConvertOptions(object options)
    {
        if (options is string[] strings)
            return strings.Select(s => (Option)new Option<string>(s, s.Wordify())).ToArray();
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
