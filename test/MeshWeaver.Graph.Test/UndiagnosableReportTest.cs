using System;
using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The ingest boundary must be able to refuse an undiagnosable report itself</b> (#2466).
///
/// <para><c>BurstAggregator</c> already refuses to fingerprint a bodyless burst — but the
/// aggregator runs in the WATCHER, a separately shipped binary. Production proved the gap: the
/// exclusion merged on 2026-08-25, yet the <c>mw-log-watcher</c> image in the cluster
/// (<c>memex-log-watcher:1.3.0</c>, built 2026-08-10) kept fingerprinting bare console headers —
/// minting <c>Admin/_LogIncident/b0a265175dc88c80</c> (<c>OrleansRoutingService[{n}]</c>,
/// 2026-08-26) and folding recurrences into <c>ce8d2e8715bf9aa0</c>
/// (<c>Orleans.Messaging[{n}]</c>) as late as 2026-08-27 — two days after the classifier was
/// "fixed". These tests pin the shared predicate the portal-side ingest (in
/// <c>MeshWeaver.Plugins</c>) uses to close that hole for every watcher vintage, and the reroute
/// that preserves the evidence instead of dropping it.</para>
///
/// <para>This copy of the contract is the one <c>memex/Memex.Portal.Shared</c> compiles against;
/// the plugins repo carries an identical copy for the watcher and the Observability module, and
/// the portal image builds BOTH — so the predicate has to exist in each, or the type is missing
/// at runtime whenever the other copy wins the copy-local step.</para>
/// </summary>
public class UndiagnosableReportTest
{
    private static readonly DateTimeOffset Seen =
        DateTimeOffset.Parse("2026-08-26T23:04:55Z", CultureInfo.InvariantCulture);

    private static LogIncidentReport Report(
        string category,
        string message,
        string? exceptionType = null,
        string? topFrame = null,
        string detail = "") => new()
    {
        Fingerprint = "b0a265175dc88c80",
        Category = category,
        Severity = LogSeverity.Error,
        NormalizedMessage = message,
        NormalizedDetail = detail,
        ExceptionType = exceptionType,
        TopFrame = topFrame,
        Namespace = "memex-cloud",
        Pods = ImmutableList.Create("memex-portal-deployment-6c954fd5bf-7b4xq"),
        Occurrences = 1,
        FirstSeen = Seen,
        LastSeen = Seen,
        Samples = ImmutableList.Create(new LogSample(
            Seen,
            "memex-portal-deployment-6c954fd5bf-7b4xq",
            "fail: MeshWeaver.Connection.Orleans.OrleansRoutingService[0]")),
    };

    // ── The shapes production actually minted ─────────────────────────────────

    [Fact]
    public void The_2466_header_echo_is_undiagnosable()
        // The incident node's exact content: the pre-#2222 parser fabricated the message from the
        // console header, so the "message" is the header with the event id masked.
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "MeshWeaver.Connection.Orleans.OrleansRoutingService",
                "MeshWeaver.Connection.Orleans.OrleansRoutingService[{n}]"))
            .Should().BeTrue("its entire identity is (category, event id) — a component, no defect");

    [Fact]
    public void The_orleans_messaging_echo_is_undiagnosable()
        // Admin/_LogIncident/ce8d2e8715bf9aa0 — recurrences kept folding into it through 2026-08-27.
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "Orleans.Messaging", "Orleans.Messaging[{n}]"))
            .Should().BeTrue();

    [Fact]
    public void A_truly_empty_report_is_undiagnosable()
        // What a CURRENT parser would produce if a bodyless burst ever reached fingerprinting.
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "MeshWeaver.Connection.Orleans.OrleansRoutingService", ""))
            .Should().BeTrue();

    [Fact]
    public void A_bare_category_echo_is_undiagnosable()
        // A header without an event-id bracket echoes as the category alone.
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "Orleans.Messaging", "Orleans.Messaging"))
            .Should().BeTrue();

    [Fact]
    public void A_category_with_digits_still_matches_its_own_masked_echo()
        // The masking rewrites digits in the MESSAGE copy but not in the category field, so the
        // comparison must run the category through the same masking ("memory-7" → "memory-{n}").
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "memory-7", "memory-{n}[{n}]"))
            .Should().BeTrue();

    // ── What must always pass ─────────────────────────────────────────────────

    [Fact]
    public void A_real_message_is_diagnosable()
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "MeshWeaver.Mesh.CreateNode", "Unexpected error during node creation at {path}"))
            .Should().BeFalse("a logged message, however short, is something an engineer can act on");

    [Fact]
    public void An_exception_type_is_diagnosable_even_with_no_message()
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "OrleansRoutingService", "", exceptionType: "Orleans.Streams.QueueCacheMissException"))
            .Should().BeFalse();

    [Fact]
    public void A_stack_frame_is_diagnosable_even_with_no_message()
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "OrleansRoutingService", "",
                topFrame: "MeshWeaver.Connection.Orleans.OrleansRoutingService.RegisterStream"))
            .Should().BeFalse();

    [Fact]
    public void A_detail_beyond_the_message_is_diagnosable()
        => LogIncidentReportSanity.IsUndiagnosable(Report(
                "Orleans.Messaging", "Orleans.Messaging[{n}]",
                detail: "Failed to address message Request {hex}"))
            .Should().BeFalse("the detail carries the fault's own words");

    /// <summary>
    /// The reroute target must pass the guard it feeds, or a refused report would be refused again
    /// on the way in and the reroute would never terminate. (The watcher's other pipeline findings
    /// are pinned against the same predicate in the plugins repo, next to <c>LogPipelineGap</c>.)
    /// </summary>
    [Fact]
    public void The_capture_gap_finding_is_never_refused()
        => LogIncidentReportSanity.IsUndiagnosable(
                LogIncidentReportSanity.AsCaptureGap(Report("Orleans.Messaging", "Orleans.Messaging[{n}]")))
            .Should().BeFalse("the finding names what went wrong in its message");

    // ── The reroute ───────────────────────────────────────────────────────────

    [Fact]
    public void The_reroute_folds_onto_the_watchers_own_capture_gap_incident()
    {
        var gap = LogIncidentReportSanity.AsCaptureGap(Report(
            "MeshWeaver.Connection.Orleans.OrleansRoutingService",
            "MeshWeaver.Connection.Orleans.OrleansRoutingService[{n}]"));

        // Must equal the fingerprint LogPipelineGap.HeaderOnlyReport uses (pinned against the real
        // report in the plugins repo), so a refused report and a current watcher's own finding land
        // on the SAME per-namespace incident.
        gap.Fingerprint.Should().Be("log-burst-header-only-memex-cloud");
        gap.Category.Should().Be(LogIncidentReportSanity.PipelineCategory);
        gap.Namespace.Should().Be("memex-cloud");
        gap.Occurrences.Should().Be(1);
        gap.Pods.Should().ContainSingle().Which.Should().Be("memex-portal-deployment-6c954fd5bf-7b4xq");
    }

    [Fact]
    public void The_reroute_keeps_the_original_category_and_fingerprint_as_evidence()
    {
        var gap = LogIncidentReportSanity.AsCaptureGap(Report(
            "Orleans.Messaging", "Orleans.Messaging[{n}]"));

        gap.Samples.Should().HaveCountGreaterThan(1, "the original samples must survive the reroute");
        gap.Samples[0].Line.Should()
            .Contain("Orleans.Messaging", "the finding must still say WHERE the capture came from")
            .And.Contain("b0a265175dc88c80", "and which degenerate fingerprint was refused");
        gap.Samples[1].Line.Should().StartWith("fail:", "the bare header itself is the evidence");
    }

    [Fact]
    public void The_reroute_never_downgrades_a_critical()
        => LogIncidentReportSanity.AsCaptureGap(
                Report("Orleans.Messaging", "") with { Severity = LogSeverity.Critical })
            .Severity.Should().Be(LogSeverity.Critical);
}
