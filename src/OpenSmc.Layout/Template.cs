using System.Collections.Immutable;
using System.Linq.Expressions;
using OpenSmc.Layout.DataBinding;

namespace OpenSmc.Layout;


//result into ui control with DataBinding set
public record ItemTemplateControl : UiControl<ItemTemplateControl>
{
    public static string ViewArea = nameof(View);

    public ItemTemplateControl(UiControl view, string dataContext)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
    {
        View = view;
        DataContext = dataContext;
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

    public UiControl View { get; init; }

}


