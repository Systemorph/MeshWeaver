using System.Collections.Immutable;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// The recurrence policy: what a repeat report does to an existing incident. This is the rule that
/// keeps a continuously-firing fault from spamming its own GitHub issue, and the rule that makes
/// sure a fault whose triage was interrupted is not stranded forever.
/// </summary>
public class LogIncidentMergeTest
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly LogWatchOptions Options = new() { MaxSamples = 3 };

    private static LogIncident Existing(LogIncidentStatus status, long occurrences = 10) => new()
    {
        Fingerprint = "abc123",
        Category = "MeshWeaver.Data.MeshDataSource",
        Severity = LogSeverity.Error,
        Occurrences = occurrences,
        FirstSeen = T0,
        LastSeen = T0.AddMinutes(1),
        Status = status,
        Samples = ImmutableList.Create(new LogSample(T0, "pod-a", "old line")),
        Pods = ImmutableList.Create("pod-a"),
        // A Filed incident ALWAYS carries its issue — the filer writes the status and the link in
        // one go, and the link is what a recurrence is folded onto. A fixture that files without
        // one describes a state the system cannot reach, and would have the recurrence ask for a
        // comment on an issue that does not exist.
        IssueNumber = status == LogIncidentStatus.Filed ? 1113 : null,
        Repository = status == LogIncidentStatus.Filed ? "Systemorph/MeshWeaver" : null,
    };

    private static LogIncidentReport Repeat(
        long occurrences = 5, LogSeverity severity = LogSeverity.Error, DateTimeOffset? at = null) => new()
    {
        Fingerprint = "abc123",
        Category = "MeshWeaver.Data.MeshDataSource",
        NormalizedMessage = "boom",
        Severity = severity,
        Occurrences = occurrences,
        FirstSeen = at ?? T0.AddMinutes(5),
        LastSeen = at ?? T0.AddMinutes(5),
        Pods = ImmutableList.Create("pod-b"),
        Samples = ImmutableList.Create(new LogSample(at ?? T0.AddMinutes(5), "pod-b", "new line")),
    };

    [Fact]
    public void Merge_AccumulatesCountsPodsAndWindow()
    {
        var merged = LogIncidentIngestService.Merge(Existing(LogIncidentStatus.Filed), Repeat(), Options);

        merged.Occurrences.Should().Be(15);
        merged.LastSeen.Should().Be(T0.AddMinutes(5));
        merged.FirstSeen.Should().Be(T0);
        merged.Pods.Should().BeEquivalentTo(
            new[] { "pod-a", "pod-b" }, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Merge_BoundsSamplesKeepingTheNewest()
    {
        var report = Repeat() with
        {
            Samples = ImmutableList.Create(
                new LogSample(T0.AddMinutes(5), "pod-b", "l1"),
                new LogSample(T0.AddMinutes(6), "pod-b", "l2"),
                new LogSample(T0.AddMinutes(7), "pod-b", "l3")),
        };

        var merged = LogIncidentIngestService.Merge(Existing(LogIncidentStatus.Filed), report, Options);

        merged.Samples.Should().HaveCount(3);
        merged.Samples[^1].Line.Should().Be("l3");
        merged.Samples.Should().NotContain(s => s.Line == "old line");
    }

    [Fact]
    public void Merge_UpgradesSeverityButNeverDowngradesIt()
    {
        var upgraded = LogIncidentIngestService.Merge(
            Existing(LogIncidentStatus.Filed), Repeat(severity: LogSeverity.Critical), Options);
        upgraded.Severity.Should().Be(LogSeverity.Critical);

        var stillCritical = LogIncidentIngestService.Merge(
            Existing(LogIncidentStatus.Filed) with { Severity = LogSeverity.Critical },
            Repeat(severity: LogSeverity.Error), Options);
        stillCritical.Severity.Should().Be(LogSeverity.Critical);
    }

    [Fact]
    public void Merge_AsksForACommentOnTheFirstRecurrenceOfAFiledIncident()
        => LogIncidentIngestService.Merge(Existing(LogIncidentStatus.Filed), Repeat(), Options)
            .RequestedStatus.Should().Be(LogIncidentRequest.Comment);

    [Fact]
    public void Merge_StaysQuietInsideTheCommentInterval()
    {
        var existing = Existing(LogIncidentStatus.Filed) with
        {
            LastCommentedAt = T0.AddMinutes(4),
        };
        var options = Options with { CommentInterval = TimeSpan.FromHours(6) };

        // 1 minute since the last comment — a fault firing constantly must not comment every tick.
        LogIncidentIngestService.Merge(existing, Repeat(at: T0.AddMinutes(5)), options)
            .RequestedStatus.Should().Be(LogIncidentRequest.None);

        // …and once the window has passed, it speaks again.
        LogIncidentIngestService.Merge(existing, Repeat(at: T0.AddHours(7)), options)
            .RequestedStatus.Should().Be(LogIncidentRequest.Comment);
    }

    [Fact]
    public void Merge_NeverTicketsASuppressedIncidentButKeepsCounting()
    {
        var merged = LogIncidentIngestService.Merge(
            Existing(LogIncidentStatus.Suppressed), Repeat(), Options);

        merged.RequestedStatus.Should().Be(LogIncidentRequest.None);
        merged.Occurrences.Should().Be(15);
    }

    [Theory]
    [InlineData(LogIncidentStatus.New)]
    [InlineData(LogIncidentStatus.Failed)]
    public void Merge_RetriesTriageForAnIncidentThatNeverGotOne(LogIncidentStatus status)
        // A portal restart mid-triage, or a transient triage failure, must not strand the incident.
        => LogIncidentIngestService.Merge(Existing(status), Repeat(), Options)
            .RequestedStatus.Should().Be(LogIncidentRequest.Triage);

    [Theory]
    [InlineData(LogIncidentStatus.New)]
    [InlineData(LogIncidentStatus.Failed)]
    [InlineData(LogIncidentStatus.Triaging)]
    public void Merge_NeverReTriagesAnIncidentThatAlreadyHasAnIssue(LogIncidentStatus status)
    {
        // 🚨 The issue link outranks the status. A ticketed incident that later reads Failed — a
        // recurrence comment that errored, or the stranded-triage reconcile marking it retryable —
        // used to re-enter triage, and the agent's fresh draft then asked to File again. That chain
        // filed ROUTER_TRAFFIC eight times in seven minutes on the watcher's first live run.
        var ticketed = Existing(status) with
        {
            IssueNumber = 1113,
            Repository = "Systemorph/MeshWeaver",
        };

        LogIncidentIngestService.Merge(ticketed, Repeat(), Options).RequestedStatus
            .Should().Be(LogIncidentRequest.Comment,
                "a fault that comes back belongs on the ticket it already has");
    }

    [Fact]
    public void Merge_KeepsASuppressedIncidentQuietEvenWhenItWasTicketed()
        => LogIncidentIngestService.Merge(
                Existing(LogIncidentStatus.Suppressed) with { IssueNumber = 1113 }, Repeat(), Options)
            .RequestedStatus.Should().Be(LogIncidentRequest.None,
                "suppression is the operator's decision and outranks every other rule");

    [Theory]
    [InlineData(LogIncidentStatus.Triaging)]
    [InlineData(LogIncidentStatus.Filing)]
    public void Merge_LeavesInFlightWorkAlone(LogIncidentStatus status)
    {
        var existing = Existing(status) with { RequestedStatus = LogIncidentRequest.None };

        // Re-requesting mid-flight would start a second triage thread / file a second issue.
        LogIncidentIngestService.Merge(existing, Repeat(), Options)
            .RequestedStatus.Should().Be(LogIncidentRequest.None);
    }
}
