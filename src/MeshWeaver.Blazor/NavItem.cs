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
            DataBindProperty(ViewModel.Data, x => x.Title );
            DataBindProperty(ViewModel.Href, x => x.Href);
            DataBindProperty(ViewModel.Icon, x => x.Icon);
        }
    }

}
