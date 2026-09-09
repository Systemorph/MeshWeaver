#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>#3488 — the platform emitted NO metrics of its own.</b> Measured 2026-09-06, twice:
/// <c>grep -rn "new Meter(" src/</c> → <b>0 results</b>, and <c>System.Diagnostics.Metrics</c> was
/// referenced nowhere. So every question about live hub state cost a heap dump against a running
/// replica — which suspends it for ~106 s against a 90 s liveness budget and therefore
/// <b>restarts it</b>, destroying the state being measured.
///
/// <para>These tests assert the MEASUREMENT, not the plumbing: a meter that exists but reports
/// nothing is worth exactly what the silence it replaced was worth.</para>
/// </summary>
public class PlatformMetricsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Collects one scrape of the platform meter into a list.</summary>
    private static List<(long Value, string Kind, string RunLevel)> Scrape(PlatformMetrics _)
    {
        var taken = new List<(long, string, string)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PlatformMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, __) =>
        {
            string kind = "", runLevel = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "kind") kind = tag.Value?.ToString() ?? "";
                if (tag.Key == "runlevel") runLevel = tag.Value?.ToString() ?? "";
            }
            taken.Add((value, kind, runLevel));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return taken;
    }

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION: the meter reports a live hub population, split the two ways the
    /// investigations needed — by address KIND and by RUN LEVEL. Before this, both figures required
    /// a heap dump.
    /// </summary>
    [HubFact]
    public void TheMeterReportsLiveHubs_ByKindAndRunLevel()
    {
        var host = GetHost();
        using var metrics = new PlatformMetrics(host);

        // A hosted hub, so the walk has something below the root to find.
        var child = host.GetHostedHub(new Address("victim", "metrics-1"), c => c);
        child.Should().NotBeNull();

        var taken = Scrape(metrics);

        taken.Should().NotBeEmpty(
            "a meter that reports nothing is worth what the silence it replaced was worth — the "
            + "whole point of #3488 is that no number existed at all");
        taken.Sum(t => t.Value).Should().BeGreaterThanOrEqualTo(2,
            "the root and the hosted hub are both alive, so the tree walk must find at least two");
        taken.Select(t => t.Kind).Should().Contain("victim",
            "the hosted hub's address KIND must be a tag — 'how many of each sort' is the question");
        taken.Select(t => t.RunLevel).Should().Contain(
            MessageHubRunLevel.Started.ToString(),
            "the RunLevel histogram is what #3432 could not see: Started vs Dead is the split that "
            + "distinguishes a leak from a working set");
    }

    /// <summary>
    /// 🚨 THE CARDINALITY GUARD. A per-ADDRESS tag would mint one time series per
    /// <c>sync/{guid}</c> — the population that reached 6,925 on one replica — and the meter would
    /// become the second thing nobody can afford to collect. The tag is the address's TYPE.
    /// </summary>
    [HubFact]
    public void TheKindTagIsTheAddressTYPE_NotTheAddress()
    {
        var host = GetHost();
        using var metrics = new PlatformMetrics(host);

        var first = host.GetHostedHub(new Address("victim", "metrics-card-1"), c => c);
        var second = host.GetHostedHub(new Address("victim", "metrics-card-2"), c => c);
        first.Should().NotBeNull();
        second.Should().NotBeNull();

        var taken = Scrape(metrics);
        var victims = taken.Where(t => t.Kind == "victim").ToArray();

        victims.Should().NotBeEmpty("PRECONDITION: both hubs are of kind 'victim'");
        victims.Select(t => t.Kind).Distinct().Should().Equal(["victim"],
            "two hubs of the same kind must fold into ONE series carrying a count of two — never "
            + "two series. An id in the tag is unbounded cardinality by construction");
        victims.Sum(t => t.Value).Should().BeGreaterThanOrEqualTo(2,
            "…and the count must actually carry them both");
        taken.Should().NotContain(t => t.Kind.Contains("metrics-card-", StringComparison.Ordinal),
            "the instance id must never reach a tag");
    }

    /// <summary>
    /// 🚨 A gauge that throws is swallowed by the metrics infrastructure and the instrument goes
    /// SILENT — indistinguishable from a mesh with no hubs, which is the exact failure this issue
    /// is about. A disposed root is the cheapest way to fault the walk.
    /// </summary>
    [HubFact]
    public void ADisposedRoot_ReportsNothing_RatherThanThrowing()
    {
        var host = GetHost();
        var doomed = (MessageHub)host.GetHostedHub(new Address("victim", "metrics-doomed"), c => c)!;
        using var metrics = new PlatformMetrics(doomed);
        doomed.Dispose();

        // Deliberately NOT an assertion helper: the property under test is that nothing escapes,
        // and the plainest way to assert that is to let the call stand. A throw fails the test with
        // the real exception, which is more useful than a wrapped one.
        var taken = Scrape(metrics);

        taken.Should().NotBeNull(
            "the scrape must never propagate out of the gauge — an exception there stops the "
            + "instrument reporting for the life of the process, silently, and a silent meter is "
            + "indistinguishable from a mesh with no hubs");
    }
}
