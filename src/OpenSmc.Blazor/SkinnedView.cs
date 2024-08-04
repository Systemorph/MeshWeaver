using Microsoft.AspNetCore.Components;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public class SkinnedView<TSkin> : SkinnedView<UiControl, TSkin>
    where TSkin : Skin<TSkin>
{
}
public class SkinnedView<TControl, TSkin> : BlazorView<TControl> 
    where TControl : UiControl
    where TSkin : Skin<TSkin>
{
    [Parameter]
    public TSkin Skin { get; set; }

    protected override void BindData()
    {
        DisposeBindings();
        DataBind<string>(Skin.Class, x => Class = x);
        DataBind<string>(Skin.Style, x => Style = x);
    }
}
