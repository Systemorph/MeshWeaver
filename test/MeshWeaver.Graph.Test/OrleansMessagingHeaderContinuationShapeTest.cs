using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The <c>Orleans.Messaging[100071]</c> parse shape of #2321, VERBATIM.</b>
///
/// <para><b>What production filed.</b> <c>Admin/_LogIncident/ce8d2e8715bf9aa0</c> — five red bursts
/// across four pods and three ReplicaSet revisions over ~91 hours. Four of the five samples were
/// the bare console header <c>fail: Orleans.Messaging[100071]</c> and nothing else; one carried the
/// six-space-indented continuation <c>Failed to address message Request […]->[ messagehub/…/_Activity/compile-state]
/// …DeliverMessage(…) #3F065532253D3F17</c>. The incident's <c>normalizedMessage</c> was
/// <c>Orleans.Messaging[{n}]</c> — the HEADER with its event id masked, fabricated by the pre-#2222
/// parser when a burst had no body — so the fingerprint named a component and no defect, and every
/// later <c>100071</c> failure of any cause folded onto it (the collision bucket shared with #2357).</para>
///
/// <para><b>What this pins.</b> #2222 (<c>6b13ef888</c>) reworked the parser: a bodyless burst keeps an
/// EMPTY message and is refused by the aggregator (<c>ParsedBurst.IsHeaderOnly</c>), continuation
/// lines are re-attached per pod (<c>BurstAggregator.SplitPodBursts</c>), and the ingest boundary
/// mirrors the refusal (<c>LogIncidentReportSanity</c>, #2466). Nobody had run THIS log shape through
/// that code — the live incident node is in the Admin partition and unreadable from a dev session —
/// so this test feeds the issue's Evidence block, line for line, through the same call the watcher
/// makes (<c>LogWatchWorker</c> → <c>BurstAggregator.Aggregate</c> with the watcher's option
/// defaults) and asserts the three properties the ticket left open.</para>
/// </summary>
public class OrleansMessagingHeaderContinuationShapeTest
{
    private const string Namespace = "memex-cloud";

    // The four pods from the incident's Evidence table.
    private const string Pod9t64t = "memex-portal-deployment-65fbcf6cd5-9t64t";
    private const string PodSpgc9 = "memex-portal-deployment-65fbcf6cd5-spgc9";
    private const string Pod8ccj2 = "memex-portal-deployment-999c9c79d-8ccj2";
    private const string PodJr82z = "memex-portal-deployment-76c65fb457-jr82z";

    private const string Header = "fail: Orleans.Messaging[100071]";

    /// <summary>The one continuation line the incident captured — Orleans' own text, verbatim.</summary>
    private const string Continuation =
        "      Failed to address message Request [S10.244.3.46:11111:146491218 "
        + "sys.client/hosted-10.244.3.46:11111@146491218]->[ "
        + "messagehub/Doc/DataMesh/PythonPandasNode/PandasExplorer/_Activity/compile-state] "
        + "MeshWeaver.Connection.Orleans.IMessageHubGrain.DeliverMessage(MeshWeaver.Messaging.IMessageDelivery) "
        + "#3F065532253D3F17";

    /// <summary>
    /// The incident node's recorded <c>normalizedMessage</c>: the console header, masked. This is the
    /// string the pre-#2222 parser fabricated for a bodyless burst — the shape every assertion below
    /// must NOT see again.
    /// </summary>
    private const string FabricatedHeaderEcho = "Orleans.Messaging[{n}]";

