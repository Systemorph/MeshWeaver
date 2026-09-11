using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A census entry must PRINT its reading on <c>/health</c> even when it is Healthy — otherwise
/// "I measured nothing" and "I measured, and it was clean" are the same silence</b> (MeshWeaver
/// #3703, #3704).
///
/// <para>Both issues were blocked on the same thing and it was never their own mechanism: the
/// reading that would settle them existed only as a boot LOG line, and log access on this fleet is
/// break-glass. Publishing the numbers on <c>/health</c> — public, unauthenticated, past RLS — turns
/// two permanently-unmeasurable issues into two measurable ones. That only works if a CLEAN reading
/// is visible: <c>WriteHealthWithDetail</c> prints non-Healthy entries only, so an entry that
/// answered Healthy-and-silent would be byte-identical on the wire to one that was never
/// registered.</para>
///
/// <para>Everything here drives the REAL <see cref="ServiceDefaults.AddDefaultHealthChecks"/> +
/// <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition over a TestServer, for the same
/// reason <see cref="ProbeSeparationTest"/> does: a restatement of the wiring would agree with
/// itself while the deployment answered something else.</para>
/// </summary>
public class HealthCensusTest
{
    /// <summary>
    /// 🚨 The property, and the POSITIVE CONTROL that keeps every "does not print" assertion below
    /// from being vacuous: a Healthy census entry appears in the body, with its description.
    /// </summary>
    [Fact]
    public async Task ACleanCensusReading_IsPrintedAlthoughItIsHealthy()
    {
        var body = await HealthBodyAsync(services => services.AddSingleton(CleanBakeRegistry()));

        Assert.True(body.Contains($"{NodeTypeBakeReportRegistry.HealthCheckName}: Healthy", StringComparison.Ordinal),
            "a CLEAN bake reading did not appear on /health. The whole publication is the NUMBER, "
            + "so a census entry that prints only when it is unhappy is indistinguishable from one "
            + "that was never registered — which is the ambiguity #3703 and #3704 are stuck in. "
            + $"Body was:\n{body}");

        Assert.True(body.Contains("total=209 baked=5 pending=204", StringComparison.Ordinal),
            $"the bake summary's numbers are missing from /health. Body was:\n{body}");
        Assert.True(body.Contains("Adoption stamps held by this process: 78", StringComparison.Ordinal),
            "the adoption-stamp count is the OTHER half of the 78-vs-5 pair #3703 was filed as; "
            + $"published apart from the bake counts it settles nothing. Body was:\n{body}");
    }

    /// <summary>
    /// 🚨 The NEGATIVE half, paired with the case above so neither can go vacuous: the census tag is
    /// opt-in and did NOT turn /health into a wall of green. `self` is Healthy and untagged, and it
    /// must stay silent.
    /// </summary>
    [Fact]
    public async Task AHealthyCheckWithoutTheCensusTag_StaysSilent()
    {
        var body = await HealthBodyAsync(services => services.AddSingleton(CleanBakeRegistry()));

        Assert.False(body.Contains("self:", StringComparison.Ordinal),
            "the trivial process-up check printed on /health. The census tag must be OPT-IN: "
            + "printing every Healthy entry would bury the ones that matter and change what every "
            + $"existing reader of this body sees. Body was:\n{body}");
    }

    /// <summary>
    /// 🚨 The absence case, and the reason it is Degraded rather than Healthy: the adopt-only probe
    /// runs on every boot, so a replica with NO report measured nothing about its bake — and a
    /// missing instrument may not read as a clean result.
    /// </summary>
    [Fact]
    public async Task NoBakeReportAtAll_IsDegraded_AndSaysItMeasuredNothing()
    {
        var body = await HealthBodyAsync(configure: null);

        Assert.True(body.Contains($"{NodeTypeBakeReportRegistry.HealthCheckName}: Degraded", StringComparison.Ordinal),
            "a replica that published NO bake report reported clean. That is the "
            + "missing-instrument-reads-as-a-pass defect, and publishing a report that prints "
            + $"nothing when it has nothing would leave #3703 exactly where it was. Body was:\n{body}");
        Assert.True(body.Contains("measured NOTHING", StringComparison.Ordinal),
            $"the absence is not spelled out in the body a reader curls. Body was:\n{body}");
    }

    /// <summary>
    /// A report whose enumeration snapshot was behind this process's own adoptions — #3703's actual
    /// verdict — is Degraded and names the count.
    /// </summary>
    [Fact]
    public async Task ASnapshotBehindItsOwnAdoptions_IsDegraded_AndNamesTheCount()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(BakeReading(fromLocalAdoption: 73));
        var body = await HealthBodyAsync(services => services.AddSingleton(registry));

