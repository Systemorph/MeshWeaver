using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;


public abstract class NavItemView<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : IMenuItem
    where TView : NavItemView<TViewModel, TView>

{
    protected string Href { get; set; }
    protected string Title { get; set; }
    protected Icon Icon { get; set; }

    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind(ViewModel.Title, x => x.Title);
            DataBind(ViewModel.Href, x => x.Href);
            DataBind(ViewModel.Icon, x => x.Icon);
        }
    }

}
