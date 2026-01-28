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
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds an overview layout for MeshNode content with read-only display and click-to-edit.
/// Shows read-only views by default, click switches to edit mode, and data changes auto-switch back.
/// Markdown properties are handled separately with full width and Done button.
/// </summary>
public static class OverviewLayoutArea
{
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

        // 2. Store JsonElement in data stream (NO deserialization)
        var dataId = $"content_{nodePath.Replace("/", "_")}";
        host.UpdateData(dataId, jsonContent);

        // 3. Get browsable properties (skip Title - shown in header)
        var properties = contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => !IsTitleProperty(p.Name))
            .ToList();

        // 4. Separate properties into regular vs markdown (SeparateEditView)
        var regularProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView != true)
            .ToList();

        var markdownProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView == true)
            .ToList();

        var stack = Controls.Stack.WithWidth("100%");

        // 5. Build grid for regular properties (read-only with click-to-edit)
        if (regularProps.Count > 0)
        {
            var propsGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var prop in regularProps)
            {
                var propCell = BuildPropertyCell(host, prop, dataId, nodePath, node);
                propsGrid = propsGrid.WithView(propCell, s => s.WithXs(12).WithMd(6).WithLg(4));
            }

            stack = stack.WithView(propsGrid);

            // 6. Set up stream subscription for auto-switch-back
            SetupAutoSwitchBack(host, dataId, nodePath, regularProps, node);
        }

        // 7. Build markdown sections (full width, title + MarkdownControl)
        foreach (var prop in markdownProps)
        {
            var markdownSection = BuildMarkdownSection(host, prop, dataId, nodePath, node);
            stack = stack.WithView(markdownSection);
        }

        return stack;
    }

    private static bool IsTitleProperty(string name) =>
        name.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a property cell with label and read-only value that switches to edit on click.
    /// </summary>
    private static UiControl BuildPropertyCell(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string nodePath,
        MeshNode node)
    {
        var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? prop.Name.Wordify();

        var propName = prop.Name.ToCamelCase()!;
        var areaId = $"{nodePath}/prop_{propName}";
        var editingPropertyId = $"{dataId}_editing";

        var stack = Controls.Stack.WithStyle("padding: 4px 8px;");

        // Label
        stack = stack.WithView(Controls.Label(displayName)
            .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem;"));

        // Read-only control area with click-to-edit
        stack = stack.WithView((h, ctx) =>
        {
            // Create named area for this property value
            var readOnlyControl = BuildReadOnlyControl(h, prop, dataId, node);

            var isEditable = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                             && !prop.HasAttribute<KeyAttribute>();

            if (!isEditable)
                return Observable.Return<UiControl?>(readOnlyControl);

            return Observable.Return<UiControl?>(readOnlyControl.WithClickAction(ctx2 =>
            {
                // Track which property is being edited
                ctx2.Host.UpdateData(editingPropertyId, propName);

                var editControl = BuildEditControl(h, prop, dataId, ctx2.Area, node);
                ctx2.Host.UpdateArea(ctx2.Area, editControl);
                return Task.CompletedTask;
            }));
        });

        return stack;
    }

    /// <summary>
    /// Builds a read-only control for a property using LabelControl with data binding.
    /// </summary>
    private static UiControl BuildReadOnlyControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        MeshNode node)
    {
        var propName = prop.Name.ToCamelCase()!;
        var propType = prop.PropertyType;

        UiControl readOnlyControl;

        if (propType == typeof(bool) || propType == typeof(bool?))
        {
            // For booleans, show a disabled checkbox or a Yes/No label
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
                .WithStyle("cursor: pointer; padding: 8px; min-height: 32px;");
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            // For dates, show formatted date
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
                .WithStyle("cursor: pointer; padding: 8px; min-height: 32px;");
        }
        else
        {
            // For strings and numbers, use Label with data binding
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
                .WithStyle("cursor: pointer; padding: 8px; min-height: 32px; background: var(--neutral-fill-rest); border-radius: 4px;");
        }

        return readOnlyControl with { DataContext = LayoutAreaReference.GetDataPointer(dataId) };
    }

    /// <summary>
    /// Builds an editable control for a property when clicked.
    /// </summary>
    private static UiControl BuildEditControl(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string areaId,
        MeshNode node)
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
            editCtrl = CreateFromUiControlAttribute(host, uiAttr, prop, jsonPointer, isRequired, dataId);
        }
        // Check for DimensionAttribute
        else if (prop.GetCustomAttribute<DimensionAttribute>() is { } dimAttr)
        {
            editCtrl = CreateDimensionSelect(host, jsonPointer, dimAttr, isRequired, dataId);
        }
        // Type-based control
        else if (propType.IsNumber())
        {
            editCtrl = new NumberFieldControl(jsonPointer, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            };
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            editCtrl = new DateTimeControl(jsonPointer) { Required = isRequired };
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            editCtrl = new CheckBoxControl(jsonPointer) { Required = isRequired };
        }
        else
        {
            editCtrl = new TextFieldControl(jsonPointer)
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            };
        }

        return editCtrl with { DataContext = LayoutAreaReference.GetDataPointer(dataId) };
    }

    /// <summary>
    /// Sets up a stream subscription to auto-switch back to read-only when data changes.
    /// </summary>
    private static void SetupAutoSwitchBack(
        LayoutAreaHost host,
        string dataId,
        string nodePath,
        List<PropertyInfo> regularProps,
        MeshNode node)
    {
        var editingPropertyId = $"{dataId}_editing";

        host.RegisterForDisposal(dataId,
            host.Stream.GetDataStream<JsonElement>(dataId)
                .Skip(1) // Skip initial value
                .Subscribe(jsonElement =>
                {
                    // Get the currently editing property
                    var editingPropStream = host.Stream.GetDataStream<string?>(editingPropertyId);
                    var editingProp = editingPropStream
                        .Take(1)
                        .Wait();

                    if (!string.IsNullOrEmpty(editingProp))
                    {
                        var prop = regularProps.FirstOrDefault(p =>
                            p.Name.ToCamelCase() == editingProp);

                        if (prop != null)
                        {
                            // Build the area ID for this property
                            var propAreaId = $"{nodePath}/prop_{editingProp}";

                            // Switch back to read-only
                            var readOnlyCtrl = BuildReadOnlyControl(host, prop, dataId, node);

                            var isEditable = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                                             && !prop.HasAttribute<KeyAttribute>();

                            if (isEditable)
                            {
                                readOnlyCtrl = readOnlyCtrl.WithClickAction(ctx =>
                                {
                                    ctx.Host.UpdateData(editingPropertyId, prop.Name.ToCamelCase() ?? string.Empty);
                                    var editControl = BuildEditControl(host, prop, dataId, ctx.Area, node);
                                    ctx.Host.UpdateArea(ctx.Area, editControl);
                                    return Task.CompletedTask;
                                });
                            }

                            // We need to find the actual area and update it
                            // The area is a child of the property cell stack
                            // For now, clear the editing state
                            host.UpdateData(editingPropertyId, string.Empty);
                        }
                    }
                }));
    }

    /// <summary>
    /// Builds a markdown section with title, full width, and Done button.
    /// </summary>
    private static UiControl BuildMarkdownSection(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string nodePath,
        MeshNode node)
    {
        var propName = prop.Name.ToCamelCase()!;
        var displayName = prop.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? prop.Name.Wordify();
        var editStateId = $"editState_{dataId}_{propName}";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 24px;")
            .WithView((h, ctx) =>
            {
                return editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                    {
                        if (isEditing)
                            return BuildMarkdownEditView(h, prop, dataId, nodePath, editStateId);
                        else
                            return BuildMarkdownReadView(h, prop, dataId, displayName, editStateId);
                    });
            });
    }

    /// <summary>
    /// Builds the read-only view for a markdown property.
    /// </summary>
    private static UiControl BuildMarkdownReadView(
        LayoutAreaHost host,
        PropertyInfo prop,
        string dataId,
        string displayName,
        string editStateId)
    {
        var propName = prop.Name.ToCamelCase()!;
        var isEditable = prop.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false
                         && !prop.HasAttribute<KeyAttribute>();

        var markdownControl = new MarkdownControl(new JsonPointerReference(propName))
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        };

        if (isEditable)
        {
            markdownControl = markdownControl
                .WithStyle("cursor: pointer;")
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, true);
                    return Task.CompletedTask;
                });
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("background: var(--neutral-fill-rest); border-radius: 8px; padding: 16px 20px;")
            .WithView(Controls.H3(displayName).WithStyle("margin-bottom: 12px;"))
            .WithView(markdownControl);
    }

    /// <summary>
    /// Builds the edit view for a markdown property with Done button.
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
            .WithPlaceholder(markdownAttr?.Placeholder ?? "Enter content...")
            .WithAutoSave(hubAddress, nodePath) with
        {
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
        string dataId)
    {
        if (attr.ControlType == typeof(TextAreaControl))
            return new TextAreaControl(jsonPointer) { Required = isRequired, AutoFocus = true };

        if (attr.ControlType == typeof(SelectControl) && attr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptions(attr.Options));
            return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired
            };
        }

        return new TextFieldControl(jsonPointer) { Required = isRequired, Immediate = true, AutoFocus = true };
    }

    private static UiControl CreateDimensionSelect(
        LayoutAreaHost host,
        JsonPointerReference jsonPointer,
        DimensionAttribute dimensionAttr,
        bool isRequired,
        string dataId)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);
        if (string.IsNullOrEmpty(collectionName))
            return new TextFieldControl(jsonPointer) { Required = isRequired, Immediate = true, AutoFocus = true };

        var optionsId = Guid.NewGuid().AsString();
        host.RegisterForDisposal(dataId,
            host.Workspace.GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptions(x.Value!,
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(opts => host.UpdateData(optionsId, opts)));

        return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired
        };
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
