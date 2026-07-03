using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Courses.Configuration;

/// <summary>
/// Internal fire-and-forget message posted by
/// <see cref="ExerciseValidationWatcher.Install"/> when it observes an
/// unhandled <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/> trigger
/// on the attempt's own MeshNode. The handler
/// (<see cref="ExerciseValidationWatcher.HandleDispatchValidation"/>) runs on
/// the per-attempt hub's ActionBlock — the single-threaded dispatcher that
/// owns "drive a validation for this attempt". Routing the dispatch through a
/// message instead of executing in the watcher's Subscribe callback avoids the
/// cross-scheduler deadlock class (same shape as
/// <c>DispatchCompileTrigger</c> in MeshWeaver.Graph).
/// </summary>
/// <param name="Node">Snapshot of the attempt MeshNode at the moment the
/// trigger was observed. The handler reads the observed trigger timestamp off
/// it and CAS-claims against the live state.</param>
public record DispatchValidationTrigger(MeshNode Node);

/// <summary>
/// The per-attempt validation control plane, cloned from
/// <c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>:
/// <list type="number">
///   <item><see cref="Install"/> subscribes the hub's OWN node stream and, on an
///   unhandled <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/>
///   trigger, does NOTHING but post a <see cref="DispatchValidationTrigger"/>
///   to the OWN hub.</item>
///   <item><see cref="HandleDispatchValidation"/> (on the ActionBlock)
///   CAS-claims the trigger by stamping
///   <see cref="ExerciseAttemptStatus.LastValidationHandledAt"/> inside the
///   Update lambda (status-based single-flight, no in-memory flag), then
///   dispatches: reads the attempt's code and the exercise's validation
///   tests, creates an Activity node under <c>{userHome}/_Activity/{id}</c>,
///   posts a <see cref="SubmitCodeRequest"/> with the concatenated script to
///   the activity address (the Activity hub hosts the kernel), and observes
///   the activity to its terminal status.</item>
///   <item>The terminal status stamps the attempt:
///   <c>Succeeded</c>/<c>Warning</c> → <see cref="AttemptStatus.Passed"/>
///   (+ <see cref="ExerciseAttemptStatus.PassedAt"/>), <c>Failed</c>/<c>Cancelled</c>
///   → <see cref="AttemptStatus.Failed"/>; either way
///   <see cref="ExerciseAttemptStatus.LastValidationActivityPath"/> is set.
///   Kernel semantics give pass/fail for free — an unhandled exception in the
///   validation script terminates the activity as <c>Failed</c>.</item>
/// </list>
/// Reactive end-to-end — no <c>async</c>/<c>await</c>/<c>Task</c>, every cold
/// <c>Update</c>/<c>CreateNode</c> is subscribed with an error sink.
/// </summary>
public static class ExerciseValidationWatcher
{
    private const string LoggerCategory = "MeshWeaver.Courses.ValidationWatcher";

