using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// MeshWeaver.AI.Thread, not System.Threading.Thread (which the implicit usings bring in).
using AiThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Observability;

/// <summary>
/// Drives every incident from "just ingested" to "ticket filed", reacting to the
/// <see cref="LogIncident.RequestedStatus"/> control-plane field rather than to any bespoke
/// request message (Doc/Architecture/ActivityControlPlane.md, AGENTS.md → "GetMeshNodeStream().Update()
/// is the ONLY mutation API"). The ingest service asks for triage; the triage agent asks to file;
/// this watcher performs both and writes the resulting status back.
///
/// <para>Because the state machine lives in the mesh, a portal restart mid-triage is not a lost
/// incident: the node still says what it is waiting for, and the next emission picks it up.</para>
/// </summary>
public sealed class LogIncidentControlPlane : BackgroundService
{
    /// <summary>Synced-query id for the live incident collection.</summary>
    private const string QueryId = "log-incidents";

    private readonly IMessageHub hub;
    private readonly LogIncidentFiler filer;
    private readonly LogWatchOptions options;
    private readonly ILogger<LogIncidentControlPlane>? logger;

    /// <summary>
    /// Fingerprints with work in flight. Instance state owned by this mesh-scoped singleton, never
    /// static (AGENTS.md → "No static collections"): the live query re-emits on every node change,
    /// including the change this watcher itself is making, so without it one incident would be
    /// triaged or filed twice.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Fingerprints with a stranded-triage RECONCILE in flight — a separate set from
    /// <see cref="inFlight"/> on purpose. Sharing one would let a repair pass swallow the very
    /// emission that carries the agent's <c>File</c> request: the agent's patch IS the node change
    /// that produces that emission, so if nothing changes afterwards there is no second chance and
    /// the ticket is never opened. Two sets means a reconcile can never mask a real request; they
    /// still cannot collide on a write, because the queue runs items one at a time and the
    /// reconcile re-reads the status inside its update lambda.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> reconciling = new(StringComparer.Ordinal);

    /// <summary>
    /// Work queue. Items run ONE AT A TIME via <c>Select(Run).Concat()</c> — the sanctioned way to
    /// serialise without a lock or a semaphore (AGENTS.md → "No hand-woven async/concurrency
    /// primitives"). Serialising also keeps a burst of new fingerprints from opening a dozen
    /// GitHub issues in parallel and tripping the API's abuse limits.
    /// </summary>
    private readonly Subject<LogIncidentWork> queue = new();