        Assert.True(body.Contains($"{NodeTypeBakeReportRegistry.HealthCheckName}: Degraded", StringComparison.Ordinal),
            $"#3703's verdict did not reach /health as a non-Healthy entry. Body was:\n{body}");
        Assert.True(body.Contains("PREDATED this replica's own prebuilt adoptions for 73 of 209", StringComparison.Ordinal),
            $"the count that IS the verdict is missing from the body. Body was:\n{body}");
    }

    /// <summary>
    /// 🚨 A warm replica issues no discovery query at all, so "no pass" is Healthy — but it must
    /// still be VISIBLE, or the check is back to being silence.
    /// </summary>
    [Fact]
    public async Task NoDiscoveryPass_IsHealthy_AndStillPrintsThatNothingWasMeasured()
    {
        var body = await HealthBodyAsync(configure: null);

        Assert.True(body.Contains($"{SourceDiscoveryRegistry.HealthCheckName}: Healthy", StringComparison.Ordinal),
            "a replica that ran no discovery pass reported Degraded (or did not report at all). "
            + "Degrading on it would make every warm portal permanently non-Healthy — a check that "
            + $"cannot pass — and silence would say nothing. Body was:\n{body}");
        Assert.True(body.Contains("NO source-discovery pass recorded", StringComparison.Ordinal),
            "'nothing ran' and 'it ran and was clean' have to be two different printed sentences; "
            + $"that is the entire reason this entry carries the census tag. Body was:\n{body}");
    }

    /// <summary>#3704's discriminator, reaching the wire: a gap that approached the window.</summary>
    [Fact]
    public async Task AWideInterChunkGap_IsDegraded_AndPublishesTheNumbers()
    {
        var registry = new SourceDiscoveryRegistry();
        registry.Record(new SourceDiscoveryPass(
            "nodeType:Code partitions:all", Settled: 1145, Chunks: 7, Items: 1145,
            ElapsedMs: 2100, LargestGapMs: 900, WindowMs: 1000, At: DateTimeOffset.UnixEpoch));

        var body = await HealthBodyAsync(services => services.AddSingleton(registry));

        Assert.True(body.Contains($"{SourceDiscoveryRegistry.HealthCheckName}: Degraded", StringComparison.Ordinal),
            $"a 90%-of-window inter-chunk gap did not reach /health as Degraded. Body was:\n{body}");
        Assert.True(body.Contains("largest inter-chunk gap 900ms = 90% of the 1000ms completion window", StringComparison.Ordinal),
            "#3704 turns on this exact number, and it existed only in a Loki line before. Body "
            + $"was:\n{body}");
        Assert.True(body.Contains("settled at 1145 node(s) from 7 change(s)", StringComparison.Ordinal),
            $"the folded-change count is the other half of the discriminator. Body was:\n{body}");
    }

    /// <summary>
    /// 🚨 Publishing a number may NEVER restart a pod or evict it from the Service. The census tag
    /// is not a probe tag, so neither post-startup probe can see these entries — and both must
    /// still answer 200 with a Degraded census on <c>/health</c>.
    /// </summary>
    [Fact]
    public async Task ADegradedCensus_ReachesNeitherPostStartupProbe()
    {
        // No registries at all: bake-report is Degraded on /health (asserted above).
        var (health, healthBody) = await ProbeAsync(ProbeEndpoints.Health, configure: null);
        var (live, _) = await ProbeAsync(ProbeEndpoints.Live, configure: null);
        var (ready, _) = await ProbeAsync(ProbeEndpoints.Ready, configure: null);

        Assert.True(healthBody.Contains($"{NodeTypeBakeReportRegistry.HealthCheckName}: Degraded", StringComparison.Ordinal),
            "the precondition of this test — a Degraded census entry — is not present, so the "
            + $"assertions below would hold vacuously. Body was:\n{healthBody}");
        Assert.True(health.StatusCode == HttpStatusCode.OK,
            $"/health answered {(int)health.StatusCode} with a Degraded census. Degraded is a 200; "
            + "the startupProbe reads this path and a census must never hold a pod out of its "
            + "startup.");
        Assert.True(live.StatusCode == HttpStatusCode.OK,
            $"{ProbeEndpoints.Live} answered {(int)live.StatusCode} because a CENSUS entry was "
            + "Degraded. Failing liveness means 'restart me', and no restart makes a replica able "
            + "to describe a bake it never ran.");
        Assert.True(ready.StatusCode == HttpStatusCode.OK,
            $"{ProbeEndpoints.Ready} answered {(int)ready.StatusCode} because a CENSUS entry was "
            + "Degraded. Failing readiness hands this pod's traffic to its siblings — a claim about "
            + "the siblings, made here on the strength of a diagnostic number.");
    }

    private static NodeTypeBakeReportRegistry CleanBakeRegistry()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(BakeReading(fromLocalAdoption: 0));
        return registry;
    }

    private static BakeReportReading BakeReading(int fromLocalAdoption) =>
        new(
            NodeTypeBakeReportRegistry.AdoptOnlyProbe,
            "sb43f9287dbd6922a7937bd24be103937",
            Total: 209,
            Baked: 5,
            Pending: 204,
            ClassifiedFromLocalAdoption: fromLocalAdoption,
            AdoptionStamps: 78,
            Summary: "framework=sb43f928 total=209 baked=5 pending=204 frameworkstale=201",
            At: DateTimeOffset.UnixEpoch);

    private static async Task<string> HealthBodyAsync(Action<IServiceCollection>? configure)
        => (await ProbeAsync(ProbeEndpoints.Health, configure)).Body;

    /// <summary>
    /// The pipeline under test: the REAL health-check composition over a TestServer, with whatever
    /// census registries <paramref name="configure"/> adds to the host container — which is the same
    /// container <see cref="BakeReportHealthCheck"/> resolves them from in production.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, string Body)> ProbeAsync(
        string route, Action<IServiceCollection>? configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapDefaultEndpoints();

        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return (response, body);
        }
    }
}
