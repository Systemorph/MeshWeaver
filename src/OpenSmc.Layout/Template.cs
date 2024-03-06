using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Serialization;

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
                //.WithBuildAction((c, sp) =>
                //                 {
                //                     var eventsRegistry = sp.GetService<ITypeRegistry>();
                //                     foreach (var type in types)
                //                         eventsRegistry.WithType(type);
                //                     return c;
                //                 });
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

        if (types.Any())
            ret = ret
                .WithBuildAction((c, sp) =>
                                 {
                                     var eventsRegistry = sp.GetService<ITypeRegistry>();
                                     foreach (var type in types)
                                         eventsRegistry.WithType(type);
                                     return c;
                                 });
        return ret;

    }


}

//result into ui control with DataBinding set
public record ItemTemplateControl : UiControl<ItemTemplateControl, GenericUiControlPlugin<ItemTemplateControl>>, IObjectWithUiControl
{
    public ItemTemplateControl(object view, object data)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
    {
        View = view;
        Data = data;
    }

    public UiControl GetUiControl(IUiControlService uiControlService, IServiceProvider serviceProvider)
    {
        if (View is Composition.LayoutStackControl stackControl)
        {
            stackControl = stackControl with
                           {
                               Areas = stackControl.ViewElements
                                                   .Select(x => new LayoutArea(x.Area, uiControlService.GetUiControl(x is ViewElementWithView { View: not null } d ? d.View : null)))
                                                   .ToImmutableList()
                           };
            return uiControlService.GetUiControl(stackControl);
        }
        return uiControlService.GetUiControl(View);
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

    public void Deconstruct(out object View, out object DataContext)
    {
        View = this.View;
        DataContext = this.DataContext;
    }
}


