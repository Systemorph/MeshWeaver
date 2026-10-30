using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The IMAGE and RESTART halves of an instance reboot (<c>Doc/Architecture/InstanceReboot</c>,
/// steps 3 and 4) — registered by the portal's self-updater, which already owns the ONE image
/// selection (the instance's update policy, the availability gate) and the ONE roll/restart path
/// (self-patch with the migration first, or the hand-over to the control lane). A host without it
/// answers the reboot's Image and Restart steps by name: nothing can be rolled from here.
/// </summary>
public interface IInstanceRebootActivation
{
    /// <summary>
    /// The newest platform image this instance's update policy admits and the availability gate
    /// clears — or no target (restart on the running image), with the sentence saying why.
    /// <paramref name="wedged"/>: do NOT wait for (or be held by) a dependent-suite (combo) verdict,
    /// and say so in the detail. Cold; emits ONE choice; never faults (a failed read is a choice with
    /// no target and a detail naming the failure).
    /// </summary>
    IObservable<RebootImageChoice> SelectImage(bool wedged);

    /// <summary>
    /// ONE roll to <see cref="RebootImageChoice.Target"/> (migration first), or one restart on the
    /// running image when there is no target. Never paced by the self-updater's roll floor: the caller
    /// issues it exactly once and stamps it first. Cold; emits ONE outcome.
    /// </summary>
    IObservable<RebootActivationOutcome> Activate(RebootImageChoice choice, string reason);
}

/// <summary>What the reboot's Image step chose.</summary>
/// <param name="Running">The platform version this instance runs.</param>
/// <param name="Target">The image to roll onto, or null to restart on the running image.</param>
/// <param name="Detail">The selection's own sentence (what was listed, what was held and why, whether a suite verdict was waived).</param>
/// <param name="Failed">True when the selection itself could not be made (a listing failure) — the step is red, and the restart still runs on the running image.</param>
public sealed record RebootImageChoice(string? Running, string? Target, string Detail, bool Failed = false);

/// <summary>How the reboot's ONE roll/restart was taken — <see cref="Kind"/> is a <see cref="RebootActivationKinds"/> value.</summary>
public sealed record RebootActivationOutcome(string Kind, string Detail)
{
    /// <summary>Whether a roll or restart is on its way.</summary>
    public bool Scheduled => Kind is RebootActivationKinds.Rolled or RebootActivationKinds.Restarted or RebootActivationKinds.HandedOver;
}

/// <summary>How a reboot's activation was taken — open string constants.</summary>
public static class RebootActivationKinds
{
    /// <summary>This install patched its own workloads onto the target image (the migration ran first).</summary>
    public const string Rolled = "Rolled";

    /// <summary>This install restarted its own workloads on the running image.</summary>
    public const string Restarted = "Restarted";

    /// <summary>Handed to the control lane (<c>self-update-available</c> or <c>self-update-restart-pending</c>).</summary>
    public const string HandedOver = "HandedOver";

    /// <summary>Nothing could be rolled or restarted — a DECIDED answer (no restart path on this
    /// install, a release held by policy, a migration that failed). Final; the detail names why.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>The roll/restart request CRASHED — a hand-over that failed, a check that could not be
    /// made, a call that threw. Transient, never final: the request goes
    /// <see cref="InstanceRebootStatus.Faulted"/> and the reconcile pass re-arms it
    /// (<see cref="InstanceReboot.RetryFaulted"/>).</summary>
    public const string Faulted = "Faulted";

    /// <summary>A roll or restart was REFUSED before anything was issued — a disruptive rollout strategy, or a
    /// control instance that cannot self-patch (it would hand its own restart to itself). The detail names why.</summary>
    public const string Refused = "Refused";
}

/// <summary>
/// 🚨 The instance-side executor of an <see cref="InstanceRebootRequest"/> (<c>Doc/Architecture/InstanceReboot</c>)
/// — the ONE brain, on the request node's OWN hub, so exactly one process drives a request.
///
/// <para><b>The state machine</b> (each step a <c>stream.Update</c> on the request's own node):</para>
/// <list type="number">
/// <item><b>Requested → Preparing</b>: Sync (every module source at its branch HEAD,
/// <see cref="ModuleSourceUpdate.UpdateAll"/>), Modules (<see cref="ModuleReloadExecutor.ResolveAndLand"/>
/// for every installed module — landed, NOT activated), Image (<see cref="IInstanceRebootActivation.SelectImage"/>).
/// Each step's row is written as it ends. A red step does NOT stop the reboot — bringing the instance
/// back is the point — it makes the request Failed at the end, by name.</item>
/// <item><b>Preparing → AwaitingRestart</b>: stamp <see cref="InstanceRebootRequest.RestartRequestedAt"/>
/// FIRST, then ask <see cref="IInstanceRebootActivation.Activate"/> once — the stamp is what keeps a
/// resumed executor from asking twice. A decided "not scheduled" ⇒ Failed (nothing would verify); a
/// CRASHED request (<see cref="RebootActivationKinds.Faulted"/>, or a throw) ⇒ <see cref="InstanceRebootStatus.Faulted"/>,
/// re-armed by the reconcile pass (<see cref="InstanceReboot.RetryFaulted"/>) — never final.</item>
/// <item><b>AwaitingRestart → Done | Failed</b>: <see cref="InstanceReboot.EvaluateVerification"/>
/// over what every process booted after the stamp REPORTS from its own checks
/// (<see cref="InstanceRebootAgent"/>).</item>
/// </list>
///
/// <para>🚨 It acts only on a request written by System — through <see cref="InstanceReboot.Request"/>,
/// whose callers authorise the requester.</para>
/// </summary>
public static class InstanceRebootExecutor
{
    /// <summary>How many log lines a request keeps.</summary>
    public const int MaxLogLines = 150;

    /// <summary>
    /// One module adopt, as the reboot's audit line says it — pure. 🚨 Only files that LANDED are a
    /// landing: a decision that refused the bundle (a floor above the platform, nothing newer, no bundle)
    /// answers no failure, no files and a SERVED version, and a served version is not evidence of
    /// landing (#6625 review). Such an adopt reads "not landed", naming the decision and what keeps running.
    /// </summary>
    /// <param name="outcome">What the adopt answered.</param>
    public static string DescribeAdopt(ModuleAdoptOutcome outcome) => DescribeAdoptText(outcome).ToEnglish();

    /// <summary>The keyed form of <see cref="DescribeAdopt"/> (MeshWeaver#7322) — the words through the
    /// catalog, the versions, decision and reason as written. Pure.</summary>
    /// <param name="outcome">What the adopt answered.</param>
    public static RebootText DescribeAdoptText(ModuleAdoptOutcome outcome) =>
        outcome.Failure is { } failure
            ? RebootText.Of("reboot.adopt.failed", ("failure", failure))
            : outcome.FilesLanded > 0
                ? RebootText.Of("reboot.adopt.landed",
                    ("version", outcome.LandedVersion ?? outcome.ServedVersion ?? "?"),
                    ("files", outcome.FilesLanded.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : RebootText.Of("reboot.adopt.notLanded")
                    .Then(RebootText.Or(outcome.Verdict?.Action.ToString(), "reboot.adopt.noDecision"))
                    .Then(RebootText.Verbatim(":"), glue: "")
                    .Then(RebootText.Or(outcome.Verdict?.Reason, "reboot.noReasonRecorded"))
                    .Then(RebootText.Of("reboot.adopt.served"))
                    .Then(RebootText.Or(outcome.ServedVersion, "reboot.word.none"))
                    .Then(RebootText.Verbatim(";"), glue: "")
                    .Then(RebootText.Or(outcome.LandedBefore, "reboot.word.nothing"))
                    .Then(RebootText.Of("reboot.adopt.keepsRunning"));

    /// <summary>Arms the executor on a request node's own hub. Registered as the node type's initialization.</summary>
    public static IObservable<Unit> Arm(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceRebootExecutor));
        var run = new Run(hub, logger);
        var started = hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started);
        hub.RegisterForDisposal(started
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream())
            .Select(node => run.Step(node))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex,
                    "[Reboot] the executor on {Path} stopped — the request keeps its last status; "
                    + "the next activation of this node resumes it", hub.Address)));
        // 🚨 The OWNER answers "end it if it stalled" (MeshWeaver#6625) — a separate subscription, so a
        // step that never returns (the very thing that stalls a request) cannot hold the answer back
        // behind the Concat above. The judgement and the terminal transition happen in ONE update on
        // this hub, against this hub's own state (InstanceReboot.EndIfStalled).
        hub.RegisterForDisposal(started
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream())
            .Where(node => node.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions)?.RequestedEndIfStalledAt is not null)
            .Select(_ => AnswerEndIfStalled(hub, logger))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex,
                    "[Reboot] the stall answer on {Path} stopped — the next activation of this node answers again", hub.Address)));
        return Observable.Return(Unit.Default);
    }

    /// <summary>Applies <see cref="InstanceReboot.EndIfStalled"/> to this hub's own request, in one update.</summary>
    private static IObservable<Unit> AnswerEndIfStalled(IMessageHub hub, ILogger? logger)
    {
        var options = hub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        var catalog = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        var retryUnit = catalog.ReconcileSafetyNetInterval > TimeSpan.Zero ? catalog.ReconcileSafetyNetInterval : TimeSpan.Zero;
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                .Update<InstanceRebootRequest>(current => current.RequestedEndIfStalledAt is null
                    ? current
                    : InstanceReboot.EndIfStalled(current, DateTimeOffset.UtcNow, options.ExecutionWindow, retryUnit)))
            .Take(1)
            .Do(node =>
            {
                if (node.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions) is { Status: InstanceRebootStatus.Failed } ended
                    && ended.Failure?.Contains("stalled:", StringComparison.Ordinal) == true)
                    logger?.LogWarning("[Reboot] {Path}: ended as Failed — {Failure}", ModuleReloadExecutor.PathOf(hub), ended.Failure);
            })
            .Select(_ => Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[Reboot] {Path}: the stall question could not be answered ({Reason}) — the next ask answers again",
                    ModuleReloadExecutor.PathOf(hub), ex.Message);
                return Observable.Empty<Unit>();
            });
    }

    /// <summary>One activation's state: which steps it has started. Instance state, never static.</summary>
    private sealed class Run(IMessageHub hub, ILogger? logger)
    {
        private int preparing;
        private int restarting;
        private int finished;
        private int attempt;

        private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

        public IObservable<Unit> Step(MeshNode node)
        {
            var request = node.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions);
            if (request is null || InstanceRebootStatus.IsTerminal(request.Status))
                return Observable.Empty<Unit>();
            if (!string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
                return Once(ref finished, () => Finish(
                    RebootText.Of("reboot.finish.notSystem", ("by", node.CreatedBy ?? ""))));
            if (InstanceReboot.ValidateText(request) is { } invalid)
                return Once(ref finished, () => Finish(invalid));

            // A re-armed request (InstanceReboot.Rearm) is a new attempt: its restart may be asked for again.
            if (request.Attempt != attempt)
            {
                attempt = request.Attempt;
                Interlocked.Exchange(ref restarting, 0);
                Interlocked.Exchange(ref finished, 0);
            }

            return request.Status switch
            {
                // Faulted waits for the reconcile pass to re-arm it (InstanceReboot.RetryFaulted).
                InstanceRebootStatus.Faulted => Observable.Empty<Unit>(),
                InstanceRebootStatus.Requested or InstanceRebootStatus.Preparing when request.RestartRequestedAt is null =>
                    Once(ref preparing, () => Prepare(request)),
                InstanceRebootStatus.AwaitingRestart when request.RestartRequestedAt is null =>
                    Once(ref restarting, () => IssueRestart(request)),
                InstanceRebootStatus.AwaitingRestart => Evaluate(request),
                _ => Once(ref finished, () => Finish(RebootText.Of("reboot.finish.unknownStatus", ("status", request.Status)))),
            };
        }

        private static IObservable<Unit> Once(ref int flag, Func<IObservable<Unit>> work) =>
            Interlocked.Exchange(ref flag, 1) == 0 ? work() : Observable.Empty<Unit>();

        // ── Steps 1–3 ─────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Prepare(InstanceRebootRequest request) =>
            Write(r => r with { Status = InstanceRebootStatus.Preparing },
                    Line(RebootText.Of("reboot.log.requestedBy")
                        .Then(request.RequestedBy is { } by ? RebootText.Verbatim(by) : RebootText.Of("reboot.unattributed"))
                        .Then(RebootText.Of(request.Wedged ? "reboot.log.requestedTriggerWedged" : "reboot.log.requestedTrigger",
                            ("trigger", request.Trigger), ("reason", request.Reason)))
                        .Then(request.WedgeEvidence is { } e ? RebootText.Of("reboot.log.wedgeEvidence", ("evidence", e)) : RebootText.Empty, glue: "")))
                .SelectMany(_ => StepSync())
                .SelectMany(_ => StepModules())
                .SelectMany(_ => StepImage(request.Wedged))
                .SelectMany(_ => Write(r => r with { Status = InstanceRebootStatus.AwaitingRestart },
                    Line(RebootText.Of("reboot.log.prepared"))))
                .Catch((Exception ex) => Finish(RebootText.Of("reboot.finish.prepareFaulted", ("message", ex.Message))));

        private static RebootText SyncLine(ModuleSourceUpdateRow row) =>
            RebootText.Of("reboot.log.sync", ("source", row.ConfigPath), ("outcome", row.Outcome), ("detail", row.Detail));

        private IObservable<Unit> StepSync() =>
            Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Running, RebootText.Of("reboot.sync.running"))
                .SelectMany(_ => hub.ServiceProvider.GetService<GitHubSyncService>() is null
                    ? Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Skipped, RebootText.Of("reboot.sync.noService"))
                    // Each finished source is recorded as progress on the request at once (MeshWeaver#6625): a
                    // batch of many healthy imports must not read as one silence longer than the window.
                    // A write REFUSED because the request ended emits nothing → false: no further source is
                    // imported beside whatever replaced this reboot (#6625 review).
                    : ModuleSourceUpdate.UpdateAllReporting(hub, row => Write(r => r, Line(SyncLine(row)))
                        .Select(_ => true)
                        .DefaultIfEmpty(false)
                        .Catch((Exception ex) =>
                        {
                            // The progress could not be recorded, but the request has not ended: the batch goes on.
                            logger?.LogWarning(ex, "[Reboot] {Path}: the progress of {Source} could not be recorded ({Reason})",
                                ModuleReloadExecutor.PathOf(hub), row.ConfigPath, ex.Message);
                            return Observable.Return(true);
                        })).SelectMany(rows =>
                    {
                        var invariant = System.Globalization.CultureInfo.InvariantCulture;
                        var updated = rows.Where(r => r.Outcome == ModuleSourceUpdateOutcome.Updated).ToImmutableList();
                        var failed = rows.Where(r => r.Outcome == ModuleSourceUpdateOutcome.Failed).ToImmutableList();
                        // Rows reported as they finished are already on the log; only the ones never started
                        // (behind an unfinished import) are written here.
                        var lines = rows.Where(r => r.Outcome == ModuleSourceUpdateOutcome.Skipped && r.Detail.StartsWith("not started:", StringComparison.Ordinal))
                            .Select(r => Line(SyncLine(r))).ToArray();
                        // Reason-neutral: a row is skipped as not a module source OR as not started behind an
                        // unfinished import (#6625) — each sync line says which.
                        var summary = RebootText.Of("reboot.sync.summary",
                                ("updated", updated.Count.ToString(invariant)),
                                ("failed", failed.Count.ToString(invariant)),
                                ("skipped", (rows.Count - updated.Count - failed.Count).ToString(invariant)))
                            .Then(RebootText.Join("; ", failed.Select(f => RebootText.Verbatim($"{f.ConfigPath} — {f.Detail}"))), glue: ": ");
                        var outcome = !failed.IsEmpty ? InstanceRebootStepOutcome.Failed
                            : updated.IsEmpty ? InstanceRebootStepOutcome.Skipped
                            : InstanceRebootStepOutcome.Ok;
                        return Mark(InstanceRebootSteps.Sync, outcome,
                            updated.IsEmpty && failed.IsEmpty ? RebootText.Of("reboot.sync.nothing").Then(summary) : summary, lines);
                    }))
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Failed,
                    RebootText.Of("reboot.faulted", ("message", ex.Message))));

        private IObservable<Unit> StepModules() =>
            Mark(InstanceRebootSteps.Modules, InstanceRebootStepOutcome.Running, RebootText.Of("reboot.modules.running"))
                // Each module whose reload ended is recorded as progress at once (#6625 review), and a write
                // refused because the request ended answers false: no further module is reloaded.
                .SelectMany(_ => ModuleReloadExecutor.ResolveAndLandReporting(hub, module: null, (module, outcome) =>
                    Write(r => r, Line(RebootText.Of("reboot.log.module", ("module", module)).Then(DescribeAdoptText(outcome))))
                        .Select(_ => true)
                        .DefaultIfEmpty(false)
                        .Catch((Exception ex) =>
                        {
                            logger?.LogWarning(ex, "[Reboot] {Path}: the progress of module {Module} could not be recorded ({Reason})",
                                ModuleReloadExecutor.PathOf(hub), module, ex.Message);
                            return Observable.Return(true);
                        })))
                .SelectMany(landed =>
                {
                    if (landed.Problem is { } problem)
                        // "No installed module" is not red — there is nothing to land; everything else is.
                        return Mark(InstanceRebootSteps.Modules,
                            problem.StartsWith("no installed package", StringComparison.Ordinal)
                                ? InstanceRebootStepOutcome.Skipped
                                : InstanceRebootStepOutcome.Failed,
                            RebootText.Verbatim(problem));
                    var invariant = System.Globalization.CultureInfo.InvariantCulture;
                    var items = landed.Items;
                    var failures = items.Where(i => i.Failure is not null).ToImmutableList();
                    var newly = items.Where(i => i.Failure is null && i.Landed).ToImmutableList();
                    var summary = RebootText.Of("reboot.modules.summary",
                            ("count", items.Count.ToString(invariant)),
                            ("landed", newly.Count.ToString(invariant)),
                            ("failed", failures.Count.ToString(invariant)))
                        .Then(newly.IsEmpty ? RebootText.Empty : RebootText.Of("reboot.modules.landedList",
                            ("list", string.Join(", ", newly.Select(i => $"{i.Module} {i.TargetVersion}")))))
                        .Then(failures.IsEmpty ? RebootText.Empty
                            : RebootText.Verbatim("— " + string.Join("; ", failures.Select(i => $"{i.Module}: {i.Failure}"))));
                    // A floor above the running platform is a DECLINE by name — the right answer, not a red step;
                    // anything else that kept a module from landing is red.
                    var red = failures.Any(i => !i.Failure!.StartsWith("declined", StringComparison.Ordinal));
                    return Write(r => r with { Modules = items }, Line(RebootText.Of("reboot.log.modules").Then(summary)))
                        .SelectMany(_ => Mark(InstanceRebootSteps.Modules,
                            red ? InstanceRebootStepOutcome.Failed : InstanceRebootStepOutcome.Ok, summary));
                })
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Modules, InstanceRebootStepOutcome.Failed,
                    RebootText.Of("reboot.faulted", ("message", ex.Message))));

        private IObservable<Unit> StepImage(bool wedged)
        {
            var activation = hub.ServiceProvider.GetService<IInstanceRebootActivation>();
            if (activation is null)
                return Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Skipped, RebootText.Of("reboot.image.noActivation"));
            return Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Running, RebootText.Of("reboot.image.running"))
                .SelectMany(_ => activation.SelectImage(wedged).Take(1))
                .SelectMany(choice => Write(r => r with { RunningImage = choice.Running, TargetImage = choice.Target },
                        Line(RebootText.Of("reboot.log.image", ("running", choice.Running ?? "?"))
                            .Then(choice.Target is { } target ? RebootText.Verbatim(target) : RebootText.Of("reboot.image.noTarget"))
                            .Then(RebootText.Verbatim($"— {choice.Detail}"))))
                    .SelectMany(_ => Mark(InstanceRebootSteps.Image,
                        choice.Failed ? InstanceRebootStepOutcome.Failed
                        : choice.Target is null ? InstanceRebootStepOutcome.Skipped
                        : InstanceRebootStepOutcome.Ok,
                        choice.Target is null
                            ? RebootText.Of("reboot.image.noNewer", ("detail", choice.Detail))
                            : RebootText.Verbatim($"{choice.Target} — {choice.Detail}"))))
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Failed,
                    RebootText.Of("reboot.faulted", ("message", ex.Message))));
        }

        // ── Step 4 ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> IssueRestart(InstanceRebootRequest request)
        {
            var activation = hub.ServiceProvider.GetService<IInstanceRebootActivation>();
            if (activation is null)
                return Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed, RebootText.Of("reboot.restart.noPath"))
                    .SelectMany(_ => Finish(RebootText.Of("reboot.finish.noRestartPath")));
            var choice = new RebootImageChoice(request.RunningImage, request.TargetImage, "as chosen by the Image step");
            var reason = $"reboot {ModuleReloadExecutor.PathOf(hub)} by {request.RequestedBy ?? "(unattributed)"} ({request.Trigger}): {request.Reason}";
            // 🚨 Stamp FIRST, then ask. A resumed executor sees the stamp and never asks again.
            return Write(r => r with { RestartRequestedAt = DateTimeOffset.UtcNow }, Line(
                    request.TargetImage is null
                        ? RebootText.Of("reboot.log.restartRunning")
                        : RebootText.Of("reboot.log.rollTo", ("target", request.TargetImage))))
                .SelectMany(_ => Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Running, RebootText.Of("reboot.restart.requested")))
                .SelectMany(_ => activation.Activate(choice, reason).Take(1))
                .SelectMany(outcome => outcome.Scheduled
                    ? Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Ok, RebootText.Verbatim($"{outcome.Kind}: {outcome.Detail}"))
                        .SelectMany(_ => Mark(InstanceRebootSteps.Verify, InstanceRebootStepOutcome.Running, RebootText.Of("reboot.verify.waiting")))
                    : string.Equals(outcome.Kind, RebootActivationKinds.Faulted, StringComparison.Ordinal)
                        ? Fault(RebootText.Of("reboot.restart.faultedKind", ("kind", outcome.Kind), ("detail", outcome.Detail)))
                        : Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed, RebootText.Verbatim($"{outcome.Kind}: {outcome.Detail}"))
                            .SelectMany(_ => Finish(RebootText.Of("reboot.finish.restartRefused", ("kind", outcome.Kind), ("detail", outcome.Detail)))))
                // A restart request that THREW is a crash, never a decided "cannot restart" (#6172).
                .Catch((Exception ex) => Fault(RebootText.Of("reboot.restart.faulted", ("message", ex.Message))));
        }

        /// <summary>The restart request crashed: the step is red for THIS attempt and the request goes
        /// Faulted (not terminal) — the reconcile pass re-arms it once its backoff is due.</summary>
        private IObservable<Unit> Fault(RebootText why)
        {
            var english = why.ToEnglish();
            logger?.LogWarning("[Reboot] {Path}: attempt {Attempt} Faulted — {Why}; retried once its backoff is due",
                ModuleReloadExecutor.PathOf(hub), attempt, english);
            return Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed, why.Then(RebootText.Of("reboot.restart.retried")))
                .SelectMany(_ => Write(r => r with
                {
                    Status = InstanceRebootStatus.Faulted,
                    FaultedAt = DateTimeOffset.UtcNow,
                    Failure = english,
                    FailureText = why,
                }, Line(RebootText.Of("reboot.log.attemptFaulted", ("attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .Then(why)
                    .Then(RebootText.Of("reboot.log.rearmsIt")))));
        }

        // ── Step 5 ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Evaluate(InstanceRebootRequest request)
        {
            var membership = hub.ServiceProvider.GetService<IClusterMembership>();
            var verdict = InstanceReboot.EvaluateVerification(request,
                process => membership is null || membership.StateOf(process) != ClusterMemberState.Gone);
            if (verdict is null || Interlocked.Exchange(ref finished, 1) != 0)
                return Observable.Empty<Unit>();
            var detail = RebootText.Faithful(verdict.Detail, verdict.DetailText);
            // A self-patch restart can end the process that asked for it before the outcome line is
            // written; a process booted after the stamp reporting IS the evidence the restart happened.
            var restartRow = request.Steps.FirstOrDefault(s => s.Name == InstanceRebootSteps.Restart);
            var settleRestart = restartRow?.Outcome == InstanceRebootStepOutcome.Running
                ? Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Ok, RebootText.Of("reboot.restart.happened"))
                : Observable.Return(Unit.Default);
            return settleRestart.SelectMany(_ => Mark(InstanceRebootSteps.Verify,
                    verdict.Passed ? InstanceRebootStepOutcome.Ok : InstanceRebootStepOutcome.Failed, detail)
                .SelectMany(_ => Write(r =>
                {
                    var red = InstanceReboot.RedStepsText(r);
                    var failure = red.IsEmpty ? null : RebootText.Join("; ", red);
                    return r with
                    {
                        Status = red.IsEmpty ? InstanceRebootStatus.Done : InstanceRebootStatus.Failed,
                        Failure = failure?.ToEnglish(),
                        FailureText = failure,
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }, Line(RebootText.Of(verdict.Passed ? "reboot.log.verified" : "reboot.log.verificationRed").Then(detail)))));
        }

        // ── Writes ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Mark(string step, string outcome, RebootText detail, params RebootText[] extraLines) =>
            Write(r => r with { Steps = InstanceReboot.WithStep(r.Steps, step, outcome, detail, DateTimeOffset.UtcNow) },
                [.. extraLines, Line(RebootText.Of("reboot.log.step", ("step", step), ("outcome", outcome)).Then(detail))]);

        private IObservable<Unit> Finish(RebootText failure)
        {
            Interlocked.Exchange(ref finished, 1);
            var english = failure.ToEnglish();
            logger?.LogWarning("[Reboot] {Path}: Failed — {Failure}", ModuleReloadExecutor.PathOf(hub), english);
            return Write(r => r with
            {
                Status = InstanceRebootStatus.Failed,
                Failure = r.Failure is null ? english : $"{r.Failure}; {english}",
                FailureText = r.Failure is null ? failure : RebootText.Faithful(r.Failure, r.FailureText).Then(failure, glue: "; "),
                CompletedAt = DateTimeOffset.UtcNow,
            }, Line(failure));
        }

        /// <summary>
        /// One write on the request's own node. 🚨 A terminal request is final (MeshWeaver#6625): a write
        /// that meets one changes nothing AND emits nothing, so the step chain behind it ends there — a late
        /// Sync result cannot start the module landing, and a request ended before the restart stamp cannot
        /// go on to ask for the restart. Each line is stored in English AND keyed (MeshWeaver#7322).
        /// </summary>
        private IObservable<Unit> Write(Func<InstanceRebootRequest, InstanceRebootRequest> change, params RebootText[] lines)
        {
            var refused = 0;
            return Access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                    .Update<InstanceRebootRequest>(current =>
                    {
                        // Judged on THIS hub's state, inside the update — the owner's serialized step.
                        var ended = InstanceRebootStatus.IsTerminal(current.Status);
                        Volatile.Write(ref refused, ended ? 1 : 0);
                        if (ended)
                            return current;
                        // Every write is progress: the request's declared execution window runs from here.
                        return InstanceReboot.AppendLog(change(current) with { ProgressAt = DateTimeOffset.UtcNow }, MaxLogLines, lines);
                    }))
                .Take(1)
                .Where(_ => Volatile.Read(ref refused) == 0)
                .Do(_ =>
                {
                    foreach (var line in lines)
                        logger?.LogInformation("[Reboot] {Path}: {Line}", ModuleReloadExecutor.PathOf(hub), line.ToEnglish());
                })
                .Select(_ => Unit.Default);
        }

        private static RebootText Line(RebootText text) =>
            RebootText.Verbatim(InstanceReboot.Stamp(DateTimeOffset.UtcNow)).Then(text);
    }
}

