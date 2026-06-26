using MeshWeaver.Domain;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;


/// <summary>
/// Abstract base view for navigation-item controls. Binds the URL, title, icon,
/// and active-state properties from the view-model, and exposes whether the item
/// is rendered inside a dropdown menu context.
/// </summary>
/// <typeparam name="TViewModel">The menu-item view-model type, constrained to <c>IMenuItem</c>.</typeparam>
/// <typeparam name="TView">The concrete Blazor component type that derives from this class.</typeparam>
public abstract class NavItemView<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : IMenuItem
    where TView : NavItemView<TViewModel, TView>

{
    /// <summary>The navigation URL for this menu item.</summary>
    protected string? Href { get; set; }
    /// <summary>The display label shown for this menu item.</summary>
    protected string? Title { get; set; }
    /// <summary>The optional icon rendered alongside the menu item label.</summary>
    protected Icon? Icon { get; set; }
    /// <summary>Whether this menu item represents the currently active route.</summary>
    protected bool IsActive { get; set; }

    /// <summary>
    /// Cascading parameter indicating whether this component is rendered inside a dropdown menu.
    /// When true, the component should render as a styled menu item.
    /// </summary>
    [CascadingParameter(Name = "IsInMenuContext")]
    protected bool IsInMenuContext { get; set; }

    /// <summary>
    /// Binds <c>Title</c>, <c>Href</c>, <c>Icon</c>, and (for <c>NavLinkControl</c>) the
    /// <c>IsActive</c> flag from the view-model using the reactive data-binding pipeline.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind(ViewModel.Title, x => x.Title);
            DataBind(ViewModel.Url, x => x.Href);
            DataBind(ViewModel.Icon, x => x.Icon);
            if (ViewModel is NavLinkControl link)
                DataBind(link.IsActive, x => x.IsActive);
        }
    }

}
