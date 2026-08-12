using System.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Blazor.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the Executive-Assistant consent route to the endpoint that actually serves it.
///
/// <para><b>Why this test exists.</b> The "Connect Microsoft 365" button in the send-document dialog
/// shipped pointing at a hard-coded <c>"/auth/ea/connect"</c> string, and the portal answered
/// <i>"Page not found: 'auth/ea/connect' does not match any registered address pattern."</i> Nothing
/// failed to compile, no test went red, and the button looked perfectly wired. A route expressed as
/// a string literal in one project and as an attribute in another has no link between the two, so
/// the only way it can be verified is to ask the routing table.</para>
///
/// <para><b>What is asserted.</b> That <see cref="EaConsentController.ConnectPath"/> — the single
/// constant every consumer now reads (the dialog through
/// <see cref="MeshWeaver.Mesh.IEmailSender.ConnectAsUserHref"/>, the Executive Assistant's chat
/// link) — names a route that a real ASP.NET pipeline registers. Not that it compiles; that it is
/// <i>routable</i>. A rename that forgets a consumer now fails compilation, and a rename that
/// changes the served path fails here.</para>
/// </summary>
public class EaConnectRouteTest
{
    /// <summary>
    /// Every route pattern the REAL controller registration produces, normalised to a leading slash.
    /// Uses the same <c>AddControllers().AddApplicationPart(...)</c> + <c>MapControllers()</c> pair
    /// the portal composes in <c>MemexConfiguration</c>, so this reflects how the app actually
    /// routes rather than a hand-mapped stand-in. The app is never started — endpoint construction
    /// is all this needs, and it does not instantiate controllers (so no DI graph is required).
    /// </summary>
    private static async Task<string[]> RegisteredRoutePatternsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(EaConsentController).Assembly);

        var app = builder.Build();
        await using (app)
        {
            app.MapControllers();
            return [.. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))];
        }
    }

    /// <summary>
    /// The constant the UI links to is a route this application serves.
    /// </summary>
    [Fact]
    public async Task ConnectPath_is_a_route_the_application_registers()
    {
        var patterns = await RegisteredRoutePatternsAsync();

        patterns.Should().Contain(EaConsentController.ConnectPath,
            "the connect button links to this exact path — if no endpoint claims it, the portal "
            + "answers 'does not match any registered address pattern' and the button 404s");
    }

    /// <summary>
    /// Non-vacuity guard. Without it, an enumeration that silently returned everything (or a
    /// <c>Contain</c> against an over-broad catch-all pattern) would pass the assertion above while
    /// proving nothing — the exact failure mode that let the original bug ship.
    /// </summary>
    [Fact]
    public async Task A_path_no_controller_serves_is_absent_from_the_routing_table()
    {
        var patterns = await RegisteredRoutePatternsAsync();

        patterns.Should().NotBeEmpty("controller discovery must have found the portal's controllers");
        patterns.Should().NotContain("/auth/ea/this-endpoint-does-not-exist",
            "otherwise the registration assertion would pass for any string at all");
    }

    /// <summary>
    /// The OAuth callback is registered from the same constants, so consent can complete. A connect
    /// endpoint that redirects to Microsoft and then has nowhere to land is only half a route.
    /// </summary>
    [Fact]
    public async Task Callback_path_is_registered_alongside_connect()
    {
        var patterns = await RegisteredRoutePatternsAsync();

        patterns.Should().Contain($"/{EaConsentController.BasePath}/{EaConsentController.CallbackAction}");
    }

    /// <summary>
    /// The other half of "a full browser load reaches the server": the Blazor catch-all page must
    /// DECLINE this path. If it matched, a navigation would render the SPA shell (and resolve the
    /// path as a mesh node) instead of hitting the controller — which is exactly the shape of the
    /// original failure, just arriving by a different door.
    /// </summary>
    [Fact]
    public void The_blazor_catch_all_declines_the_connect_path()
    {
        var constraint = new NonfileRouteConstraint();
        var values = new RouteValueDictionary { ["Path"] = EaConsentController.ConnectPath.TrimStart('/') };

        constraint.Match(null, null, "Path", values, RouteDirection.IncomingRequest)
            .Should().BeFalse("the Blazor application page must not swallow the consent endpoint");

        // Non-vacuity: an ordinary mesh path IS matched, so the constraint is not simply refusing
        // everything.
        var meshPath = new RouteValueDictionary { ["Path"] = "TestOrg/SomeDocument" };
        constraint.Match(null, null, "Path", meshPath, RouteDirection.IncomingRequest)
            .Should().BeTrue("ordinary mesh paths must still route to the application page");
    }
}
