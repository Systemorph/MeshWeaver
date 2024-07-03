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

    public Orientation Orientation { get; init; }
    public bool Wrap { get; init; }
    
    public ItemTemplateControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };

    public ItemTemplateControl WithWrap(bool wrap) => this with {Wrap = wrap};

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


