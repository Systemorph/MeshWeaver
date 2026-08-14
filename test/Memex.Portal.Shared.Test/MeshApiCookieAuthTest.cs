using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using Xunit;

// The MCP SDK ships its own McpAuthenticationExtensions in Microsoft.Extensions.DependencyInjection,
// which the `using` above pulls into scope — alias the portal's so every reference is unambiguous.
using PortalMcpAuth = Memex.Portal.Shared.Authentication.McpAuthenticationExtensions;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The credential contract of <c>/api/mesh/*</c> (issue #1477).
///
/// <para><b>Why this exists.</b> A server-side renderer that already holds the visitor's session
/// cookie used to MINT an API token per page render just to read a snapshot back, and every mint
/// writes TWO PERMANENT mesh nodes into that visitor's partition. Ordinary page traffic therefore
/// grew a user's partition without bound. The fix is that a READ accepts the credential the caller
/// already has — and the danger in that fix is doing it one verb too far: a session cookie must
/// never be able to drive a create, patch, delete, script execution or upload.</para>
///
/// <para><b>What is asserted.</b> Three links of one chain, because each is invisible from the
/// others: (1) which POLICY guards which verb, read off the real routing table rather than the
/// source; (2) what each policy AUTHENTICATES with, and that the composite scheme's challenge stays
/// API-shaped; (3) that a real cookie session actually passes the read policy and is actually
/// refused by the Bearer-only one, over a real HTTP pipeline. A regression in any single link would
/// otherwise look exactly like working code.</para>
/// </summary>
public class MeshApiCookieAuthTest
{
    /// <summary>
    /// The READ-ONLY verbs a browser session may drive. Anything not listed here must stay
    /// Bearer-only — extending this set is a security decision, which is precisely why it is
    /// written down as data and asserted exhaustively in both directions.
    /// </summary>
    private static readonly string[] CookieReadableRoutes =
    [
        "/api/mesh/get",
        "/api/mesh/whoami",
        "/api/mesh/render-area",
        "/api/mesh/resolve",
    ];

    /// <summary>
    /// Verbs that MUTATE (or execute, or upload). Named explicitly — not merely "everything else" —
    /// so that deleting a route from the routing table cannot silently satisfy the assertion.
    /// </summary>
    private static readonly string[] MustStayBearerOnly =
    [
        "/api/mesh/create",
        "/api/mesh/update",
        "/api/mesh/patch",
        "/api/mesh/delete",
        "/api/mesh/move",
        "/api/mesh/copy",
        "/api/mesh/recycle",
        "/api/mesh/compile",
        "/api/mesh/execute-script",
        "/api/mesh/mirror",
        "/api/mesh/upload",
    ];

    /// <summary>
    /// Every <c>/api/mesh</c> route the REAL <see cref="MeshApiEndpoints.MapMeshApi"/> registers,
    /// paired with the authorization policy guarding it. The app is never started — endpoint
    /// construction and its metadata are all this needs.
    /// </summary>
    private static async Task<IDictionary<string, string?>> RoutePoliciesAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // The endpoint lambdas take IMessageHub; minimal-API parameter binding must see it as a
        // SERVICE (otherwise it is inferred as a second JSON body parameter and mapping throws).
        // Never resolved — no endpoint is invoked here.
        builder.Services.AddSingleton<IMessageHub>(_ => null!);

        var app = builder.Build();
        await using (app)
        {
            app.MapMeshApi();
            return ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToDictionary(
                    e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                    e => e.Metadata.GetMetadata<IAuthorizeData>()?.Policy,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The read verbs an SSR page render needs — and ONLY those — accept a session cookie.
    /// </summary>
    [Fact]
    public async Task Only_the_read_verbs_carry_the_cookie_or_bearer_policy()
    {
        var policies = await RoutePoliciesAsync();

        policies.Should().NotBeEmpty("MapMeshApi must have registered its routes");

        foreach (var route in CookieReadableRoutes)
        {
            policies.Keys.Should().Contain(route, "the route this test guards must actually exist");
            policies[route].Should().Be(
                PortalMcpAuth.ReadPolicyName,
                "{0} is what an SSR render reads; requiring a Bearer token here is what forced a "
                + "token mint (and two permanent ApiToken nodes) per page view", route);
        }

        var unexpectedlyCookieReadable = policies
            .Where(kv => kv.Value == PortalMcpAuth.ReadPolicyName)
            .Select(kv => kv.Key)
            .Except(CookieReadableRoutes, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        unexpectedlyCookieReadable.Should().BeEmpty(
            "a verb reachable with nothing but the browser's session cookie must be a pure READ — "
            + "add it to CookieReadableRoutes deliberately, or leave it Bearer-only");
    }

    /// <summary>
    /// Every mutating verb still demands a Bearer token. This is the half that keeps the fix from
    /// becoming a CSRF surface.
    /// </summary>
    [Fact]
    public async Task Every_mutating_verb_stays_bearer_only()
    {
        var policies = await RoutePoliciesAsync();

        foreach (var route in MustStayBearerOnly)
        {
            policies.Keys.Should().Contain(route,
                "the route set this test guards must actually exist — a renamed or deleted verb "
                + "would otherwise pass by absence");
            policies[route].Should().Be(
                PortalMcpAuth.PolicyName,
                "{0} mutates; a session cookie must never be sufficient to drive it", route);
        }
    }

    /// <summary>
    /// The policies authenticate what they claim to, and the composite scheme picks its inner
    /// scheme off what the request actually presents — while CHALLENGING like an API in every case.
    /// A cookie challenge here would answer an unauthenticated API call with <c>302 → /login</c>
    /// and an HTML page, which is the failure <c>McpAuthenticationExtensions</c> exists to prevent.
    /// </summary>
    [Fact]
    public async Task The_read_policy_accepts_cookie_or_bearer_but_always_challenges_as_an_api()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        AddPortalAuth(builder.Services);

        var app = builder.Build();
        await using (app)
        {
            var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();

            var readPolicy = await provider.GetPolicyAsync(PortalMcpAuth.ReadPolicyName);
            readPolicy.Should().NotBeNull();
            readPolicy!.AuthenticationSchemes.Should().Equal(PortalMcpAuth.CookieOrBearerScheme);

            // The Bearer-only policy must not have gained the cookie scheme.
            var mcpPolicy = await provider.GetPolicyAsync(PortalMcpAuth.PolicyName);
            mcpPolicy.Should().NotBeNull();
            mcpPolicy!.AuthenticationSchemes.Should().Equal(McpAuthenticationDefaults.AuthenticationScheme);

            var options = app.Services.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>()
                .Get(PortalMcpAuth.CookieOrBearerScheme);

            options.ForwardChallenge.Should().Be(McpAuthenticationDefaults.AuthenticationScheme,
                "an unauthenticated API caller must get 401 + WWW-Authenticate, never a redirect to "
                + "the HTML login page");

            options.ForwardDefaultSelector.Should().NotBeNull();
            options.ForwardDefaultSelector!(ContextWith("Bearer mw_something"))
                .Should().Be(McpAuthenticationDefaults.AuthenticationScheme);
            options.ForwardDefaultSelector(ContextWith(null))
                .Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static HttpContext ContextWith(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        return context;
    }

    private const string ReadRoute = "/probe/read";
    private const string MutateRoute = "/probe/mutate";
    private const string SignInRoute = "/probe/signin";

    /// <summary>
    /// The portal's authentication composition, reduced to what these policies depend on: the
    /// unified cookie scheme as the default, plus the real <see cref="McpAuthenticationExtensions"/>
    /// registration on top of it.
    /// </summary>
    private static void AddPortalAuth(IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options => options.Cookie.Name = "MemexAuth");
        services.AddMcpAuthentication();
    }

    /// <summary>
    /// A real HTTP pipeline carrying both policies on stub routes: the routing test above pins
    /// which verb gets which policy, and this pins what each policy DOES.
    /// </summary>
    private static WebApplication BuildProbeApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        AddPortalAuth(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(SignInRoute, (HttpContext http) =>
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "rbuergi"), new Claim("preferred_username", "rbuergi")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            return http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }).AllowAnonymous();

        app.MapGet(ReadRoute, () => "read")
            .RequireAuthorization(PortalMcpAuth.ReadPolicyName);
        app.MapGet(MutateRoute, () => "mutate")
            .RequireAuthorization(PortalMcpAuth.PolicyName);
        return app;
    }

    /// <summary>
    /// Signs in through the pipeline and returns the session cookie the portal issued, so the
    /// assertions below run against a REAL cookie principal rather than a stand-in scheme.
    /// </summary>
    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.GetAsync(SignInRoute, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The point of the whole change: a signed-in browser session is enough to READ, so a server
    /// renderer holding that cookie never has to mint a credential to fetch a snapshot.
    /// </summary>
    [Fact]
    public async Task A_session_cookie_authorizes_a_read()
    {
        var app = BuildProbeApp();
        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var cookie = await SignInAsync(client);

            using var response = await GetAsync(client, ReadRoute, cookie);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// …and it is enough for NOTHING else. The same cookie, the same pipeline, a mutating verb's
    /// policy: refused.
    /// </summary>
    [Fact]
    public async Task The_same_session_cookie_does_not_authorize_a_mutating_verb()
    {
        var app = BuildProbeApp();
        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var cookie = await SignInAsync(client);

            using var response = await GetAsync(client, MutateRoute, cookie);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "accepting a cookie on a write verb would turn every mutating mesh operation into a "
                + "cross-site-request-forgery target");
        }
    }

    /// <summary>
    /// An anonymous caller on the read policy gets an API refusal — a 401 it can act on — never a
    /// 302 to the HTML login page (which a REST/SSR client would follow and parse as a 200 of
    /// markup).
    /// </summary>
    [Fact]
    public async Task An_anonymous_read_is_refused_with_401_not_a_login_redirect()
    {
        var app = BuildProbeApp();
        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();

            using var response = await GetAsync(client, ReadRoute, cookie: null);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.Location.Should().BeNull("a redirect here would be answered as HTML");
        }
    }
}
