using System.Collections.Immutable;
using Xunit;
using AiThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// The recovery rule for an incident whose triage never came back. <c>Triaging</c> is the one
/// lifecycle state nothing else can leave: the agent is supposed to write a draft and ask to File,
/// and the ingest path's <c>NextRequest</c> re-triages <c>New</c> and <c>Failed</c> but never
/// <c>Triaging</c>. So when a round ends without writing anything back — the configured triage
/// agent is missing from the deployment's catalog, the model is down, the portal restarted
/// mid-round — the incident is both invisible and unrecoverable, and stays parked even after the
/// missing dependency lands. Nineteen incidents accumulated exactly that way on
/// memex.systemorph.com while <c>LogTriage</c> was absent from the served agent catalog.
///
/// <para>These pin the two halves of the repair: <b>noticing</b> a stranded incident, and
/// <b>what it means</b> once the thread confirms the round is over — including the case that must
/// stay untouched, a successful triage the reconcile merely raced.</para>
/// </summary>
public class StrandedTriageTest
{
    private const string ThreadPath = "Admin/_LogIncident/abc123/_Thread/triage-abc";
    private const string Agent = "LogTriage";

    private static LogIncident Incident(
        LogIncidentStatus status,
        LogIncidentRequest requested = LogIncidentRequest.None,
        string? threadPath = ThreadPath) => new()
    {
        Fingerprint = "abc123",
        Category = "MeshWeaver.Connection.Orleans.OrleansRoutingService",
        Severity = LogSeverity.Error,
        Status = status,
        RequestedStatus = requested,
        TriageThreadPath = threadPath,
    };

    private static AiThread Thread(
        MeshWeaver.AI.ThreadExecutionStatus status, int messages, bool pending = false) => new()
    {
        Status = status,
        Messages = Enumerable.Range(0, messages).Select(i => $"m{i}").ToImmutableList(),
        PendingUserMessages = pending
            ? ImmutableDictionary<string, MeshWeaver.AI.ThreadMessage>.Empty
                .Add("queued", new MeshWeaver.AI.ThreadMessage { Role = "user", Text = "triage it" })
            : ImmutableDictionary<string, MeshWeaver.AI.ThreadMessage>.Empty,
    };

    // ── Noticing ──────────────────────────────────────────────────────────────

    [Fact]
    public void TriagingWithAThread_IsPickedUpForReconcile()
        => LogIncidentControlPlane.NeedsTriageReconcile(Incident(LogIncidentStatus.Triaging))
            .Should().BeTrue("a Triaging incident with nothing requested can never leave that state "
                             + "on its own — the thread is the only evidence of what happened");

    [Theory]
    [InlineData(LogIncidentStatus.New)]
    [InlineData(LogIncidentStatus.Filed)]
    [InlineData(LogIncidentStatus.Failed)]
    [InlineData(LogIncidentStatus.Suppressed)]
    public void OtherStatuses_AreNotReconciled(LogIncidentStatus status)
        => LogIncidentControlPlane.NeedsTriageReconcile(Incident(status))
            .Should().BeFalse("only Triaging is unreachable by the ingest path's retry rule");

    [Fact]
    public void TriagingWithSomethingRequested_IsLeftToTheNormalPath()
        => LogIncidentControlPlane
            .NeedsTriageReconcile(Incident(LogIncidentStatus.Triaging, LogIncidentRequest.File))
            .Should().BeFalse("a pending request means the agent DID come back — the File "
                              + "transition is what runs, not the repair");

    [Fact]
    public void TriagingWithoutAThread_IsNotGuessedAt()
        => LogIncidentControlPlane
            .NeedsTriageReconcile(Incident(LogIncidentStatus.Triaging, threadPath: null))
            .Should().BeFalse("with no thread there is no evidence to reconcile against, and "
                              + "acting anyway would be a watchdog");

    // ── Is the round actually over? ───────────────────────────────────────────

    [Fact]
    public void AFinishedRound_IsRecognised()
        => LogIncidentControlPlane
            .RoundFinished(Thread(MeshWeaver.AI.ThreadExecutionStatus.Idle, messages: 2))
            .Should().BeTrue();

    [Fact]
    public void ARunningRound_IsNotTouched()
        => LogIncidentControlPlane
            .RoundFinished(Thread(MeshWeaver.AI.ThreadExecutionStatus.Executing, messages: 2))
            .Should().BeFalse("the agent is still streaming — failing it here would abort live work");

    [Fact]
    public void AJustCreatedThread_IsNotMistakenForAFinishedOne()
    {
        // Idle with the first message still queued is the state a thread is born in: the
        // submission watcher has not claimed the round yet. Reading that as "finished, produced
        // nothing" would fail every incident microseconds after triage started.
        LogIncidentControlPlane
            .RoundFinished(Thread(MeshWeaver.AI.ThreadExecutionStatus.Idle, messages: 0, pending: true))
            .Should().BeFalse();
        LogIncidentControlPlane
            .RoundFinished(Thread(MeshWeaver.AI.ThreadExecutionStatus.Idle, messages: 0))
            .Should().BeFalse();
    }

    // ── What a finished-but-empty round means ─────────────────────────────────

    [Fact]
    public void FinishedWithoutADraft_FailsTheIncidentSoItIsRetried()
    {
        var outcome = LogIncidentControlPlane.StrandedTriageOutcome(
            Incident(LogIncidentStatus.Triaging), ThreadPath, Agent);

        outcome.Should().NotBeNull();
        // Failed — NOT a new terminal state: NextRequest re-triages Failed, so the incident heals
        // itself on the next recurrence once the missing agent (or model) is back.
        outcome!.Status.Should().Be(LogIncidentStatus.Failed);
        outcome.RequestedStatus.Should().Be(LogIncidentRequest.None);
        outcome.Error.Should().Contain(Agent, "the operator needs to know WHICH agent never answered")
            .And.Contain(ThreadPath, "and where to read what the round actually said");
    }

    [Fact]
    public void ASuccessfulTriageTheReconcileRaced_IsNeverOverwritten()
    {
        // The agent patched the incident (draft + RequestedStatus=File) and the round then ended.
        // Both facts are true at once, and the reconcile sees the finished round first. It re-reads
        // the incident at write time and must stand down — overwriting here would throw away a
        // ticket that was ready to file.
        LogIncidentControlPlane.StrandedTriageOutcome(
                Incident(LogIncidentStatus.Triaging, LogIncidentRequest.File), ThreadPath, Agent)
            .Should().BeNull();

        LogIncidentControlPlane.StrandedTriageOutcome(
                Incident(LogIncidentStatus.Filed), ThreadPath, Agent)
            .Should().BeNull();
    }
}