    private static LogLine At(string utc, string pod, string text) =>
        new(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture), Namespace, pod, text);

    /// <summary>
    /// The issue's Evidence block as <c>LokiClient.Flatten</c> hands it to the aggregator: one entry
    /// per line, oldest first, the timestamp and pod as stream labels and the text after them as the
    /// line. (The <c>{ts} {pod}</c> prefixes in the issue are what <c>LogIncidentFiler</c> prints in
    /// front of each sample; the continuation was one sample joined to its header by a newline.)
    /// </summary>
    private static ImmutableList<LogLine> Incident() => ImmutableList.Create(
        At("2026-08-23T12:10:13Z", Pod9t64t, Header),
        At("2026-08-23T12:10:19Z", PodSpgc9, Header),
        At("2026-08-23T12:11:00Z", Pod9t64t, Header),
        At("2026-08-23T12:11:00.001Z", Pod9t64t, Continuation),
        At("2026-08-23T19:34:30Z", Pod8ccj2, Header),
        At("2026-08-26T06:55:33Z", PodJr82z, Header));

    /// <summary>
    /// The watcher's own call — <c>LogWatchWorker.Collect</c> — with <c>LogWatcherOptions</c>' defaults:
    /// 5 samples per report, 2000 characters each, no ignored categories, a 20-variant fold budget.
    /// </summary>
    private static BurstAggregation Aggregate() =>
        BurstAggregator.Aggregate(
            Incident(),
            maxSamples: 5,
            maxSampleLength: 2000,
            ignoreCategories: ImmutableList<string>.Empty,
            maxVariantsPerSite: 20);

    /// <summary>
    /// (a) The burst WITH the continuation is the incident, and its message is Orleans' text with
    /// the volatile parts masked — never the header echo. Without a body-bearing burst the shape has
    /// nothing to ticket; with one, the ticket must carry what Orleans said.
    /// </summary>
    [Fact]
    public void The_burst_with_the_continuation_is_the_only_incident_and_carries_the_message()
    {
        var report = Aggregate().Reports.Should().ContainSingle(
            "one of the five bursts carried a body, and only that one is a fault report").Which;

        report.Category.Should().Be("Orleans.Messaging");
        report.NormalizedMessage.Should().StartWith("Failed to address message",
            "the message is the continuation line, not the console header");
        report.NormalizedMessage.Should().NotBe(FabricatedHeaderEcho,
            "the header echo is the fabricated message of #2321's incident node");
        report.NormalizedMessage.Should().NotContain("Orleans.Messaging[",
            "no part of the console header may leak into the message");
        report.NormalizedMessage.Should().Contain("{path}",
            "the target hub address is an instance, masked so every dead-hub target folds together");
        report.NormalizedMessage.Should().Contain("{hex}",
            "the Orleans message id is per message and must not fork the fingerprint");
        report.NormalizedMessage.Should().NotContain("3F065532253D3F17",
            "the message id is the volatile part");

        report.NormalizedDetail.Should().Be(report.NormalizedMessage,
            "Orleans logs no exception here, so the logged message is all the fingerprint has");
        report.ExceptionType.Should().BeNull("the continuation names no exception type");
        report.TopFrame.Should().BeNull("there is no stack trace in this shape");

        report.Occurrences.Should().Be(1);
        report.Pods.Should().ContainSingle().Which.Should().Be(Pod9t64t);
        report.FirstSeen.Should().Be(DateTimeOffset.Parse("2026-08-23T12:11:00Z", CultureInfo.InvariantCulture),
            "the report opens at the header of the burst that carried the body");

        var sample = report.Samples.Should().ContainSingle().Which;
        sample.Line.Should().StartWith(Header, "the evidence keeps the header");
        sample.Line.Should().Contain("Failed to address message",
            "and the continuation the parser re-attached to it");
        sample.Line.Should().Contain("messagehub/Doc/DataMesh/PythonPandasNode/PandasExplorer/_Activity/compile-state",
            "the evidence names the target verbatim — the masked message no longer can, and a "
            + "responder needs it to see WHICH hub was gone");
    }

    /// <summary>
    /// (b) The four bare headers are refused: they open no report, and nothing in the aggregation
    /// ever carries the header echo. They are still counted as red bursts and surfaced as bodyless
    /// captures, with the one whose own pod moved on marked final and the three at their pods' window
    /// edge marked recoverable — so the watcher can hold its cursor at the header and read the body
    /// next poll rather than filing a bodyless incident and dropping the body when it arrives.
    /// </summary>
    [Fact]
    public void The_four_bare_headers_are_refused_and_never_fingerprint_as_the_header_echo()
    {
        var aggregation = Aggregate();

        aggregation.Reports.Should().NotContain(r => r.NormalizedMessage == FabricatedHeaderEcho,
            "a fingerprint keyed on category+event id names a component and no defect");
        aggregation.Reports.Should().NotContain(r => r.NormalizedMessage.Length == 0,
            "a bodyless burst is never fingerprinted, not even with an empty message");
        aggregation.Reports.Should().HaveCount(1,
            "only the burst with the continuation is a fault report");

        aggregation.HeaderOnly.Should().HaveCount(4, "four of the five samples were bare headers");
        aggregation.HeaderOnly.Should().OnlyContain(h => h.Category == "Orleans.Messaging",
            "the category is the actionable part of a bodyless capture and must survive");
        aggregation.RedBursts.Should().Be(5, "all five WERE red bursts and are counted honestly");
        aggregation.TotalLines.Should().Be(6);
        aggregation.FoldedSites.Should().Be(0);

        var final = aggregation.HeaderOnly.Should().ContainSingle(h => !h.AtWindowEdge,
            "exactly one header was followed by another red header from its OWN pod").Which;
        final.Pod.Should().Be(Pod9t64t);
        final.Timestamp.Should().Be(DateTimeOffset.Parse("2026-08-23T12:10:13Z", CultureInfo.InvariantCulture),
            "the 12:10:13 header on 9t64t provably had no body: the pod's next line opened another burst");

        aggregation.HeaderOnly.Where(h => h.AtWindowEdge).Select(h => h.Pod)
            .Should().Equal(PodSpgc9, Pod8ccj2, PodJr82z);
        // Each of those was the last thing its pod wrote in the window, so its body may simply be
        // past the edge; they come back in header-timestamp order across pods.
    }

    /// <summary>
    /// The parser half on its own: a bare header parses to an EMPTY message, and the guard is armed —
    /// masking the header the way the old parser did yields exactly the echo the negative assertions
    /// above check for, so they would fail if the fallback ever came back.
    /// </summary>
    [Fact]
    public void A_bare_header_parses_to_an_empty_message_not_a_fabricated_one()
    {
        var parsed = LogLineParser.Parse(ImmutableList.Create(Header));

        parsed.Should().NotBeNull();
        parsed!.Category.Should().Be("Orleans.Messaging");
        parsed.EventId.Should().Be(100071);
        parsed.Message.Should().BeEmpty("there was nothing after the header");
        parsed.NormalizedMessage.Should().BeEmpty("nothing is masked into something");
        parsed.NormalizedDetail.Should().BeEmpty();
        parsed.ExceptionType.Should().BeNull();
        parsed.TopFrame.Should().BeNull();
        parsed.IsHeaderOnly.Should().BeTrue("this is the state the aggregator refuses on");

        LogLineParser.Normalize("Orleans.Messaging[100071]").Should().Be(FabricatedHeaderEcho,
            "the echo this test guards against is what the pre-#2222 fallback produced — if the "
            + "masking ever changes, the negative assertions must follow it");
    }

    /// <summary>
    /// The burst with the body, parsed alone, yields the same message the aggregation carried — the
    /// per-pod reconstruction added nothing and lost nothing.
    /// </summary>
    [Fact]
    public void The_header_plus_continuation_parses_to_orleans_own_message()
    {
        var parsed = LogLineParser.Parse(ImmutableList.Create(Header, Continuation));

        parsed.Should().NotBeNull();
        parsed!.IsHeaderOnly.Should().BeFalse();
        parsed.Message.Should().Be(Continuation.Trim(),
            "the message is the continuation, whitespace-trimmed and otherwise verbatim");
        parsed.NormalizedMessage.Should().Be(
            Aggregate().Reports.Should().ContainSingle().Which.NormalizedMessage,
            "the aggregated report's message is this burst's own");
    }

    /// <summary>
    /// (c) The ingest boundary refuses the incident as it was RECORDED — the header echo with the
    /// five evidence lines — for any watcher vintage that still fabricates it, and passes the report
    /// a current parser produces for the same lines. Both halves on the same shape, so the two
    /// definitions of "carries no diagnostic" are proven to agree here.
    /// </summary>
    [Fact]
    public void The_sanity_check_refuses_the_recorded_incident_and_passes_the_reparsed_one()
    {
        var seen = DateTimeOffset.Parse("2026-08-26T06:55:33Z", CultureInfo.InvariantCulture);
        var recorded = new LogIncidentReport
        {
            Fingerprint = "ce8d2e8715bf9aa0",
            Category = "Orleans.Messaging",
            Severity = LogSeverity.Error,
            NormalizedMessage = FabricatedHeaderEcho,
            NormalizedDetail = FabricatedHeaderEcho,
            Namespace = Namespace,
            Pods = ImmutableList.Create(Pod9t64t, PodSpgc9, Pod8ccj2, PodJr82z),
            Occurrences = 5,
            FirstSeen = DateTimeOffset.Parse("2026-08-23T12:10:13Z", CultureInfo.InvariantCulture),
            LastSeen = seen,
            Samples = ImmutableList.Create(
                new LogSample(DateTimeOffset.Parse("2026-08-23T12:10:13Z", CultureInfo.InvariantCulture), Pod9t64t, Header),
                new LogSample(DateTimeOffset.Parse("2026-08-23T12:10:19Z", CultureInfo.InvariantCulture), PodSpgc9, Header),
                new LogSample(DateTimeOffset.Parse("2026-08-23T12:11:00Z", CultureInfo.InvariantCulture), Pod9t64t, Header + "\n" + Continuation),
                new LogSample(DateTimeOffset.Parse("2026-08-23T19:34:30Z", CultureInfo.InvariantCulture), Pod8ccj2, Header),
                new LogSample(seen, PodJr82z, Header)),
        };

        LogIncidentReportSanity.IsUndiagnosable(recorded).Should().BeTrue(
            "the recorded incident's whole identity is (category, event id) — the ingest must refuse "
            + "it however old the watcher that sent it");

        var reparsed = Aggregate().Reports.Should().ContainSingle().Which;
        LogIncidentReportSanity.IsUndiagnosable(reparsed).Should().BeFalse(
            "the same lines through a current parser carry Orleans' own message, which is diagnosable");

        var gap = LogIncidentReportSanity.AsCaptureGap(recorded);
        gap.Fingerprint.Should().Be(LogIncidentReportSanity.HeaderOnlyFingerprint(Namespace),
            "a refused report folds onto the per-namespace capture-gap incident");
        gap.Samples[^1].Line.Should().Contain("ce8d2e8715bf9aa0",
            "the refused fingerprint survives as provenance");
    }
}
