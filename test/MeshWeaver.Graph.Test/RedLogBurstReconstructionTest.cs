using System;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A red-log incident must carry a defect, not just a component</b> — issues #2153 and #2222.
///
/// <para><b>What production filed.</b> Two tickets, minutes of triage each, both unactionable and
/// both about the SAME thing. #2222: an Error from
/// <c>MeshWeaver.Layout.Composition.LayoutAreaHost</c> with "no message body, no exception, and no
/// stack frame" — nothing on the wire but the console header. #2153: an Error from
/// <c>MeshWeaver.Mesh.CreateNode</c> whose message was intact but whose exception was gone, filed
/// with the conclusion that the call site "does not pass the caught exception to the logger". It
/// does, and always did (<c>logger.LogError(ex, …)</c>).</para>
///
/// <para><b>The actual defect was in the READER.</b> The watcher's Loki query is namespace-wide
/// (deliberately — a line filter returns headers without their stack traces), every replica writes
/// to its own stream, the CRI log format stamps each LINE with its own timestamp, and the collector
/// merges those streams by timestamp. So a burst whose header and stack trace fall in different
/// milliseconds has any other pod's line from in between sorted into the middle of it — and the
/// grouper, which reconstructed over the merged sequence, ENDED the burst there and dropped
/// everything after. Cut after the header ⇒ #2222. Cut after the message ⇒ #2153. Both then
/// fingerprinted on whatever survived, and the bodyless one could only key on category + event id:
/// a token that names a component and no defect, into which every later truncated burst from that
/// site would fold.</para>
///
/// <para>These tests pin both halves of the fix: reconstruction is per POD, so interleaving cannot
/// truncate a burst; and a burst that arrives bodyless anyway is never fingerprinted — it is
/// surfaced as what it is, a capture with no body.</para>
/// </summary>
public class RedLogBurstReconstructionTest
{
    private const string Pod = "memex-portal-deployment-867dc45877-wtcks";
    private const string OtherPod = "memex-portal-deployment-867dc45877-xqs4p";
    private const string Namespace = "memex-cloud";

    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-08-24T14:21:24Z", System.Globalization.CultureInfo.InvariantCulture);

    private static LogLine Line(int millis, string pod, string text) =>
        new(T0.AddMilliseconds(millis), Namespace, pod, text);

    /// <summary>
    /// #2153 verbatim: the CreateNode burst, with another pod's line landing between its message and
    /// its exception. <b>Fail-without:</b> the burst ended at the interleaved line, the exception was
    /// discarded, and the incident carried <c>exceptionType</c>, <c>topFrame</c> and
    /// <c>normalizedDetail</c> all empty — which is exactly what the ticket reported.
    /// </summary>
    [Fact]
    public void Another_pods_line_in_the_middle_does_not_truncate_a_burst()
    {
        var entries = ImmutableList.Create(
            Line(0, Pod, "fail: MeshWeaver.Mesh.CreateNode[0]"),
            Line(0, Pod, "      Unexpected error during node creation at Pricing/Program/Release/20260824140324-Jb5MOj-p"),
            // Another replica writes in the millisecond between the message and the exception. The
            // merged, namespace-wide stream puts it right here; nothing about it belongs to the burst.
            Line(1, OtherPod, "info: MeshWeaver.Mesh.Services.IMeshCatalog[0]"),
            Line(1, OtherPod, "      catalog refreshed"),
            Line(2, Pod, "      System.Threading.Tasks.TaskCanceledException: A task was canceled."),
            Line(2, Pod, "         at MeshWeaver.Mesh.MeshExtensions.HandleCreateNodeRequest(IMessageHub hub) in /src/MeshExtensions.cs:line 966"));

        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 10, maxSampleLength: 4000);

