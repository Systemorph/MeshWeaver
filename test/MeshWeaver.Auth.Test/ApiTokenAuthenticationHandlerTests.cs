using System.Linq;
using System.Security.Claims;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Regression for the 2026-05-01 distributed-mode permission denial.
/// Every API-token request was denied even when the token's
/// <see cref="ApiToken.Roles"/> in the database contained "Admin",
/// because <see cref="ApiTokenAuthenticationHandler"/> built the
/// principal's claim list from the token's user/email/label fields but
/// never copied <see cref="ApiToken.Roles"/> across as
/// <see cref="ClaimTypes.Role"/> claims. UserContextMiddleware then
/// stamped an empty <c>AccessContext.Roles</c>, and the
/// SecurityService claim-based path couldn't grant.
/// </summary>
public class ApiTokenAuthenticationHandlerTests
{
    [Fact]
    public void BuildClaims_TokenWithRoles_StampsEachRoleAsRoleClaim()
    {
        var token = new ApiToken
        {
            UserId = "alice",
            UserName = "Alice",
            UserEmail = "alice@example.com",
            Label = "Test Token",
            TokenHash = "deadbeef",
            Roles = ["Admin", "Editor"],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Admin",
            "every entry in ApiToken.Roles must mint a ClaimTypes.Role claim");
        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Editor",
            "multiple roles must each become a separate Role claim");
    }

    [Fact]
    public void BuildClaims_TokenWithoutRoles_HasNoRoleClaims()
    {
        var token = new ApiToken
        {
            UserId = "bob",
            UserName = "Bob",
            UserEmail = "bob@example.com",
            Label = "No-roles token",
            TokenHash = "deadbeef",
            Roles = [],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().NotContain(c => c.Type == ClaimTypes.Role,
            "an empty Roles list must not introduce any Role claims");
    }

    [Fact]
    public void BuildClaims_AlwaysIncludesIdentityClaims()
    {
        var token = new ApiToken
        {
            UserId = "carol",
            UserName = "Carol",
            UserEmail = "carol@example.com",
            Label = "Identity test",
            TokenHash = "deadbeef",
            Roles = [],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().Contain(c => c.Type == "preferred_username" && c.Value == "carol");
        claims.Should().Contain(c => c.Type == ClaimTypes.Name && c.Value == "Carol");
        claims.Should().Contain(c => c.Type == ClaimTypes.Email && c.Value == "carol@example.com");
        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "carol");
        claims.Should().Contain(c => c.Type == "token_label" && c.Value == "Identity test");
    }

    [Fact]
    public void BuildClaims_DropsEmptyRoleEntries()
    {
        var token = new ApiToken
        {
            UserId = "dave",
            UserName = "Dave",
            UserEmail = "dave@example.com",
            Label = "Empty-role test",
            TokenHash = "deadbeef",
            Roles = ["Admin", "", "Viewer"],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "Admin", "Viewer" },
                System.Text.Json.JsonSerializerOptions.Default,
                because: "blank role entries must be silently dropped, never stamped as empty Role claims");
    }
}