/// <summary>
/// The per-PROCESS half of a reboot (step 5): a process that BOOTED after a request's restart stamp
/// runs every registered <see cref="IInstanceRebootCheck"/> here and reports under its OWN key of
/// <see cref="InstanceRebootRequest.Replicas"/>. It lists open requests at boot (which also re-activates
/// the executor) and hears new ones on the invalidation feed. A mesh-scoped singleton: its handled set
/// and start instant are instance state.
/// </summary>
public sealed class InstanceRebootAgent
{
    private readonly ConcurrentDictionary<string, byte> handled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When this process (its mesh) started — a report counts only when this is after the restart stamp.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The process identity a report is written under. Production reads the silo; a test that simulates a booted process supplies its own.</summary>
    internal Func<IMessageHub, string> ProcessOf { get; init; } = ActivationRecycle.ProcessIdentity;

    /// <summary>Arms the agent on the mesh hub of this process.</summary>
    internal IObservable<Unit> Arm(IMessageHub meshHub)
    {
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<InstanceRebootAgent>();
        var feed = meshHub.ServiceProvider.GetService<IMeshInvalidationFeed>();
        var started = meshHub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started);
        // The feed announces a COMMIT; its version is the floor of the read in Handle (the mirror can
        // trail the feed — see ModuleReloadAgent.Handle). The boot listing announces none.
        var heard = feed is null
            ? Observable.Empty<(string Path, long Committed)>()
            : Observable.Create<MeshChangeEvent>(observer => feed.Subscribe(observer.OnNext))
                .Where(change => change.Kind != MeshChangeKind.Deleted
                                 && string.Equals(change.NodeType, InstanceRebootRequest.NodeType, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(change.Namespace?.Trim('/'), InstanceRebootRequest.Namespace, StringComparison.OrdinalIgnoreCase))
                // A versionless feed event is a path-only invalidation: no floor is known, so the
                // read may trail the commit — named here, never acted on in silence.
                .Do(change =>
                {
                    if (change.Version <= 0)
                        logger?.LogWarning("[Reboot] {Path}: the feed announced a commit WITHOUT a version (path-only invalidation); "
                            + "this process reads its mirror unfloored and may act on the state before that commit", change.Path);
                })
                .Select(change => (change.Path, Committed: change.Version));
        var open = started.SelectMany(_ => OpenRequests(meshHub)).SelectMany(paths => paths)
            .Select(path => (Path: path, Committed: 0L));
        meshHub.RegisterForDisposal(started.SelectMany(_ => heard).Merge(open)
            .Select(heardOf => Handle(meshHub, heardOf.Path, heardOf.Committed)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "[Reboot] {Path}: this process could not report its verification (announced v{Committed})",
                        heardOf.Path, heardOf.Committed);
                    return Observable.Empty<Unit>();
                }))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[Reboot] the per-process agent on {Hub} stopped", meshHub.Address)));
        return Observable.Return(Unit.Default);
    }

    private static IObservable<ImmutableList<string>> OpenRequests(IMessageHub meshHub)
    {
        var mesh = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            // A terminal status is final, so a stale listing that says so is right; everything else is
            // read from its own node stream in Handle.
            .Select(change => change.Items
                .Where(n => n.ContentAs<InstanceRebootRequest>(meshHub.JsonSerializerOptions) is not { } r
                            || !InstanceRebootStatus.IsTerminal(r.Status))
                .Select(n => n.Path)
                .ToImmutableList());
    }

    /// <summary>
    /// Reads the request (which also activates its executor) and, when THIS process booted after its
    /// restart stamp and has not reported yet, runs every check and reports. The read is of the
    /// commit the feed announced (<paramref name="committed"/>), never of a mirror that trails it.
    /// </summary>
    /// <param name="meshHub">The mesh hub of this process.</param>
    /// <param name="path">The request's path.</param>
    /// <param name="committed">The version the feed announced, or <c>0</c> for a path from the boot listing.</param>
    internal IObservable<Unit> Handle(IMessageHub meshHub, string path, long committed = 0)
    {
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshHub.GetMeshNodeStream(path)
                .Where(node => node is not null && node.Version >= committed)
                // FirstAsync, never Take(1): a stream that COMPLETES before reaching the floor (a
                // disposed synchronization stream forwards OnCompleted) must surface as an error on
                // the agent's warning path, never as a silent empty that reports nothing.
                .FirstAsync()
                .Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(node =>
            {
                var request = node.ContentAs<InstanceRebootRequest>(meshHub.JsonSerializerOptions);
                if (request is null
                    || !string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
                    || request.Status != InstanceRebootStatus.AwaitingRestart
                    || request.RestartRequestedAt is not { } restart
                    || StartedAt <= restart
                    || !handled.TryAdd($"{path}|{restart:O}", 0))
                    return Observable.Empty<Unit>();
                return Report(meshHub, path, request);
            });
    }

    /// <summary>Runs every registered check in this process and writes the report under this process's own key.</summary>
    internal IObservable<Unit> Report(IMessageHub meshHub, string path, InstanceRebootRequest request)
    {
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        var options = meshHub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        var checks = meshHub.ServiceProvider.GetServices<IInstanceRebootCheck>().ToImmutableList();
        var process = ProcessOf(meshHub);
        return checks.ToObservable()
            // The checks run side by side — one may wait half an hour for a singleton's next pass — and
            // are reported in name order.
            .Select(check => check.Run(meshHub, request).Take(1)
                .Timeout(check.Budget ?? options.CheckBudget)
                .Catch((Exception ex) => Observable.Return(InstanceReboot.CheckOf(check.Name, InstanceRebootCheckOutcome.Failed,
                    ex is TimeoutException
                        ? RebootText.Of("reboot.check.noAnswer",
                            ("minutes", (check.Budget ?? options.CheckBudget).TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))
                        : RebootText.Of("reboot.check.faulted", ("type", ex.GetType().Name), ("message", ex.Message))))))
            .Merge()
            .ToList()
            .Select(results => results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList())
            .Select(results => new InstanceRebootReplica
            {
                Process = process,
                StartedAt = StartedAt,
                ReportedAt = DateTimeOffset.UtcNow,
                Image = PrebuiltAdoptionPolicy.RunningPlatformVersion,
                Checks = results.ToImmutableList(),
            })
            .SelectMany(report => access.RunAsSystem(() => meshHub.GetMeshNodeStream(path)
                .Update<InstanceRebootRequest>(current => current with { Replicas = current.Replicas.SetItem(process, report) })))
            .Take(1)
            .Select(_ => Unit.Default);
    }
}

