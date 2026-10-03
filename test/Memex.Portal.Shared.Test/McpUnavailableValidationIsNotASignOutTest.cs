using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A token the mesh could not look up for a moment is NOT a sign-out</b> (maintainer,
/// 2026-09-30: <i>"this must not happen"</i>).
///
/// <para>The ApiToken handler flags a request whose validation could not RUN, and its own
/// challenge answers <c>503 + Retry-After</c> (issue #637). But the MCP endpoint's policy
/// challenges on the MCP scheme, which wrote <c>401 + WWW-Authenticate: Bearer</c> itself — so
/// on /mcp the 503 override never ran, and an MCP client read a validation stall as "sign in
/// again". Driven here over real HTTP with the production wiring
/// (<see cref="McpAuthenticationExtensions.AddMcpAuthentication"/>); only the ApiToken handler's
/// AUTHENTICATE is stubbed, so the challenge under test is the real one.</para>
/// </summary>
public class McpUnavailableValidationIsNotASignOutTest
{
    /// <summary>Validation could not run → 503, Retry-After, and NO auth challenge.</summary>
    [Fact]
    public async Task UnavailableValidation_On_Mcp_Answers503_WithoutAnAuthChallenge()
    {
        await using var app = BuildApp(typeof(UnavailableTokenHandler));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await Post(app);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"), "a retryable answer must say when to retry");
        Assert.False(response.Headers.Contains("WWW-Authenticate"),
            "an auth challenge on a validation stall is what makes an MCP client discard a valid token and re-authenticate");
    }

    /// <summary>
    /// The positive control: a genuinely INVALID token must still get the discovery challenge —
    /// otherwise the test above could pass on a pipeline that never challenges at all.
    /// </summary>
    [Fact]
    public async Task InvalidToken_On_Mcp_StillAnswers401_WithTheBearerChallenge()
    {
        await using var app = BuildApp(typeof(InvalidTokenHandler));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await Post(app);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("WWW-Authenticate"),
            "a definitive invalid token must keep the MCP discovery challenge");
    }

    private static async Task<HttpResponseMessage> Post(WebApplication app)
    {
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "mw_test");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static WebApplication BuildApp(System.Type apiTokenHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddMcpAuthentication();
        // Swap ONLY the handler type behind the ApiToken scheme; every forward, selector and
        // challenge stays the production registration.
        builder.Services.Configure<AuthenticationOptions>(o =>
            o.SchemeMap[ApiTokenAuthenticationHandler.SchemeName].HandlerType = apiTokenHandler);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/mcp", () => "mcp").RequireAuthorization(Memex.Portal.Shared.Authentication.McpAuthenticationExtensions.PolicyName);
        return app;
    }

    /// <summary>Authenticates exactly as the real handler does when the store did not answer.</summary>
    private sealed class UnavailableTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder,
        System.IServiceProvider serviceProvider)
        : ApiTokenAuthenticationHandler(options, loggerFactory, encoder, serviceProvider)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Context.Items[ValidationUnavailableItemKey] = "token store did not answer";
            return Task.FromResult(AuthenticateResult.Fail("API token validation is temporarily unavailable (retryable)"));
        }
    }

    /// <summary>Authenticates exactly as the real handler does for a definitive invalid token.</summary>
    private sealed class InvalidTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder,
        System.IServiceProvider serviceProvider)
        : ApiTokenAuthenticationHandler(options, loggerFactory, encoder, serviceProvider)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("Invalid or expired API token"));
    }
}