    /// <summary>
    /// Installs the validation watcher on the per-attempt hub. Wired from the
    /// ExerciseAttempt NodeType's <c>WithInitialization</c> hook so the
    /// watcher's lifetime matches the hub's; the subscription is registered
    /// for hub disposal.
    /// </summary>
    /// <param name="hub">The per-attempt hub.</param>
    public static void Install(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var hubPath = hub.Address.Path;

        IWorkspace workspace;
        try
        {
            workspace = hub.GetWorkspace();
        }
        catch (Exception ex)
        {
            // No workspace → no MeshDataSource on this hub (misconfiguration).
            // Surface it — a silently-missing watcher would strand every
            // validation request on this attempt.
            logger?.LogWarning(ex,
                "Validation watcher NOT installed for {HubPath}: no workspace available", hubPath);
            return;
        }

        // Single-flight is STATUS-BASED: the Where only passes triggers strictly
        // past LastValidationHandledAt, and HandleDispatchValidation atomically
        // stamps LastValidationHandledAt inside the serialized ActionBlock Update —
        // only the FIRST trigger of a burst dispatches, every later one no-ops.
        // DistinctUntilChanged coalesces duplicate emissions of the same trigger
        // at the Subscribe layer so the inbox isn't flooded.
        //
        // 🚨 The Subscribe callback does NOTHING but post to the OWN hub — no
        // Update / read / query inside the callback (it can fire on the workspace
        // emission thread; doing work there is the deadlock class the compile
        // watcher eliminated).
        var watcherSub = workspace.GetMeshNodeStream()
            .Where(node => node?.Content is ExerciseAttemptStatus status
                && status.ValidationRequestedAt is { } req
                && (status.LastValidationHandledAt is null || req > status.LastValidationHandledAt.Value))
            .DistinctUntilChanged(node => ((ExerciseAttemptStatus)node!.Content!).ValidationRequestedAt)
            .Subscribe(
                pendingNode =>
                {
                    logger?.LogDebug(
                        "Validation watcher: trigger observed for {HubPath} — posting DispatchValidationTrigger to OWN hub",
                        hubPath);
                    // The watcher trigger has no caller context (the requester's
                    // identity is persisted on ValidationRequestedBy). The dispatch
                    // runs under SYSTEM — the access gate is upstream: the user had
                    // to be permitted to flip ValidationRequestedAt on the attempt
                    // node. Same credential split as the compile watcher.
                    using (AccessContextScope.AsSystem(accessService))
                    {
                        hub.Post(new DispatchValidationTrigger(pendingNode!),
                            o => o.WithTarget(hub.Address));
                    }
                },
                ex => logger?.LogWarning(ex,
                    "Validation watcher faulted for {HubPath}", hubPath));

        hub.RegisterForDisposal(watcherSub);
    }

    /// <summary>
    /// Per-attempt-hub handler for <see cref="DispatchValidationTrigger"/>.
    /// Runs on the hub's ActionBlock. Owns the CAS claim (stamp
    /// <see cref="ExerciseAttemptStatus.LastValidationHandledAt"/> only when
    /// the observed trigger is still unhandled) + the validation dispatch.
    /// </summary>
    public static IMessageDelivery HandleDispatchValidation(
        IMessageHub hub, IMessageDelivery<DispatchValidationTrigger> request)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(LoggerCategory);
        var hubPath = hub.Address.Path;
        var workspace = hub.GetWorkspace();

        // The trigger value OBSERVED by the watcher for THIS dispatch. The claim
        // gates on this CARRIED value — never a live re-read, which may have
        // moved by the time the Update lambda runs.
        var triggerAt = request.Message.Node
            .ContentAs<ExerciseAttemptStatus>(hub.JsonSerializerOptions)?.ValidationRequestedAt;
        if (triggerAt is not { } trigger)
            return request.Processed();

        // Atomic claim. The ActionBlock serialises deliveries, so two
        // DispatchValidationTriggers cannot run in parallel — the second sees
        // LastValidationHandledAt >= trigger and the lambda short-circuits.
        var claimed = false;
        var exercisePath = string.Empty;
        workspace.GetMeshNodeStream().Update(curr =>
            {
                if (curr.Content is not ExerciseAttemptStatus status) return curr;
                if (status.LastValidationHandledAt is { } handled && trigger <= handled) return curr;
                claimed = true;
                exercisePath = status.ExercisePath;
                return curr with
                {
                    Content = status with { LastValidationHandledAt = trigger }
                };
            })
            .Take(1)
            .Subscribe(
                _ =>
                {
                    if (!claimed)
                    {
                        logger.LogDebug(
                            "Validation dispatch: trigger already handled for {HubPath} — skipping",
                            hubPath);
                        return;
                    }
                    RunValidation(hub, workspace, exercisePath, logger);
                },
                ex => logger.LogWarning(ex,
                    "Validation dispatch: claim Update faulted for {HubPath}", hubPath));