/// <summary>Step 5's <c>pending_module_activation</c> reading: nothing landed is still waiting for an activation.</summary>
public sealed class PendingModuleActivationRebootCheck : IInstanceRebootCheck
{
    /// <inheritdoc />
    public string Name => "health:pending_module_activation";

    /// <inheritdoc />
    public IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request)
    {
        var activations = meshHub.ServiceProvider.GetService<PendingModuleActivations>();
        if (activations is null)
            return Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.NotMeasured,
                RebootText.Of("reboot.check.notRegistered", ("component", nameof(PendingModuleActivations)))));
        var pool = meshHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem);
        var read = pool is null
            ? Observable.Defer(() => Observable.Return(activations.Read()))
            : pool.InvokeBlocking(ct => activations.Read(ct));
        return read.Select(report => Judge(Name, report));
    }

    /// <summary>The verdict over one activation report. Pure.</summary>
    public static InstanceRebootCheck Judge(string name, ModuleActivationReport report) =>
        new()
        {
            Name = name,
            Outcome = report.IsUndetermined || report.HasPending || report.HasUnresolvable
                ? InstanceRebootCheckOutcome.Failed
                : InstanceRebootCheckOutcome.Passed,
            Detail = report.Describe(),
        };
}

/// <summary>Step 5's <c>content-types</c> reading: no NodeType's content degraded to untyped on this process.</summary>
public sealed class ContentTypesRebootCheck : IInstanceRebootCheck
{
    /// <inheritdoc />
    public string Name => "health:" + ContentDegradationRegistry.HealthCheckName;

