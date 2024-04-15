using System.Collections.Immutable;
using System.Linq.Expressions;
using OpenSmc.Layout.DataBinding;

namespace OpenSmc.Layout;

public static class Template
{
    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    public static UiControl Bind<T, TView>(T data, Expression<Func<T, TView>> dataTemplate)
        where TView : UiControl
        => BindObject(data, dataTemplate);

    internal static UiControl BindObject<T, TView>(object data, Expression<Func<T, TView>> dataTemplate)
        where TView : UiControl
    {
        var view = dataTemplate.Build("", out var types);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        view = view with { DataContext = data };
        return view;
    }

    public static ItemTemplateControl Bind<T, TView>(IEnumerable<T> data, Expression<Func<T, TView>> dataTemplate)
        where TView : UiControl
        => BindEnumerable(data, dataTemplate);

    internal static ItemTemplateControl BindEnumerable<T, TView>(object data, Expression<Func<T, TView>> dataTemplate) 
        where TView : UiControl
    {
        var view = dataTemplate.Build("item", out var types);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        var ret = data is Binding 
                      ? new ItemTemplateControl(view, data)
                      : new ItemTemplateControl(view, new Binding("")){ DataContext = data };

        return ret;

    }


}

//result into ui control with DataBinding set
public record ItemTemplateControl : UiControl<ItemTemplateControl>
{
    public ItemTemplateControl(object view, object data)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
    {
        View = view;
        Data = data;
    }


    protected override void Dispose()
    {
    }

    //protected override ItemTemplate Build(ILayoutHub hub, IServiceProvider serviceProvider, string area)
    //{
    //    var subArea = $"{area}/template";
    //    var areaChangedEvent = hub.SetArea(subArea, View, o => o.WithParentArea(area));
    //    var ret = base.Build(hub, serviceProvider, area) with
    //           {
    //               View = areaChangedEvent.View
    //           };

    //    return ret;
    //}

    public object View { get; init; }

}


