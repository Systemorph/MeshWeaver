#nullable enable
using Microsoft.AspNetCore.Components;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Convenience base for skinned views whose control type is the generic <c>UiControl</c>.
/// Derives from <c>SkinnedView&lt;UiControl, TSkin, TView&gt;</c>.
/// </summary>
/// <typeparam name="TSkin">The skin type that carries theming and layout configuration.</typeparam>
/// <typeparam name="TView">The concrete Blazor component type that derives from this class.</typeparam>
public class SkinnedView<TSkin, TView> : SkinnedView<UiControl, TSkin, TView>
    where TSkin : Skin<TSkin>
    where TView : SkinnedView<TSkin, TView>
{
}
/// <summary>
/// Base view for controls that are rendered with an explicit skin parameter. The skin is
/// passed as a Blazor <c>[Parameter]</c> and carries theming data; the control view-model
/// is received through the standard <c>BlazorView</c> binding pipeline.
/// </summary>
/// <typeparam name="TControl">The UI control view-model type, constrained to <c>IUiControl</c>.</typeparam>
/// <typeparam name="TSkin">The skin type that carries theming and layout configuration.</typeparam>
/// <typeparam name="TView">The concrete Blazor component type that derives from this class.</typeparam>
public class SkinnedView<TControl, TSkin, TView> : BlazorView<TControl, TView>
    where TControl : IUiControl
    where TSkin : Skin<TSkin>
    where TView : SkinnedView<TControl, TSkin, TView>
{
    /// <summary>The skin instance passed from the parent that controls theming and layout.</summary>
    [Parameter]
    public required TSkin Skin { get; set; }

    /// <summary>
    /// Disposes existing bindings, then binds <c>Id</c>, <c>Class</c>, and <c>Style</c>
    /// from the control view-model. Skips the base <c>BindData</c> call because skin-driven
    /// views manage their own binding lifecycle.
    /// </summary>
    protected override void BindData()
    {
        DisposeBindings();
        DataBind(ViewModel.Id, x => x.Id);
        DataBind(ViewModel.Class, x => x.Class);
        DataBind(ViewModel.Style, x => x.Style);
    }
}
