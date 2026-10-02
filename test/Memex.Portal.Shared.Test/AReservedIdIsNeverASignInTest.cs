using System.Security.Claims;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A sign-in can never BE the System identity, or one of the audience pseudo-users.</b>
///
/// <para>Ingress audit for Plugins#2601. A session's object id is the local part of the
/// provider's <c>preferred_username</c> (or email). The portal accepts multi-tenant sign-in, so an
/// account <c>system-security@attacker.onmicrosoft.com</c> arrived as ObjectId
/// <c>system-security</c>: the System identity, which <c>PermissionEvaluator</c> grants every
/// permission on every partition. The same holds for <c>Public</c>, whose grants every signed-in
/// user inherits, and for a hub-shaped principal.</para>
/// </summary>
public class AReservedIdIsNeverASignInTest
{
    private static ClaimsPrincipal SignIn(string username) =>
        new(new ClaimsIdentity(
            [
                new Claim("preferred_username", username),
                new Claim(ClaimTypes.Email, username),
                new Claim(ClaimTypes.Name, username),
            ],
            authenticationType: "OpenIdConnect"));

    [Theory]
    [InlineData("system-security@attacker.onmicrosoft.com")]
    [InlineData("SYSTEM-SECURITY@attacker.onmicrosoft.com")]
    [InlineData("public@attacker.onmicrosoft.com")]
    [InlineData("anonymous@attacker.onmicrosoft.com")]
    public void AReservedLocalPart_ResolvesToAnonymous(string username)
    {
        UserContextMiddleware.ExtractUserContext(SignIn(username))?.ObjectId
            .Should().NotBeNull("the premise: the claims alone do yield the reserved local part");

        var caller = UserContextMiddleware.ResolveHttpCaller(SignIn(username), new ServiceCollection().BuildServiceProvider());

        caller.ObjectId.Should().Be(WellKnownUsers.Anonymous,
            "a reserved id is never a person's; the sign-in is refused to least privilege, never adopted");
    }

    [Fact]
    public void AnOrdinaryLocalPart_IsTheUser()
    {
        var caller = UserContextMiddleware.ResolveHttpCaller(
            SignIn("jdoe@example.org"), new ServiceCollection().BuildServiceProvider());

        caller.ObjectId.Should().Be("jdoe", "the control: an ordinary sign-in keeps its own id");
    }

    [Theory]
    [InlineData("system-security", true)]
    [InlineData("Public", true)]
    [InlineData("Anonymous", true)]
    [InlineData("mesh/abc", true)]
    [InlineData("node/x", true)]
    [InlineData("jdoe", false)]
    [InlineData(null, false)]
    public void TheReservedIds(string? id, bool reserved) =>
        RequestIdentity.IsReservedPrincipal(id).Should().Be(reserved);
}