        var create = aggregation.Reports
            .Should().ContainSingle(r => r.Category == "MeshWeaver.Mesh.CreateNode").Which;
        create.ExceptionType.Should().Be("System.Threading.Tasks.TaskCanceledException",
            "the exception line belongs to this burst however many other pods wrote in between");
        create.TopFrame.Should().Contain("HandleCreateNodeRequest",
            "the stack trace belongs to this burst too — it is what locates the fault");
        create.NormalizedDetail.Should().Contain("task was canceled",
            "the detail — the exception's OWN words — is what tells two faults at one site apart");
        aggregation.HeaderOnly.Should().BeEmpty("nothing here was bodyless");
    }

    /// <summary>
    /// #2222 verbatim: another pod's RED header lands immediately after this one's. The
    /// interleaved header used to steal the following continuation lines AND leave the first burst
    /// with nothing but its header — one truncated incident plus one degenerate one, from two
    /// perfectly well-formed log events.
    /// </summary>
    [Fact]
    public void An_interleaved_red_header_from_another_pod_steals_nothing()
    {
        var entries = ImmutableList.Create(
            Line(0, Pod, "fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]"),
            Line(1, OtherPod, "fail: MeshWeaver.Mesh.MeshNode[0]"),
            Line(1, OtherPod, "      [DeleteNode] not-found path=Chess/Play partial-deleted=0"),
            Line(2, Pod, "      Rendering failed for area Overview"),
            Line(2, Pod, "      System.InvalidOperationException: Sequence contains no elements"),
            Line(2, Pod, "         at MeshWeaver.Layout.Composition.LayoutAreaHost.RenderArea(RenderingContext context)"));

        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 10, maxSampleLength: 4000);

        aggregation.HeaderOnly.Should().BeEmpty(
            "neither burst is bodyless — the LayoutAreaHost one only looked that way because the "
            + "other pod's header cut it off");

        var layout = aggregation.Reports
            .Should().ContainSingle(r => r.Category == "MeshWeaver.Layout.Composition.LayoutAreaHost").Which;
        layout.ExceptionType.Should().Be("System.InvalidOperationException");
        layout.TopFrame.Should().Contain("LayoutAreaHost.RenderArea");

        var delete = aggregation.Reports
            .Should().ContainSingle(r => r.Category == "MeshWeaver.Mesh.MeshNode").Which;
        delete.NormalizedMessage.Should().Contain("DeleteNode",
            "the other pod's burst keeps its own message and gains none of this one's lines");
    }

    /// <summary>
    /// The guarantee behind the fix: a burst that really is nothing but a header opens NO incident.
    /// Its only possible identity is category + event id — a fingerprint that names a component and
    /// no defect, which then swallows every later bodyless capture from the same site. It is
    /// reported as a capture with no body instead, which names the same category and says something
    /// true about it.
    /// </summary>
    [Fact]
    public void A_bodyless_burst_is_reported_but_never_fingerprinted()
    {
        var entries = ImmutableList.Create(
            Line(0, Pod, "fail: MeshWeaver.Layout.Composition.LayoutAreaHost[0]"),
            // A following level header from the SAME pod proves the body is not merely unread:
            // the next log event has already begun, so there was nothing after that header.
            Line(1, Pod, "info: MeshWeaver.Hosting.MeshNodeStreamCache[0]"),
            Line(1, Pod, "      handle opened"));

        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 10, maxSampleLength: 4000);

        aggregation.Reports.Should().BeEmpty(
            "a red line with no message, no exception and no frame carries no defect to ticket");
        aggregation.RedBursts.Should().Be(1, "it WAS a red burst — it is counted, never hidden");
        var bodyless = aggregation.HeaderOnly.Should().ContainSingle().Which;
        bodyless.Category.Should().Be("MeshWeaver.Layout.Composition.LayoutAreaHost",
            "the category is the actionable part and must survive");
        bodyless.AtWindowEdge.Should().BeFalse(
            "another event from this pod followed, so the body was not merely on the other side of "
            + "the window's edge");
    }

    /// <summary>
    /// The recoverable case, and why the two are told apart: the header is the LAST thing this pod
    /// produced in the window, so its body is on the other side of the edge. Marking it lets the
    /// watcher hold its cursor at the header and read the burst whole next poll, instead of filing
    /// a bodyless incident for it and dropping the body when it arrives headerless.
    /// </summary>
    [Fact]
    public void A_burst_still_open_at_the_window_edge_is_marked_recoverable()
    {
        var entries = ImmutableList.Create(
            Line(0, OtherPod, "fail: MeshWeaver.Mesh.MeshNode[0]"),
            Line(0, OtherPod, "      [DeleteNode] not-found path=Chess/Play partial-deleted=0"),
            Line(5, Pod, "fail: MeshWeaver.Mesh.CreateNode[0]"));

        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 10, maxSampleLength: 4000);

        var open = aggregation.HeaderOnly.Should().ContainSingle().Which;
        open.AtWindowEdge.Should().BeTrue("no further line from this pod followed it in the window");
        open.Timestamp.Should().Be(T0.AddMilliseconds(5),
            "the cursor must resume AT the header so the next poll reads the burst whole");
        aggregation.Reports.Should().ContainSingle(r => r.Category == "MeshWeaver.Mesh.MeshNode",
            "the complete burst from the other pod is unaffected");
    }

    /// <summary>
    /// The masking / dedup contract still holds across the per-pod grouping: the same fault on two
    /// replicas is ONE incident with both pods on it, not one incident per replica.
    /// </summary>
    [Fact]
    public void The_same_fault_on_two_pods_is_still_one_incident()
    {
        var entries = ImmutableList.Create(
            Line(0, Pod, "fail: MeshWeaver.Mesh.CreateNode[0]"),
            Line(0, Pod, "      Unexpected error during node creation at Pricing/Program/Release/aaa"),
            Line(0, Pod, "      System.Threading.Tasks.TaskCanceledException: A task was canceled."),
            Line(1, OtherPod, "fail: MeshWeaver.Mesh.CreateNode[0]"),
            Line(1, OtherPod, "      Unexpected error during node creation at Reinsurance/Acceptance/Release/bbb"),
            Line(1, OtherPod, "      System.Threading.Tasks.TaskCanceledException: A task was canceled."));

        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 10, maxSampleLength: 4000);

        var report = aggregation.Reports.Should().ContainSingle().Which;
        report.Occurrences.Should().Be(2);
        report.Pods.Should().Contain(Pod).And.Contain(OtherPod);
    }

    /// <summary>
    /// Bursts come back in emission order even though they are reconstructed per pod — the reports'
    /// FirstSeen and the evidence samples depend on it.
    /// </summary>
    [Fact]
    public void Bursts_are_returned_in_timestamp_order_across_pods()
    {
        var entries = ImmutableList.Create(
            Line(10, Pod, "fail: MeshWeaver.Mesh.CreateNode[0]"),
            Line(10, Pod, "      later burst"),
            Line(0, OtherPod, "fail: MeshWeaver.Mesh.MeshNode[0]"),
            Line(0, OtherPod, "      earlier burst"));

        var aggregation = BurstAggregator.Aggregate(
            entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp)),
            maxSamples: 10, maxSampleLength: 4000);

        aggregation.Reports.Select(r => r.Category).Should().Equal(
            "MeshWeaver.Mesh.MeshNode", "MeshWeaver.Mesh.CreateNode");
    }
}
