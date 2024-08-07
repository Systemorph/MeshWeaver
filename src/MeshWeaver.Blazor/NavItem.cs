using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;


public abstract class NavItem<TViewModel> : BlazorView<TViewModel>
    where TViewModel : NavItemControl<TViewModel> 
{
    protected string Href { get; set; }
    protected string Title { get; set; }
    protected Application.Styles.Icon Icon { get; set; }

    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind<string>(ViewModel.Data, x => Title = x);
            DataBind<string>(ViewModel.Href, x => Href = x);
            DataBind<Application.Styles.Icon>(ViewModel.Icon, x => Icon = x);
        }
    }

}
