using MeshWeaver.Layout;
using Orientation = Microsoft.FluentUI.AspNetCore.Components.Orientation;

namespace MeshWeaver.Blazor
{
    public class ContainerView<TViewModel, TView> : BlazorView<TViewModel, TView>
        where TViewModel: ContainerControl<TViewModel>
        where TView : ContainerView<TViewModel, TView>
    {

    }
}
