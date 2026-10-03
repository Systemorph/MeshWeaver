using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// The TARGET's audit record of one lane request, at <c>Admin/ControlLane/{requestId}</c>. Created
/// BEFORE anything runs — its creation is the single-use claim (a second create of the same id is
/// refused, so a replayed request never runs twice) — and appended to at every step.
/// </summary>
public sealed record ControlLaneRecord
{
    /// <summary>The request as verified.</summary>
    public ControlLaneRequest Request { get; init; } = new();

    /// <summary>The latest <see cref="ControlLaneStatus"/>.</summary>
    public string Status { get; init; } = ControlLaneStatus.Accepted;

    /// <summary>When the target received it.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When the record last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The digest of the plan the target computed.</summary>
    public string? PlanDigest { get; init; }

    /// <summary>The plan the target computed.</summary>
    public ControlLanePlan? Plan { get; init; }

    /// <summary>Every step, refusal, failure and report delivery, one line each.</summary>
    public ImmutableList<string> Log { get; init; } = [];
}

/// <summary>What the target answered one delivery: the verdict, and why when it is not accepted.</summary>
/// <param name="Verdict">A <see cref="ControlLaneVerdict"/>.</param>
/// <param name="Why">The reason, for a refusal — never for a signature failure (that says nothing).</param>
/// <param name="RequestId">The request id, once the request could be read.</param>
public sealed record ControlLaneReceipt(string Verdict, string? Why = null, string? RequestId = null)
{
    /// <summary>The HTTP status the endpoint answers with.</summary>
    public int StatusCode => ControlLaneVerdict.StatusCodeOf(Verdict);
}

/// <summary>
/// Where a target sends its signed reports. The default posts to the control instance's inbox
/// (<see cref="HttpControlLaneReportSink"/>); a test delivers them to a second mesh's inbox.
/// </summary>
public interface IControlLaneReportSink
{
    /// <summary>Delivers one signed report. Errors when the control inbox did not ACCEPT it VERIFIED.</summary>
    IObservable<Unit> Send(IMessageHub hub, ControlLaneReport report, string body, string signature);
}

