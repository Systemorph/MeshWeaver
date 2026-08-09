using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

// The class and its namespace share the name "ServiceDefaults", so the bare name binds to the
// namespace here. Alias it so the endpoint's own constants stay readable.
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>/api/version</c> contract (issue #956, second half): a machine-readable build identity
/// that answers WITHOUT a session, so a deployment can be checked from outside the portal.
///
/// <para>Anonymity is the entire point — an endpoint that quietly required a login would satisfy
/// nothing — so it is proved the only way that means anything: the pipeline under test carries a
/// deny-by-default authorization fallback, and a control endpoint in the SAME pipeline is refused.
/// Without that control the 200 would be vacuous (any route answers when nothing is guarding).</para>
///
/// <para>The other half of the contract is what the response must NOT contain. It is exactly two
/// fields; no environment, cluster, namespace, configuration or user data may ever ride along, so
/// the property set is asserted exhaustively rather than by presence.</para>
/// </summary>
public class VersionEndpointTest
{
    /// <summary>A route with no <c>AllowAnonymous</c> — the proof the fallback policy is real.</summary>
    private const string GuardedRoute = "/guarded";

    private const string TestScheme = "NoSession";

    /// <summary>
    /// The pipeline under test: the REAL <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition
    /// (so this pins the endpoint as it actually ships, not a hand-mapped copy), behind the real
    /// authentication + authorization middleware with a fallback policy that denies everything not
    /// explicitly marked anonymous — i.e. the shape of a portal that requires a login by default.
    /// </summary>
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();

        // A scheme that never authenticates anyone: every caller here is anonymous, and a challenge
        // answers 401 (the handler's default) instead of throwing for want of a registered scheme.
        builder.Services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, NoSessionHandler>(TestScheme, _ => { });

        // Deny by default. AllowAnonymous on the version endpoint is what must beat this.
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

    /// <summary>Authenticates nobody — the "no session" the endpoint must answer under.</summary>
    private sealed class NoSessionHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(string route)
    {
        var app = BuildApp();
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
    /// Non-vacuity guard: the pipeline really does refuse an unauthenticated caller. Should this
    /// route ever start answering instead, the anonymity assertion below would be proving nothing —
    /// every route answers when nothing is guarding.
    /// </summary>
    [Fact]
    public async Task A_guarded_route_in_the_same_pipeline_refuses_an_anonymous_caller()
    {
        var (response, _) = await GetAsync(GuardedRoute);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>The point of the endpoint: it answers with no session at all, and both fields carry a value.</summary>
    [Fact]
    public async Task Version_endpoint_answers_anonymously_with_version_and_commit()
    {
        var (response, body) = await GetAsync(Defaults.VersionRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "verifying a deployment from outside must not require a login");

        var payload = JsonSerializer.Deserialize<BuildIdentity>(body, JsonSerializerOptions.Web)!;
        payload.Version.Should().NotBeNullOrWhiteSpace();
        payload.Commit.Should().NotBeNullOrWhiteSpace(
            "the git SHA is stamped into every assembly by the AddCommitHashMetadata target");
    }

    /// <summary>
    /// The disclosure contract, asserted exhaustively: the body has these two properties and no
    /// others. A future field added carelessly here would be published to anonymous callers.
    /// </summary>
    [Fact]
    public async Task Version_endpoint_discloses_nothing_beyond_version_and_commit()
    {
        var (_, body) = await GetAsync(Defaults.VersionRoute);

        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        properties.Should().Equal("commit", "version");
    }

    /// <summary>
    /// The projection itself, off an assembly THIS build stamped: both values come from assembly
    /// metadata — no poller, no node, no configuration key involved.
    /// </summary>
    [Fact]
    public void ReadBuildIdentity_reads_the_stamps_off_a_build_assembly()
    {
        var identity = Defaults.ReadBuildIdentity(typeof(BuildIdentity).Assembly);

        identity.Version.Should().NotBeNullOrWhiteSpace();
        identity.Commit.Should().NotBeNullOrWhiteSpace(
            "AddCommitHashMetadata stamps every assembly built from this repo");
    }

    /// <summary>
    /// The fallback that makes the endpoint answer correctly under a host that is not part of this
    /// build — and this test run IS that case, which is what gives the assertion teeth.
    ///
    /// <para><c>test/Directory.Build.props</c> deliberately does NOT import the root props, so
    /// <c>AddCommitHashMetadata</c> never runs for test projects and the entry assembly here carries
    /// no <c>CommitHash</c>. Reading the entry assembly blindly would report the runner's <c>1.0.0</c>
    /// and an empty commit; the fallback reports the real build instead. Production is the other
    /// branch — <c>memex/Directory.Build.props</c> imports the root, so the portal executable IS
    /// stamped and wins, which is what keeps this endpoint and the About tab in agreement.</para>
    /// </summary>
    [Fact]
    public void Build_falls_back_to_a_stamped_assembly_when_the_host_is_not_part_of_this_build()
    {
        var entry = Assembly.GetEntryAssembly();
        entry.Should().NotBeNull();

        // Non-vacuity guard: the host really is unstamped, so the fallback really is exercised.
        Defaults.ReadBuildIdentity(entry!).Commit.Should().BeEmpty();

        Defaults.Build.Commit.Should().NotBeNullOrWhiteSpace();
        Defaults.Build.Version.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A build with no source-control information reports an EMPTY commit rather than throwing or
    /// inventing one — the same "not recorded" case the About tab renders as a note.
    /// </summary>
    [Fact]
    public void ReadBuildIdentity_reports_an_empty_commit_when_the_build_carries_no_sha()
    {
        // The framework's own assembly is built outside this repo, so it carries no CommitHash.
        var identity = Defaults.ReadBuildIdentity(typeof(object).Assembly);

        identity.Commit.Should().BeEmpty();
        identity.Version.Should().NotBeNullOrWhiteSpace();
    }
}
