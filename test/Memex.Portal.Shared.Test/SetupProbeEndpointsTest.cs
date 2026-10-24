using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using Memex.Portal.Shared.Setup;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The setup-only host's probe paths are answered, never redirected to <c>/setup</c>.
///
/// <para>The companion fact — every path the CHART probes is one the setup host maps — reads the
/// chart, so it moved to MeshWeaver.Deploy (test/MeshWeaver.Deploy.ChartGuards/), with the deployment chart it reads — the platform repository carries no deployment knowledge.</para>
/// </summary>
public class SetupProbeEndpointsTest
{
    /// <summary>
    /// Every probe path is EXEMPT from the redirect-everything-to-/setup middleware.
    ///
    /// <para>🚨 This assertion used to read <c>Assert.DoesNotContain(p, new[] { "/setup" })</c> —
    /// i.e. "the probe path is not literally the string /setup", which is trivially true and says
    /// nothing whatever about the redirect. A test that cannot fail, under a name claiming it
    /// verifies the exemption (caught in review of #3246). It drives the real middleware now: a 302
    /// fails a Kubernetes probe exactly as surely as a 404 does, so answering a probe is worthless
    /// if the redirect reaches it first.</para>
    /// </summary>
    [Fact]
    public async Task TheProbePaths_AreAlsoExemptFromTheRedirectToSetup()
    {
        using var app = SetupSurfaceTest.BuildProbeApp();
        var client = app.GetTestClient();

        foreach (var path in SetupOnlyHost.ProbePaths)
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{path} answered {(int)response.StatusCode} "
                + $"{(response.Headers.Location is { } l ? $"→ {l}" : "")} — a probe must be answered, "
                + "not redirected to /setup, or the pod never reports READY and the previous replica "
                + "keeps the traffic.");
        }

        // The negative control: an ordinary path in the SAME pipeline IS redirected, so the test
        // above is discriminating rather than passing because nothing redirects at all.
        var ordinary = await client.GetAsync("/some/ordinary/page");
        Assert.Equal(HttpStatusCode.Redirect, ordinary.StatusCode);
        Assert.Equal("/setup", ordinary.Headers.Location?.ToString());
    }
}
