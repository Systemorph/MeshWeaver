using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Base Blazor view for controls that derive from <c>ContainerControl</c>.
/// Subclasses render the container's child areas using the standard
/// <c>BlazorView</c> data-binding and stream infrastructure.
/// </summary>
/// <typeparam name="TViewModel">The container control view-model type (must derive from <c>ContainerControl&lt;TViewModel&gt;</c>).</typeparam>
/// <typeparam name="TView">The concrete Blazor view type (must derive from <c>ContainerView&lt;TViewModel, TView&gt;</c>).</typeparam>
public class ContainerView<TViewModel, TView> : BlazorView<TViewModel, TView>
    where TViewModel : ContainerControl<TViewModel>
    where TView : ContainerView<TViewModel, TView>
{

}
