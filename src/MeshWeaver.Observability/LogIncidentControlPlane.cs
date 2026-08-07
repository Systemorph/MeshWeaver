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

    /// <summary>One queued transition.</summary>
    private record LogIncidentWork(string Path, LogIncident Incident, LogIncidentRequest Request);

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
                continue;
            if (!inFlight.TryAdd(incident.Fingerprint, 0))
                continue;
            queue.OnNext(new LogIncidentWork(node.Path, incident, incident.RequestedStatus));
        }
    }

    /// <summary>
    /// Performs one transition. Always completes — a failure is written onto the incident as
    /// <see cref="LogIncidentStatus.Failed"/> rather than left to fault the work loop, so one bad
    /// incident can never stop the others (AGENTS.md → "Wedges to zero").
    /// </summary>
    private IObservable<Unit> Run(LogIncidentWork work)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                _ => work.Request switch
                {
                    LogIncidentRequest.Triage => Triage(work),
                    LogIncidentRequest.File => Apply(work, filer.File(work.Incident)),
                    LogIncidentRequest.Comment => Apply(work, filer.Comment(work.Incident)),
                    LogIncidentRequest.Suppress => Apply(work, Observable.Return(work.Incident with
                    {
                        Status = LogIncidentStatus.Suppressed,
                        RequestedStatus = LogIncidentRequest.None,
                    })),
                    _ => Observable.Return(Unit.Default),
                })
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
            .Finally(() => inFlight.TryRemove(work.Incident.Fingerprint, out _));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Triage
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hands the incident to the triage agent. Marks the node <see cref="LogIncidentStatus.Triaging"/>
    /// FIRST and only then starts the thread: the status write is what stops the next query
    /// emission from starting a second thread for the same fault.
    ///
    /// <para>The agent is given the incident as its <c>MainNode</c>, so it reads the evidence from
    /// the node itself rather than from a prompt-stuffed copy, and writes its draft back to the
    /// same node. The thread path is recorded so the reasoning behind a ticket stays reachable.</para>
    /// </summary>
    private IObservable<Unit> Triage(LogIncidentWork work)
    {
        var fingerprint = work.Incident.Fingerprint;

        return Write(work.Path, current => current with
        {
            Status = LogIncidentStatus.Triaging,
            RequestedStatus = LogIncidentRequest.None,
            Error = null,
        }).Select(_ =>
        {
            hub.StartThread(
                namespacePath: LogIncidentNodeType.IncidentNamespace,
                userText: Prompt(work.Incident),
                agentName: options.TriageAgent,
                contextPath: work.Path,
                mainNode: work.Path,
                onCreated: thread => Write(work.Path, current =>
                        current with { TriageThreadPath = thread.Path })
                    .Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "Incident {Fingerprint}: could not record the triage thread path", fingerprint)),
                onError: error =>
                {
                    logger?.LogWarning("Incident {Fingerprint}: triage thread failed to start — {Error}",
                        fingerprint, error);
                    Write(work.Path, current => current with
                    {
                        Status = LogIncidentStatus.Failed,
                        Error = error,
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex, "Incident {Fingerprint}: could not record the triage failure",
                            fingerprint));
                });
            return Unit.Default;
        });
    }

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
    private IObservable<Unit> Apply(LogIncidentWork work, IObservable<LogIncident> operation) =>
        operation.SelectMany(filed => Write(work.Path, current => current with
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