    /// <summary>Initializes the control plane.</summary>
    public LogIncidentControlPlane(
        IMessageHub hub,
        LogIncidentFiler filer,
        IOptions<LogWatchOptions> options,
        ILogger<LogIncidentControlPlane>? logger = null)
    {
        this.hub = hub;
        this.filer = filer;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>
    /// One queued transition. <paramref name="Reconcile"/> marks the internal repair pass rather
    /// than a requested transition: nobody asks for it on the wire, the watcher notices a
    /// <see cref="LogIncidentStatus.Triaging"/> incident and checks it against the thread it is
    /// waiting for. It is deliberately NOT a <see cref="LogIncidentRequest"/> value — the request
    /// enum is the control-plane contract an agent or an admin writes, and this is neither.
    /// </summary>
    private record LogIncidentWork(
        string Path, LogIncident Incident, LogIncidentRequest Request, bool Reconcile = false);

    /// <inheritdoc />
    // The hosted-service boundary — the single sanctioned Task bridge. Everything inside is one
    // observable chain (Doc/Architecture/AsynchronousCalls.md).
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No destination ⇒ nothing this watcher does could ever end in a ticket, and triage is not
        // free (one agent round per new fingerprint). Ingest still records incidents, so they are
        // visible and counted; they simply stay New until a repository is configured.
        if (options.DefaultRepository is not { Length: > 0 } && options.Routes.IsEmpty)
        {
            logger?.LogWarning(
                "Log-incident control plane is idle: no LogWatch:DefaultRepository and no LogWatch:Routes. "
                + "Red logs are still recorded at {Namespace} but nothing is triaged or ticketed.",
                LogIncidentNodeType.IncidentNamespace);
            return Task.CompletedTask;
        }

        logger?.LogInformation(
            "Log-incident control plane started (triage agent '{Agent}', comment interval {Interval})",
            options.TriageAgent, options.CommentInterval);

        // 🚨 ObserveOn before the work loop. Enqueue runs on the QUERY's emission thread — which is
        // a hub scheduler — and every transition below writes back to the mesh. Running those
        // writes inline on the emitting thread means posting to a hub from inside its own emission:
        // the classic action-block wedge (Doc/Architecture/ActionBlockWedgePrevention.md). The hop
        // moves the work off that thread. Concat then runs items ONE AT A TIME, which is also what
        // keeps a burst of new fingerprints from opening a dozen GitHub issues in parallel.
        var worker = queue
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(work => Observable.Defer(() => Run(work)))
            .Concat();

        var incidents = hub.GetQuery(QueryId,
                $"path:{LogIncidentNodeType.IncidentNamespace} scope:children "
                + $"nodeType:{LogIncidentNodeType.NodeType} select:path,id,name,nodeType,content")
            .Do(Enqueue)
            .Select(_ => Unit.Default)
            // A failing incident query must not take the portal down with it (a faulted
            // BackgroundService stops the host by default). Log it loudly and stop watching —
            // silence here is visible in the logs, whereas a dead portal is not recoverable.
            .Catch((Exception ex) =>
            {
                logger?.LogError(ex,
                    "Log-incident query failed — red logs will no longer be ticketed until restart");
                return Observable.Empty<Unit>();
            });

        // 🚨 SubscribeOn: opening a synced mesh query is real work, and ExecuteAsync runs on the
        // host's startup path — a BackgroundService that blocks there blocks startup itself, and
        // subscribing to a hub-backed query before the mesh has finished starting deadlocks.
        // One chain, cancelled by the host: ToTask unsubscribes both branches on shutdown.
        return incidents.Merge(worker)
            .SubscribeOn(TaskPoolScheduler.Default)
            .IgnoreElements()
            .ToTask(stoppingToken);
    }

    /// <summary>Queues every incident that is asking for something and is not already in flight.</summary>
    private void Enqueue(IEnumerable<MeshNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ContentAs<LogIncident>(hub.JsonSerializerOptions, logger) is not { } incident)
                continue;

            if (incident.RequestedStatus == LogIncidentRequest.None)
            {
                // 🚨 Triaging is the one state nothing else can leave. The agent is supposed to
                // write the draft and ask to File; if its round ends without doing that — the
                // configured agent is not in this deployment's catalog, the model is down, the
                // portal restarted mid-round — the incident sits in Triaging forever. NextRequest
                // re-triages New and Failed, never Triaging (it cannot tell "in flight" from
                // "stranded"), so it is invisible AND unrecoverable: it stays parked even after
                // the missing dependency lands. That is how nineteen incidents accumulated on
                // memex.systemorph.com while LogTriage was absent from the served agent catalog.
                //
                // The thread is the authority on whether the round is still running, so ask it.
                // This is a reconcile against observable state, NOT a timer or a retry watchdog:
                // it fires only on an emission that already carries a stranded-looking incident,
                // and does nothing at all while the round is genuinely in flight.
                if (NeedsTriageReconcile(incident) && reconciling.TryAdd(incident.Fingerprint, 0))
                    queue.OnNext(new LogIncidentWork(
                        node.Path, incident, LogIncidentRequest.Triage, Reconcile: true));
                continue;
            }