    /// <inheritdoc />
    public IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request) => Observable.Defer(() =>
    {
        var registry = meshHub.ServiceProvider.GetService<ContentDegradationRegistry>();
        if (registry is null)
            return Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.NotMeasured,
                RebootText.Of("reboot.check.notRegistered", ("component", nameof(ContentDegradationRegistry)))));
        var degraded = registry.Unresolved(meshHub.ServiceProvider.GetService<IMeshContentTypeRegistry>());
        return Observable.Return(InstanceReboot.CheckOf(Name,
            degraded.Count == 0 ? InstanceRebootCheckOutcome.Passed : InstanceRebootCheckOutcome.Failed,
            RebootText.Verbatim(ContentDegradationRegistry.Describe(degraded)).Then(RebootText.Of("reboot.check.contentTypesScope"))));
    });
}

/// <summary>
/// Step 5's <c>nodetype_bake</c> reading: waits (bounded) for this process's bake to settle, then
/// fails on any CRITICAL NodeType (<see cref="InstanceRebootOptions.CriticalNamespaces"/>) that
/// regressed, is content-broken, failed with no baseline, or could not be evaluated. Non-critical
/// failures are named in the detail and do not fail the reboot.
/// </summary>
public sealed class NodeTypeBakeRebootCheck : IInstanceRebootCheck
{
    /// <summary>How often the settle wait re-reads the bake phase — the state is a synchronous
    /// in-process reading with no change signal (it serves the readiness probe), so the wait polls it.</summary>
    public static readonly TimeSpan SettlePoll = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string Name => "health:" + NodeTypeBakeGateExtensions.HealthCheckName;

