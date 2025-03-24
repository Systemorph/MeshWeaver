using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout;

public static class EditorExtensions
{
    #region editor overloads
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance,
        Func<T, object> result)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit(this LayoutAreaHost host, Type type, string id)
        => host.Hub.ServiceProvider.Edit(type, id);
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance, string id)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), id);
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance,
        Func<T, IObservable<object>> result)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit<T>(this LayoutAreaHost host, T instance)
        => host.Hub.ServiceProvider.Edit(Observable.Return(instance));
    public static UiControl Edit<T>(this IMessageHub hub, T instance,
        Func<T, object> result)
        => hub.ServiceProvider.Edit(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Edit<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, object> result)
        => hub.ServiceProvider.Edit(observable, (i, _, _) => result(i));
    public static UiControl Edit<T>(this IMessageHub hub, T instance,
        Func<T,LayoutAreaHost, RenderingContext, object> result)
        => hub.ServiceProvider.Edit(Observable.Return(instance), result);
    public static UiControl Edit<T>(this IServiceProvider serviceProvider, T instance,
        Func<T,LayoutAreaHost, RenderingContext, object> result)
        => serviceProvider.Edit(Observable.Return(instance), result);

    public static UiControl Edit<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T,LayoutAreaHost, RenderingContext, object> result)
    => hub.ServiceProvider.Edit(observable, result);

    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider, T instance,
        string id = null)
        => serviceProvider.Edit(Observable.Return(instance), id);
    public static UiControl Edit(
        this IMessageHub hub, Type type,
        string id)
        => hub.ServiceProvider.Edit(type, id);
    public static UiControl Edit<T>(
        this IMessageHub hub, T instance,
        string id = null)
        => hub.ServiceProvider.Edit(Observable.Return(instance), id);

    public static UiControl Edit<T>(
        this IMessageHub hub,
        IObservable<T> observable,
        string id = null)
        => hub.ServiceProvider.Edit(observable, id);
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
                        serviceProvider.MapToControl<EditorControl, EditorSkin>) with{DataContext = LayoutAreaReference.GetDataPointer(id)};



    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider,
            IObservable<T> observable,
        Func<T,LayoutAreaHost, RenderingContext, object> result)
    {
        var id = Guid.NewGuid().AsString();
        var editor = serviceProvider.Edit(observable, id);
        if (result == null)
            return editor;
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
            host.Stream.GetDataStream<T>(id)
                .Debounce(TimeSpan.FromMilliseconds(20)) // Throttle the stream to take snapshots every 100ms
                .Select(x => result.Invoke(x,host, ctx)));
    }

    #endregion
    #region Toolbar overloads
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, object> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, IObservable<object>> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, LayoutAreaHost, RenderingContext, object> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this LayoutAreaHost host, T instance,
        Func<T, LayoutAreaHost, RenderingContext, IObservable<object>> result)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this IMessageHub hub, T instance,
        Func<T, object> result)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, object> result)
        => hub.ServiceProvider.Toolbar(observable, (i, _, _) => result(i));
    public static UiControl Toolbar<T>(this IMessageHub hub, T instance,
        Func<T, LayoutAreaHost, RenderingContext, object> result)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), result);
    public static UiControl Toolbar<T>(this IServiceProvider serviceProvider, T instance,
        Func<T, LayoutAreaHost, RenderingContext, object> result)
        => serviceProvider.Toolbar(Observable.Return(instance), result);

    public static UiControl Toolbar<T>(this IMessageHub hub, IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, object> result)
    => hub.ServiceProvider.Toolbar(observable, result);

    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider, T instance,
        string id = null)
        => serviceProvider.Toolbar(Observable.Return(instance), id);
    public static UiControl Toolbar<T>(
        this IMessageHub hub, T instance,
        string id = null)
        => hub.ServiceProvider.Toolbar(Observable.Return(instance), id);

    public static UiControl Toolbar<T>(
        this IMessageHub hub,
        IObservable<T> observable,
        string id = null)
        => hub.ServiceProvider.Toolbar(observable, id);
    public static UiControl Toolbar<T>(
        this LayoutAreaHost host, 
        T instance,
        string id)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), id);

    public static UiControl Toolbar<T>(
        this LayoutAreaHost host,
        IObservable<T> observable,
        string id = null)
        => host.Hub.ServiceProvider.Toolbar(observable, id);
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
        Func<T, LayoutAreaHost, RenderingContext, object> result)
        => serviceProvider.Toolbar(observable, (t, host, ctx) => Observable.Return(result.Invoke(t, host, ctx)));
    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable,
        Func<T, LayoutAreaHost, RenderingContext, IObservable<object>> result)
    {
        var id = Guid.NewGuid().AsString();
        var editor = serviceProvider.Toolbar(observable, id);
        if (result == null)
            return editor;
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
                host.Stream.GetDataStream<T>(id)
                    .Debounce(TimeSpan.FromMilliseconds(20)) // Throttle the stream to take snapshots every 100ms
                    .SelectMany(x => result.Invoke(x, host, ctx)));
    }


    #endregion

    public static T MapToControl<T>(
        this IServiceProvider serviceProvider,
        T editor,
        PropertyInfo propertyInfo)
    where T: ContainerControl<T>
    {
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();

        var jsonPointerReference = GetJsonPointerReference(propertyInfo);



        var uiControlAttribute = propertyInfo.GetCustomAttribute<UiControlAttribute>();
        if (uiControlAttribute != null)
            return editor.WithView(RenderControl(uiControlAttribute.ControlType, propertyInfo, label, jsonPointerReference, uiControlAttribute.Options));


        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        if (dimensionAttribute != null)
        {
            if (dimensionAttribute.Options is not null)
                return editor.WithView(RenderListControl(Controls.Select, jsonPointerReference, dimensionAttribute.Options));
            return editor.WithView((host, ctx) =>
            {
                var id = Guid.NewGuid().AsString();
                host.RegisterForDisposal(ctx.Area,
                    GetStream(host, dimensionAttribute).Subscribe(x => host.UpdateData(id, x)));
                return RenderListControl(Controls.Select,  jsonPointerReference, id);
            });
        }


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderControl(typeof(NumberFieldControl), propertyInfo, label, jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)));
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((_, _) => RenderControl(typeof(TextFieldControl), propertyInfo, label, jsonPointerReference));
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((_, _) => RenderControl(typeof(DateTimeControl), propertyInfo, label, jsonPointerReference));
        if (propertyInfo.PropertyType == typeof(bool) || propertyInfo.PropertyType == typeof(bool?))
            return editor.WithView((_, _) => RenderControl(typeof(CheckBoxControl), propertyInfo, label, jsonPointerReference));

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;

    }

    public static T MapToControl<T, TSkin>(
        this IServiceProvider serviceProvider,
        T editor, 
        PropertyInfo propertyInfo)
        where T: ContainerControlWithItemSkin<T,TSkin, PropertySkin>
        where TSkin : Skin<TSkin>
    {
        var propertySkinLabel = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();
        string label = null; // // TODO V10: This is to avoid duplication with property skin. do consistently in future. (19.01.2025, Roland Bürgi)

        Func<PropertySkin, PropertySkin> skinConfiguration = skin =>
            skin with
            {
                Name = propertyInfo.Name.ToCamelCase(),
                Description = propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description
                              ?? serviceProvider
                                  .GetRequiredService<IDocumentationService>()
                                  .GetDocumentation(propertyInfo)?.Summary?.Text,
                Label = propertySkinLabel
            };
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);



        var uiControlAttribute = propertyInfo.GetCustomAttribute<UiControlAttribute>();
        if(uiControlAttribute != null)
            return editor.WithView(RenderControl(uiControlAttribute.ControlType, propertyInfo, label, jsonPointerReference, uiControlAttribute.Options), skinConfiguration);


        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        if (dimensionAttribute != null)
        {
            if(dimensionAttribute.Options is not null)
                return editor.WithView(RenderListControl(Controls.Select, jsonPointerReference, dimensionAttribute.Options));
            return editor.WithView((host, ctx) =>
            {
                var id = Guid.NewGuid().AsString();
                host.RegisterForDisposal(ctx.Area,
                    GetStream(host, dimensionAttribute).Subscribe(x => host.UpdateData(id, x)));
                return RenderListControl(Controls.Select, jsonPointerReference, id);
            });
        }


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderControl(typeof(NumberFieldControl), propertyInfo, label, jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((_,_) => RenderControl(typeof(TextFieldControl), propertyInfo, label, jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((_, _) => RenderControl(typeof(DateTimeControl), propertyInfo, label, jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(bool) || propertyInfo.PropertyType == typeof(bool?))
            return editor.WithView((_, _) => RenderControl(typeof(CheckBoxControl), propertyInfo, label, jsonPointerReference), skinConfiguration);

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;
    }


    private static IObservable<IReadOnlyCollection<Option>> GetStream(LayoutAreaHost host, DimensionAttribute dimensionAttribute)
    {
        return host.Workspace
            .GetStream(
                new CollectionReference(
                    host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)))
            .Select(x => 
                ConvertToOptions(
                    x.Value, 
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)));
    }


    private static IReadOnlyCollection<Option> ConvertToOptions(ICollection collection)
    {
        var elementType = 
            collection is Array array 
                ? array.GetType().GetElementType()
                : collection.GetType().GetInterfaces().Select(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>) ? i.GenericTypeArguments.First() : null).FirstOrDefault();
        if (elementType == null)
        {
            throw new ArgumentException("Collection does not have a generic type argument.");
        }

        var optionType = typeof(Option<>).MakeGenericType(elementType);
        return collection.Cast<object>().Select(x => (Option)Activator.CreateInstance(optionType, x, x.ToString().Wordify())).ToArray();
    }

    private static JsonPointerReference GetJsonPointerReference(PropertyInfo propertyInfo) => 
        new(propertyInfo.Name.ToCamelCase());


    private static IUiControl RenderControl(Type controlType,
        PropertyInfo propertyInfo,
        string label,
        JsonPointerReference reference,
        object parameter = null)
    {
        if (BasicControls.TryGetValue(controlType, out var factory))
            return ((IFormControl)factory.Invoke(reference, parameter)).WithLabel(label);
        if (ListControls.TryGetValue(controlType, out var factory2))
            return ((IListControl)factory2.Invoke(propertyInfo, reference, parameter)).WithLabel(label);

        throw new ArgumentException($"Cannot convert type {controlType.FullName} to an editor field.");
    }
    private static readonly Dictionary<Type, Func<PropertyInfo, JsonPointerReference, object, UiControl>>
        ListControls = new()
        {
            {typeof(SelectControl), (_, reference,options)=> RenderListControl(Controls.Select,  reference, options)},
            {typeof(RadioGroupControl), (propertyInfo, reference,options)=> RenderListControl((d,o)=>Controls.RadioGroup(d,o, propertyInfo.PropertyType.Name), reference, options)},
            {typeof(ComboboxControl), (_, reference,options)=> RenderListControl(Controls.Combobox, reference, options)},
            {typeof(ListboxControl), (_, reference,options)=> RenderListControl(Controls.Listbox, reference, options)},
        };


    private static TControl RenderListControl<TControl>(
        Func<object, object, TControl> controlFactory,
       object data, 
        object options)
    where TControl: ListControlBase<TControl>
    {
        if (options is string id)
            return controlFactory.Invoke(data,  new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)));
        if (options is JsonPointerReference)
            return controlFactory.Invoke(data, options);

        if (options is ICollection collection)
        {
            id = Guid.NewGuid().AsString();
            return controlFactory.Invoke(data, new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
                .WithBuildup((_,_,es) => es.UpdateData(id, ConvertToOptions(collection)));
        }

        throw new ArgumentException(
            $"No implementation to parse dimension options of type {options.GetType().FullName}.");
    }

    private static readonly Dictionary<Type, Func<JsonPointerReference, object, UiControl>> 
        BasicControls = new()
    {
        {typeof(DateTimeControl), (reference,_) => Controls.DateTime(reference)},
        {typeof(TextFieldControl), (reference,_)=> Controls.Text(reference)},
        {typeof(NumberFieldControl), Controls.Number},
        {typeof(CheckBoxControl), (reference,_) => Controls.CheckBox(reference)},
    };


    private static IReadOnlyCollection<Option> ConvertToOptions(InstanceCollection instances, ITypeDefinition dimensionType)
    {
        var displayNameSelector =
            typeof(INamed).IsAssignableFrom(dimensionType.Type)
                ? (Func<object, string>)(x => ((INamed)x).DisplayName)
                : o => o.ToString();

        var keyType = dimensionType.GetKeyType();
        var optionType = typeof(Option<>).MakeGenericType(keyType);
        return instances.Instances
            .Select(kvp => (Option)Activator.CreateInstance(optionType, [kvp.Key, displayNameSelector(kvp.Value)])).ToArray();
    }

}