            if (!inFlight.TryAdd(incident.Fingerprint, 0))
                continue;
            queue.OnNext(new LogIncidentWork(node.Path, incident, incident.RequestedStatus));
        }
    }

    /// <summary>
    /// Whether an incident looks stranded in <see cref="LogIncidentStatus.Triaging"/> and should be
    /// checked against the thread it is waiting for. A thread path is required: without one there
    /// is no evidence to reconcile against, and guessing would be a watchdog.
    /// </summary>
    internal static bool NeedsTriageReconcile(LogIncident incident) =>
        incident.RequestedStatus == LogIncidentRequest.None
        && incident.Status == LogIncidentStatus.Triaging
        && incident.TriageThreadPath is { Length: > 0 };

    /// <summary>
    /// Runs one transition exactly as the work queue does — the seam the idempotency tests drive,
    /// so they exercise the real claim + write path rather than a re-implementation of it.
    /// </summary>
    internal IObservable<Unit> RunRequest(string path, LogIncident incident, LogIncidentRequest request)
        => Run(new LogIncidentWork(path, incident, request));

    /// <summary>
    /// Performs one transition. Always completes — a failure is written onto the incident as
    /// <see cref="LogIncidentStatus.Failed"/> rather than left to fault the work loop, so one bad
    /// incident can never stop the others (AGENTS.md → "Wedges to zero").
    ///
    /// <para>Every transition CLAIMS the request first and then acts on what the claim granted —
    /// never on <c>work.Incident</c>, which is the query snapshot that produced this work item and
    /// is stale by the time it runs.</para>
    /// </summary>
    private IObservable<Unit> Run(LogIncidentWork work)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                _ => work.Reconcile
                    ? ReconcileTriaging(work)
                    : Claim(work).SelectMany(claim => Perform(work.Path, claim)))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Incident {Fingerprint}: {Request} failed",
                    work.Incident.Fingerprint, work.Request);
                return Write(work.Path, current => current with
                {
                    Status = LogIncidentStatus.Failed,
                    RequestedStatus = LogIncidentRequest.None,
                    Error = ex.Message,
                }).Catch((Exception writeEx) =>
                {
                    logger?.LogError(writeEx, "Incident {Fingerprint}: could not record the failure",
                        work.Incident.Fingerprint);
                    return Observable.Return(Unit.Default);
                });
            })
            .Finally(() => (work.Reconcile ? reconciling : inFlight)
                .TryRemove(work.Incident.Fingerprint, out _));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The claim — what makes a transition happen at most once
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Consumes the request on the LIVE node and emits what this worker actually won.
    ///
    /// <para>🚨 The claim is a WRITE, and it lands BEFORE the outward mutation it guards — the
    /// claim-the-guard-before-the-mutation rule (#826). Everything downstream keys off that write
    /// having COMPLETED, so a second work item for the same incident finds the request already
    /// consumed and stands down. Without it, the queue's guards are all in-process: the live query
    /// is eventually consistent and re-emits the pre-write snapshot after
    /// <see cref="inFlight"/> has been released, which is how #1124/#1125 and #1111/#1112 were each
    /// opened twice inside one second.</para>
    /// </summary>
    private IObservable<LogIncidentClaim> Claim(LogIncidentWork work)
    {
        // Declined until the update lambda says otherwise: content that cannot be read never
        // reaches the lambda (UpdateIncident skips it), and "we did not win" is the only safe
        // reading of that. Assigned inside the lambda — which runs, on the node's serialised write
        // queue, before this write completes — and read afterwards, exactly as the ingest path's
        // fold captures its merged result.
        var claim = new LogIncidentClaim(LogIncidentRequest.None, work.Incident);
        return Write(work.Path, current => (claim = ClaimRequest(current, work.Request, options)).Incident)
            .Select(_ =>
            {
                if (claim.Granted != work.Request)
                    logger?.LogInformation(
                        "Incident {Fingerprint}: {Request} resolved to {Granted} — the incident is "
                        + "{Status} and carries issue {IssueNumber}",
                        work.Incident.Fingerprint, work.Request, claim.Granted,
                        claim.Incident.Status, claim.Incident.IssueNumber);
                return claim;
            });
    }

    /// <summary>
    /// The rule that makes filing idempotent, applied to the incident's LIVE content at write time.
    /// Pure, so the whole policy is testable without a mesh.
    ///
    /// <para>Two things can take a request away between the query emission that queued the work and
    /// the moment it runs: another worker (or another replica) consuming it, and the incident having
    /// been ticketed already. Both mean the same thing — do not perform this operation again.</para>
    /// </summary>
    /// <param name="current">The incident as it is at write time.</param>
    /// <param name="request">What the work item was queued for.</param>
    /// <param name="options">The recurrence policy (the comment rate limit).</param>
    /// <returns>What this worker may perform, and the content the claim writes.</returns>
    internal static LogIncidentClaim ClaimRequest(
        LogIncident current, LogIncidentRequest request, LogWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);

        // Nothing left to consume — someone else already took this request.
        if (request == LogIncidentRequest.None || current.RequestedStatus != request)
            return new LogIncidentClaim(LogIncidentRequest.None, current);

        // 🚨 Already ticketed ⇒ a File request can only mean "this fault came back". It belongs on
        // the issue that exists, under the SAME rate limit a recurrence comment obeys — never in a
        // second issue. A recurrence on a CLOSED issue reopens it (LogIncidentFiler.Comment): a
        // defect that returns after someone closed its ticket is exactly what should notify.
        if (request == LogIncidentRequest.File && current.IssueNumber is not null)
            return new LogIncidentClaim(
                LogIncidentIngestService.CommentDue(current, options, current.LastSeen)
                    ? LogIncidentRequest.Comment
                    : LogIncidentRequest.None,
                current with
                {
                    Status = LogIncidentStatus.Filed,
                    RequestedStatus = LogIncidentRequest.None,
                    Error = null,
                });

        return new LogIncidentClaim(request, current with
        {
            Status = InFlightStatus(request) ?? current.Status,
            RequestedStatus = LogIncidentRequest.None,
            Error = null,
        });
    }

    /// <summary>
    /// The status that says "this transition is running", or null for a request whose operation
    /// writes its own terminal status (<c>Comment</c> stays <c>Filed</c>, <c>Suppress</c> becomes
    /// <c>Suppressed</c> when it lands).
    ///
    /// <para>A crash between the claim and the write-back parks the incident in the in-flight
    /// status. That is recoverable and NOT a new dead end: the claim keys on
    /// <see cref="LogIncident.RequestedStatus"/>, never on the status, so asking again — an admin in
    /// the incident view, or the ingest path's retry rule for <c>Triaging</c> via the stranded-triage
    /// reconcile — is honoured.</para>
    /// </summary>
    private static LogIncidentStatus? InFlightStatus(LogIncidentRequest request) => request switch
    {
        LogIncidentRequest.Triage => LogIncidentStatus.Triaging,
        LogIncidentRequest.File => LogIncidentStatus.Filing,
        _ => null,
    };

    /// <summary>Performs what the claim granted, against the content the claim wrote.</summary>
    private IObservable<Unit> Perform(string path, LogIncidentClaim claim) => claim.Granted switch
    {
        LogIncidentRequest.Triage => Triage(path, claim.Incident),
        LogIncidentRequest.File => Apply(path, filer.File(claim.Incident)),
        LogIncidentRequest.Comment => Apply(path, filer.Comment(claim.Incident)),
        LogIncidentRequest.Suppress => Apply(path, Observable.Return(claim.Incident with
        {
            Status = LogIncidentStatus.Suppressed,
            RequestedStatus = LogIncidentRequest.None,
        })),
        // Nothing granted: the claim write already recorded whatever bookkeeping it owed.
        _ => Observable.Return(Unit.Default),
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  Triage
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hands the incident to the triage agent. The claim has already marked the node
    /// <see cref="LogIncidentStatus.Triaging"/> and cleared the request — that write is what stops
    /// the next query emission from starting a second thread for the same fault.
    ///
    /// <para>The agent is given the incident as its <c>MainNode</c>, so it reads the evidence from
    /// the node itself rather than from a prompt-stuffed copy, and writes its draft back to the
    /// same node. The thread path is recorded so the reasoning behind a ticket stays reachable.</para>
    /// </summary>
    private IObservable<Unit> Triage(string path, LogIncident incident)
    {
        var fingerprint = incident.Fingerprint;

        return Observable.Defer(() =>
        {
            // 🚨 The thread hangs off the INCIDENT node, not the satellite namespace. Passing
            // `Admin/_LogIncident` here failed every triage in production with "No node found at
            // 'Admin/_LogIncident'. Closest ancestor is 'Admin'" — that path is a namespace, not a
            // node, and StartThread needs a real owner to anchor under (satellites never sit at top
            // level). The incident IS the owner, so its thread lands at {incidentPath}/_Thread/{id}
            // and stays reachable from the ticket.
            hub.StartThread(
                namespacePath: path,
                userText: Prompt(incident),
                agentName: options.TriageAgent,
                contextPath: path,
                mainNode: path,
                onCreated: thread => Write(path, current =>
                        current with { TriageThreadPath = thread.Path })
                    .Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "Incident {Fingerprint}: could not record the triage thread path", fingerprint)),
                onError: error =>
                {
                    logger?.LogWarning("Incident {Fingerprint}: triage thread failed to start — {Error}",
                        fingerprint, error);
                    Write(path, current => current with
                    {
                        Status = LogIncidentStatus.Failed,
                        Error = error,
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex, "Incident {Fingerprint}: could not record the triage failure",
                            fingerprint));
                });
            return Observable.Return(Unit.Default);
        });
    }

    /// <summary>
    /// Repairs an incident stranded in <see cref="LogIncidentStatus.Triaging"/>: if the round it is
    /// waiting for has finished without producing anything actionable, record that as
    /// <see cref="LogIncidentStatus.Failed"/> — a state the ingest path re-triages on the next
    /// recurrence (<c>NextRequest</c>), so the incident recovers by itself once whatever was
    /// missing is back.
    ///
    /// <para>Does nothing while the round is genuinely in flight, and nothing if the incident has
    /// moved on in the meantime — the status is re-read INSIDE the update lambda, so a draft that
    /// landed between the thread read and the write is never clobbered.</para>
    ///
    /// <para>A thread node that cannot be read at all is not swallowed: the error propagates to
    /// <see cref="Run"/>, which records it on the incident as <c>Failed</c> with the reason. Failed
    /// is retryable; stranded is not.</para>
    /// </summary>
    private IObservable<Unit> ReconcileTriaging(LogIncidentWork work)
    {
        var threadPath = work.Incident.TriageThreadPath!;
        return hub.GetWorkspace().GetMeshNodeStream(threadPath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(ThreadReadTimeout)
            .Select(node => node.ContentAs<AiThread>(hub.JsonSerializerOptions, logger))
            .SelectMany(thread =>
            {
                if (thread is null || !RoundFinished(thread))
                    return Observable.Return(Unit.Default);

                logger?.LogWarning(
                    "Incident {Fingerprint}: triage thread {Thread} finished without a draft — "
                    + "marking the incident Failed so it is retried (triage agent '{Agent}')",
                    work.Incident.Fingerprint, threadPath, options.TriageAgent);

                return Write(work.Path, current =>
                    StrandedTriageOutcome(current, threadPath, options.TriageAgent) ?? current);
            });
    }

    /// <summary>
    /// The rule the reconcile applies once the triage round is known to be over: an incident still
    /// sitting in <see cref="LogIncidentStatus.Triaging"/> with nothing requested got nothing out of
    /// its agent, so it FAILED — and <c>Failed</c> is a state the ingest path re-triages.
    ///
    /// <para>Returns null when there is nothing to do. That is the important half: the incident is
    /// re-read at write time, so a draft that landed between the thread read and the write (status
    /// moved on, or <c>RequestedStatus</c> now asks to File) leaves the incident untouched. The
    /// reconcile must never be able to overwrite a successful triage it merely raced.</para>
    /// </summary>
    internal static LogIncident? StrandedTriageOutcome(
        LogIncident current, string threadPath, string agentName) =>
        current.Status == LogIncidentStatus.Triaging
        && current.RequestedStatus == LogIncidentRequest.None
            ? current with
            {
                Status = LogIncidentStatus.Failed,
                RequestedStatus = LogIncidentRequest.None,
                Error = StrandedTriageError(threadPath, agentName),
            }
            : null;

    /// <summary>
    /// How long the reconcile waits for the triage thread node to emit. This bounds a ONE-SHOT read
    /// of a specific node (AGENTS.md → "Reads: GetMeshNodeStream(path) … Take(1).Timeout(…)"), it
    /// does not bound the agent round — a running round simply reads as "not finished".
    /// </summary>
    private static readonly TimeSpan ThreadReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the thread's execution round is over. <c>Idle</c> alone is not enough — a
    /// just-created thread is Idle too, with its first message still queued — so the queue must be
    /// drained AND at least one message must have been materialised into the conversation.
    /// </summary>
    internal static bool RoundFinished(AiThread thread) =>
        thread.Status is ThreadExecutionStatus.Idle
            or ThreadExecutionStatus.Cancelled
            or ThreadExecutionStatus.Done
        && thread.PendingUserMessages.IsEmpty
        && thread.Messages.Count > 0;

    /// <summary>
    /// Why the incident failed, in terms an operator can act on. Names the most common cause first:
    /// a triage agent that is configured but absent from the deployment's agent catalog produces
    /// exactly this shape — a round that runs, says so in the thread, and writes nothing back.
    /// Technical diagnostic text like <c>Error</c>'s other writers (an exception message), not UI
    /// copy — the incident view labels are what carry translations.
    /// </summary>
    internal static string StrandedTriageError(string threadPath, string agentName) =>
        $"Triage finished without a draft. Thread '{threadPath}' completed but nothing was written "
        + $"back to the incident — most often the configured triage agent '{agentName}' is not "
        + "present in this deployment's agent catalog. Retried on the next recurrence.";

    /// <summary>
    /// The one instruction the agent gets here. Deliberately short: the incident node IS the
    /// input, and the agent's own definition carries the method — duplicating either into the
    /// prompt would leave two copies to drift apart.
    /// </summary>
    private static string Prompt(LogIncident incident) =>
        $"Triage the log incident at `{LogIncidentNodeType.IncidentPath(incident.Fingerprint)}` "
        + "and draft the GitHub issue for it.";

    // ══════════════════════════════════════════════════════════════════════════
    //  Writes
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a filer operation and persists the LIFECYCLE fields it produced — not the whole object.
    ///
    /// <para>The filer works from the snapshot it was handed, so writing its result back wholesale
    /// would revert whatever the ingest path folded in while the GitHub round-trip was in flight
    /// (occurrences, pods, samples — during an active incident, constantly). Projecting only the
    /// fields the filer owns keeps both writers correct.</para>
    /// </summary>
    private IObservable<Unit> Apply(string path, IObservable<LogIncident> operation) =>
        operation.SelectMany(filed => Write(path, current => current with
        {
            Status = filed.Status,
            RequestedStatus = filed.RequestedStatus,
            Repository = filed.Repository,
            IssueNumber = filed.IssueNumber,
            IssueUrl = filed.IssueUrl,
            LastCommentedAt = filed.LastCommentedAt,
            OccurrencesAtLastComment = filed.OccurrencesAtLastComment,
            Error = filed.Error,
        }));

    /// <summary>
    /// Writes incident content back, mutating the node's LIVE content. Cold — the caller subscribes
    /// (AGENTS.md → "Cold observables: Subscribe is mandatory").
    /// </summary>
    private IObservable<Unit> Write(string path, Func<LogIncident, LogIncident> mutate) =>
        hub.UpdateIncident(path, mutate, logger)
            .FirstAsync()
            .Select(_ => Unit.Default);
}

/// <summary>
/// The outcome of consuming an incident's <see cref="LogIncident.RequestedStatus"/>.
/// </summary>
/// <param name="Granted">What this worker may actually perform — never simply the request echoed
/// back. <see cref="LogIncidentRequest.None"/> means "stand down": another worker already took it,
/// or the incident is ticketed and the recurrence is inside the comment window. A
/// <see cref="LogIncidentRequest.File"/> on an already-ticketed incident is granted as a
/// <see cref="LogIncidentRequest.Comment"/>, which is what makes filing idempotent.</param>
/// <param name="Incident">The content the claim writes: the request cleared and the in-flight
/// status stamped on a grant, unchanged when nothing was granted (so a declined claim emits an
/// empty patch and costs nothing).</param>
internal sealed record LogIncidentClaim(LogIncidentRequest Granted, LogIncident Incident);
