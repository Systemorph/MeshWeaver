using MeshWeaver.Domain;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;


public abstract class NavItemView<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : IMenuItem
    where TView : NavItemView<TViewModel, TView>

{
    protected string? Href { get; set; }
    protected string? Title { get; set; }
    protected Icon? Icon { get; set; }

    /// <summary>
    /// Cascading parameter indicating whether this component is rendered inside a dropdown menu.
    /// When true, the component should render as a styled menu item.
    /// </summary>
    [CascadingParameter(Name = "IsInMenuContext")]
    protected bool IsInMenuContext { get; set; }

    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind(ViewModel.Title, x => x.Title);
            DataBind(ViewModel.Url, x => x.Href);
            DataBind(ViewModel.Icon, x => x.Icon);
        }
    }

}
