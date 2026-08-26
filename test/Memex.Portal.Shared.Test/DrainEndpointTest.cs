using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The session drain, pinned end to end</b> (#1971 / #1342).
///
/// <para><b>What was NOT pinned.</b> A rollout is non-disruptive only because the container's
/// <c>preStop</c> polls <c>http://127.0.0.1:8080/drain</c> until this pod's last Blazor circuit
/// closes. #2043 pinned the ARITHMETIC of that loop (<c>DrainDeadlineGuard</c>, a text guard over
/// the chart) and <c>DrainProgressTest</c> pins what each probe is worth logging — but nothing
/// pinned the thing in the middle: that the portal actually MAPS <c>/drain</c>, that it answers
/// 503-with-a-count while circuits are open and 200 when drained, and that it answers with no
/// session at all. Every one of those is a chart↔code agreement, and this chart already carries a
/// scar of exactly that class: <c>preStop</c> once probed with <c>wget</c>, which is absent from
/// the image, so the loop could never succeed and EVERY termination hung to the grace ceiling. It
/// was caught by reading, not by a test.</para>
///
/// <para><b>Why a wrong answer here is silent.</b> preStop probes with
/// <c>curl -sf -m 5 -o /dev/null</c>, which cannot tell a 404 from a 503 from a refused
/// connection — all three are "not drained yet, keep waiting". So an unmapped, renamed, or
/// login-gated <c>/drain</c> does not fail: it makes every roll sit out its whole drain window and
/// then cut every open session at the deadline, with nothing in the log that names the cause. That
/// is the same disease as the rest of this issue — silence indistinguishable from success — and
/// these cases are what make it fail loudly instead, at build time.</para>
///
/// <para>Driven over a REAL HTTP pipeline through the REAL
/// <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition (never a hand-mapped copy), behind
/// a deny-by-default authorization fallback so the anonymity assertion is not vacuous — the same
/// harness shape as <see cref="VersionEndpointTest"/> beside it.</para>
/// </summary>
public class DrainEndpointTest
{
    /// <summary>The route the chart's preStop hook polls. Changing it is a chart change too.</summary>
    private const string DrainRoute = "/drain";

    /// <summary>A route with no <c>AllowAnonymous</c> — the proof the fallback policy is real.</summary>
    private const string GuardedRoute = "/guarded";

    private const string TestScheme = "NoSession";

    /// <summary>
    /// The pipeline under test. <paramref name="tracker"/> is registered exactly as the portal
    /// registers it, so the endpoint reads the same counter a live pod's circuits feed.
    /// </summary>
    private static WebApplication BuildApp(ActiveCircuitTracker? tracker)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();

        if (tracker is not null)
            builder.Services.AddSingleton(tracker);

        builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, NoSessionHandler>(TestScheme, _ => { });

        // Deny by default. AllowAnonymous on /drain is what must beat this — a preStop hook carries
        // no cookie and no bearer token.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => false)
                .Build());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultEndpoints();
        app.MapGet(GuardedRoute, () => "secret");
        return app;
    }

    /// <summary>Authenticates nobody — the "no session" a kubelet preStop probe arrives with.</summary>
    private sealed class NoSessionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(
        string route, ActiveCircuitTracker? tracker = null)
    {
        var app = BuildApp(tracker);
        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return (response, body);
        }
    }

    /// <summary>
    /// Non-vacuity guard: the pipeline really does refuse an unauthenticated caller. Without it the
    /// anonymity assertion below proves nothing — every route answers when nothing is guarding.
    /// </summary>
    [Fact]
    public async Task A_guarded_route_in_the_same_pipeline_refuses_an_anonymous_caller()
    {
        var (response, _) = await GetAsync(GuardedRoute);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// 🚨 The success signal preStop waits for. A pod with no circuits answers 200, which is the
    /// ONLY thing that ends the drain loop early — every other outcome, including a 404 from an
    /// unmapped route, reads to <c>curl -sf</c> as "still draining".
    /// </summary>
    [Fact]
    public async Task WithNoCircuits_TheDrainRouteAnswers200_TheOnlyThingThatEndsThePreStopLoop()
    {
        var (response, body) = await GetAsync(DrainRoute, new ActiveCircuitTracker());

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "preStop polls until this route SUCCEEDS; a 404 or a 5xx is indistinguishable from "
            + "'sessions are still open' and rides the whole drain window before cutting them off");
        body.Should().Be("drained");
    }

    /// <summary>
    /// 🚨 The hold signal. A live circuit must produce a NON-success status, or a roll would tear
    /// down a pod that is still serving someone — the regression #1342 fixed.
    /// </summary>
    [Fact]
    public async Task WithALiveCircuit_TheDrainRouteAnswers503_AndNamesTheCount()
    {
        var tracker = new ActiveCircuitTracker();
        tracker.Opened();
        tracker.Opened();

        var (response, body) = await GetAsync(DrainRoute, tracker);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("2",
            "the count is the diagnostic — DrainProgress reports it, and an operator reading a "
            + "Terminating pod has nothing else to go on");
    }

    /// <summary>
    /// 🚨 Anonymous, because the caller is a kubelet exec with no cookie and no token. A portal
    /// that put this route behind its login would answer 302/401 to every probe — which
    /// <c>curl -sf</c> reads as "not drained" — so every roll would ride to the deadline and cut
    /// every session. Proved against the deny-by-default fallback above, which the control case
    /// shows is real.
    /// </summary>
    [Fact]
    public async Task TheDrainRoute_AnswersWithoutASession()
    {
        var (response, _) = await GetAsync(DrainRoute, new ActiveCircuitTracker());

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A host with no Blazor tracker at all (a worker, a test host) is trivially drained. Pinned
    /// because the alternative — a 500 from a missing service — is read by <c>curl -sf</c> as
    /// "keep waiting", so the pod would sit out its whole window and then be killed anyway.
    /// </summary>
    [Fact]
    public async Task WithNoCircuitTrackerRegistered_TheRouteStillAnswersDrained()
    {
        var (response, body) = await GetAsync(DrainRoute, tracker: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Be("drained");
    }
}
