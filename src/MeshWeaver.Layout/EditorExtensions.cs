using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
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
        , string id = null)
        => observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>),
            id ?? Guid.NewGuid().AsString());



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
                .Throttle(TimeSpan.FromMilliseconds(100)) // Throttle the stream to take snapshots every 100ms
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
        string id = null)
        => host.Hub.ServiceProvider.Toolbar(Observable.Return(instance), id);

    public static UiControl Toolbar<T>(
        this LayoutAreaHost host,
        IObservable<T> observable,
        string id = null)
        => host.Hub.ServiceProvider.Toolbar(observable, id);
    public static UiControl Toolbar<T>(
        this IServiceProvider serviceProvider,
        IObservable<T> observable, 
        string id = null)
        => observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>),
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
            .Toolbar
            .WithView(editor)
            .WithView((host, ctx) =>
                host.Stream.GetDataStream<T>(id)
                    .Throttle(TimeSpan.FromMilliseconds(100)) // Throttle the stream to take snapshots every 100ms
                    .SelectMany(x => result.Invoke(x, host, ctx)));
    }


    #endregion

    public static T MapToControl<T, TSkin>(
        this IServiceProvider serviceProvider,
        T editor, 
        PropertyInfo propertyInfo)
        where T: ContainerControlWithItemSkin<T,TSkin, PropertySkin>
        where TSkin : Skin<TSkin>
    {
        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();

        Func<PropertySkin, PropertySkin> skinConfiguration = skin =>
            skin with
            {
                Name = propertyInfo.Name.ToCamelCase(),
                Description = propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description
                              ?? serviceProvider
                                  .GetRequiredService<IDocumentationService>()
                                  .GetDocumentation(propertyInfo)?.Summary?.Text,
                Label = label
            };
        if (dimensionAttribute != null)
        {
            return editor.WithView((host, _) => GetStream(host, dimensionAttribute)
                        .Select(options =>
                            Controls.Select(jsonPointerReference)
                                .WithOptions(options)),
                    skinConfiguration)
                ;
        }


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderNumber(jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((_,_)=>RenderText(jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((_, _) => RenderDateTime(jsonPointerReference), skinConfiguration);

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;
    }

    private static IObservable<IReadOnlyCollection<Option>> GetStream(LayoutAreaHost host, DimensionAttribute dimensionAttribute)
    {
        if (dimensionAttribute.OptionStream == null)
            return host.Workspace
            .GetStream(
                new CollectionReference(
                    host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)))
            .Select(x => 
                ConvertToOptions(
                    x.Value, 
                    host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)));
        return host.GetDataStream<IReadOnlyCollection<Option>>(dimensionAttribute.OptionStream);
    }

    private static JsonPointerReference GetJsonPointerReference(PropertyInfo propertyInfo)
    {
        return new JsonPointerReference($"/{propertyInfo.Name.ToCamelCase()}");
    }
    private static DateTimeControl RenderDateTime(JsonPointerReference jsonPointerReference)
    {
        return Controls.DateTime(jsonPointerReference);
    }

    private static TextFieldControl RenderText(JsonPointerReference jsonPointerReference)
    {
        // TODO V10: Add validations. (17.09.2024, Roland Bürgi)
        return Controls.Text(jsonPointerReference);
    }

    private static NumberFieldControl RenderNumber(
        JsonPointerReference jsonPointerReference, 
        string type)
    {
        // TODO V10: Add range validation, etc. (17.09.2024, Roland Bürgi)
        return Controls.Number(jsonPointerReference, type);
    }


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
