using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Layout;

public static class Template
{

    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static TView Bind<T, TView>(T data,
        Expression<Func<T, TView>>? dataTemplate,
        string? id = null)
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
        var view = dataTemplate.Build("", out var _);
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

        object? current = null;

        return new ItemTemplateControl(view, "/") { DataContext = LayoutAreaReference.GetDataPointer(id) }
            .WithBuildup((host, context, store) =>
            {
                var forwardSubscription = stream.Subscribe(val =>
                {
                    if (Equals(val, current))
                        return;
                    current = val;
                    host.Stream.SetData(id, val, host.Stream.StreamId);
                });
                host.RegisterForDisposal(context.Area, forwardSubscription);
                return new(store, [], null);
            });

    }

    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static TView Bind<T, TView>(this IObservable<T> stream,
        Expression<Func<T, TView>>? dataTemplate,
        string? id)
        where TView : UiControl
    {
        object? current = null;
        id ??= Guid.NewGuid().AsString();
        return (TView)GetTemplateControl(id!, dataTemplate)
            .WithBuildup((host, context, store) =>
            {
                var forwardSubscription = stream.Subscribe(val =>
                {
                    if (Equals(val, current))
                        return;
                    current = val;
                    host.Stream.SetData(id!, val, host.Stream.StreamId);
                });
                host.RegisterForDisposal(context.Area, forwardSubscription);
                return new(store, [], null);
            });
    }

    private static readonly MethodInfo ItemTemplateMethodNonGeneric = ReflectionHelper.GetStaticMethodGeneric(
        () => ItemTemplateNonGeneric<object, UiControl>(null, null)
    );

    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static UiControl ItemTemplateNonGeneric<T, TView>(
        object? data,
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
            { } m when m == BindManyMethod => ItemTemplateMethodNonGeneric,
            _ => throw new ArgumentException("Method is not supported.")
        }
    ).MakeGenericMethod(method.GetGenericArguments());

    private static readonly MethodInfo BindObjectMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindObject<object, UiControl>(null, null, null)
    );

    internal static TView BindObject<T, TView>(
        object? data,
        Expression<Func<T, TView>>? dataTemplate,
        string? id
    )
        where TView : UiControl
    {
        id ??= Guid.NewGuid().AsString()!;
        var view = GetTemplateControl(id, dataTemplate);
        if (data != null)
            view = (TView)view.WithBuildup((_, _, store) => store.UpdateData(id, data));
        return view;
    }

    private static TView GetTemplateControl<T, TView>(string id, Expression<Func<T, TView>>? dataTemplate)
        where TView : UiControl
    {
        var dataContext = LayoutAreaReference.GetDataPointer(id);
        var view = dataTemplate.Build(dataContext, out var _);
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
        var view = dataTemplate.Build("", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, pointer);
    }


    private static readonly MethodInfo BindManyMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindMany<object, UiControl>(null!, null!)
    );

}
