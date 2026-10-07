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

    /// <summary>Arms the executor on a request node's own hub. Registered as the node type's initialization.</summary>
    public static IObservable<Unit> Arm(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceRebootExecutor));
        var run = new Run(hub, logger);
        hub.RegisterForDisposal(hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream())
            .Select(node => run.Step(node))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex,
                    "[Reboot] the executor on {Path} stopped — the request keeps its last status; "
                    + "the next activation of this node resumes it", hub.Address)));
        return Observable.Return(Unit.Default);
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
                    $"written by '{node.CreatedBy}', not by System — only InstanceReboot.Request may issue a "
                    + "reboot (its callers authorise the requester); nothing was synced, landed or restarted"));
            if (InstanceReboot.Validate(request) is { } invalid)
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
                _ => Once(ref finished, () => Finish($"the request carries the status '{request.Status}', which this executor does not know")),
            };
        }

        private static IObservable<Unit> Once(ref int flag, Func<IObservable<Unit>> work) =>
            Interlocked.Exchange(ref flag, 1) == 0 ? work() : Observable.Empty<Unit>();

        // ── Steps 1–3 ─────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Prepare(InstanceRebootRequest request) =>
            Write(r => r with { Status = InstanceRebootStatus.Preparing },
                    Line($"reboot requested by {request.RequestedBy ?? "(unattributed)"} ({request.Trigger}"
                         + (request.Wedged ? ", wedged" : "") + $") — reason: {request.Reason}"
                         + (request.WedgeEvidence is { } e ? $"; wedge evidence: {e}" : "")))
                .SelectMany(_ => StepSync())
                .SelectMany(_ => StepModules())
                .SelectMany(_ => StepImage(request.Wedged))
                .SelectMany(_ => Write(r => r with { Status = InstanceRebootStatus.AwaitingRestart },
                    Line("steps 1–3 done — requesting the ONE roll/restart")))
                .Catch((Exception ex) => Finish($"the preparation faulted: {ex.Message}"));

        private IObservable<Unit> StepSync() =>
            Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Running, "importing every module source at its branch HEAD")
                .SelectMany(_ => hub.ServiceProvider.GetService<GitHubSyncService>() is null
                    ? Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Skipped,
                        "this host registers no GitSync service — no module source can be synced here")
                    : ModuleSourceUpdate.UpdateAll(hub).SelectMany(rows =>
                    {
                        var updated = rows.Where(r => r.Outcome == ModuleSourceUpdateOutcome.Updated).ToImmutableList();
                        var failed = rows.Where(r => r.Outcome == ModuleSourceUpdateOutcome.Failed).ToImmutableList();
                        var lines = rows.Select(r => $"sync {r.ConfigPath}: {r.Outcome} — {r.Detail}").ToArray();
                        var summary = $"{updated.Count} module source(s) updated, {failed.Count} failed, "
                                      + $"{rows.Count - updated.Count - failed.Count} skipped (not module sources)"
                                      + (failed.IsEmpty ? "" : ": " + string.Join("; ", failed.Select(f => $"{f.ConfigPath} — {f.Detail}")));
                        var outcome = !failed.IsEmpty ? InstanceRebootStepOutcome.Failed
                            : updated.IsEmpty ? InstanceRebootStepOutcome.Skipped
                            : InstanceRebootStepOutcome.Ok;
                        return Mark(InstanceRebootSteps.Sync, outcome,
                            updated.IsEmpty && failed.IsEmpty ? $"no module source to sync — {summary}" : summary, lines);
                    }))
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Failed, $"faulted: {ex.Message}"));

        private IObservable<Unit> StepModules() =>
            Mark(InstanceRebootSteps.Modules, InstanceRebootStepOutcome.Running, "resolving and landing the newest compatible version of every installed module")
                .SelectMany(_ => ModuleReloadExecutor.ResolveAndLand(hub, module: null))
                .SelectMany(landed =>
                {
                    if (landed.Problem is { } problem)
                        // "No installed module" is not red — there is nothing to land; everything else is.
                        return Mark(InstanceRebootSteps.Modules,
                            problem.StartsWith("no installed package", StringComparison.Ordinal)
                                ? InstanceRebootStepOutcome.Skipped
                                : InstanceRebootStepOutcome.Failed,
                            problem);
                    var items = landed.Items;
                    var failures = items.Where(i => i.Failure is not null).ToImmutableList();
                    var newly = items.Where(i => i.Failure is null && i.Landed).ToImmutableList();
                    var summary = $"{items.Count} module(s): {newly.Count} newly landed, {failures.Count} declined or failed"
                                  + (newly.IsEmpty ? "" : " — landed " + string.Join(", ", newly.Select(i => $"{i.Module} {i.TargetVersion}")))
                                  + (failures.IsEmpty ? "" : " — " + string.Join("; ", failures.Select(i => $"{i.Module}: {i.Failure}")));
                    // A floor above the running platform is a DECLINE by name — the right answer, not a red step;
                    // anything else that kept a module from landing is red.
                    var red = failures.Any(i => !i.Failure!.StartsWith("declined", StringComparison.Ordinal));
                    return Write(r => r with { Modules = items }, Line($"modules: {summary}"))
                        .SelectMany(_ => Mark(InstanceRebootSteps.Modules,
                            red ? InstanceRebootStepOutcome.Failed : InstanceRebootStepOutcome.Ok, summary));
                })
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Modules, InstanceRebootStepOutcome.Failed, $"faulted: {ex.Message}"));

        private IObservable<Unit> StepImage(bool wedged)
        {
            var activation = hub.ServiceProvider.GetService<IInstanceRebootActivation>();
            if (activation is null)
                return Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Skipped,
                    "this host registers no self-updater (IInstanceRebootActivation) — no image can be picked; the restart step names what that means");
            return Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Running, "picking the newest image the update policy admits")
                .SelectMany(_ => activation.SelectImage(wedged).Take(1))
                .SelectMany(choice => Write(r => r with { RunningImage = choice.Running, TargetImage = choice.Target },
                        Line($"image: running {choice.Running ?? "?"}, target {choice.Target ?? "(none — restart on the running image)"} — {choice.Detail}"))
                    .SelectMany(_ => Mark(InstanceRebootSteps.Image,
                        choice.Failed ? InstanceRebootStepOutcome.Failed
                        : choice.Target is null ? InstanceRebootStepOutcome.Skipped
                        : InstanceRebootStepOutcome.Ok,
                        choice.Target is null ? $"no newer image — {choice.Detail}" : $"{choice.Target} — {choice.Detail}")))
                .Catch((Exception ex) => Mark(InstanceRebootSteps.Image, InstanceRebootStepOutcome.Failed, $"faulted: {ex.Message}"));
        }

        // ── Step 4 ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> IssueRestart(InstanceRebootRequest request)
        {
            var activation = hub.ServiceProvider.GetService<IInstanceRebootActivation>();
            if (activation is null)
                return Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed,
                        "this host has no roll/restart path (no IInstanceRebootActivation is registered) — restart the workloads by hand")
                    .SelectMany(_ => Finish("the restart could not be requested: no roll/restart path on this host"));
            var choice = new RebootImageChoice(request.RunningImage, request.TargetImage, "as chosen by the Image step");
            var reason = $"reboot {ModuleReloadExecutor.PathOf(hub)} by {request.RequestedBy ?? "(unattributed)"} ({request.Trigger}): {request.Reason}";
            // 🚨 Stamp FIRST, then ask. A resumed executor sees the stamp and never asks again.
            return Write(r => r with { RestartRequestedAt = DateTimeOffset.UtcNow }, Line(
                    request.TargetImage is null ? "requesting ONE restart on the running image" : $"requesting ONE roll to {request.TargetImage}"))
                .SelectMany(_ => Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Running, "requested"))
                .SelectMany(_ => activation.Activate(choice, reason).Take(1))
                .SelectMany(outcome => outcome.Scheduled
                    ? Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Ok, $"{outcome.Kind}: {outcome.Detail}")
                        .SelectMany(_ => Mark(InstanceRebootSteps.Verify, InstanceRebootStepOutcome.Running,
                            "waiting for a process booted after the restart to report its checks"))
                    : string.Equals(outcome.Kind, RebootActivationKinds.Faulted, StringComparison.Ordinal)
                        ? Fault($"the restart request faulted ({outcome.Kind}): {outcome.Detail}")
                        : Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed, $"{outcome.Kind}: {outcome.Detail}")
                            .SelectMany(_ => Finish($"the restart could not be requested ({outcome.Kind}): {outcome.Detail}")))
                // A restart request that THREW is a crash, never a decided "cannot restart" (#6172).
                .Catch((Exception ex) => Fault($"the restart request faulted: {ex.Message}"));
        }

        /// <summary>The restart request crashed: the step is red for THIS attempt and the request goes
        /// Faulted (not terminal) — the reconcile pass re-arms it once its backoff is due.</summary>
        private IObservable<Unit> Fault(string why)
        {
            logger?.LogWarning("[Reboot] {Path}: attempt {Attempt} Faulted — {Why}; retried once its backoff is due",
                ModuleReloadExecutor.PathOf(hub), attempt, why);
            return Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Failed, $"{why} — retried once its backoff is due")
                .SelectMany(_ => Write(r => r with
                {
                    Status = InstanceRebootStatus.Faulted,
                    FaultedAt = DateTimeOffset.UtcNow,
                    Failure = why,
                }, Line($"attempt {attempt} faulted: {why} — the reconcile pass re-arms it")));
        }

        // ── Step 5 ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Evaluate(InstanceRebootRequest request)
        {
            var membership = hub.ServiceProvider.GetService<IClusterMembership>();
            var verdict = InstanceReboot.EvaluateVerification(request,
                process => membership is null || membership.StateOf(process) != ClusterMemberState.Gone);
            if (verdict is null || Interlocked.Exchange(ref finished, 1) != 0)
                return Observable.Empty<Unit>();
            // A self-patch restart can end the process that asked for it before the outcome line is
            // written; a process booted after the stamp reporting IS the evidence the restart happened.
            var restartRow = request.Steps.FirstOrDefault(s => s.Name == InstanceRebootSteps.Restart);
            var settleRestart = restartRow?.Outcome == InstanceRebootStepOutcome.Running
                ? Mark(InstanceRebootSteps.Restart, InstanceRebootStepOutcome.Ok,
                    "a process booted after the restart stamp reported — the restart happened (its own outcome line was not written before the process ended)")
                : Observable.Return(Unit.Default);
            return settleRestart.SelectMany(_ => Mark(InstanceRebootSteps.Verify,
                    verdict.Passed ? InstanceRebootStepOutcome.Ok : InstanceRebootStepOutcome.Failed, verdict.Detail)
                .SelectMany(_ => Write(r =>
                {
                    var red = InstanceReboot.RedSteps(r);
                    return r with
                    {
                        Status = red.IsEmpty ? InstanceRebootStatus.Done : InstanceRebootStatus.Failed,
                        Failure = red.IsEmpty ? null : string.Join("; ", red),
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }, Line(verdict.Passed ? $"verified: {verdict.Detail}" : $"verification RED: {verdict.Detail}"))));
        }

        // ── Writes ────────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Mark(string step, string outcome, string detail, params string[] extraLines) =>
            Write(r => r with { Steps = InstanceReboot.WithStep(r.Steps, step, outcome, detail, DateTimeOffset.UtcNow) },
                [.. extraLines, Line($"{step}: {outcome} — {detail}")]);

        private IObservable<Unit> Finish(string failure)
        {
            Interlocked.Exchange(ref finished, 1);
            logger?.LogWarning("[Reboot] {Path}: Failed — {Failure}", ModuleReloadExecutor.PathOf(hub), failure);
            return Write(r => r with
            {
                Status = InstanceRebootStatus.Failed,
                Failure = r.Failure is null ? failure : $"{r.Failure}; {failure}",
                CompletedAt = DateTimeOffset.UtcNow,
            }, Line(failure));
        }

        private IObservable<Unit> Write(Func<InstanceRebootRequest, InstanceRebootRequest> change, params string[] lines)
        {
            foreach (var line in lines)
                logger?.LogInformation("[Reboot] {Path}: {Line}", ModuleReloadExecutor.PathOf(hub), line);
            return Access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                    .Update<InstanceRebootRequest>(current =>
                    {
                        var next = change(current);
                        var log = next.Log.AddRange(lines);
                        return next with { Log = log.Count > MaxLogLines ? log.RemoveRange(0, log.Count - MaxLogLines) : log };
                    }))
                .Take(1)
                .Select(_ => Unit.Default);
        }

        private static string Line(string text) => $"{DateTimeOffset.UtcNow:u} {text}";
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
                .Take(1)
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
                .Catch((Exception ex) => Observable.Return(new InstanceRebootCheck
                {
                    Name = check.Name,
                    Outcome = InstanceRebootCheckOutcome.Failed,
                    Detail = ex is TimeoutException
                        ? $"did not answer within {(check.Budget ?? options.CheckBudget).TotalMinutes:0} min — NOT proven"
                        : $"faulted: {ex.GetType().Name}: {ex.Message}",
                })))
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
            return Observable.Return(new InstanceRebootCheck
            {
                Name = Name, Outcome = InstanceRebootCheckOutcome.NotMeasured,
                Detail = "PendingModuleActivations is not registered on this host",
            });
        var pool = meshHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem);
        var read = pool is null
            ? Observable.Defer(() => Observable.Return(activations.Read()))
            : pool.InvokeBlocking(_ => activations.Read());
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
            return Observable.Return(new InstanceRebootCheck
            {
                Name = Name, Outcome = InstanceRebootCheckOutcome.NotMeasured,
                Detail = "ContentDegradationRegistry is not registered on this host",
            });
        var degraded = registry.Unresolved(meshHub.ServiceProvider.GetService<IMeshContentTypeRegistry>());
        return Observable.Return(new InstanceRebootCheck
        {
            Name = Name,
            Outcome = degraded.Count == 0 ? InstanceRebootCheckOutcome.Passed : InstanceRebootCheckOutcome.Failed,
            Detail = ContentDegradationRegistry.Describe(degraded)
                     + " (a type nobody has read on this process yet is not in this reading)",
        });
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
            return Observable.Return(new InstanceRebootCheck
            {
                Name = Name, Outcome = InstanceRebootCheckOutcome.NotMeasured,
                Detail = "NodeTypeBakeGateState is not registered on this host",
            });
        return Observable.Interval(SettlePoll).StartWith(0L)
            .Select(_ => state.Phase)
            .Where(phase => phase is not (BakePhase.NotStarted or BakePhase.Running))
            .Take(1)
            .Timeout(options.BakeSettleBudget)
            .Select(_ => Judge(Name, state.Phase, state.Detail,
                [.. state.Regressions, .. state.ContentBroken, .. state.WithoutBaseline, .. state.Unevaluated],
                options.CriticalNamespaces))
            .Catch((TimeoutException _) => Observable.Return(new InstanceRebootCheck
            {
                Name = Name,
                Outcome = InstanceRebootCheckOutcome.Failed,
                Detail = $"the bake had not settled after {options.BakeSettleBudget.TotalMinutes:0} min (phase {state.Phase}: {state.Detail}) — NOT proven",
            }));
    }

    /// <summary>The verdict over a settled bake: red iff a critical type failed. Pure.</summary>
    public static InstanceRebootCheck Judge(
        string name, BakePhase phase, string detail,
        IReadOnlyCollection<KeyValuePair<string, string>> failing, IReadOnlyCollection<string> critical)
    {
        bool IsCritical(string type) => critical.Any(ns =>
            type.Equals(ns, StringComparison.OrdinalIgnoreCase) || type.StartsWith(ns.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        var red = failing.Where(f => IsCritical(f.Key)).Select(f => $"{f.Key} ({f.Value})").Distinct().ToImmutableList();
        var other = failing.Where(f => !IsCritical(f.Key)).Select(f => f.Key).Distinct().ToImmutableList();
        return new InstanceRebootCheck
        {
            Name = name,
            Outcome = red.IsEmpty ? InstanceRebootCheckOutcome.Passed : InstanceRebootCheckOutcome.Failed,
            Detail = (red.IsEmpty
                         ? $"no critical NodeType ({string.Join(", ", critical)}) failed the bake (phase {phase})"
                         : $"{red.Count} critical NodeType(s) failed the bake (phase {phase}): {string.Join("; ", red)}")
                     + (other.IsEmpty ? "" : $"; {other.Count} non-critical type(s) also failing: {string.Join(", ", other.Take(10))}"),
        };
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
            return Observable.Return(new InstanceRebootCheck
            {
                Name = Name, Outcome = InstanceRebootCheckOutcome.NotMeasured, Detail = "no singleton pass is configured",
            });
        if (request.RestartRequestedAt is not { } restart)
            return Observable.Return(new InstanceRebootCheck
            {
                Name = Name, Outcome = InstanceRebootCheckOutcome.Failed, Detail = "the reboot carries no restart stamp to compare passes against",
            });
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
                ? Observable.Return(new InstanceRebootCheck
                {
                    Name = pass.Name, Outcome = InstanceRebootCheckOutcome.NotMeasured,
                    Detail = $"{pass.Path} does not exist on this instance",
                })
                : access.RunAsSystem(() => meshHub.GetMeshNodeStream(pass.Path))
                    .Select(node => PassAt(node?.Content, pass.Field, meshHub))
                    .Do(at => last = at ?? last)
                    .Where(at => at > restart)
                    .Take(1)
                    .Timeout(budget)
                    .Select(at => new InstanceRebootCheck
                    {
                        Name = pass.Name, Outcome = InstanceRebootCheckOutcome.Passed,
                        Detail = $"{pass.Path}.{pass.Field} = {at:u}, after the restart at {restart:u}",
                    })
                    .Catch((Exception ex) => Observable.Return(new InstanceRebootCheck
                    {
                        Name = pass.Name, Outcome = InstanceRebootCheckOutcome.Failed,
                        Detail = ex is TimeoutException
                            ? $"no pass after the restart at {restart:u} within {budget.TotalMinutes:0} min — the last pass stamped on "
                              + $"{pass.Path}.{pass.Field} is {(last is { } l ? $"{l:u}" : "none")}; the singleton did NOT resume"
                            : $"{pass.Path} could not be read ({ex.GetType().Name}: {ex.Message})",
                    })))
            .Catch((Exception ex) => Observable.Return(new InstanceRebootCheck
            {
                Name = pass.Name, Outcome = InstanceRebootCheckOutcome.Failed,
                Detail = $"whether {pass.Path} exists could not be established ({ex.GetType().Name}: {ex.Message}) — NOT proven",
            }));
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
    public static InstanceRebootCheck Judge(string name, IReadOnlyList<InstanceRebootCheck> rows) => new()
    {
        Name = name,
        Outcome = rows.Any(r => r.Outcome == InstanceRebootCheckOutcome.Failed) ? InstanceRebootCheckOutcome.Failed
            : rows.All(r => r.Outcome == InstanceRebootCheckOutcome.NotMeasured) ? InstanceRebootCheckOutcome.NotMeasured
            : InstanceRebootCheckOutcome.Passed,
        Detail = string.Join("; ", rows.Select(r => $"{r.Name}: {r.Outcome} — {r.Detail}")),
    };
}
