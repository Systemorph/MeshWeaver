using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;

namespace MeshWeaver.Layout;

public static class Template{

    /// <summary>
    /// This is a generic template method which can be used if streams are connected to synchronize with Workspace.
    /// </summary>
    /// <typeparam name="TView"></typeparam>
    /// <param name="id">Id to be referenced in the data binding</param>
    /// <param name="dataTemplate">View Template.</param>
    /// <returns></returns>
    public static TView Bind<TView>(string id, Expression<Func<object, TView>> dataTemplate)
        where TView : UiControl => BindObject(null, id, dataTemplate);


    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static TView Bind<T, TView>(
        T data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindObject(data, id, dataTemplate);

    private static readonly MethodInfo ItemTemplateMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => ItemTemplate<object, UiControl>(default(IEnumerable<object>), null)
    );
    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static UiControl ItemTemplate<T, TView>(
        IEnumerable<T> data,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");


        return new ItemTemplateControl(view, data);
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
            { } m when m == BindEnumerableMethod => BindEnumerableMethod,
            {} m when m == ItemTemplateMethod => ItemTemplateMethodNonGeneric,
            _ => throw new ArgumentException("Method is not supported.")
        }
    ).MakeGenericMethod(method.GetGenericArguments());

    private static readonly MethodInfo BindObjectMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindObject<object, UiControl>(null, default, null)
    );

    internal static TView BindObject<T, TView>(
        object data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var topLevel = UpdateData(data, id);
        var view = dataTemplate.Build(topLevel, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        if(data != null)
            view = (TView)view.WithUpdates(store => store.UpdateData(id, data));
        return view;
    }

    private static string UpdateData(object data, string id)
    {
        if (data is JsonPointerReference reference)
            return reference.Pointer;

        return LayoutAreaReference.GetDataPointer(id);
    }

    [ReplaceBindMethod]
    public static ItemTemplateControl Bind<T, TView>(
        IEnumerable<T> data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindEnumerable(data, id, dataTemplate);

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

    private static readonly MethodInfo BindEnumerableMethod =
        ReflectionHelper.GetStaticMethodGeneric(
            () => BindEnumerable<object, UiControl>(null, default, null)
        );

    internal static ItemTemplateControl BindEnumerable<T, TView>(
        object data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        var dataContext = UpdateData(data, id);
        return (ItemTemplateControl)new ItemTemplateControl(view, data)
                {
                    DataContext = dataContext
                }
                .WithUpdates(store => store.UpdateData(id, data))
            ;
    }


}

//result into ui control with DataBinding set
public record ItemTemplateControl(UiControl View, object Data) :
    UiControl<ItemTemplateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public static string ViewArea = nameof(View);


    public Orientation? Orientation { get; init; }

    public bool Wrap { get; init; }

    public ItemTemplateControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };

    public ItemTemplateControl WithWrap(bool wrap) => this with { Wrap = wrap };
    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var ret = base.Render(host, context, store);
        var renderedView = host.RenderArea(GetContextForArea(context, ItemTemplateControl.ViewArea), View, ret.Store);
        return renderedView with { Changes = ret.Changes.Concat(renderedView.Changes) };
    }

    public virtual bool Equals(ItemTemplateControl other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (!base.Equals(other))
            return false;
        return Equals(View, other.View)
               && Equals(Wrap, other.Wrap)
               && Equals(Orientation, other.Orientation)
               && LayoutHelperExtensions.DataEquality(Data, other.Data);
    }

    public override int GetHashCode() => 
        HashCode.Combine(base.GetHashCode(),
            Wrap,
            View,
            Orientation,
            LayoutHelperExtensions.DataHashCode(Data)
            );
}