        return request.Processed();
    }

    /// <summary>
    /// The claimed validation run: read the attempt's working copy
    /// (<c>{attempt}/Source/Code</c>) and the exercise's validation tests
    /// (<c>{exercisePath}/Test/Validation</c>), create the Activity node,
    /// submit the concatenated script to the kernel hosted on the activity
    /// hub, and observe the activity to its terminal status. Runs under
    /// SYSTEM at each subscribe boundary (the delivery scope has cleared by
    /// the time the deferred pipelines subscribe — the compile watcher's
    /// re-establish-System pattern).
    /// </summary>
    private static void RunValidation(
        IMessageHub hub, IWorkspace workspace, string exercisePath, ILogger logger)
    {
        var hubPath = hub.Address.Path;
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        if (string.IsNullOrWhiteSpace(exercisePath))
        {
            logger.LogWarning(
                "Validation for {HubPath} has no ExercisePath — stamping Failed", hubPath);
            StampResult(workspace, accessService, logger, hubPath, activityPath: null, passed: false);
            return;
        }

        var attemptCodePath =
            $"{hubPath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseAttemptNodeType.AttemptCodeNodeId}";
        var validationPath =
            $"{exercisePath}/{ExerciseNodeType.TestSubNamespace}/{ExerciseNodeType.ValidationNodeId}";

        // The activity anchors under the trainee's home (the attempt's partition
        // root) — every validation shows up in the user's activity feed, and the
        // satellite path stays shallow (clone of CodeNodeType's default).
        var activityId = Guid.NewGuid().ToString("N");
        var userHome = hubPath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs
            ? segs[0]
            : hubPath;
        var activityNamespace = $"{userHome}/_Activity";
        var activityPath = $"{activityNamespace}/{activityId}";

        Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => Observable.Zip(
                    hub.GetMeshNode(attemptCodePath),
                    hub.GetMeshNode(validationPath),
                    (attemptNode, validationNode) => (
                        Attempt: attemptNode?.Content as CodeConfiguration,
                        Validation: validationNode?.Content as CodeConfiguration)))
            .SelectMany(pair =>
            {
                if (pair.Attempt?.Code is not { } traineeCode)
                    return Observable.Throw<(string Code, string Language)>(
                        new InvalidOperationException($"No attempt code found at {attemptCodePath}"));
                if (pair.Validation?.Code is not { } testCode)
                    return Observable.Throw<(string Code, string Language)>(
                        new InvalidOperationException($"No validation tests found at {validationPath}"));

                // ONE submission — trainee code and validation tests share the
                // same REPL scope, so the tests can assert on anything the
                // trainee defined.
                var combined = traineeCode + "\n\n// --- validation tests ---\n" + testCode;
                var language = string.IsNullOrWhiteSpace(pair.Attempt.Language)
                    ? "csharp"
                    : pair.Attempt.Language;

                var activityNode = new MeshNode(activityId, activityNamespace)
                {
                    Name = $"Validate {hubPath}",
                    NodeType = ActivityNodeType.NodeType,
                    MainNode = userHome,
                    State = MeshNodeState.Active,
                    Content = new ActivityLog("ExerciseValidation")
                    {
                        Id = activityId,
                        HubPath = hubPath,
                        Status = ActivityStatus.Running
                    }
                };
                // Re-establish SYSTEM at this subscribe boundary — the activity
                // create is framework infrastructure (the user's gate was the
                // ValidationRequestedAt flip).
                return Observable.Using(
                        () => AccessContextScope.AsSystem(accessService),
                        _ => meshService.CreateNode(activityNode))
                    .Take(1)
                    .Select(_ => (Code: combined, Language: language));
            })
            .Subscribe(
                submission =>
                {
                    // The Activity hub hosts the kernel (AddKernelSubHubHandlers):
                    // SubmitCodeRequest lands inside the activity's own action
                    // block and streams progress + terminal status into the
                    // ActivityLog node — exactly the Code node's run pipeline.
                    hub.Post(
                        new SubmitCodeRequest(submission.Code)
                        {
                            Id = activityId,
                            ActivityLogPath = activityPath,
                            Language = submission.Language
                        },
                        o => o.WithTarget(new Address(activityPath)));

                    ObserveActivityToTerminal(hub, workspace, accessService, logger, hubPath, activityPath);
                },
                ex =>
                {
                    // Graceful sink — a validation that cannot even dispatch is a
                    // FAILED validation, surfaced on the attempt node (never a
                    // silent hang).
                    logger.LogWarning(ex,
                        "Validation dispatch failed for {HubPath} (exercise {ExercisePath})",
                        hubPath, exercisePath);
                    StampResult(workspace, accessService, logger, hubPath, activityPath: null, passed: false);
                });
    }

    /// <summary>
    /// Subscribes the validation activity's node stream until it reaches a
    /// terminal <see cref="ActivityStatus"/> and stamps the outcome onto the
    /// attempt. The subscription is registered for hub disposal so an
    /// activity that never terminates cannot outlive the hub.
    /// </summary>
    private static void ObserveActivityToTerminal(
        IMessageHub hub, IWorkspace workspace, AccessService? accessService,
        ILogger logger, string hubPath, string activityPath)
    {
        // System scope wraps the LIVE cross-hub subscription (Observable.Using),
        // not just the build call — reading the activity is watcher
        // infrastructure, same as the sources watcher's GetSources read.
        var terminalSub = Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => workspace.GetMeshNodeStream(activityPath))
            .Select(node => node?.Content as ActivityLog)
            .Where(log => log is not null && log.Status != ActivityStatus.Running)
            .Take(1)
            .Subscribe(
                terminal =>
                {
                    // Succeeded / Warning → Passed (a logged warning is not a
                    // failed assertion); Failed / Cancelled → Failed.
                    var passed = terminal!.Status is ActivityStatus.Succeeded or ActivityStatus.Warning;
                    logger.LogDebug(
                        "Validation for {HubPath} terminated {Status} (activity {ActivityPath})",
                        hubPath, terminal.Status, activityPath);
                    StampResult(workspace, accessService, logger, hubPath, activityPath, passed);
                },
                ex =>
                {
                    logger.LogWarning(ex,
                        "Validation activity stream faulted for {HubPath} (activity {ActivityPath})",
                        hubPath, activityPath);
                    StampResult(workspace, accessService, logger, hubPath, activityPath, passed: false);
                });
        hub.RegisterForDisposal(terminalSub);
    }

    /// <summary>
    /// Stamps the validation outcome onto the attempt's own MeshNode:
    /// <see cref="ExerciseAttemptStatus.Status"/>,
    /// <see cref="ExerciseAttemptStatus.LastValidationActivityPath"/> (when a
    /// validation activity was created) and
    /// <see cref="ExerciseAttemptStatus.PassedAt"/> on pass.
    /// </summary>
    private static void StampResult(
        IWorkspace workspace, AccessService? accessService, ILogger logger,
        string hubPath, string? activityPath, bool passed)
    {
        // The stamp targets the hub's OWN node; it runs from a stream callback
        // where the delivery scope has cleared, so re-establish SYSTEM for the
        // write (the compile watcher's kickoff shape).
        using var systemScope = AccessContextScope.AsSystem(accessService);
        workspace.GetMeshNodeStream().Update(curr =>
            {
                if (curr.Content is not ExerciseAttemptStatus status) return curr;
                return curr with
                {
                    Content = status with
                    {
                        Status = passed ? AttemptStatus.Passed : AttemptStatus.Failed,
                        LastValidationActivityPath = activityPath ?? status.LastValidationActivityPath,
                        PassedAt = passed ? DateTimeOffset.UtcNow : status.PassedAt
                    }
                };
            })
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex,
                    "Validation stamp failed for {HubPath}", hubPath));
    }
}
