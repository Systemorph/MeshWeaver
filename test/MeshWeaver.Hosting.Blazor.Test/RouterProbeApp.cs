using MeshWeaver.Blazor.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Root component hosting the real Blazor Router over the MeshWeaver.Blazor page
/// assembly. Found/NotFound render plain text markers instead of RouteView, so tests
/// observe the ROUTING decision without instantiating the matched page
/// (ApplicationPage needs the full portal DI graph to render).
/// </summary>
public sealed class RouterProbeApp : ComponentBase
{
    /// <summary>Marker emitted when the Router matched a page; followed by the page type name.</summary>
    public const string FoundMarker = "ROUTED-TO:";

    /// <summary>Marker emitted when the Router rendered its NotFound content.</summary>
    public const string NotFoundMarker = "ROUTER-NOT-FOUND";

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Router>(0);
        builder.AddComponentParameter(1, nameof(Router.AppAssembly), typeof(ApplicationPage).Assembly);
        builder.AddComponentParameter(2, nameof(Router.Found),
            (RenderFragment<RouteData>)(routeData => b => b.AddContent(0, $"{FoundMarker}{routeData.PageType.Name}")));
        // Router.NotFound is obsolete in .NET 10 in favor of NotFoundPage, but the
        // portal's Routes.razor still renders its "Sorry, there's nothing at this
        // address." message through NotFound — the probe mirrors that exact surface
        // so the tests observe the same not-found decision the portal shows.
#pragma warning disable CS0618
        builder.AddComponentParameter(3, nameof(Router.NotFound),
            (RenderFragment)(b => b.AddContent(0, NotFoundMarker)));
#pragma warning restore CS0618
        builder.CloseComponent();
    }
}