/// <summary>
/// 🚨 THE TARGET HALF of the control lane (Doc/Architecture/ControlLane): verifies one delivery —
/// armed, signed with THIS deployment's own key, a request, naming this deployment, inside its
/// validity window, an operation this instance executes, single use — records it, answers, and runs
/// it as system through the operation's own engine, reporting every step back signed.
///
/// <para>🚨 The order of the checks is contract. Arming first (a misconfiguration here answers 503,
/// never "your key is wrong"); the SIGNATURE before a single field is parsed; admission before
/// anything is written; the single-use claim (the ledger node's creation) before anything runs. A
/// delivery refused at any step leaves nothing behind but a log line.</para>
///
/// <para>🚨 The TARGET computes the plan. A real run executes only when the plan it computes NOW has
/// the digest the approval bound; any other plan is refused (<see cref="ControlLaneStatus.Refused"/>)
/// with both digests named and nothing touched.</para>
/// </summary>
public sealed class ControlLaneReceiver : IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger logger;
    private readonly ImmutableDictionary<string, IControlLaneOperation> operations;
    private readonly IControlLaneReportSink sink;
    private readonly CompositeDisposable runs = new();

    /// <summary>Created by DI as a mesh-scoped singleton; its runs end with the mesh.</summary>
    public ControlLaneReceiver(
        IMessageHub hub, IEnumerable<IControlLaneOperation> operations, IControlLaneReportSink sink,
        ILogger<ControlLaneReceiver> logger)
    {
        this.hub = hub;
        this.logger = logger;
        this.sink = sink;
        var claimed = operations.GroupBy(o => o.Operation, StringComparer.Ordinal).ToList();
        // 🚨 Two executors claiming one value would let DI enumeration order decide which system
        // action an unchanged signed request runs — refused at construction, loudly.
        if (claimed.FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidOperationException(
                $"control lane operation '{duplicate.Key}' is claimed by {duplicate.Count()} executors "
                + $"({string.Join(", ", duplicate.Select(o => o.GetType().FullName))}) — each value has exactly one");
        this.operations = claimed.ToImmutableDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The HTTP answer to a receipt: the status, the body, and — for an ACCEPTED receipt only — the
    /// body signed with this deployment's key, so the control instance can tell this deployment's
    /// acceptance from a 2xx by anything else on the path. The anonymous caller learns the verdict;
    /// the REASON only once its signature verified (how this instance is — not — configured is
    /// logged here, never told to whoever asked). Pure over the configuration.
    /// </summary>
    public static ControlLaneAnswer Answer(ControlLaneReceipt receipt, IConfiguration? configuration)
    {
        var body = ControlLaneClient.AnswerBody(
            receipt.Verdict is ControlLaneVerdict.NotArmed or ControlLaneVerdict.SignatureInvalid
                ? receipt with { Why = null }
                : receipt);
        var key = configuration?[ControlLaneKeys.TargetKey];
        var signature = receipt.Verdict == ControlLaneVerdict.Accepted && !string.IsNullOrWhiteSpace(key)
            ? ControlLaneWire.Sign(body, key)
            : null;
        return new ControlLaneAnswer(receipt.StatusCode, body, signature);
    }

    /// <summary>The operations this target executes.</summary>
    public IReadOnlyCollection<string> Operations => operations.Keys.ToList();

    /// <summary>
    /// Verifies, admits and claims one delivery, and — when accepted — starts the run. The receipt
    /// is emitted once the request is RECORDED (or refused); the run continues on its own and
    /// reports to the control instance. Cold; never errors — a refusal is data.
    /// </summary>
    public IObservable<ControlLaneReceipt> Receive(string body, string? signature) =>
        Observable.Defer(() =>
        {
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            if (ControlLaneKeys.ArmingRefusal(configuration) is { } notArmed)
            {
                logger.LogError("[ControlLane] delivery REFUSED — {Why}. Nothing was stored.", notArmed);
                return Observable.Return(new ControlLaneReceipt(ControlLaneVerdict.NotArmed, notArmed));
            }
            // Read at delivery, never captured: the key is rotatable configuration.
            var key = configuration![ControlLaneKeys.TargetKey]!;
            if (!ControlLaneWire.Verify(signature, body, key))
            {
                logger.LogWarning("[ControlLane] delivery REFUSED — the signature is absent or does not verify with "
                                  + "{Key}. Nothing was read or stored.", ControlLaneKeys.TargetKey);
                return Observable.Return(new ControlLaneReceipt(ControlLaneVerdict.SignatureInvalid));
            }
            var self = configuration[ControlLaneKeys.DeploymentKey]!.Trim();
            var now = DateTimeOffset.UtcNow;
            // A FORWARDED EVENT (Doc/Architecture/ControlLane → "Forwarded events") shares the
            // arming and the signature above and nothing after them: it names no operation, runs
            // nothing and is never reported — its whole effect is one node in a declared inbox.
            if (ControlLaneWire.ParseEvent(body) is { } forwarded)
                return ReceiveEvent(forwarded, self, now, configuration);
            var request = ControlLaneWire.ParseRequest(body);
            var (verdict, why) = ControlLaneAdmission.Admit(request, self, now, Operations);
            if (verdict == ControlLaneVerdict.Accepted
                && operations[request!.Operation].ShapeRefusal(request) is { } shape)
                (verdict, why) = (ControlLaneVerdict.Refused, shape);
            if (verdict != ControlLaneVerdict.Accepted)
            {
                logger.LogWarning("[ControlLane] request {RequestId} REFUSED ({Verdict}) — {Why}. Nothing was stored.",
                    request?.RequestId ?? "(unreadable)", verdict, why);
                return Observable.Return(new ControlLaneReceipt(verdict, why, request?.RequestId));
            }
            return Claim(request!, now)
                .Select(claimed =>
                {
                    if (!claimed.Ok)
                    {
                        logger.LogWarning("[ControlLane] request {RequestId} REFUSED ({Verdict}) — {Why}.",
                            request!.RequestId, claimed.Verdict, claimed.Why);
                        return new ControlLaneReceipt(claimed.Verdict, claimed.Why, request.RequestId);
                    }
                    logger.LogWarning(
                        "[ControlLane] request {RequestId} ACCEPTED: {Operation} of '{Target}' ({Mode}) for control action "
                        + "/{Action}, requested by {Requester}, approved by {Approver}, plan {Digest}",
                        request!.RequestId, request.Operation, request.Target, request.DryRun ? "dry run" : "real run",
                        request.Action, request.RequestedBy ?? "(unattributed)", request.ApprovedBy ?? "(none)",
                        request.PlanDigest ?? "(none)");
                    Start(request, key);
                    return new ControlLaneReceipt(ControlLaneVerdict.Accepted, null, request.RequestId);
                });
        });

    /// <summary>
    /// A verified forwarded event: admitted (this deployment, its window, a DECLARED target, the
    /// size cap) and stored — the inbox node's creation is the single-use claim. Cold; never errors.
    /// </summary>
    private IObservable<ControlLaneReceipt> ReceiveEvent(ControlLaneEvent evt, string self, DateTimeOffset now, IConfiguration configuration)
    {
        var (verdict, why) = ControlLaneEvents.Admit(evt, self, now, ControlLaneEvents.Targets(configuration));
        if (verdict != ControlLaneVerdict.Accepted)
        {
            logger.LogWarning("[ControlLane] forwarded event {EventId} REFUSED ({Verdict}) — {Why}. Nothing was stored.",
                evt.EventId, verdict, why);
            return Observable.Return(new ControlLaneReceipt(verdict, why, evt.EventId));
        }
        return ControlLaneEvents.Store(hub, evt, now)
            .Do(receipt =>
            {
                if (receipt.Verdict == ControlLaneVerdict.Accepted)
                    logger.LogInformation("[ControlLane] forwarded {Source} event {Name} {EventId} stored in {Target}/_Inbox",
                        evt.Source, evt.Name, evt.EventId, evt.Target);
                else
                    logger.LogWarning("[ControlLane] forwarded event {EventId} REFUSED ({Verdict}) — {Why}",
                        evt.EventId, receipt.Verdict, receipt.Why);
            });
    }

    /// <summary>
    /// The single-use claim: CREATE the ledger node. A create of an id that already exists is the
    /// replay; any other failure refuses too (nothing runs without its audit record).
    /// </summary>
    private IObservable<(bool Ok, string Verdict, string? Why)> Claim(ControlLaneRequest request, DateTimeOffset now)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(request.RequestId, ControlLaneAdmission.LedgerNamespace)
        {
            Name = $"{request.Operation} {request.Target}{(request.DryRun ? " (dry run)" : "")}",
            NodeType = ControlLaneExtensions.RecordNodeType,
            Content = new ControlLaneRecord
            {
                Request = request,
                ReceivedAt = now,
                UpdatedAt = now,
                Log = [$"[{Stamp(now)}] accepted — {(request.DryRun ? "dry run" : $"real run of plan {request.PlanDigest}")}, "
                       + $"expires {Stamp(request.ExpiresAt)}"],
            },
        };
        return SpaceDeletion.AsSystem(hub, () => mesh.CreateNode(node))
            .Take(1)
            .Timeout(MeshReading.DefaultBudget)
            .Select(_ => (true, ControlLaneVerdict.Accepted, (string?)null))
            .Catch((Exception ex) => Observable.Return(ex.IsNodeAlreadyExists()
                ? (false, ControlLaneVerdict.Replayed, (string?)$"request {request.RequestId} was already received — a lane request is single use")
                : (false, ControlLaneVerdict.Refused, (string?)$"the audit record could not be written ({ex.GetType().Name}: {ex.Message}), so nothing runs")));
    }

    /// <summary>Starts the run on the lane's off-router execution hub; held until it ends or the mesh does.</summary>
    private void Start(ControlLaneRequest request, string key)
    {
        var execution = hub.NodeOperationIssuingHub();
        var sequence = 0;
        IObservable<Unit> Report(string status, string message, ControlLanePlan? plan = null) =>
            Observable.Defer(() => SendReport(execution, request, key, status, message, plan, Interlocked.Increment(ref sequence)));

        var run = operations[request.Operation].Prepare(execution, request)
            .Take(1)
            .Catch((Exception ex) => Observable.Throw<ControlLanePreparation>(new LaneRefusal(ex.Message)))
            .SelectMany(prepared =>
            {
                var digest = prepared.Plan.Digest();
                var planned = Report(ControlLaneStatus.Planned,
                    $"plan {ControlLaneText.Short(digest)} computed on '{request.Deployment}': {prepared.Plan.Steps.Count} step(s), "
                    + $"{prepared.Plan.Steps.Count(s => s.Destructive)} destructive", prepared.Plan);
                if (request.DryRun)
                    return planned;
                // 🚨 The approval bound ONE plan. What this target computes NOW must be that plan, or
                // nothing is touched.
                if (!string.Equals(digest, request.PlanDigest, StringComparison.Ordinal))
                    return planned.Concat(Observable.Throw<Unit>(new LaneRefusal(
                        $"the plan this target would run now ({ControlLaneText.Short(digest)}) is not the plan that was approved "
                        + $"({ControlLaneText.Short(request.PlanDigest)}) — nothing was touched; re-request to plan again")));
                return planned.Concat(prepared.Execute()
                    .Select(line => Report(ControlLaneStatus.Progress, line))
                    .Concat()
                    .LastOrDefaultAsync()
                    .SelectMany(_ => Report(ControlLaneStatus.Done, $"{request.Operation} of '{request.Target}' completed and verified")));
            })
            .Catch((Exception ex) => ex is LaneRefusal
                ? Report(ControlLaneStatus.Refused, ex.Message)
                : Report(ControlLaneStatus.Failed, $"{ex.GetType().Name}: {ex.Message}"));

        var subscription = new SingleAssignmentDisposable();
        runs.Add(subscription);
        subscription.Disposable = run.Subscribe(
            _ => { },
            ex =>
            {
                logger.LogError(ex, "[ControlLane] request {RequestId} could not record its outcome", request.RequestId);
                runs.Remove(subscription);
            },
            () => runs.Remove(subscription));
    }

    /// <summary>
    /// Records one status on the ledger, logs it, and sends it signed to the control instance. A
    /// report the control inbox did not accept is itself recorded and logged at Error — fail loud:
    /// the control action then waits out its expiry and says it heard nothing.
    /// </summary>
    private IObservable<Unit> SendReport(
        IMessageHub execution, ControlLaneRequest request, string key, string status, string message,
        ControlLanePlan? plan, int sequence)
    {
        var now = DateTimeOffset.UtcNow;
        var report = new ControlLaneReport
        {
            Deployment = request.Deployment,
            RequestId = request.RequestId,
            Action = request.Action,
            Operation = request.Operation,
            Target = request.Target,
            DryRun = request.DryRun,
            Status = status,
            Plan = plan,
            PlanDigest = plan?.Digest(),
            Message = message,
            Sequence = sequence,
            At = now,
        };
        var level = status is ControlLaneStatus.Failed ? LogLevel.Error
            : status is ControlLaneStatus.Refused or ControlLaneStatus.Done or ControlLaneStatus.Planned ? LogLevel.Warning
            : LogLevel.Information;
        logger.Log(level, "[ControlLane] request {RequestId} {Status}: {Message}", request.RequestId, status, message);
        var body = ControlLaneWire.Body(report);
        return AppendLedger(execution, request.RequestId, status, plan, $"[{Stamp(now)}] {status}: {message}")
            .SelectMany(_ => sink.Send(execution, report, body, ControlLaneWire.Sign(body, key))
                .Catch((Exception ex) =>
                {
                    logger.LogError(ex,
                        "[ControlLane] request {RequestId}: the {Status} report was NOT accepted by the control instance",
                        request.RequestId, status);
                    return AppendLedger(execution, request.RequestId, null, null,
                        $"[{Stamp(DateTimeOffset.UtcNow)}] the {status} report (#{sequence}) was NOT delivered to the control instance: {ex.Message}");
                }));
    }

    private IObservable<Unit> AppendLedger(
        IMessageHub execution, string requestId, string? status, ControlLanePlan? plan, string line)
    {
        var cache = execution.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return SpaceDeletion.AsSystem(execution, () => cache.Update(ControlLaneAdmission.LedgerPath(requestId), node =>
            {
                var record = node.ContentAs<ControlLaneRecord>(execution.JsonSerializerOptions);
                return record is null
                    ? throw new InvalidOperationException(
                        $"the ledger {ControlLaneAdmission.LedgerPath(requestId)} carries no readable ControlLaneRecord, so the audit line could not be appended")
                    : node with
                    {
                        Content = record with
                        {
                            Status = status ?? record.Status,
                            Plan = plan ?? record.Plan,
                            PlanDigest = plan?.Digest() ?? record.PlanDigest,
                            UpdatedAt = DateTimeOffset.UtcNow,
                            Log = record.Log.Add(line),
                        },
                    };
            }, execution.JsonSerializerOptions))
            .Take(1)
            .Timeout(MeshReading.DefaultBudget)
            .Select(_ => Unit.Default)
            // 🚨 Not swallowed: an operation whose audit record cannot be written does not carry on.
            // The error ends the run (reported as Failed, which tries the ledger once more) and, if
            // that cannot be recorded either, surfaces as the run's Error log line.
            .Catch((Exception ex) => Observable.Throw<Unit>(new InvalidOperationException(
                $"the audit record {ControlLaneAdmission.LedgerPath(requestId)} could not be appended ({ex.GetType().Name}: {ex.Message})",
                ex)));
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>A refusal before anything was touched — reported as <see cref="ControlLaneStatus.Refused"/>, not Failed.</summary>
    private sealed class LaneRefusal(string message) : Exception(message);

    /// <inheritdoc />
    public void Dispose() => runs.Dispose();
}
