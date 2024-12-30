using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using Json.More;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Domain;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout;

public static class Template{

    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static TView Bind<T, TView>(T data,
        Expression<Func<T, TView>> dataTemplate,
        string id = null)
        where TView : UiControl => BindObject(data, dataTemplate, id);

    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static UiControl BindMany<T, TView>(
        this IEnumerable<T> data,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, data);
    }
    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static ItemTemplateControl BindMany<T, TView>(
        this IObservable<IEnumerable<T>> stream,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        object current = null;
        
        return new ItemTemplateControl(view, "/")
            {
                DataContext = LayoutAreaReference.GetDataPointer(id)
            }
            .WithBuildup((host, context, store) =>
        {
            var forwardSubscription = stream.Subscribe(val =>
            {
                if (Equals(val, current))
                    return;
                current = val;
                host.Stream.SetData(id, val, host.Stream.StreamId);
            });
            host.AddDisposable(context.Area, forwardSubscription);
            return new(store, [], null);
        });

    }
    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static TView Bind<T, TView>(this IObservable<T> stream,
        Expression<Func<T, TView>> dataTemplate,
        string id = null)
        where TView : UiControl
    {
        object current = null;
        return (TView)GetTemplateControl(id, dataTemplate)
            .WithBuildup((host, context, store) =>
            {
                var forwardSubscription = stream.Subscribe(val =>
                {
                    if (Equals(val, current))
                        return;
                    current = val;
                    host.Stream.SetData(id, val, host.Stream.StreamId);
                });
                host.AddDisposable(context.Area, forwardSubscription);
                return new(store, [], null);
            });
    }

    private static readonly MethodInfo ItemTemplateMethodNonGeneric = ReflectionHelper.GetStaticMethodGeneric(
        () => ItemTemplateNonGeneric<object, UiControl>(default(IEnumerable<object>), null)
    );
    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static UiControl ItemTemplateNonGeneric<T, TView>(
        object data,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, data);
    }

    private class ReplaceBindMethodAttribute : ReplaceMethodInTemplateAttribute
    {
        public override MethodInfo Replace(MethodInfo method) => ReplaceBindObjects(method);
    }

    private static MethodInfo ReplaceBindObjects(MethodInfo method) =>
    (
        method.GetGenericMethodDefinition() switch
        {
            { } m when m == BindObjectMethod => BindObjectMethod,
            {} m when m == BindManyMethod => ItemTemplateMethodNonGeneric,
            _ => throw new ArgumentException("Method is not supported.")
        }
    ).MakeGenericMethod(method.GetGenericArguments());

    private static readonly MethodInfo BindObjectMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindObject<object, UiControl>(default, default, default)
    );

    internal static TView BindObject<T, TView>(
        object data,
        Expression<Func<T, TView>> dataTemplate,
        string id
    )
        where TView : UiControl
    {
        id ??= Guid.NewGuid().AsString();
        var view = GetTemplateControl(id, dataTemplate);
        if(data != null)
            view = (TView)view.WithBuildup((_,_,store) => store.UpdateData(id, data));
        return view;
    }

    private static TView GetTemplateControl<T, TView>(string id, Expression<Func<T, TView>> dataTemplate)
        where TView : UiControl
    {
        var topLevel = LayoutAreaReference.GetDataPointer(id);
        var view = dataTemplate.Build(topLevel, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        return view;
    }


    [ReplaceBindMethod]
    public static UiControl Bind<T, TView>(
        this IEnumerable<T> data,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindMany(data, dataTemplate);

    public static TView Bind<T, TView>(
        string pointer,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl 
    {
        var view = dataTemplate.Build(pointer, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        return view;
    }

    public static ItemTemplateControl BindEnumerable<T, TView>(
        JsonPointerReference pointer,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, pointer);
    }


    private static readonly MethodInfo BindManyMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindMany<object, UiControl>(default(IEnumerable<object>), null)
    );


    public static T MapToControl<T, TSkin>(
        this IServiceProvider serviceProvider,
        T editor, 
        PropertyInfo propertyInfo)
        where T: ContainerControlWithItemSkin<T,TSkin, EditFormItemSkin>
        where TSkin : Skin<TSkin>
    {
        var dimensionAttribute = propertyInfo.GetCustomAttribute<DimensionAttribute>();
        var jsonPointerReference = GetJsonPointerReference(propertyInfo);
        var label = propertyInfo.GetCustomAttribute<DisplayAttribute>()?.Name ?? propertyInfo.Name.Wordify();
        var documentationService = serviceProvider.GetRequiredService<IDocumentationService>();
        var description = documentationService.GetDocumentation(propertyInfo)?.Summary?.Text;

        Func<EditFormItemSkin, EditFormItemSkin> skinConfiguration = skin => skin with { Name = propertyInfo.Name.ToCamelCase(), Description = description, Label = label };
        if (dimensionAttribute != null)
            return editor.WithView((host, _) => host.Workspace
                    .GetStream(
                        new CollectionReference(
                            host.Workspace.DataContext.GetCollectionName(dimensionAttribute.Type)))
                    .Select(changeItem =>
                        Controls.Select(jsonPointerReference)
                            .WithOptions(ConvertToOptions(changeItem.Value, host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(dimensionAttribute.Type)))), skinConfiguration)
                ;


        if (propertyInfo.PropertyType.IsNumber())
            return editor.WithView((host, _) => RenderNumber(jsonPointerReference, serviceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(propertyInfo.PropertyType)), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(string))
            return editor.WithView(RenderText(jsonPointerReference), skinConfiguration);
        if (propertyInfo.PropertyType == typeof(DateTime) || propertyInfo.PropertyType == typeof(DateTime?))
            return editor.WithView(RenderDateTime(jsonPointerReference), skinConfiguration);

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

