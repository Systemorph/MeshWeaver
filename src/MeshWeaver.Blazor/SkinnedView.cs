using Microsoft.AspNetCore.Components;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;

public class SkinnedView<TSkin, TView> : SkinnedView<UiControl, TSkin, TView>
    where TSkin : Skin<TSkin>
    where TView:SkinnedView<TSkin,TView>
{
}
public class SkinnedView<TControl, TSkin, TView> : BlazorView<TControl, TView> 
    where TControl : UiControl
    where TSkin : Skin<TSkin>
    where TView : SkinnedView<TControl,TSkin, TView>
{
    [Parameter]
    public TSkin Skin { get; set; }

    protected override void BindData()
    {
        DisposeBindings();
        DataBindProperty(Skin.Class, x => x.Class);
        DataBindProperty(Skin.Style, x => x.Style);
    }
}