    /// <inheritdoc />
    public IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request)
    {
        var state = meshHub.ServiceProvider.GetService<NodeTypeBakeGateState>();
        var options = meshHub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        if (state is null)
            return Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.NotMeasured,
                RebootText.Of("reboot.check.notRegistered", ("component", nameof(NodeTypeBakeGateState)))));
        return Observable.Interval(SettlePoll).StartWith(0L)
            .Select(_ => state.Phase)
            .Where(phase => phase is not (BakePhase.NotStarted or BakePhase.Running))
            .Take(1)
            .Timeout(options.BakeSettleBudget)
            .Select(_ => Judge(Name, state.Phase, state.Detail,
                [.. state.Regressions, .. state.ContentBroken, .. state.WithoutBaseline, .. state.Unevaluated],
                options.CriticalNamespaces))
            .Catch((TimeoutException _) => Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.Failed,
                RebootText.Of("reboot.check.bakeUnsettled",
                    ("minutes", options.BakeSettleBudget.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)),
                    ("phase", state.Phase.ToString()), ("detail", state.Detail)))));
    }

    /// <summary>The verdict over a settled bake: red iff a critical type failed. Pure.</summary>
    public static InstanceRebootCheck Judge(
        string name, BakePhase phase, string detail,
        IReadOnlyCollection<KeyValuePair<string, string>> failing, IReadOnlyCollection<string> critical)
    {
        bool IsCritical(string type) => critical.Any(ns =>
            type.Equals(ns, StringComparison.OrdinalIgnoreCase) || type.StartsWith(ns.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var red = failing.Where(f => IsCritical(f.Key)).Select(f => $"{f.Key} ({f.Value})").Distinct().ToImmutableList();
        var other = failing.Where(f => !IsCritical(f.Key)).Select(f => f.Key).Distinct().ToImmutableList();
        var text = (red.IsEmpty
                ? RebootText.Of("reboot.check.bakeGreen", ("critical", string.Join(", ", critical)), ("phase", phase.ToString()))
                : RebootText.Of("reboot.check.bakeRed", ("count", red.Count.ToString(invariant)), ("phase", phase.ToString()),
                    ("types", string.Join("; ", red))))
            .Then(other.IsEmpty ? RebootText.Empty : RebootText.Of("reboot.check.bakeOther",
                ("count", other.Count.ToString(invariant)), ("types", string.Join(", ", other.Take(10)))), glue: "; ");
        return InstanceReboot.CheckOf(name, red.IsEmpty ? InstanceRebootCheckOutcome.Passed : InstanceRebootCheckOutcome.Failed, text);
    }
}

/// <summary>
/// 🚨 Step 5's singleton reading: every configured singleton pass (<see cref="InstanceRebootOptions.SingletonPasses"/> —
/// on the control instance, the PR babysitter and the PR review sweep) must stamp a pass NEWER than the
/// reboot's restart. "Healthy" is not evidence that a singleton resumed: a health check once read healthy
/// while the last pass was 49 minutes old. Waits (bounded by <see cref="InstanceRebootOptions.SingletonResumeBudget"/>)
/// on each node's own stream; red, naming the singleton and its last pass, when one did not resume. A
/// singleton whose node this instance does not have is reported not measured, by name.
/// </summary>
public sealed class SingletonsResumedRebootCheck : IInstanceRebootCheck
{
    /// <inheritdoc />
    public string Name => "health:singletons-resumed";

    /// <inheritdoc />
    public TimeSpan? Budget => null;

    /// <inheritdoc />
    public IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request)
    {
        var options = meshHub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        if (options.SingletonPasses.IsDefaultOrEmpty)
            return Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.NotMeasured,
                RebootText.Of("reboot.check.noSingletons")));
        if (request.RestartRequestedAt is not { } restart)
            return Observable.Return(InstanceReboot.CheckOf(Name, InstanceRebootCheckOutcome.Failed,
                RebootText.Of("reboot.check.noRestartStamp")));
        return options.SingletonPasses.ToObservable()
            .Select(pass => One(meshHub, pass, restart, options.SingletonResumeBudget))
            .Merge()
            .ToList()
            .Select(rows => Judge(Name, rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToImmutableList()));
    }

    /// <summary>One singleton's reading: Passed (resumed), Failed (did not) or NotMeasured (no node here).</summary>
    private static IObservable<InstanceRebootCheck> One(IMessageHub meshHub, RebootSingletonPass pass, DateTimeOffset restart, TimeSpan budget)
    {
        var mesh = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        var parent = pass.Path.Contains('/') ? pass.Path[..pass.Path.LastIndexOf('/')] : pass.Path;
        DateTimeOffset? last = null;
        // Existence is a LISTING (a point read of an absent node opens the storm breaker); the stamp is
        // read from the node's own live stream.
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{parent} scope:children"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .Select(change => change.Items.Any(n => string.Equals(n.Path, pass.Path, StringComparison.Ordinal)))
            .SelectMany(exists => !exists
                ? Observable.Return(InstanceReboot.CheckOf(pass.Name, InstanceRebootCheckOutcome.NotMeasured,
                    RebootText.Of("reboot.check.singletonMissing", ("path", pass.Path))))
                : access.RunAsSystem(() => meshHub.GetMeshNodeStream(pass.Path))
                    .Select(node => PassAt(node?.Content, pass.Field, meshHub))
                    .Do(at => last = at ?? last)
                    .Where(at => at > restart)
                    .Take(1)
                    .Timeout(budget)
                    .Select(at => InstanceReboot.CheckOf(pass.Name, InstanceRebootCheckOutcome.Passed,
                        RebootText.Of("reboot.check.singletonResumed", ("path", pass.Path), ("field", pass.Field),
                            ("at", InstanceReboot.Stamp(at!.Value)), ("restart", InstanceReboot.Stamp(restart)))))
                    .Catch((Exception ex) => Observable.Return(InstanceReboot.CheckOf(pass.Name, InstanceRebootCheckOutcome.Failed,
                        ex is TimeoutException
                            ? RebootText.Of("reboot.check.singletonStuck",
                                    ("restart", InstanceReboot.Stamp(restart)),
                                    ("minutes", budget.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)),
                                    ("path", pass.Path), ("field", pass.Field))
                                .Then(RebootText.Or(last is { } l ? InstanceReboot.Stamp(l) : null, "reboot.word.none"))
                                .Then(RebootText.Of("reboot.check.singletonDidNotResume"), glue: "")
                            : RebootText.Of("reboot.check.unreadable", ("path", pass.Path), ("type", ex.GetType().Name), ("message", ex.Message))))))
            .Catch((Exception ex) => Observable.Return(InstanceReboot.CheckOf(pass.Name, InstanceRebootCheckOutcome.Failed,
                RebootText.Of("reboot.check.existenceUnknown", ("path", pass.Path), ("type", ex.GetType().Name), ("message", ex.Message)))));
    }

    /// <summary>The instant a pass stamped in <paramref name="field"/> of <paramref name="content"/>, or null. Pure over the content.</summary>
    public static DateTimeOffset? PassAt(object? content, string field, IMessageHub hub)
    {
        if (content is null)
            return null;
        var element = content is System.Text.Json.JsonElement e ? e : System.Text.Json.JsonSerializer.SerializeToElement(content, hub.JsonSerializerOptions);
        return element.ValueKind == System.Text.Json.JsonValueKind.Object
               && element.TryGetProperty(field, out var value)
               && value.ValueKind == System.Text.Json.JsonValueKind.String
               && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    /// <summary>The verdict over the per-singleton rows: red if any is red, not measured if none was measured. Pure.</summary>
    public static InstanceRebootCheck Judge(string name, IReadOnlyList<InstanceRebootCheck> rows) =>
        InstanceReboot.CheckOf(name,
            rows.Any(r => r.Outcome == InstanceRebootCheckOutcome.Failed) ? InstanceRebootCheckOutcome.Failed
            : rows.All(r => r.Outcome == InstanceRebootCheckOutcome.NotMeasured) ? InstanceRebootCheckOutcome.NotMeasured
            : InstanceRebootCheckOutcome.Passed,
            RebootText.Join("; ", rows.Select(r => RebootText.Of("reboot.check.row", ("check", r.Name), ("outcome", r.Outcome))
                .Then(RebootText.Faithful(r.Detail, r.DetailText)))));
}
