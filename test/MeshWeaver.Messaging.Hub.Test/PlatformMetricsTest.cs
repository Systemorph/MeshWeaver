#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
    /// 🚨 <b>The invariant <c>KindOf</c> rests on, pinned where it is actually decidable.</b>
    ///
    /// <para><c>KindOf</c> first derived the kind by splitting <c>Address.ToString()</c> at its
    /// first <c>/</c>. That is wrong by construction, and this test says why:
    /// <c>ToString()</c> is <c>Path + '~' + Host</c>, so for a path of ONE segment the first
    /// <c>/</c> comes out of the HOST chain — the kind becomes <c>solo~portal</c>, host ids are
    /// back in the tag, and the unbounded cardinality the tag exists to bound is recreated. The
    /// two-segment case above cannot catch it: there the first <c>/</c> is inside the path, so the
    /// parse looks right.</para>
    ///
    /// <para>The fix is to stop parsing — <c>Address.Type</c> IS <c>Segments[0]</c>, host-free.
    /// This asserts both halves of that claim on <see cref="Address"/> itself, because a hub whose
    /// own address carries a host chain is not constructible through the public hub API this rig
    /// uses, and an assertion that cannot fail is not one.</para>
    /// </summary>
    [HubFact]
    public void ToStringCarriesTheHostChain_TypeDoesNot()
    {
        var hosted = new Address("solo") { Host = new Address("portal", "xyz") };

        hosted.ToString().Should().Be("solo~portal/xyz",
            "PRECONDITION: ToString appends the host, and for a one-segment path the FIRST '/' in "
            + "it therefore belongs to the host — which is what made the old parse wrong");
        hosted.ToString().IndexOf('/').Should().Be("solo~portal".Length,
            "…and precisely there, so splitting on it yielded 'solo~portal' as the kind");

        hosted.Type.Should().Be("solo",
            "Type is Segments[0]: the type segment by construction, with no host and no parsing");
    }

    /// <summary>
    /// 🚨 A gauge that throws is swallowed by the metrics infrastructure and the instrument goes
    /// SILENT — indistinguishable from a mesh with no hubs, which is the exact failure this issue
    /// is about. A disposed root is the cheapest way to fault the walk.
    /// </summary>
    [HubFact]
    public async Task ADisposedRoot_ReportsNothing_RatherThanThrowing()
    {
        var host = GetHost();
        var doomed = (MessageHub)host.GetHostedHub(new Address("victim", "metrics-doomed"), c => c)!;
        using var metrics = new PlatformMetrics(doomed);
        doomed.Dispose();
        // 🚨 Dispose() RETURNS before the hub is Dead — teardown drains in-flight work, so the run
        // level walks Quiescing → DisposeHostedHubs → … → Dead afterwards. Scraping straight after
        // the call measures a hub that is winding DOWN, not one that is gone, and asserting
        // "nothing" there would be asserting the wrong property (a hub still draining SHOULD be
        // reported — its run level is exactly what the runlevel tag is for). Wait for the terminal
        // state, which is what the guard under test keys on.
        await doomed.DisposalCompleted.FirstOrDefaultAsync().Timeout(TimeSpan.FromSeconds(15)).Await();
        doomed.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "PRECONDITION: the guard keys on the terminal state, so the test must reach it");

        // Deliberately NOT an assertion helper: the property under test is that nothing escapes,
        // and the plainest way to assert that is to let the call stand. A throw fails the test with
        // the real exception, which is more useful than a wrapped one.
        var taken = Scrape(metrics);

        // 🚨 BOTH halves, and the second one is why this assertion changed. `NotBeNull` on a list
        // that can never be null is vacuous — it would have passed just as well if the gauge kept
        // emitting after disposal, i.e. it tested the half named in the method name and not the
        // half named in the sentence. The throw-safety is asserted by the call standing above; the
        // "reports nothing" is asserted here.
        taken.Should().BeEmpty(
            "a disposed root is a torn-down mesh: the scrape must neither propagate out of the "
            + "gauge — an exception there stops the instrument reporting for the life of the "
            + "process, silently, and a silent meter is indistinguishable from a mesh with no "
            + "hubs — nor report a phantom population for a mesh that is gone");
    }
}
