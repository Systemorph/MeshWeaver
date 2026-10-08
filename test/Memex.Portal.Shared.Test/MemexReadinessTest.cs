using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Testcontainers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The memex test container's readiness must prove a started MESH, and the first-run setup
/// wizard must never satisfy it</b> (MeshWeaver#6037).
///
/// <para><b>What went wrong.</b> <c>MemexBuilder</c> waited for <c>200</c> on <c>/healthz</c> and
/// never stated <c>Graph:Storage:Type</c>. The image deliberately bakes no storage type, so every
/// container the helper started served the SETUP wizard — which answers every probe path with a flat
/// <c>ok</c> and composes no mesh. Measured on the portal image <c>3.0.0-ci.8323</c> with the
/// helper's exact environment: <c>/healthz</c>, <c>/health</c>, <c>/ready</c> and <c>/alive</c> all
/// <c>200 ok</c>, <c>/api/version</c> → <c>302 /setup</c>, and the log line "Serving the FIRST-RUN
/// SETUP wizard and nothing else". A green start proved a container, never a portal.</para>
///
/// <para><b>Why these hosts and not recorded strings.</b> Both sides are the REAL pipelines: the
/// setup-only host's probe + setup surface (<see cref="SetupSurfaceTest.BuildProbeApp"/>, the same
/// mapping <c>SetupOnlyHost.TryRun</c> serves) and the composed host's
/// <see cref="ServiceDefaults.MapDefaultEndpoints"/>. A recorded body would agree with itself while
/// either host changed shape.</para>
/// </summary>
public class MemexReadinessTest
{
    /// <summary>
    /// The negative control, and the incident: the wizard is named — terminally — and is never
    /// read as a started mesh, although every probe path it serves answers 200.
    /// </summary>
    [Fact]
    public async Task TheSetupWizard_IsNamed_AndNeverReadAsAStartedMesh()
    {
        using var wizard = SetupSurfaceTest.BuildProbeApp();
        using var client = wizard.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        // The premise: the wizard satisfies the OLD wait. Without this the test below could pass
        // against a wizard that had simply stopped answering /healthz.
        var healthz = await client.GetAsync("/healthz", ct);
        Assert.True(healthz.IsSuccessStatusCode,
            $"premise: the setup host answers /healthz {(int)healthz.StatusCode} — the old wait's "
            + "condition no longer holds on the wizard, so this test no longer reproduces #6037");

        var verdict = await MemexReadiness.ProbeAsync(client, ct);

        Assert.True(verdict.State == MemexReadinessState.SetupWizard,
            $"the setup wizard was read as {verdict.State} ({verdict.Reason}). It composes no mesh, "
            + "and a wait that does not name it either reports a hollow start or polls to its "
            + "timeout with no reason given (#6037).");
    }

    /// <summary>
    /// The INSTALLED predicate inside Testcontainers' OWN retry loop
    /// (<see cref="DotNet.Testcontainers.Configurations.WaitStrategy.WaitUntilAsync"/>, the loop
    /// <c>DockerContainer.CheckReadinessAsync</c> runs) against the real wizard: the wait ends on the
    /// FIRST poll with the wizard named — not a <see cref="System.TimeoutException"/> at the budget,
    /// which is what a retry-and-swallow loop would produce.
    /// </summary>
    [Fact]
    public async Task TheInstalledWait_EndsAtOnceOnTheWizard_InsideTestcontainersRetryLoop()
    {
        using var wizard = SetupSurfaceTest.BuildProbeApp();
        using var client = wizard.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var polls = 0;

        var ex = await Record.ExceptionAsync(() => DotNet.Testcontainers.Configurations.WaitStrategy.WaitUntilAsync(
            async () =>
            {
                polls++;
                return UntilMeshStarted.Decide(await MemexReadiness.ProbeAsync(client, ct));
            },
            interval: System.TimeSpan.FromMilliseconds(50),
            // A budget the assertion below never comes near: reaching it is the failure.
            timeout: System.TimeSpan.FromMinutes(1),
            retries: 0,
            ct));

        Assert.True(ex is System.InvalidOperationException && ex.Message.Contains("SETUP wizard"),
            $"the wait over the wizard ended with {ex?.GetType().Name ?? "no exception"} "
            + $"({ex?.Message}) after {polls} poll(s) — it must end on the first poll, naming the wizard.");
        Assert.Equal(1, polls);
    }

    /// <summary>
    /// The positive control: the composed host's startup census satisfies the wait — so the
    /// negative case above is discriminating, not a wait that nothing can satisfy.
    /// </summary>
    [Fact]
    public async Task AComposedHostWhoseStartupCensusPasses_IsAStartedMesh()
    {
        var verdict = await ProbeComposedHost(meshCheck: HealthCheckResult.Healthy("mesh composed"));

        Assert.True(verdict.State == MemexReadinessState.MeshStarted,
            $"the composed host's healthy census was read as {verdict.State} ({verdict.Reason})");
    }

    /// <summary>
    /// A composed host whose startup checks still fail (the database, the schema, the mesh) is not
    /// started yet — keep waiting, never declare it ready and never declare it the wizard.
    /// </summary>
    [Fact]
    public async Task AComposedHostStillFailingItsStartupCensus_IsNotYetStarted()
    {
        var verdict = await ProbeComposedHost(meshCheck: HealthCheckResult.Unhealthy("mesh not composed yet"));

        Assert.True(verdict.State == MemexReadinessState.NotYet,
            $"a composed host answering its startup census Unhealthy was read as {verdict.State} ({verdict.Reason})");
    }

    /// <summary>The readings a live container answers, pinned on the pure classifier.</summary>
    [Theory]
    // The wizard, as measured on image 3.0.0-ci.8323 with the helper's old environment.
    [InlineData(302, "http://127.0.0.1:8080/setup", 200, "ok", MemexReadinessState.SetupWizard)]
    // A probe path's flat ok is not a census, even when nothing redirects.
    [InlineData(200, null, 200, "ok", MemexReadinessState.NotYet)]
    // The same image configured and migrated: /health "Degraded" with census lines, /api/version 200.
    [InlineData(200, null, 200, "Degraded\ncontent-types: Degraded — 1 node type(s) …", MemexReadinessState.MeshStarted)]
    [InlineData(200, null, 200, "Healthy\nchecks: 12 in 40ms", MemexReadinessState.MeshStarted)]
    [InlineData(200, null, 503, "Unhealthy\ndb_version: Unhealthy — schema behind", MemexReadinessState.NotYet)]
    // A redirect elsewhere (an auth challenge) is not the wizard.
    [InlineData(302, "/signin?returnUrl=%2Fapi%2Fversion", 200, "Healthy", MemexReadinessState.MeshStarted)]
    public void TheClassifier_ReadsWhatALiveContainerAnswers(
        int meshStatus, string? meshLocation, int healthStatus, string healthBody, MemexReadinessState expected)
    {
        var verdict = MemexReadiness.Classify(meshStatus, meshLocation, healthStatus, healthBody);
        Assert.True(verdict.State == expected, $"expected {expected}, read {verdict.State}: {verdict.Reason}");
    }

    private static async Task<MemexReadinessVerdict> ProbeComposedHost(HealthCheckResult meshCheck)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();
        builder.Services.AddHealthChecks().AddCheck("mesh", () => meshCheck);

        var app = builder.Build();
        app.MapDefaultEndpoints();

        await using (app)
        {
            var ct = TestContext.Current.CancellationToken;
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            return await MemexReadiness.ProbeAsync(client, ct);
        }
    }
}
