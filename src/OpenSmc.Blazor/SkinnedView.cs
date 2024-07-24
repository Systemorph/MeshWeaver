using Microsoft.AspNetCore.Components;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public class SkinnedView<TSkin> : BlazorView<UiControl> 
{
    [Parameter]
    public TSkin Skin { get; set; }

}
