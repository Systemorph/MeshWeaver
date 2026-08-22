using System.Linq;
using System.Reflection;
using Memex.Portal.Shared.Pages;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the production error route to a component that actually serves it.
///
/// <para><b>Why this test exists.</b> The portal wires
/// <c>app.UseExceptionHandler("/Error", createScopeForErrors: true)</c> — in production only — and
/// for a long time <i>nothing in the solution served that path</i>. Every unhandled server-side
/// exception was therefore re-executed onto a route the Blazor router could not match, and the user
/// got a <b>black screen</b>: no message, no status, no request id. Nothing failed to compile and no
/// test went red, because a route written as a string literal in the pipeline and as a page
/// elsewhere has no link between the two — the same failure shape as the EA connect button
/// (see <see cref="EaConnectRouteTest"/>). It was invisible locally too: the handler is registered
/// only when <c>!IsDevelopment()</c>, so developers always saw the developer exception page.</para>
///
/// <para><b>What is asserted.</b> That <see cref="ErrorRoutes.Path"/> — the single constant the
/// pipeline now passes to <c>UseExceptionHandler</c> — is claimed by a routable component in the
/// portal assembly. Not that it compiles; that it is <i>routable</i>.</para>
/// </summary>
public class ErrorPageRouteTest
{
    /// <summary>
    /// Every route template declared by a routable component in the portal assembly, the way the
    /// Blazor <c>Router</c> discovers them: components carrying <see cref="RouteAttribute"/>.
    /// </summary>
    private static string[] ComponentRouteTemplates() =>
        [.. typeof(ErrorRoutes).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(a => a.Template)];

    /// <summary>
    /// The path the production exception handler re-executes onto is served by a real page.
    /// </summary>
    [Fact]
    public void The_exception_handler_path_is_served_by_a_routable_component()
    {
        var templates = ComponentRouteTemplates();

        templates.Should().Contain(ErrorRoutes.Path,
            "UseExceptionHandler re-executes onto this exact path — if no component claims it, "
            + "every production exception renders a blank page instead of an error message");
    }

    /// <summary>
    /// Non-vacuity guard. Without it, a reflection bug that returned everything (or an empty set
    /// compared with a permissive assertion) would pass the test above while proving nothing —
    /// exactly the failure mode that let the blank error page ship in the first place.
    /// </summary>
    [Fact]
    public void A_path_no_component_serves_is_absent_from_the_route_templates()
    {
        var templates = ComponentRouteTemplates();

        templates.Should().NotBeEmpty("component discovery must have found the portal's pages");
        templates.Should().NotContain("/Error-no-component-serves-this",
            "otherwise the registration assertion would pass for any string at all");
    }
}
