﻿using System.ComponentModel;
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
    public static UiControl Edit<T>(this IMessageHub hub, T instance,
        Func<LayoutAreaHost, RenderingContext, T, object> result = null)
        => hub.ServiceProvider.Edit(Observable.Return(instance), result);
    public static UiControl Edit<T>(this IServiceProvider serviceProvider, T instance,
        Func<LayoutAreaHost, RenderingContext, T, object> result = null)
        => serviceProvider.Edit(Observable.Return(instance), result);

    public static UiControl Edit<T>(this IMessageHub hub, IObservable<T> observable,
        Func<LayoutAreaHost, RenderingContext, T, object> result = null)
    => hub.ServiceProvider.Edit(observable, result);

    public static UiControl Edit<T>(
        this IServiceProvider serviceProvider,
            IObservable<T> observable,
        Func<LayoutAreaHost, RenderingContext, T, object> result = null)
    {
        var id = Guid.NewGuid().AsString();
        var editor = observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new EditorControl(),
                        serviceProvider.MapToControl<EditorControl, EditorSkin>),
            id);
        if (result == null)
            return editor;
        return Controls
            .Stack
            .WithView(editor)
            .WithView((host, ctx) =>
            host.Stream.GetDataStream<T>(id)
                .Select(x => result.Invoke(host, ctx, x)));
    }


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
            return editor.WithView((host, _) => host.Workspace
                        .GetStream(
                            new CollectionReference(
                                host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)))
                        .Select(changeItem =>
                            Controls.Select(jsonPointerReference)
                                .WithOptions(ConvertToOptions(changeItem.Value, host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)))), 
                    skinConfiguration)
                ;


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderNumber(jsonPointerReference, host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView((_,_)=>RenderText(jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView((_, _) => RenderDateTime(jsonPointerReference), skinConfiguration);

        // TODO V10: Need so see if we can at least return some readonly display (20.09.2024, Roland Bürgi)
        return editor;
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