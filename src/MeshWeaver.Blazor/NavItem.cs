using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;


public abstract class NavItem<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : NavItemControl<TViewModel>
where TView:NavItem<TViewModel, TView>

{
    protected string Href { get; set; }
    protected string Title { get; set; }
    protected Application.Styles.Icon Icon { get; set; }

    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind(ViewModel.Data, x => x.Title );
            DataBind(ViewModel.Href, x => x.Href);
            DataBind(ViewModel.Icon, x => x.Icon);
        }
    }

}
