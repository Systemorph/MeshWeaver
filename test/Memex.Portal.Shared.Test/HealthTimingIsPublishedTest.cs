using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b><c>/health</c> is the <c>startupProbe</c>'s instrument, so it must publish what it SPENT —
/// otherwise a probe that times out names no cause, and the slowest check is by construction the one
/// nobody can see</b> (MeshWeaver#4588).
///
/// <para><b>The incident.</b> Measured 2026-09-17 from outside memex.systemorph.com, three
/// consecutive reads: <c>/health</c> answered 200 in 8.12 s, 9.62 s and 9.52 s, while
/// <c>/alive</c> and <c>/ready</c> on the same host and pod answered in 0.12 s — so the seconds were
/// entirely in the untagged checks. The chart's <c>startupProbe</c> reads <c>/health</c>, and that
/// instance gives it <c>timeoutSeconds: 5</c> — so every probe ran out of time before the endpoint
/// could answer. A container that never records ONE startup success never leaves startup, is never
/// Ready, and is killed when <c>periodSeconds × failureThreshold</c> runs out (10 s × 1080 = 3 h
/// there) — then starts over. The replica rolled onto
/// <c>3.0.0-ci.8812</c> at 11:23:40Z was still not Ready at 14:51Z with <c>restarts: 1</c>, and the
/// bake gate was GREEN across the reading at 14:38:15Z: the aggregate word on line one was
/// <c>Degraded</c>, which is a 200, which means no registered check was Unhealthy.</para>
///
/// <para><b>Why nothing could name the cost.</b> The framework logs every check's duration, but a
/// check answering <see cref="HealthStatus.Healthy"/> logs it at Information — which this fleet
/// filters out of Loki for the <c>Microsoft.*</c> categories (measured: not one line matching
/// <c>with status Healthy</c> has ever reached the log store). And
/// <c>WriteHealthWithDetail</c> prints only entries that are not Healthy, plus the census-tagged
/// ones. So an expensive check that is perfectly healthy appeared NOWHERE: not in the body, not in
/// the log. That is the same blind spot #3703 and #3704 were stuck in, this time over the endpoint's
/// own cost, and it is exactly the hole behind #4588's attribution caveat.</para>
///
/// <para>Everything here drives the REAL <see cref="ServiceDefaults.AddDefaultHealthChecks"/> +
/// <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition over a TestServer, for the same
/// reason <see cref="HealthCensusTest"/> does: a restatement of the wiring would agree with itself
/// while the deployment answered something else.</para>
/// </summary>
public class HealthTimingIsPublishedTest
{
    /// <summary>
    /// How long the deliberately expensive check below burns. Comfortably above
    /// <c>ServiceDefaults.TimingNamedAboveMs</c> (10 ms) and small enough that the suite does not
    /// notice — the assertion is that the check is NAMED, never that a particular number came back.
    /// </summary>
    private const int SlowCheckMs = 80;

    /// <summary>
    /// 🚨 <b>The property: a check that is expensive AND Healthy AND uncensused is named on
    /// <c>/health</c>, with its cost.</b> That combination is the one the body used to drop
    /// entirely, and it is the only combination that can explain a probe timeout on a pod whose
    /// every verdict is fine.
    /// </summary>
    [Fact]
    public async Task AnExpensiveButHealthyCheck_IsNamedWithItsCost()
    {
        var body = await HealthBodyAsync(services => services.AddHealthChecks()
            .AddCheck("slow_and_healthy", Burn(SlowCheckMs)));

        var timing = TimingLineOf(body);

        Assert.Contains("slow_and_healthy", timing, StringComparison.Ordinal);
        Assert.DoesNotContain("slow_and_healthy", string.Join('\n', body.Split('\n').Skip(2)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The NEGATIVE CONTROL that keeps the assertion above from passing on a line that simply
    /// names everything: a cheap check is NOT named — and is still COUNTED, so the line states its
    /// own denominator rather than quietly dropping what it did not print.
    /// </summary>
    [Fact]
    public async Task ACheapCheck_IsCountedRatherThanNamed()
    {
        var body = await HealthBodyAsync(services => services.AddHealthChecks()
            .AddCheck("cheap_and_healthy", () => HealthCheckResult.Healthy()));

        var timing = TimingLineOf(body);

        Assert.DoesNotContain("cheap_and_healthy", timing, StringComparison.Ordinal);
        Assert.Contains("check(s)", timing, StringComparison.Ordinal);
        Assert.Contains("under 10ms", timing, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 <b>Line ONE stays the bare status word.</b> The fleet watch takes the first
    /// whitespace-delimited token of the body as the replica's verdict
    /// (<c>ObservationQueries.HealthVerdict</c>, in MeshWeaver.Plugins), and every deployment record
    /// on the control instance is written from it. A timing line placed anywhere but line two would
    /// either be read as the verdict or be lost to the 2000-character detail clip — so its position
    /// is part of the contract, not formatting.
    /// </summary>
    [Fact]
    public async Task TheVerdictStaysOnLineOneAndTheTimingIsLineTwo()
    {
        var body = await HealthBodyAsync(services => services.AddHealthChecks()
            .AddCheck("slow_and_healthy", Burn(SlowCheckMs)));

        var lines = body.Split('\n');
        Assert.True(Enum.TryParse<HealthStatus>(lines[0], out _),
            $"line one must be the bare status word the fleet watch parses. Body was:\n{body}");
        Assert.StartsWith("timing: ", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A check that burns <paramref name="milliseconds"/> of wall clock and answers Healthy. The
    /// wait is a bounded <c>SpinWait.SpinUntil</c> over a <see cref="Stopwatch"/> — the shape this
    /// repository sanctions for a deliberate park in a test — never a sleep or a delay task.
    /// </summary>
    private static Func<HealthCheckResult> Burn(int milliseconds) => () =>
    {
        var clock = Stopwatch.StartNew();
        SpinWait.SpinUntil(() => clock.ElapsedMilliseconds >= milliseconds, milliseconds * 20);
        return HealthCheckResult.Healthy($"burned {clock.ElapsedMilliseconds}ms on purpose");
    };

    /// <summary>The timing line, with a failure message that shows the whole body when it is absent.</summary>
    private static string TimingLineOf(string body)
    {
        var lines = body.Split('\n');
        Assert.True(lines.Length > 1 && lines[1].StartsWith("timing: ", StringComparison.Ordinal),
            "/health published no timing line. The endpoint that gates every rollout has to say what "
            + "it SPENT: once its own latency reaches the startup probe's timeout the verdict stops "
            + $"mattering, and nothing else can name the cost (#4588). Body was:\n{body}");
        return lines[1];
    }

    /// <summary>
    /// The pipeline under test: the REAL health-check composition over a TestServer, plus whatever
    /// <paramref name="configure"/> registers — the same container the shipped checks resolve from.
    /// </summary>
    private static async Task<string> HealthBodyAsync(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();
        configure(builder.Services);

        var app = builder.Build();
        app.MapDefaultEndpoints();

        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(ProbeEndpoints.Health, TestContext.Current.CancellationToken);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
    }
}
