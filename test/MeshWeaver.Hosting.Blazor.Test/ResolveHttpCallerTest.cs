using System.Security.Claims;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pins <see cref="UserContextMiddleware.ResolveHttpCaller"/> — the identity source for HTTP
/// surfaces the user-context middleware deliberately skips (<c>/static/…</c>). Those endpoints
/// post hub messages, and a post with no <c>AccessContext</c> is refused by the never-null
/// PostPipeline guard — invisible at one replica (local delivery), fatal cross-silo: at two
/// replicas ~half of all /static requests died with "AccessContext must never be null" (#694).
/// The resolver must therefore NEVER return null, never widen anonymous rights, and never emit
/// an email-shaped ObjectId (the mis-partitioning trap).
/// </summary>
public class ResolveHttpCallerTest
{
    // The resolver needs only a service provider (an absent UserIdentityCache means
    // "no mesh-user resolution", which is a legal production state on a fresh host).
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Anonymous_YieldsTheWellKnownAnonymousContext_NeverNull()
    {
        var services = Services();
        var ctx = UserContextMiddleware.ResolveHttpCaller(null, services);
        Assert.NotNull(ctx);
        Assert.Equal(WellKnownUsers.Anonymous, ctx.ObjectId);

        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx2 = UserContextMiddleware.ResolveHttpCaller(unauthenticated, services);
        Assert.Equal(WellKnownUsers.Anonymous, ctx2.ObjectId);
    }

    [Fact]
    public void Authenticated_CarriesTheUsername_NeverTheEmailShape()
    {
        var services = Services();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("preferred_username", "rbuergi@systemorph.com"),
            new Claim(ClaimTypes.Email, "rbuergi@systemorph.com"),
            new Claim(ClaimTypes.Name, "Roland"),
        ], authenticationType: "test"));

        var ctx = UserContextMiddleware.ResolveHttpCaller(principal, services);
        // The email local-part is the mesh partition key; an email-shaped ObjectId routes
        // to a parallel partition owning none of the user's data.
        Assert.Equal("rbuergi", ctx.ObjectId);
        Assert.DoesNotContain("@", ctx.ObjectId);
    }

    [Fact]
    public void ResolutionFaults_FailClosedToAnonymous()
    {
        // A principal whose identity claims are pathological must never produce null or throw —
        // the static pipeline turns any throw into a 500 for the asset. Fail closed: anonymous.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "not-an-email")], authenticationType: "test"));
        var ctx = UserContextMiddleware.ResolveHttpCaller(principal, Services());
        Assert.NotNull(ctx);
    }
}
