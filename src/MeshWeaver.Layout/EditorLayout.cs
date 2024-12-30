using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout;

public static class EditorLayout
{
    public static LayoutStackControl Edit<T>(
        this IObservable<T> observable,
        Func<LayoutAreaHost, RenderingContext, T, object> result = null)
    {
        var id = Guid.NewGuid().AsString();
        var editor = observable.Bind(_ =>
                typeof(T).GetProperties()
                    .Aggregate(new EditorControl(), MapToControl<EditorControl, EditorSkin>),
            id);
        var ret = Controls.Stack.WithView(editor);
        if (result == null)
            return ret;
        return ret.WithView((host, ctx) =>
            host.Stream.GetDataStream<T>(id).Distinct().Select(x => result.Invoke(host, ctx, x)));
    }


    public static T MapToControl<T, TSkin>(
        T editor, 
        PropertyInfo propertyInfo)
        where T: ContainerControlWithItemSkin<T,TSkin, EditFormItemSkin>
        where TSkin : Skin<TSkin>
    {
        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();

        Func<LayoutAreaHost, RenderingContext, EditFormItemSkin, EditFormItemSkin> skinConfiguration = (host, _, skin) =>
            skin with
            {
                Name = propertyInfo.Name.ToCamelCase(),
                Description = host.Hub.ServiceProvider
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
    public static JsonPointerReference GetJsonPointerReference(PropertyInfo propertyInfo)
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
