using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

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

    /// <summary>
    /// The pipeline under test: the REAL <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition
    /// (so this pins the endpoint as it actually ships, not a hand-mapped copy), behind authentication
    /// and an authorization fallback that denies everything not explicitly anonymous.
    /// </summary>
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();

        // Deny by default. AllowAnonymous on the version endpoint is what must beat this.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => false)
                .Build());

        var app = builder.Build();
        app.UseAuthorization();
        app.MapDefaultEndpoints();
        app.MapGet(GuardedRoute, () => "secret");
        return app;
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
    /// Non-vacuity guard: the pipeline really does refuse an unauthenticated caller. If this ever
    /// passes, the anonymity assertion below is proving nothing.
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
        var (response, body) = await GetAsync(ServiceDefaults.VersionRoute);

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
        var (_, body) = await GetAsync(ServiceDefaults.VersionRoute);

        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["version", "commit"]);
    }

    /// <summary>
    /// The projection itself, off a known assembly: both stamps are read from the assembly the build
    /// produced — no poller, no node, no configuration key involved.
    /// </summary>
    [Fact]
    public void ReadBuildIdentity_reads_the_stamps_off_the_assembly()
    {
        var identity = ServiceDefaults.ReadBuildIdentity(typeof(VersionEndpointTest).Assembly);

        identity.Version.Should().NotBeNullOrWhiteSpace();
        identity.Commit.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A build with no source-control information reports an EMPTY commit rather than throwing or
    /// inventing one — the same "not recorded" case the About tab renders as a note.
    /// </summary>
    [Fact]
    public void ReadBuildIdentity_reports_an_empty_commit_when_the_build_carries_no_sha()
    {
        // The framework's own assembly is built outside this repo, so it carries no CommitHash.
        var identity = ServiceDefaults.ReadBuildIdentity(typeof(object).Assembly);

        identity.Commit.Should().BeEmpty();
        identity.Version.Should().NotBeNullOrWhiteSpace();
    }
}
