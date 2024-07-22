using OpenSmc.Data;

namespace OpenSmc.Layout;


//result into ui control with DataBinding set
public record ItemTemplateControl(UiControl View, object Data) :
    UiControl<ItemTemplateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public static string ViewArea = nameof(View);


    public Orientation? Orientation { get; init; }

    public bool Wrap { get; init; }

    public ItemTemplateControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };

    public ItemTemplateControl WithWrap(bool wrap) => this with {Wrap = wrap};

    protected override void Dispose()
    {
    }


    public void Deconstruct(out UiControl View, out object Data)
    {
        View = this.View;
        Data = this.Data;
    }
}


