using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the auth-context guards in <see cref="VirtualUserMiddleware"/>: VUser
/// provisioning must only fire for genuinely-anonymous requests. Logged-in
/// users (either via <c>HttpContext.User.Identity.IsAuthenticated == true</c>
/// or via a real-user AccessContext already set by
/// <c>UserContextMiddleware</c>) skip the whole block.
///
/// <para>Why these tests matter: the prod 2026-05-24 thread-page crash
/// (<c>No handler found for message type CreateNodeRequest in
/// portal/anonymous</c>) was caused by a legitimately-authed user whose
/// HttpContext.User happened to be unauthenticated for that one
/// pipeline pass (Bearer-token resolution happened later inside
/// UserContextMiddleware). VirtualUserMiddleware then provisioned a guest
/// VUser node and the CreateNodeRequest blew up. The fix is two-layered:
/// (1) skip VUser when AccessService.Context already carries a real user,
/// (2) target the mesh hub (not the portal hub) for the create-node post.
/// These tests pin (1); the integration test in MeshWeaver.Hosting.Blazor.Test
/// pins (2).</para>
/// </summary>
public class VirtualUserMiddlewareAuthContextTest
{
    [Fact]
    public async Task AuthenticatedUserViaHttpContext_SkipsVUserBlock_AndCallsNext()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            Request = { Path = "/Systemorph/_Thread/foo" },
            User = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"))
        };

        // No RequestServices — if VirtualUserMiddleware tried to resolve
        // PortalApplication it would NRE. The IsAuthenticated guard must
        // short-circuit before that point.
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(
            "an authenticated HttpContext.User must skip the VUser block entirely " +
            "and pass through to the next middleware");
    }

    [Fact]
    public async Task UnauthenticatedHttpContext_EntersVUserBlock_ThrowsOnMissingPortalApplication()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext
        {
            Request = { Path = "/Systemorph/_Thread/foo" }
            // No User — IsAuthenticated == false; falls into VUser provisioning.
        };

        // With no PortalApplication registered, the VUser path NREs / DI-throws.
        // Asserting the throw proves we DID enter the block (vs. the guard above
        // short-circuiting). The integration test in MeshWeaver.Hosting.Blazor.Test
        // covers the happy path with a real DI scope.
        await FluentActions.Invoking(() => middleware.InvokeAsync(context))
            .Should().ThrowAsync<System.Exception>(
                "unauthenticated requests must enter the VUser provisioning block");
    }
}
