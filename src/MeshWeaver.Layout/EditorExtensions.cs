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
                    .Aggregate(new EditorControl() { Style = "width: 100%;" },
                        serviceProvider.MapToControl<EditorControl, EditorSkin>),
            id);
    public static EditorControl Edit(this IServiceProvider serviceProvider, Type type, string id)
        => type.GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>) with
        { DataContext = LayoutAreaReference.GetDataPointer(id), Style = "width: 100%;" };


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
        // Only use UiControlAttribute if it specifies a control type (not just styling)
        if (uiControlAttribute != null && (uiControlAttribute.EditControlType != null || uiControlAttribute.DisplayControlType != null))
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
        // Only use UiControlAttribute if it specifies a control type (not just styling)
        if (uiControlAttribute != null && (uiControlAttribute.EditControlType != null || uiControlAttribute.DisplayControlType != null))
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
    /// <param name="isToggleable">If true (default), starts read-only and toggles on click/blur. If false, stays in edit mode.</param>
    /// <returns>A reactive control that switches between read and edit modes.</returns>
    public static UiControl MapToToggleableControl(
        this IServiceProvider serviceProvider,
        PropertyInfo property,
        string dataId,
        bool canEdit,
        LayoutAreaHost host,
        bool isToggleable = true)
    {
        var propName = property.Name.ToCamelCase()!;
        var editStateId = $"editState_{dataId}_{propName}";

        // Initialize edit state only once per session to prevent reset on re-render
        // Use a unique key combining stream ID and edit state ID
        // When not toggleable, start in edit mode
        var initKey = $"{host.Stream.StreamId}_{editStateId}";
        lock (InitializedEditStates)
        {
            if (!InitializedEditStates.Contains(initKey))
            {
                host.UpdateData(editStateId, !isToggleable); // Start in edit mode when not toggleable
                InitializedEditStates.Add(initKey);
            }
        }

        // Get the edit state stream - now it will emit the stored value
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId)
            .DistinctUntilChanged();

        var isEditable = canEdit &&
                         property.GetCustomAttribute<EditableAttribute>()?.AllowEdit != false &&
                         !property.HasAttribute<KeyAttribute>();

        // Handle MeshNodeCollectionAttribute - full width inline collection view
        if (property.GetCustomAttribute<MeshNodeCollectionAttribute>() != null)
        {
            return BuildCollectionSection(host, property, dataId, isEditable);
        }

        // Handle markdown properties - full width with Done button
        var uiControlAttr = property.GetCustomAttribute<UiControlAttribute>();
        if (IsMarkdownProperty(property))
        {
            return BuildMarkdownToggle(host, property, dataId, editStateId, editStateStream, isEditable, isToggleable);
        }

        // Regular property: Label + reactive read/edit view
        var displayName = GetToggleableDisplayName(property);

        // Apply style from UiControlAttribute if present - minimal padding for alignment
        var containerStyle = "padding: 4px 0;";
        if (!string.IsNullOrEmpty(uiControlAttr?.Style))
        {
            containerStyle += " " + uiControlAttr.Style;
        }

        return Controls.Stack
            .WithStyle(containerStyle)
            .WithView(Controls.Label(displayName)
                .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem;"))
            .WithView((h, _) => editStateStream
                .Select(isEditing => isEditing && isEditable
                    ? BuildEditControl(h, property, dataId, editStateId, isToggleable)
                    : BuildReadonlyControl(h, property, dataId, editStateId, isEditable)));
    }

    /// <summary>
    /// Returns true if the property should render as markdown.
    /// This includes properties with [Markdown] attribute (SeparateEditView)
    /// and string properties named "Description" (by convention).
    /// </summary>
    internal static bool IsMarkdownProperty(PropertyInfo property)
        => property.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView == true
           || (property.PropertyType == typeof(string)
               && property.Name.Equals("Description", StringComparison.OrdinalIgnoreCase)
               && property.GetCustomAttribute<UiControlAttribute>() == null);

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
        else if (property.GetCustomAttribute<MeshNodeAttribute>() != null
                 && propType == typeof(string))
        {
            readOnlyControl = new LabelControl(new JsonPointerReference(propName))
            {
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("padding: 8px; min-height: 32px;");
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
    /// Builds the edit view for a property with optional blur action for auto-switching back to read mode.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="property">The property to create the control for.</param>
    /// <param name="dataId">The data ID used for data binding.</param>
    /// <param name="editStateId">The edit state ID for toggling.</param>
    /// <param name="isToggleable">If true, blur switches back to read-only mode. If false, stays in edit mode.</param>
    private static UiControl BuildEditControl(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId,
        bool isToggleable)
    {
        var propName = property.Name.ToCamelCase()!;
        var jsonPointer = new JsonPointerReference(propName);
        var propType = property.PropertyType;
        var isRequired = property.HasAttribute<RequiredMemberAttribute>() || property.HasAttribute<RequiredAttribute>();
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        UiControl editCtrl;

        var uiAttr = property.GetCustomAttribute<UiControlAttribute>();
        // Only use CreateEditControlFromUiAttribute if a control type is actually specified
        if (uiAttr != null && uiAttr.SeparateEditView != true && (uiAttr.EditControlType != null || uiAttr.DisplayControlType != null || uiAttr.Options != null))
        {
            editCtrl = CreateEditControlFromUiAttribute(host, uiAttr, property, jsonPointer, isRequired, editStateId, isToggleable);
        }
        else if (property.GetCustomAttribute<DimensionAttribute>() is { } dimAttr)
        {
            editCtrl = CreateDimensionSelectControl(host, jsonPointer, dimAttr, isRequired, dataId, editStateId, isToggleable);
        }
        else if (property.GetCustomAttribute<MeshNodeAttribute>() is { } meshNodeAttr
                 && propType == typeof(string))
        {
            var nodeNamespace = host.Hub.Address.ToString();
            editCtrl = new MeshNodePickerControl(jsonPointer)
            {
                Queries = MeshNodeAttribute.ResolveQueries(meshNodeAttr.Queries, nodeNamespace, nodeNamespace),
                Required = isRequired
            };
        }
        else if (propType.IsIntegerType() || propType.IsRealType())
        {
            var numCtrl = new NumberFieldControl(jsonPointer, typeRegistry.GetOrAddType(propType))
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = isToggleable // Only auto-focus when toggleable (user just clicked)
            };
            editCtrl = isToggleable
                ? numCtrl.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : numCtrl;
        }
        else if (propType == typeof(DateTime) || propType == typeof(DateTime?))
        {
            var dateCtrl = new DateTimeControl(jsonPointer)
            {
                Required = isRequired
            };
            editCtrl = isToggleable
                ? dateCtrl.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : dateCtrl;
        }
        else if (propType == typeof(bool) || propType == typeof(bool?))
        {
            editCtrl = new CheckBoxControl(jsonPointer) { Required = isRequired };
        }
        else
        {
            var textCtrl = new TextFieldControl(jsonPointer)
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = isToggleable // Only auto-focus when toggleable
            };
            editCtrl = isToggleable
                ? textCtrl.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : textCtrl;
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
        string editStateId,
        bool isToggleable)
    {
        var controlType = attr.EditControlType ?? attr.DisplayControlType;

        if (controlType == typeof(TextAreaControl))
        {
            var textArea = new TextAreaControl(jsonPointer)
            {
                Required = isRequired,
                AutoFocus = isToggleable
            };
            return isToggleable
                ? textArea.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : textArea;
        }

        // SelectControl with options (either explicit SelectControl type or just Options set)
        if ((controlType == typeof(SelectControl) || controlType == null) && attr.Options != null)
        {
            var optionsId = Guid.NewGuid().AsString();
            host.UpdateData(optionsId, ConvertOptionsForToggle(attr.Options));
            var selectCtrl = new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
            {
                Required = isRequired
            };
            return isToggleable
                ? selectCtrl.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : selectCtrl;
        }

        // Default to TextField
        var textField = new TextFieldControl(jsonPointer)
        {
            Required = isRequired,
            Immediate = true,
            AutoFocus = isToggleable
        };
        return isToggleable
            ? textField.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
            : textField;
    }

    private static UiControl CreateDimensionSelectControl(
        LayoutAreaHost host,
        JsonPointerReference jsonPointer,
        DimensionAttribute dimensionAttr,
        bool isRequired,
        string dataId,
        string editStateId,
        bool isToggleable)
    {
        var collectionName = host.Workspace.DataContext.GetCollectionName(dimensionAttr.Type);
        if (string.IsNullOrEmpty(collectionName))
        {
            var fallback = new TextFieldControl(jsonPointer)
            {
                Required = isRequired,
                Immediate = true,
                AutoFocus = isToggleable
            };
            return isToggleable
                ? fallback.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
                : fallback;
        }

        var registrationKey = $"dimensionOptions_{dataId}_{jsonPointer.Pointer}";
        var optionsId = $"dimOpts_{dataId}_{jsonPointer.Pointer}"; // Use stable ID instead of Guid
        // Use ReplaceDisposable to prevent duplicate subscriptions when control is rebuilt
        host.ReplaceDisposable(registrationKey,
            host.Workspace.GetStream(new CollectionReference(collectionName))!
                .Select(x => ConvertDimensionToOptionsForToggle(x.Value!,
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttr.Type)!))
                .Subscribe(opts => host.UpdateData(optionsId, opts)));

        var ctrl = new SelectControl(jsonPointer, new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsId)))
        {
            Required = isRequired
        };
        return isToggleable
            ? ctrl.WithBlurAction(ctx => SwitchToReadOnlyMode(ctx, editStateId))
            : ctrl;
    }

    /// <summary>
    /// Builds a markdown section with full width, title, and Done button for edit mode.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="property">The property to create the control for.</param>
    /// <summary>
    /// Builds a full-width section for a collection property marked with [MeshNodeCollection].
    /// Renders items inline as chips/tags from the bound data.
    /// If editable, adds x buttons per item for deletion and a + button to add new items.
    /// </summary>
    private static UiControl BuildCollectionSection(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        bool isEditable)
    {
        var propName = property.Name.ToCamelCase()!;
        var displayName = GetToggleableDisplayName(property);

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 16px;")
            .WithView(Controls.Label(displayName)
                .WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem; margin-bottom: 8px;"))
            .WithView((h, _) =>
                h.Stream.GetDataStream<JsonElement>(dataId)
                    .Select(data => BuildCollectionChips(h, data, propName, dataId, isEditable)));

        // Add "+" button when editable
        if (isEditable)
        {
            var collectionAttr = property.GetCustomAttribute<MeshNodeCollectionAttribute>();
            if (collectionAttr != null)
            {
                stack = stack.WithView(Controls.Button("+ Add")
                    .WithAppearance(Appearance.Lightweight)
                    .WithStyle("align-self: flex-start; margin-top: 4px; font-size: 0.85rem;")
                    .WithClickAction(ctx =>
                    {
                        ShowAddCollectionItemDialog(ctx, property, collectionAttr, dataId, propName);
                        return Task.CompletedTask;
                    }));
            }
        }

        return stack;
    }

    private static UiControl BuildCollectionChips(
        LayoutAreaHost host,
        JsonElement data,
        string propName,
        string dataId,
        bool isEditable)
    {
        if (!data.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Controls.Html("<span style=\"color: var(--neutral-foreground-hint);\">None</span>");

        var chipStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("flex-wrap: wrap; gap: 6px; align-items: center;");

        var index = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var label = GetCollectionItemLabel(item);
            var isDenied = item.TryGetProperty("denied", out var d) && d.GetBoolean();
            var capturedIndex = index;

            var chipStyle = "display: inline-flex; align-items: center; gap: 4px; padding: 2px 10px; " +
                            "border-radius: 14px; background: var(--neutral-fill-secondary-rest); font-size: 0.85rem;";
            if (isDenied)
                chipStyle += " text-decoration: line-through; opacity: 0.6;";

            if (isEditable)
            {
                // Chip with label + x button using HTML for the dismiss icon
                var chipHtml = $"<span style=\"{chipStyle}\">" +
                               $"{System.Web.HttpUtility.HtmlEncode(label)}</span>";
                var chipRow = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("display: inline-flex; align-items: center; gap: 0;")
                    .WithView(Controls.Html(chipHtml))
                    .WithView(Controls.Button("×")
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle("min-width: 18px; padding: 0 2px; height: 20px; font-size: 14px; line-height: 1;")
                        .WithClickAction(ctx =>
                        {
                            RemoveCollectionItem(ctx.Host, dataId, propName, capturedIndex);
                            return Task.CompletedTask;
                        }));
                chipStack = chipStack.WithView(chipRow);
            }
            else
            {
                chipStack = chipStack.WithView(Controls.Html(
                    $"<span style=\"{chipStyle}\">{System.Web.HttpUtility.HtmlEncode(label)}</span>"));
            }

            index++;
        }

        if (index == 0)
            return Controls.Html("<span style=\"color: var(--neutral-foreground-hint);\">None</span>");

        return chipStack;
    }

    private static string GetCollectionItemLabel(JsonElement item)
    {
        // Try common property names for the display label
        foreach (var name in new[] { "role", "group", "name", "id", "value" })
        {
            if (item.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var str = val.GetString();
                if (!string.IsNullOrEmpty(str))
                    return str;
            }
        }
        // Don't show raw JSON — show a placeholder
        return "(empty)";
    }

    private static void RemoveCollectionItem(LayoutAreaHost host, string dataId, string propName, int indexToRemove)
    {
        // Read current data, remove item at index, write back
        var current = host.Stream.GetDataStream<JsonElement>(dataId);
        current.Take(1).Subscribe(data =>
        {
            if (!data.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            var jsonObj = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
            var jsonArr = jsonObj[propName]!.AsArray();
            if (indexToRemove >= 0 && indexToRemove < jsonArr.Count)
            {
                jsonArr.RemoveAt(indexToRemove);
                var updated = JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString());
                host.UpdateData(dataId, updated);
            }
        });
    }

    /// <summary>
    /// Shows a dialog with a MeshNodePickerControl to select a node, then adds it as a new item
    /// in the collection array.
    /// </summary>
    private static void ShowAddCollectionItemDialog(
        UiActionContext ctx,
        PropertyInfo property,
        MeshNodeCollectionAttribute collectionAttr,
        string dataId,
        string propName)
    {
        var formId = $"add_collection_{Guid.NewGuid().AsString()}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["selectedItem"] = "" });

        // Resolve queries with the node's namespace
        var nodeNamespace = ctx.Hub.Address.ToString();
        var queries = MeshNodeCollectionAttribute.ResolveQueries(collectionAttr.Queries, nodeNamespace, nodeNamespace);

        // Determine the element type and its key property name (first string property)
        var elementType = GetCollectionElementType(property);
        var keyPropName = GetCollectionKeyPropertyName(elementType);

        var formContent = Controls.Stack.WithStyle("gap: 16px; padding: 16px;")
            .WithView(new MeshNodePickerControl(new JsonPointerReference("selectedItem"))
            {
                Queries = queries,
                Label = property.Name.Wordify(),
                Required = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId)
            });

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: flex-end; gap: 8px;")
            .WithView(Controls.Button("Add")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async addCtx =>
                {
                    var formValues = await addCtx.Host.Stream
                        .GetDataStream<Dictionary<string, object?>>(formId).FirstAsync();

                    var selectedValue = formValues.GetValueOrDefault("selectedItem")?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(selectedValue))
                    {
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown("Please select an item."),
                            "Validation Error"
                        ).WithSize("S").WithClosable(true);
                        addCtx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    // Close dialog
                    addCtx.Host.UpdateArea(DialogControl.DialogArea, null!);

                    // Add the item to the collection
                    AddCollectionItem(addCtx.Host, dataId, propName, elementType, keyPropName, selectedValue);
                }))
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(cancelCtx =>
                {
                    cancelCtx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    return Task.CompletedTask;
                }));

        var dialog = Controls.Dialog(formContent, $"Add {property.Name.Wordify()}")
            .WithSize("M")
            .WithActions(actions);

        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// Adds a new item to a collection array in the data stream.
    /// Creates a default instance of the element type with the key property set to the selected value.
    /// </summary>
    private static void AddCollectionItem(
        LayoutAreaHost host,
        string dataId,
        string propName,
        Type? elementType,
        string? keyPropName,
        string selectedValue)
    {
        var current = host.Stream.GetDataStream<JsonElement>(dataId);
        current.Take(1).Subscribe(data =>
        {
            var jsonObj = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();

            // Ensure the array exists
            if (jsonObj[propName] is not System.Text.Json.Nodes.JsonArray jsonArr)
            {
                jsonArr = new System.Text.Json.Nodes.JsonArray();
                jsonObj[propName] = jsonArr;
            }

            // Build the new item JSON
            System.Text.Json.Nodes.JsonObject newItem;
            if (elementType != null)
            {
                // Create a default instance and set the key property
                var instance = Activator.CreateInstance(elementType)!;
                if (!string.IsNullOrEmpty(keyPropName))
                {
                    var prop = elementType.GetProperty(keyPropName);
                    prop?.SetValue(instance, selectedValue);
                }
                var serialized = JsonSerializer.Serialize(instance);
                newItem = System.Text.Json.Nodes.JsonNode.Parse(serialized)!.AsObject();
            }
            else
            {
                newItem = new System.Text.Json.Nodes.JsonObject
                {
                    [keyPropName ?? "value"] = selectedValue
                };
            }

            jsonArr.Add(newItem);
            var updated = JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString());
            host.UpdateData(dataId, updated);
        });
    }

    /// <summary>
    /// Gets the element type of a collection property (e.g., IReadOnlyList&lt;RoleAssignment&gt; → RoleAssignment).
    /// </summary>
    private static Type? GetCollectionElementType(PropertyInfo property)
    {
        var propType = property.PropertyType;
        if (propType.IsGenericType)
        {
            return propType.GetGenericArguments().FirstOrDefault();
        }
        if (propType.IsArray)
        {
            return propType.GetElementType();
        }
        // Try interfaces
        var listInterface = propType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        return listInterface?.GetGenericArguments().FirstOrDefault();
    }

    /// <summary>
    /// Gets the key property name of a collection element type (the first string property).
    /// For RoleAssignment → "Role", for MembershipEntry → "Group".
    /// </summary>
    private static string? GetCollectionKeyPropertyName(Type? elementType)
    {
        if (elementType == null) return null;
        return elementType.GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    /// <param name="dataId">The data ID used for data binding.</param>
    /// <param name="editStateId">The edit state ID for toggling.</param>
    /// <param name="editStateStream">Observable stream of edit state.</param>
    /// <param name="isEditable">Whether the property is editable.</param>
    /// <param name="isToggleable">If true, allows toggling between read/edit. If false, stays in edit mode.</param>
    private static UiControl BuildMarkdownToggle(
        LayoutAreaHost host,
        PropertyInfo property,
        string dataId,
        string editStateId,
        IObservable<bool> editStateStream,
        bool isEditable,
        bool isToggleable)
    {
        var propName = property.Name.ToCamelCase()!;
        var displayName = GetToggleableDisplayName(property);

        // When not toggleable and editable, always show edit view
        if (!isToggleable && isEditable)
        {
            return Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-top: 16px;")
                .WithView(BuildMarkdownEditView(host, property, dataId, editStateId, isToggleable));
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 16px;")
            .WithView((h, _) =>
                editStateStream
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && isEditable
                            ? BuildMarkdownEditView(h, property, dataId, editStateId, isToggleable)
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
            .WithStyle("background: var(--neutral-fill-rest); border-radius: 4px; padding: 12px;" + (isEditable ? " cursor: pointer;" : ""))
            .WithView(Controls.Label(displayName).WithStyle("font-weight: 600; color: var(--neutral-foreground-hint); font-size: 0.875rem; margin-bottom: 8px;"))
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
        string editStateId,
        bool isToggleable)
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

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithView(editor);

        // Only show Done button when toggleable
        if (isToggleable)
        {
            stack = stack.WithView(Controls.Stack
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

        return stack;
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
