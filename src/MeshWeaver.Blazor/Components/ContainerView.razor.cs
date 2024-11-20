using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public class ContainerView<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : ContainerControl<TViewModel>
    where TView : ContainerView<TViewModel, TView>
{

}
