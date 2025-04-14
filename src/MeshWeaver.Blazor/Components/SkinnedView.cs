using Microsoft.AspNetCore.Components;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public class SkinnedView<TSkin, TView> : SkinnedView<UiControl, TSkin, TView>
    where TSkin : Skin<TSkin>
    where TView : SkinnedView<TSkin, TView>
{
}
public class SkinnedView<TControl, TSkin, TView> : BlazorView<TControl, TView>
    where TControl : IUiControl
    where TSkin : Skin<TSkin>
    where TView : SkinnedView<TControl, TSkin, TView>
{
    [Parameter]
    public TSkin Skin { get; set; }

    protected override void BindData()
    {
        DisposeBindings();
        DataBind(ViewModel.Id, x => x.Id);
        DataBind(ViewModel.Class, x => x.Class);
        DataBind(ViewModel.Style, x => x.Style);
    }
}
