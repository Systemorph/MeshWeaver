using Microsoft.AspNetCore.Components;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public class SkinnedView<TSkin> : SkinnedView<UiControl, TSkin>
{
}
public class SkinnedView<TControl, TSkin> : BlazorView<TControl> where TControl : UiControl
{
    [Parameter]
    public TSkin Skin { get; set; }

}
