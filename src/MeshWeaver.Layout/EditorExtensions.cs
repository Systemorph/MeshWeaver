using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;
using System.Text.Json;

namespace MeshWeaver.Layout;

public static class EditorExtensions
{
    #region editor overloads
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance,
        Func<T, UiControl> result)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit(this LayoutAreaHost host, Type type, string id)
        => host.Hub.ServiceProvider.Edit(type, id);
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance, string id)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), id);
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance,
        Func<T, IObservable<UiControl>> result)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance));
    public static UiControl Edit<T>(this IMessageHub hub, T instance,
        Func<T, UiControl> result)
        => hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, UiControl> result)
        => hub.ServiceProvider.Edit(observable, (i, _, _) => result(i));
    public static UiControl Edit<T>(this IMessageHub hub, T instance,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => hub.ServiceProvider.Edit(Observable.Return(instance), result);
    public static UiControl Edit<T>(this IServiceProvider serviceProvider, T instance,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => serviceProvider.Edit(Observable.Return(instance), result);

    public static UiControl Edit<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
    => hub.ServiceProvider.Edit(observable, result);

    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider, T instance,
        string? id = null)
        => serviceProvider.Edit(Observable.Return(instance), id!);
    public static UiControl Edit(
        this IMessageHub hub, Type type,
        string id)
        => hub.ServiceProvider.Edit(type, id);
    public static UiControl Edit<T>(
        this IMessageHub hub, T instance,
        string? id = null)
        => hub.ServiceProvider.Edit(Observable.Return(instance), id!);

    public static UiControl Edit<T>(
        this IMessageHub hub,
        IObservable<T> observable,
        string? id = null)
        => hub.ServiceProvider.Edit(observable, id!);
    public static EditorControl Edit<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable
        , string id)
        => observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>),
            id);
    public static EditorControl Edit(this IServiceProvider serviceProvider, Type type, string id)
        => type.GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>) with
        { DataContext = LayoutAreaReference.GetDataPointer(id) };


    private static readonly int DebounceWindow = 20; // milliseconds
    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
    {
        var id = Guid.NewGuid().AsString();
        var editor = serviceProvider.Edit(observable, id);
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
                host.Stream.GetDataStream<T>(id)
                    .Debounce(TimeSpan.FromMilliseconds(DebounceWindow)) // Throttle the stream to take snapshots every 100ms
                    .Select(x => result.Invoke(x, host, ctx)));
    }
    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, IObservable<UiControl>> result)
    {
        var id = Guid.NewGuid().AsString();
        var editor = serviceProvider.Edit(observable, id);
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
                host.Stream.GetDataStream<T>(id)
                    .Debounce(TimeSpan.FromMilliseconds(DebounceWindow)) // Throttle the stream to take snapshots every 100ms
                    .SelectMany(x => result.Invoke(x, host, ctx)));
    }

    #endregion
    #region Toolbar overloads
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, UiControl> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, IObservable<UiControl>> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, LayoutAreaHost, RenderingContext, IObservable<UiControl>> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this IMessageHub hub, T instance,
        Func<T, UiControl> result)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, UiControl> result)
        => hub.ServiceProvider.Toolbar(observable, (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this IMessageHub hub, T instance,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this IServiceProvider serviceProvider, T instance,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => serviceProvider.Toolbar(Observable.Return(instance), result);

    public static UiControl Toolbar<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
    => hub.ServiceProvider.Toolbar(observable, result);

    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider, T instance,
        string? id = null)
        => serviceProvider.Toolbar(Observable.Return(instance), id!);
    public static UiControl Toolbar<T>(
        this IMessageHub hub, T instance,
        string? id = null)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), id!);

    public static UiControl Toolbar<T>(
        this IMessageHub hub,
        IObservable<T> observable,
        string? id = null)
        => hub.ServiceProvider.Toolbar(observable, id!);
    public static UiControl Toolbar<T>(
        this LayoutAreaHost host,
        T instance,
        string id)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), id);

    public static UiControl Toolbar<T>(
        this LayoutAreaHost host,
        IObservable<T> observable,
        string? id = null)
        => host.Hub.ServiceProvider.Toolbar(observable, id!);
    public static ToolbarControl Toolbar<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        string id)
        => observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new ToolbarControl(),
                        serviceProvider.MapToControl),
            id);


    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, UiControl> result)
        => serviceProvider.Toolbar(observable, (t, host, ctx) => Observable.Return(result.Invoke(t, host, ctx)));
    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, IObservable<UiControl>> result)
    {
        var id = Guid.NewGuid().AsString();
        var editor = serviceProvider.Toolbar(observable, id);
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
                host.Stream.GetDataStream<T>(id)
                    .Debounce(TimeSpan.FromMilliseconds(DebounceWindow)) // Throttle the stream to take snapshots every 100ms
                    .SelectMany(x => result.Invoke(x, host, ctx)));
    }


    #endregion

    public static T MapToControl<T>(
        this IServiceProvider serviceProvider,
        T editor,
        PropertyInfo propertyInfo)
    where T : ContainerControl<T>
    {
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();

        var jsonPointerReference = GetJsonPointerReference(propertyInfo);



        var uiControlAttribute = propertyInfo.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttribute != null)
            return editor.WithView((host, _) => RenderControl(host, uiControlAttribute.ControlType, propertyInfo, label, jsonPointerReference, uiControlAttribute.Options));


        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        if (dimensionAttribute != null)
        {
            if (dimensionAttribute.Options is not null)
                return editor.WithView((host, _) => RenderListControl(host, Controls.Select, jsonPointerReference, dimensionAttribute.Options));
            return editor.WithView((host, ctx) =>
            {
                var id = Guid.NewGuid().AsString();
                host.RegisterForDisposal(ctx.Area,
                    GetStream(host, dimensionAttribute)
                        .Subscribe(x => host.UpdateData(id, x)));
                return Controls.Select(jsonPointerReference, new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)));
            });
        }


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderControl(host, typeof(NumberFieldControl), propertyInfo, label, jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)));
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((host, _) => RenderControl(host, typeof(TextFieldControl), propertyInfo, label, jsonPointerReference));
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((host, _) => RenderControl(host, typeof(DateTimeControl), propertyInfo, label, jsonPointerReference));
        if (propertyInfo.PropertyType == typeof(bool) || propertyInfo.PropertyType == typeof(bool?))
            return editor.WithView((host, _) => RenderControl(host, typeof(CheckBoxControl), propertyInfo, label, jsonPointerReference));

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;

    }

    public static T MapToControl<T, TSkin>(
        this IServiceProvider serviceProvider,
        T editor,
        PropertyInfo propertyInfo)
        where T : ContainerControlWithItemSkin<T, TSkin, PropertySkin>
        where TSkin : Skin<TSkin>
    {
        if (propertyInfo.GetCustomAttribute<BrowsableAttribute>()?.Browsable == false)
            return editor;

        var displayAttribute = propertyInfo.GetCustomAttribute<DisplayAttribute>();
        var propertySkinLabel = displayAttribute?.Name ?? propertyInfo.Name.Wordify();
        string? label = null; // // TODO V10: This is to avoid duplication with property skin. do consistently in future. (19.01.2025, Roland Bürgi)

        Func<PropertySkin, PropertySkin> skinConfiguration = skin =>
            skin with
            {
                Name = propertyInfo.Name.ToCamelCase(),
                Description = propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description
                              ?? propertyInfo.GetXmlDocsSummary(),
                Label = propertySkinLabel
            };
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);



        var uiControlAttribute = propertyInfo.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttribute != null)
            return editor.WithView((host, _) => RenderControl(host, uiControlAttribute.ControlType, propertyInfo, label, jsonPointerReference, uiControlAttribute.Options), skinConfiguration);


        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        if (dimensionAttribute != null)
        {
            if (dimensionAttribute.Options is not null)
                return editor.WithView((host, _) => RenderListControl(host, Controls.Select, jsonPointerReference, dimensionAttribute.Options), skinConfiguration);
            return editor.WithView((host, ctx) =>
            {
                var id = Guid.NewGuid().AsString();
                host.RegisterForDisposal(ctx.Area,
                    GetStream(host, dimensionAttribute).Subscribe(x => host.UpdateData(id, x)));
                return RenderListControl(host, Controls.Select, jsonPointerReference, id);
            }, skinConfiguration);
        }


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderControl(host, typeof(NumberFieldControl), propertyInfo, label, jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((host, _) => RenderControl(host, typeof(TextFieldControl), propertyInfo, label, jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((host, _) => RenderControl(host, typeof(DateTimeControl), propertyInfo, label, jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(bool) || propertyInfo.PropertyType == typeof(bool?))
            return editor.WithView((host, _) => RenderControl(host, typeof(CheckBoxControl), propertyInfo, label, jsonPointerReference), skinConfiguration);

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;
    }


    private static IObservable<IReadOnlyCollection<Option>> GetStream(LayoutAreaHost host, DimensionAttribute dimensionAttribute)
    {
        return host.Workspace
            .GetStream(
                new CollectionReference(
                    host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)!))!
            .Select(x =>
                ConvertToOptions(
                    x.Value ?? throw new InvalidOperationException("Collection reference value is null"),
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)!));
    }


    private static IReadOnlyCollection<Option> ConvertToOptions(ICollection collection)
    {
        var elementType =
            collection is Array array
                ? array.GetType().GetElementType()
                : collection.GetType().GetInterfaces().Select(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>) ? i.GenericTypeArguments.First() : null).FirstOrDefault(x => x != null);
        if (elementType == null)
        {
            throw new ArgumentException("Collection does not have a generic type argument.");
        }

        var optionType = typeof(Option<>).MakeGenericType(elementType);
        return collection.Cast<object>().Select(x => (Option)Activator.CreateInstance(optionType, x, x.ToString()!.Wordify())!).ToArray();
    }

    private static JsonPointerReference GetJsonPointerReference(PropertyInfo propertyInfo) =>
        new(propertyInfo.Name.ToCamelCase()!);


    private static UiControl RenderControl(
        LayoutAreaHost host,
        Type controlType,
        PropertyInfo propertyInfo,
        string? label,
        JsonPointerReference reference,
        object? parameter = null)
    {
        if (BasicControls.TryGetValue(controlType, out var factory))
            return (UiControl)((IFormControl)factory.Invoke(reference, propertyInfo, parameter!)).WithLabel(label!);
        if (ListControls.TryGetValue(controlType, out var factory2))
            return (UiControl)((IListControl)factory2.Invoke(host, propertyInfo, reference, parameter!)).WithLabel(label!);
        if (SpecialControls.TryGetValue(controlType, out var specialFactory))
            return specialFactory.Invoke(propertyInfo, reference);

        throw new ArgumentException($"Cannot convert type {controlType.FullName} to an editor field.");
    }

    private static readonly Dictionary<Type, Func<PropertyInfo, JsonPointerReference, UiControl>>
        SpecialControls = new()
        {
            { typeof(MarkdownEditorControl), RenderMarkdownEditor },
            { typeof(MarkdownControl), RenderMarkdownDisplay },
        };

    private static UiControl RenderMarkdownEditor(PropertyInfo property, JsonPointerReference reference)
    {
        var markdownAttr = property.GetCustomAttribute<MarkdownAttribute>();
        return new MarkdownEditorControl()
        {
            Value = reference,
            Height = markdownAttr?.EditorHeight ?? "200px",
            Placeholder = markdownAttr?.Placeholder ?? "Enter content (supports Markdown formatting)",
            ShowPreview = markdownAttr?.ShowPreview ?? false,
            TrackChangesEnabled = markdownAttr?.TrackChanges ?? false,
            Readonly = property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false
        };
    }

    private static UiControl RenderMarkdownDisplay(PropertyInfo property, JsonPointerReference reference)
    {
        return new MarkdownControl(reference);
    }
    private static readonly Dictionary<Type, Func<LayoutAreaHost, PropertyInfo, JsonPointerReference, object, UiControl>>
        ListControls = new()
        {
            {typeof(SelectControl), (host,property, reference,options)=> RenderListControl(host, (r,o)=>new SelectControl(r,o){Required = property.HasAttribute<RequiredAttribute>(), Readonly = property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false },  reference, options)},
            {typeof(RadioGroupControl), (host,property, reference,options)=> RenderListControl(host, (d,o)=>Controls.RadioGroup(d,o, property.PropertyType.Name), reference, options)},
            {typeof(ComboboxControl), (host,property, reference,options)=> RenderListControl(host, (r,o)=>new ComboboxControl(r,o){Required = property.HasAttribute<RequiredAttribute>(), Readonly = property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false }, reference, options)},
            {typeof(ListboxControl), (host,property, reference,options)=> RenderListControl(host, (r,o) =>new ListboxControl(r,o){Required = property.HasAttribute<RequiredAttribute>(), Readonly = property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false }, reference, options)},
        };


    private static TControl RenderListControl<TControl>(
        LayoutAreaHost host,
        Func<object, object, TControl> controlFactory,
       object data,
        object options)
    where TControl : ListControlBase<TControl>
    {
        if (options is string id)
            return controlFactory.Invoke(data, new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)));
        if (options is JsonPointerReference)
            return controlFactory.Invoke(data, options);

        if (options is ICollection collection)
        {
            id = Guid.NewGuid().AsString();
            host.UpdateData(id, ConvertToOptions(collection));
            return controlFactory.Invoke(data, new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)));
        }

        throw new ArgumentException(
            $"No implementation to parse dimension options of type {options.GetType().FullName}.");
    }

    private static readonly Dictionary<Type, Func<JsonPointerReference, PropertyInfo, object, UiControl>>
        BasicControls = new()
        {
            {
                typeof(DateTimeControl),
                (reference, property, _) => new DateTimeControl(reference)
                {
                    Required =
                        property.HasAttribute<RequiredMemberAttribute>() ||
                        property.HasAttribute<RequiredAttribute>(),
                    Readonly =
                        property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false ||
                        property.HasAttribute<KeyAttribute>()
                }
            },
            {
                typeof(TextFieldControl),
                (reference, property, _) => new TextFieldControl(reference)
                {
                    Required =
                        property.HasAttribute<RequiredMemberAttribute>() ||
                        property.HasAttribute<RequiredAttribute>(),
                    Readonly =
                        property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false ||
                        property.HasAttribute<KeyAttribute>()
                }
            },
            {
                typeof(TextAreaControl),
                (reference, property, _) => new TextAreaControl(reference)
                {
                    Required =
                        property.HasAttribute<RequiredMemberAttribute>() ||
                        property.HasAttribute<RequiredAttribute>(),
                    Readonly =
                        property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false ||
                        property.HasAttribute<KeyAttribute>()
                }
            },
            {
                typeof(NumberFieldControl),
                (reference, property, type) => new NumberFieldControl(reference, type)
                {
                    Required =
                        property.HasAttribute<RequiredMemberAttribute>() ||
                        property.HasAttribute<RequiredAttribute>(),
                    Readonly =
                        property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false ||
                        property.HasAttribute<KeyAttribute>()
                }
            },
            {
                typeof(CheckBoxControl),
                (reference, property, _) => new CheckBoxControl(reference)
                {
                    Required =
                        property.HasAttribute<RequiredMemberAttribute>() ||
                        property.HasAttribute<RequiredAttribute>(),
                    Readonly = property.GetCustomAttribute<EditableAttribute>()?.AllowEdit == false ||
                               property.HasAttribute<KeyAttribute>()
                }
            },
        };


    private static IReadOnlyCollection<Option> ConvertToOptions(InstanceCollection instances, ITypeDefinition dimensionType)
    {
        var displayNameSelector =
            typeof(INamed).IsAssignableFrom(dimensionType.Type)
                ? (Func<object, string>)(x => ((INamed)x).DisplayName)
                : o => o.ToString()!;

        var keyType = dimensionType.GetKeyType();
        var optionType = typeof(Option<>).MakeGenericType(keyType);
        var sortProperty = dimensionType.Type.GetProperties()
            .Select(p => (Sort: p.GetCustomAttribute<SortAttribute>(), Property: p))
            .Where(x => x.Sort is not null && x.Sort.Sortable)
            .ToArray();

        IEnumerable<KeyValuePair<object, object>> i = instances.Instances;
        if (sortProperty.Any())
            i = GetOrderedExpression(i, sortProperty);
        return i
            .Select(kvp => (Option)Activator.CreateInstance(optionType, [kvp.Key, displayNameSelector(kvp.Value)])!).ToArray();
    }

    private static IEnumerable<KeyValuePair<object, object>> GetOrderedExpression(IEnumerable<KeyValuePair<object, object>> instances, (SortAttribute? Sort, PropertyInfo Property)[] sortProperty)
    {
        var sort = sortProperty[0]; // TODO V10: what to do when we have many? (26.09.2025, Roland Buergi)

        return sort.Sort!.SortDirection switch
        {
            SortDirection.Ascending => instances.OrderBy(x => sort.Property.GetValue(x.Value)),
            SortDirection.Descending => instances.OrderByDescending(x => sort.Property.GetValue(x.Value)),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    #region MapToToggleableControl

    // Track which edit states have been initialized to avoid re-initializing on re-render
    private static readonly HashSet<string> InitializedEditStates = new();

    /// <summary>
    /// Creates a control that toggles between read-only and edit modes.
    /// Read-only displays a LabelControl; click switches to edit mode.
    /// Edit mode shows an appropriate input control; blur switches back to read-only.
    /// Markdown properties (SeparateEditView) use a Done button instead of blur.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving dependencies.</param>
    /// <param name="property">The property to create the control for.</param>
    /// <param name="dataId">The data ID used for data binding.</param>
    /// <param name="canEdit">Whether editing is allowed based on permissions.</param>
    /// <param name="host">The layout area host.</param>
    /// <returns>A reactive control that switches between read and edit modes.</returns>
    public static UiControl MapToToggleableControl(
        this IServiceProvider serviceProvider,
        PropertyInfo property,
        string dataId,
        bool canEdit,
        LayoutAreaHost host)
    {
        var propName = property.Name.ToCamelCase()!;
        var editStateId = $"editState_{dataId}_{propName}";

        // Initialize edit state only once per session to prevent reset on re-render
        // Use a unique key combining stream ID and edit state ID
        var initKey = $"{host.Stream.StreamId}_{editStateId}";
        lock (InitializedEditStates)
        {
            if (!InitializedEditStates.Contains(initKey))
            {
                host.UpdateData(editStateId, false);
                InitializedEditStates.Add(initKey);
            }
        }

        // Get the edit state stream - now it will emit the stored value
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId)
            .DistinctUntilChanged();

        var isEditable = canEdit &&
                         property.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false &&
                         !property.HasAttribute<KeyAttribute>();

        // Handle SeparateEditView (Markdown) differently - full width with Done button
        var uiControlAttr = property.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttr?.SeparateEditView == true)
        {
            return BuildMarkdownToggle(host, property, dataId, editStateId, editStateStream, isEditable);
        }

        // Regular property: Label + reactive read/edit view
        var displayName = GetToggleableDisplayName(property);

        // Apply style from UiControlAttribute if present
        var containerStyle = "padding: 4px 8px;";
        if (!string.IsNullOrEmpty(uiControlAttr?.Style))
        {
            containerStyle += " " + uiControlAttr.Style;
        }

        return Controls.Stack
            .WithStyle(containerStyle)
            .WithView(Controls.Label(displayName)
                .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem; padding-left: 12px;"))
            .WithView((h, _) => editStateStream
                .Select(isEditing => isEditing && isEditable
                    ? BuildEditControl(h, property, dataId, editStateId)
                    : BuildReadonlyControl(h, property, dataId, editStateId, isEditable)));
    }

    private static string GetToggleableDisplayName(PropertyInfo property)
    {
        return property.GetCustomAttribute<DisplayAttribute>()?.Name
               ?? property.GetCustomAttribute<DescriptionAttribute>()?.Description
               ?? property.Name.Wordify();
    }

    /// <summary>
    /// Builds the read-only view for a property with click-to-edit support.
    /// </summary>
    private static UiControl BuildReadonlyControl(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId,
        bool isEditable)
    {
        var propName = property.Name.ToCamelCase()!;
        var propType = property.PropertyType;

        var dimAttr = property.GetCustomAttribute<DimensionAttribute>();
        var uiAttr = property.GetCustomAttribute<UiControlAttribute>();
        var displayFormatAttr = property.GetCustomAttribute<DisplayFormatAttribute>();

        UiControl readOnlyControl;

        if (dimAttr != null)
        {
            readOnlyControl = BuildDimensionReadOnlyLabel(host, propName, dataId, dimAttr);
        }
        else if (uiAttr?.Options != null)
        {
            readOnlyControl = BuildOptionsReadOnlyLabel(host, propName, dataId, uiAttr.Options);
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            var format = displayFormatAttr?.DataFormatString ?? "{0:d}";
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

        var displayLabelId = $"displayLabel_{dataId}_{propName}";

        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        var collectionStream = host.Workspace.GetStream(new CollectionReference(collectionName));

        if (collectionStream != null)
        {
            // Use ReplaceDisposable to prevent duplicate subscriptions when control is rebuilt
            // Use DistinctUntilChanged to prevent endless emissions from CombineLatest
            string? lastDisplayName = null;
            host.ReplaceDisposable(displayLabelId,
                dataStream.CombineLatest(collectionStream, (data, collection) =>
                {
                    if (data.ValueKind == JsonValueKind.Undefined || collection?.Value == null)
                        return "";

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

                    if (collection.Value.Instances.TryGetValue(keyValue, out var instance))
                    {
                        if (instance is INamed named)
                            return named.DisplayName;
                        return instance.ToString() ?? "";
                    }

                    return keyValue.ToString() ?? "";
                })
                .Subscribe(displayName =>
                {
                    // Manual DistinctUntilChanged to avoid endless emissions
                    if (displayName == lastDisplayName)
                        return;
                    lastDisplayName = displayName;
                    host.UpdateData(displayLabelId, displayName);
                }));
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
        var optionsList = ConvertOptionsForToggle(options);

        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        // Use ReplaceDisposable to prevent duplicate subscriptions when control is rebuilt
        string? lastDisplayName = null;
        host.ReplaceDisposable(displayLabelId,
            dataStream.Select(data =>
            {
                if (data.ValueKind == JsonValueKind.Undefined)
                    return "";

                if (!data.TryGetProperty(propName, out var valueElement))
                    return "";

                var keyValue = valueElement.ValueKind == JsonValueKind.String
                    ? valueElement.GetString()
                    : valueElement.ToString();

                var option = optionsList.FirstOrDefault(o => o.GetItem()?.ToString() == keyValue);
                return option?.Text ?? keyValue ?? "";
            })
            .Subscribe(displayName =>
            {
                // Manual DistinctUntilChanged to avoid unnecessary emissions
                if (displayName == lastDisplayName)
                    return;
                lastDisplayName = displayName;
                host.UpdateData(displayLabelId, displayName);
            }));

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

        var dataStream = host.Stream.GetDataStream<JsonElement>(dataId);
        // Use ReplaceDisposable to prevent duplicate subscriptions when control is rebuilt
        string? lastFormattedDate = null;
        host.ReplaceDisposable(displayLabelId,
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
            })
            .Subscribe(formattedDate =>
            {
                // Manual DistinctUntilChanged to avoid unnecessary emissions
                if (formattedDate == lastFormattedDate)
                    return;
                lastFormattedDate = formattedDate;
                host.UpdateData(displayLabelId, formattedDate);
            }));

        return new LabelControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(displayLabelId)))
            .WithStyle("padding: 8px; min-height: 32px;");
    }

    /// <summary>
    /// Builds the edit view for a property with blur action for auto-switching back to read mode.
    /// </summary>
    private static UiControl BuildEditControl(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId)
    {
        var propName = property.Name.ToCamelCase()!;
        var jsonPointer = new JsonPointerReference(propName);
        var propType = property.PropertyType;
        var isRequired = property.HasAttribute<RequiredMemberAttribute>() || property.HasAttribute<RequiredAttribute>();
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        UiControl editCtrl;

        var uiAttr = property.GetCustomAttribute<UiControlAttribute>();
        if (uiAttr != null && uiAttr.SeparateEditView != true)
        {
            editCtrl = CreateEditControlFromUiAttribute(host, uiAttr, property, jsonPointer, isRequired, editStateId);
        }
        else if (property.GetCustomAttribute<DimensionAttribute>() is { } dimAttr)
        {
            editCtrl = CreateDimensionSelectControl(host, jsonPointer, dimAttr, isRequired, dataId, editStateId);
        }
        else if (propType.IsIntegerType() || propType.IsRealType())
        {
            editCtrl = new NumberFieldControl(jsonPointer, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            editCtrl = new DateTimeControl(jsonPointer)
            {
                Required = isRequired
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
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
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
        }

        // Apply style from UiControlAttribute if present, otherwise no default constraints
        var attrStyle = property.GetCustomAttribute<UiControlAttribute>()?.Style;
        editCtrl = editCtrl with
        {
            DataContext = LayoutAreaReference.GetDataPointer(dataId),
            Style = attrStyle
        };

        return editCtrl;
    }

    private static Task SwitchToReadOnlyMode(UiActionContext ctx, string editStateId)
    {
        ctx.Host.UpdateData(editStateId, false);
        return Task.CompletedTask;
    }

    private static UiControl CreateEditControlFromUiAttribute(
        LayoutAreaHost host,
        UiControlAttribute attr,
        PropertyInfo property,
        JsonPointerReference jsonPointer,
        bool isRequired,
        string editStateId)
    {
        if (attr.ControlType == typeof(TextAreaControl))
            return new TextAreaControl(jsonPointer)
            {
                Required = isRequired,
                AutoFocus = true
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));

        if (attr.ControlType == typeof(SelectControl) && attr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptionsForToggle(attr.Options));
            return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
        }

        return new TextFieldControl(jsonPointer)
        {
            Required = isRequired,
            Immediate = true,
            AutoFocus = true
        }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
    }

    private static UiControl CreateDimensionSelectControl(
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
            }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));

        var registrationKey = $"dimensionOptions_{dataId}_{jsonPointer.Pointer}";
        var optionsId = $"dimOpts_{dataId}_{jsonPointer.Pointer}"; // Use stable ID instead of Guid
        // Use ReplaceDisposable to prevent duplicate subscriptions when control is rebuilt
        host.ReplaceDisposable(registrationKey,
            host.Workspace.GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptionsForToggle(x.Value!,
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(opts => host.UpdateData(optionsId, opts)));

        return new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired
        }.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId));
    }

    /// <summary>
    /// Builds a markdown section with full width, title, and Done button for edit mode.
    /// </summary>
    private static UiControl BuildMarkdownToggle(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId,
        IObservable<bool> editStateStream,
        bool isEditable)
    {
        var propName = property.Name.ToCamelCase()!;
        var displayName = GetToggleableDisplayName(property);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 24px;")
            .WithView((h, _) =>
                editStateStream
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && isEditable
                            ? BuildMarkdownEditView(h, property, dataId, editStateId)
                            : BuildMarkdownReadView(h, property, dataId, displayName, editStateId, isEditable)));
    }

    private static UiControl BuildMarkdownReadView(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string displayName,
        string editStateId,
        bool isEditable)
    {
        var propName = property.Name.ToCamelCase()!;

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

    private static UiControl BuildMarkdownEditView(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId)
    {
        var propName = property.Name.ToCamelCase()!;
        var markdownAttr = property.GetCustomAttribute<MarkdownAttribute>();

        var editor = new MarkdownEditorControl()
            .WithHeight(markdownAttr?.EditorHeight ?? "400px")
            .WithMaxHeight("none")
            .WithTrackChanges(markdownAttr?.TrackChanges ?? false)
            .WithPlaceholder(markdownAttr?.Placeholder ?? "Enter content...") with
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

    private static IReadOnlyCollection<Option> ConvertOptionsForToggle(object options)
    {
        if (options is string[] strings)
            return strings.Select(s => (Option)new Option<string>(s, s)).ToArray();
        if (options is IEnumerable<Option> opts)
            return opts.ToArray();
        return Array.Empty<Option>();
    }

    private static IReadOnlyCollection<Option> ConvertDimensionToOptionsForToggle(InstanceCollection instances, ITypeDefinition dimType)
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

    #endregion
}
