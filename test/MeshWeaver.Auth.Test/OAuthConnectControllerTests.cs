using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Exercises the minimal OAuth server that Claude Desktop / Claude.ai Connectors
/// hit when attaching to the MCP endpoint. The controller is unit-tested directly
/// (no MVC pipeline) — we assert the shapes the MCP SDK depends on:
///   * /.well-known/oauth-authorization-server advertises a registration_endpoint
///   * POST /register accepts RFC 7591 Dynamic Client Registration
///   * /authorize redirects unauthenticated callers to /login
///   * /authorize issues a code + redirects back to the client for an authenticated caller
///   * /token exchanges the code (with PKCE verification) for an mw_ API token
/// The "Couldn't connect" bug the user saw in prod was exactly the missing /register —
/// ASP.NET's catch-all returned a bare 400 with no log line.
/// </summary>
public class OAuthConnectControllerTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private OAuthConnectController CreateController(ClaimsPrincipal? user = null, string host = "memex.test", string scheme = "https")
    {
        var services = new ServiceCollection();
        services.AddSingleton<OAuthCodeStore>();
        services.AddSingleton(new ApiTokenService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()));
        var provider = services.BuildServiceProvider();

        var controller = new OAuthConnectController(provider, NullLogger<OAuthConnectController>.Instance);
        var httpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ClaimsPrincipal AuthenticatedUser(string email = "alice@example.com", string name = "Alice")
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim("preferred_username", email),
        ], authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    // ─── Routing attributes (regression guard) ──────────────────────────────
    // The prod bug was "POST /register returns 400" — not a logic bug in the
    // handler, but a missing handler entirely. Unit tests that call the method
    // directly can't catch that class of regression. These reflection-level
    // checks pin the routes the MCP SDK depends on, so removing an attribute
    // produces a red test instead of a silent 400 in prod.

    [Fact]
    public void RouteAttribute_RegisterClient_IsHttpPostSlashRegister()
    {
        var method = typeof(OAuthConnectController).GetMethod(nameof(OAuthConnectController.RegisterClient))!;
        var attrs = method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false);
        attrs.Should().HaveCount(1, "Claude Desktop relies on POST /register for Dynamic Client Registration");
        ((HttpPostAttribute)attrs[0]).Template.Should().Be("/register");
    }

    [Fact]
    public void RouteAttribute_GetServerMetadata_IsWellKnownPath()
    {
        var method = typeof(OAuthConnectController).GetMethod(nameof(OAuthConnectController.GetServerMetadata))!;
        var attrs = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false);
        attrs.Should().HaveCount(1);
        ((HttpGetAttribute)attrs[0]).Template.Should().Be("/.well-known/oauth-authorization-server");
    }

    [Fact]
    public void RouteAttribute_Authorize_IsConnectAuthorize()
    {
        var method = typeof(OAuthConnectController).GetMethod(nameof(OAuthConnectController.Authorize))!;
        var attrs = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false);
        attrs.Should().HaveCount(1);
        ((HttpGetAttribute)attrs[0]).Template.Should().Be("connect/authorize");
    }

    [Fact]
    public void RouteAttribute_ExchangeToken_IsConnectToken()
    {
        var method = typeof(OAuthConnectController).GetMethod(nameof(OAuthConnectController.ExchangeToken))!;
        var attrs = method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false);
        attrs.Should().HaveCount(1);
        ((HttpPostAttribute)attrs[0]).Template.Should().Be("connect/token");
    }

    // ─── Metadata ────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_AdvertisesRegistrationEndpoint()
    {
        // RFC 7591 §3: authorization servers that support DCR MUST advertise registration_endpoint
        // in their RFC 8414 metadata. Without this, spec-compliant clients skip DCR; Claude Desktop
        // still tries POST /register regardless, but we want the discovery path to work too.
        var result = CreateController().GetServerMetadata() as OkObjectResult;

        result.Should().NotBeNull();
        var meta = result!.Value!;
        var type = meta.GetType();
        type.GetProperty("registration_endpoint")!.GetValue(meta).Should().Be("https://memex.test/register");
        type.GetProperty("authorization_endpoint")!.GetValue(meta).Should().Be("https://memex.test/connect/authorize");
        type.GetProperty("token_endpoint")!.GetValue(meta).Should().Be("https://memex.test/connect/token");
        type.GetProperty("issuer")!.GetValue(meta).Should().Be("https://memex.test/connect");
    }

    [Fact]
    public void Metadata_AdvertisesPkceAndPublicClient()
    {
        // Claude Desktop is a public client with PKCE; metadata must reflect support
        // for code_challenge_method=S256 and token_endpoint_auth_method=none.
        var result = CreateController().GetServerMetadata() as OkObjectResult;

        var meta = result!.Value!;
        var type = meta.GetType();
        var pkceMethods = (string[])type.GetProperty("code_challenge_methods_supported")!.GetValue(meta)!;
        var authMethods = (string[])type.GetProperty("token_endpoint_auth_methods_supported")!.GetValue(meta)!;
        pkceMethods.Should().Contain("S256");
        authMethods.Should().Contain("none");
    }

    // ─── /register (Dynamic Client Registration) ─────────────────────────────

    [Fact]
    public void Register_WithValidRequest_Returns201AndClientId()
    {
        // Regression: in prod /register was returning 400 because there was no handler.
        // This asserts the minimum RFC 7591 contract the MCP SDK relies on.
        var req = new ClientRegistrationRequest
        {
            ClientName = "Claude Desktop",
            RedirectUris = ["https://claude.ai/callback"],
            GrantTypes = ["authorization_code"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
        };

        var result = CreateController().RegisterClient(req) as ObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(StatusCodes.Status201Created);
        var body = result.Value.Should().BeOfType<ClientRegistrationResponse>().Subject;
        body.ClientId.Should().NotBeNullOrWhiteSpace();
        body.ClientName.Should().Be("Claude Desktop");
        body.RedirectUris.Should().ContainSingle().Which.Should().Be("https://claude.ai/callback");
        body.TokenEndpointAuthMethod.Should().Be("none");
        body.ClientIdIssuedAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Register_EachCallReturnsUniqueClientId()
    {
        // Claude Desktop re-registers per install; we don't persist, but IDs must not collide.
        var req = new ClientRegistrationRequest { RedirectUris = ["https://claude.ai/callback"] };
        var controller = CreateController();

        var r1 = ((ObjectResult)controller.RegisterClient(req)!).Value as ClientRegistrationResponse;
        var r2 = ((ObjectResult)controller.RegisterClient(req)!).Value as ClientRegistrationResponse;

        r1!.ClientId.Should().NotBe(r2!.ClientId);
    }

    [Fact]
    public void Register_MissingBody_Returns400InvalidClientMetadata()
    {
        var result = CreateController().RegisterClient(null) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value)
            .Should().Be("invalid_client_metadata");
    }

    [Fact]
    public void Register_MissingRedirectUris_Returns400InvalidRedirectUri()
    {
        // Edge: RFC 7591 requires redirect_uris for every grant type we support.
        var req = new ClientRegistrationRequest { ClientName = "X", RedirectUris = null };

        var result = CreateController().RegisterClient(req) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value)
            .Should().Be("invalid_redirect_uri");
    }

    [Fact]
    public void Register_EmptyRedirectUris_Returns400InvalidRedirectUri()
    {
        var req = new ClientRegistrationRequest { ClientName = "X", RedirectUris = [] };

        var result = CreateController().RegisterClient(req) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value)
            .Should().Be("invalid_redirect_uri");
    }

    [Fact]
    public void Register_DefaultsGrantTypesAndResponseTypes()
    {
        // Client-facing SDKs sometimes omit grant/response types and expect RFC 7591 defaults.
        var req = new ClientRegistrationRequest
        {
            ClientName = "Minimal",
            RedirectUris = ["https://c/cb"],
        };

        var result = CreateController().RegisterClient(req) as ObjectResult;

        var body = result!.Value.Should().BeOfType<ClientRegistrationResponse>().Subject;
        body.GrantTypes.Should().Contain("authorization_code");
        body.ResponseTypes.Should().Contain("code");
    }

    // ─── /authorize ──────────────────────────────────────────────────────────

    [Fact]
    public void Authorize_Unauthenticated_RedirectsToLogin()
    {
        // "we should go to our login screen" — this is the step the user was waiting for.
        // Unauthenticated calls to /authorize must 302 to /login?returnUrl=... so the portal
        // can show the login page and bounce back.
        var controller = CreateController();  // no user
        controller.ControllerContext.HttpContext.Request.Path = "/connect/authorize";
        controller.ControllerContext.HttpContext.Request.QueryString = new QueryString(
            "?response_type=code&client_id=c1&redirect_uri=https%3A%2F%2Fclaude.ai%2Fcb&state=xyz&code_challenge=abc&code_challenge_method=S256");

        var result = controller.Authorize(
            response_type: "code",
            client_id: "c1",
            redirect_uri: "https://claude.ai/cb",
            state: "xyz",
            scope: "mcp",
            code_challenge: "abc",
            code_challenge_method: "S256") as RedirectResult;

        result.Should().NotBeNull();
        result!.Url.Should().StartWith("/login?returnUrl=");
        result.Url.Should().Contain(Uri.EscapeDataString("https://memex.test/connect/authorize"));
        result.Url.Should().Contain(Uri.EscapeDataString("client_id=c1"));
    }

    [Fact]
    public void Authorize_Authenticated_IssuesCodeAndRedirectsToClient()
    {
        var controller = CreateController(AuthenticatedUser());

        var result = controller.Authorize(
            response_type: "code",
            client_id: "c1",
            redirect_uri: "https://claude.ai/cb",
            state: "nonce-42",
            scope: "mcp",
            code_challenge: null,
            code_challenge_method: null) as RedirectResult;

        result.Should().NotBeNull();
        result!.Url.Should().StartWith("https://claude.ai/cb?code=");
        result.Url.Should().Contain("&state=nonce-42");
    }

    [Fact]
    public void Authorize_AuthenticatedNoState_RedirectsWithoutStateParam()
    {
        // Edge: some minimal clients don't pass state. Must not emit state= with empty value.
        var controller = CreateController(AuthenticatedUser());

        var result = controller.Authorize(
            response_type: "code",
            client_id: "c1",
            redirect_uri: "https://claude.ai/cb",
            state: null,
            scope: null,
            code_challenge: null,
            code_challenge_method: null) as RedirectResult;

        result!.Url.Should().StartWith("https://claude.ai/cb?code=");
        result.Url.Should().NotContain("state=");
    }

    [Fact]
    public void Authorize_UnsupportedResponseType_Returns400()
    {
        var controller = CreateController(AuthenticatedUser());

        var result = controller.Authorize(
            response_type: "token",
            client_id: "c1",
            redirect_uri: "https://claude.ai/cb",
            state: null, scope: null, code_challenge: null, code_challenge_method: null) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("unsupported_response_type");
    }

    [Fact]
    public void Authorize_MissingClientId_Returns400()
    {
        var controller = CreateController(AuthenticatedUser());

        var result = controller.Authorize(
            response_type: "code",
            client_id: "",
            redirect_uri: "https://claude.ai/cb",
            state: null, scope: null, code_challenge: null, code_challenge_method: null) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_request");
    }

    [Fact]
    public void Authorize_AuthenticatedWithoutEmail_Returns400()
    {
        // Degenerate: a cookie claim set without email/preferred_username — we can't issue
        // an API token for an unknown user.
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "No Email")], authenticationType: "Test");
        var controller = CreateController(new ClaimsPrincipal(identity));

        var result = controller.Authorize(
            response_type: "code",
            client_id: "c1",
            redirect_uri: "https://claude.ai/cb",
            state: null, scope: null, code_challenge: null, code_challenge_method: null) as BadRequestObjectResult;

        result.Should().NotBeNull();
        var err = result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value);
        err.Should().Be("invalid_request");
    }

    // ─── /token (with PKCE) ──────────────────────────────────────────────────

    [Fact]
    public async Task Token_FullFlowWithPkce_IssuesMwToken()
    {
        // End-to-end happy path the way Claude Desktop actually runs it:
        //   1. register → client_id
        //   2. authorize (with PKCE S256 challenge) → code
        //   3. token (with verifier) → mw_ access token
        var controller = CreateController(AuthenticatedUser("bob@example.com", "Bob"));

        // Step 1: register.
        var reg = ((ObjectResult)controller.RegisterClient(new ClientRegistrationRequest
        {
            ClientName = "Claude Desktop",
            RedirectUris = ["https://claude.ai/cb"],
        })!).Value as ClientRegistrationResponse;
        reg!.ClientId.Should().NotBeNullOrEmpty();

        // Step 2: PKCE S256 challenge derived from a random verifier.
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var authRedirect = (RedirectResult)controller.Authorize(
            response_type: "code",
            client_id: reg.ClientId,
            redirect_uri: "https://claude.ai/cb",
            state: "s1",
            scope: "mcp",
            code_challenge: challenge,
            code_challenge_method: "S256")!;

        var code = Uri.UnescapeDataString(
            authRedirect.Url["https://claude.ai/cb?code=".Length..authRedirect.Url.IndexOf("&state=")]);

        // Step 3: exchange code for token.
        var tokenResult = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = code,
            client_id = reg.ClientId,
            redirect_uri = "https://claude.ai/cb",
            code_verifier = verifier,
        }) as OkObjectResult;

        tokenResult.Should().NotBeNull();
        var body = tokenResult!.Value!;
        var t = body.GetType();
        var accessToken = (string)t.GetProperty("access_token")!.GetValue(body)!;
        accessToken.Should().StartWith("mw_");
        t.GetProperty("token_type")!.GetValue(body).Should().Be("Bearer");
        ((int)t.GetProperty("expires_in")!.GetValue(body)!).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Token_WithWrongPkceVerifier_Returns400InvalidGrant()
    {
        var controller = CreateController(AuthenticatedUser());
        var verifier = "the-right-verifier-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var authRedirect = (RedirectResult)controller.Authorize(
            "code", "c1", "https://claude.ai/cb", "s", "mcp", challenge, "S256")!;
        var code = Uri.UnescapeDataString(
            authRedirect.Url["https://claude.ai/cb?code=".Length..authRedirect.Url.IndexOf("&state=")]);

        var result = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = code,
            client_id = "c1",
            redirect_uri = "https://claude.ai/cb",
            code_verifier = "the-WRONG-verifier-yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy",
        }) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Token_WithMismatchedClientId_Returns400InvalidGrant()
    {
        // RFC 6749 §4.1.3: token endpoint MUST verify the code was issued to the same client.
        var controller = CreateController(AuthenticatedUser());

        var authRedirect = (RedirectResult)controller.Authorize(
            "code", "client-A", "https://claude.ai/cb", null, null, null, null)!;
        var code = authRedirect.Url["https://claude.ai/cb?code=".Length..];

        var result = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = Uri.UnescapeDataString(code),
            client_id = "client-B",
            redirect_uri = "https://claude.ai/cb",
        }) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Token_WithMismatchedRedirectUri_Returns400InvalidGrant()
    {
        var controller = CreateController(AuthenticatedUser());

        var authRedirect = (RedirectResult)controller.Authorize(
            "code", "c1", "https://claude.ai/cb", null, null, null, null)!;
        var code = authRedirect.Url["https://claude.ai/cb?code=".Length..];

        var result = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = Uri.UnescapeDataString(code),
            client_id = "c1",
            redirect_uri = "https://other.example.com/cb",
        }) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Token_CodeReplay_Returns400InvalidGrant()
    {
        // Codes are single-use. Second exchange of the same code must fail.
        var controller = CreateController(AuthenticatedUser());

        var authRedirect = (RedirectResult)controller.Authorize(
            "code", "c1", "https://claude.ai/cb", null, null, null, null)!;
        var code = Uri.UnescapeDataString(authRedirect.Url["https://claude.ai/cb?code=".Length..]);

        var first = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code", code = code,
            client_id = "c1", redirect_uri = "https://claude.ai/cb",
        });
        first.Should().BeOfType<OkObjectResult>();

        var second = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code", code = code,
            client_id = "c1", redirect_uri = "https://claude.ai/cb",
        }) as BadRequestObjectResult;

        second.Should().NotBeNull();
        second!.Value!.GetType().GetProperty("error")!.GetValue(second.Value).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Token_UnsupportedGrantType_Returns400()
    {
        var controller = CreateController();

        var result = await controller.ExchangeToken(new TokenRequest { grant_type = "client_credentials" })
            as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("unsupported_grant_type");
    }

    [Fact]
    public async Task Token_MissingFields_Returns400InvalidRequest()
    {
        var controller = CreateController();

        var result = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = "",
            client_id = "c1",
            redirect_uri = "https://claude.ai/cb",
        }) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_request");
    }

    [Fact]
    public async Task Token_UnknownCode_Returns400InvalidGrant()
    {
        var controller = CreateController();

        var result = await controller.ExchangeToken(new TokenRequest
        {
            grant_type = "authorization_code",
            code = "this-code-was-never-issued",
            client_id = "c1",
            redirect_uri = "https://claude.ai/cb",
        }) as BadRequestObjectResult;

        result.Should().NotBeNull();
        result!.Value!.GetType().GetProperty("error")!.GetValue(result.Value).Should().Be("invalid_grant");
    }
}
