using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Where an <see cref="InstanceRebootRequest"/> has got to — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>): a value this platform does not know is reported
/// by name, never folded into a default that means something.
/// </summary>
public static class InstanceRebootStatus
{
    /// <summary>Written, not yet picked up by the instance's executor.</summary>
    public const string Requested = "Requested";

    /// <summary>Steps 1–3 are running: sync every module source, land every module, pick the image.</summary>
    public const string Preparing = "Preparing";

    /// <summary>Step 4: the ONE roll/restart is requested (or about to be); the request completes when
    /// a process that booted after it has verified itself (step 5).</summary>
    public const string AwaitingRestart = "AwaitingRestart";

    /// <summary>Every step succeeded (or was skipped with a named reason) and every counted replica verified green.</summary>
    public const string Done = "Done";

    /// <summary>Red — <see cref="InstanceRebootRequest.Failure"/> names every failed step.</summary>
    public const string Failed = "Failed";

    /// <summary>The roll/restart request CRASHED (a hand-over that failed, a check that could not be
    /// made, a call that threw) — a transient fault, never a decided answer. NOT terminal: the plugin
    /// catalog's reconcile pass re-arms it (<see cref="InstanceReboot.RetryFaulted"/>) once its
    /// backoff is due, and the executor asks for the restart again.</summary>
    public const string Faulted = "Faulted";

    /// <summary>Whether <paramref name="status"/> is terminal. An unknown value is NOT terminal.</summary>
    public static bool IsTerminal(string? status) =>
        string.Equals(status, Done, StringComparison.Ordinal)
        || string.Equals(status, Failed, StringComparison.Ordinal);
}

/// <summary>The five steps of a reboot, in the order they run — open string constants.</summary>
public static class InstanceRebootSteps
{
    /// <summary>1. Import every GitSynced module source at its branch HEAD (the <c>git_hub_sync op=update</c> import).</summary>
    public const string Sync = "Sync";

    /// <summary>2. Resolve and land the newest COMPATIBLE published version of every installed module (the module-reload landing).</summary>
    public const string Modules = "Modules";

    /// <summary>3. Pick the newest platform image this instance's update policy admits and its availability gate clears.</summary>
    public const string Image = "Image";

    /// <summary>4. ONE roll (to the picked image) or restart (on the running one) that activates everything landed.</summary>
    public const string Restart = "Restart";

    /// <summary>5. After boot: the process's own health checks and smoke checks (a thread can start).</summary>
    public const string Verify = "Verify";

    /// <summary>Every step, in order.</summary>
    public static readonly ImmutableArray<string> All = ImmutableArray.Create(Sync, Modules, Image, Restart, Verify);
}

/// <summary>How one reboot step ended — open string constants.</summary>
public static class InstanceRebootStepOutcome
{
    /// <summary>Not started.</summary>
    public const string Pending = "Pending";

    /// <summary>Running now.</summary>
    public const string Running = "Running";

    /// <summary>Did what it was for.</summary>
    public const string Ok = "Ok";

    /// <summary>Did not apply here — the detail NAMES why (nothing to sync, no newer image, …). Never red.</summary>
    public const string Skipped = "Skipped";

    /// <summary>Red — the detail names why. A failed step before the restart does NOT stop the reboot
    /// (bringing the instance back is the point); it makes the request <see cref="InstanceRebootStatus.Failed"/>.</summary>
    public const string Failed = "Failed";

    /// <summary>Whether <paramref name="outcome"/> is red.</summary>
    public static bool IsRed(string? outcome) => string.Equals(outcome, Failed, StringComparison.Ordinal);
}

/// <summary>Who started a reboot — open string constants.</summary>
public static class InstanceRebootTrigger
{
    /// <summary>A person (MCP <c>reboot_instance</c>, the Reboot button): their call IS the signature —
    /// no second approver — and <see cref="InstanceRebootRequest.RequestedBy"/> records who.</summary>
    public const string Person = "Person";

    /// <summary>The instance's own watchdog, on the wedge predicate (<c>RebootWatchdogRules</c>).
    /// Needs no approval; rate-limited and alarmed.</summary>
    public const string Watchdog = "Watchdog";
}

/// <summary>
/// 🚨 "Reboot this instance" — bring it to a known-good, NEWEST state in one step
/// (<c>Doc/Architecture/InstanceReboot</c>, policy <c>instance-reboot</c>), as ONE durable request at
/// <c>Admin/_Reboot/{id}</c>. The instance's executor runs, in order, each step reported here with a
/// named reason when it is skipped or fails:
/// <list type="number">
/// <item><b>Sync</b> every GitSynced module source to its branch HEAD (the same import as
/// <c>git_hub_sync op=update</c>, with its per-module manifest-hash judgement and floor decline);</item>
/// <item><b>Modules</b>: land the newest COMPATIBLE published version of every installed module (the
/// module-reload landing, <c>Doc/Architecture/ModuleReload</c>);</item>
/// <item><b>Image</b>: pick the newest platform image this instance's update policy admits;</item>
/// <item><b>Restart</b>: ONE roll (or restart) that activates all of it;</item>
/// <item><b>Verify</b>: every process booted after it runs its health checks and its smoke checks
/// (a thread can start) and reports here — RED with the named failure otherwise.</item>
/// </list>
/// </summary>
public record InstanceRebootRequest
{
    /// <summary>The node type of a request.</summary>
    public const string NodeType = "InstanceReboot";

    /// <summary>Where requests live: platform infrastructure, in the Admin partition.</summary>
    public const string Namespace = "Admin/_Reboot";

    /// <summary>WHY — required; carried into the restart announcement and every log line.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Who asked: the person's user id (their call is the signature), or the watchdog's name.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When it was asked.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>Who started it — <see cref="InstanceRebootTrigger"/>.</summary>
    public string Trigger { get; init; } = InstanceRebootTrigger.Person;

    /// <summary>For a <see cref="InstanceRebootTrigger.Watchdog"/> reboot: the wedge evidence that fired it
    /// (one line), and its fingerprint (the same evidence never fires a second self-reboot).</summary>
    public string? WedgeEvidence { get; init; }

    /// <summary>The fingerprint of <see cref="WedgeEvidence"/>.</summary>
    public string? WedgeFingerprint { get; init; }

    /// <summary>Whether the instance was judged wedged when the reboot started: a watchdog reboot always is,
    /// a person may say so. A wedged reboot does not wait for a dependent-suite (combo) verdict on the image,
    /// and says so on the Image step.</summary>
    public bool Wedged { get; init; }

    /// <summary>Where the request has got to — <see cref="InstanceRebootStatus"/>. Executor-owned.</summary>
    public string Status { get; init; } = InstanceRebootStatus.Requested;

    /// <summary>One row per step, in order (<see cref="InstanceRebootSteps"/>). Executor-owned.</summary>
    public ImmutableList<InstanceRebootStep> Steps { get; init; } = ImmutableList<InstanceRebootStep>.Empty;

    /// <summary>The module rows the landing step produced (the module-reload item shape). Executor-owned.</summary>
    public ImmutableList<ModuleReloadItem> Modules { get; init; } = ImmutableList<ModuleReloadItem>.Empty;

    /// <summary>The platform version this instance ran when the reboot started. Executor-owned.</summary>
    public string? RunningImage { get; init; }

    /// <summary>The image the Image step picked, or null when nothing newer is admitted (restart on the running image).</summary>
    public string? TargetImage { get; init; }

    /// <summary>When the ONE roll/restart was requested. Stamped BEFORE it is asked for — a resumed
    /// executor that sees it never asks twice. Executor-owned.</summary>
    public DateTimeOffset? RestartRequestedAt { get; init; }

    /// <summary>What each process booted after the restart reports from its own checks, keyed by process.
    /// Each process writes only its own key.</summary>
    public ImmutableDictionary<string, InstanceRebootReplica> Replicas { get; init; } =
        ImmutableDictionary<string, InstanceRebootReplica>.Empty;

    /// <summary>Why the request is red, by name. Executor-owned. 🚨 Its <see cref="InstanceReboot.StalledMarker"/>
    /// is a MACHINE marker (<see cref="InstanceReboot.EndedAsStalled"/>) and stays English.</summary>
    public string? Failure { get; init; }

    /// <summary>The keyed form of <see cref="Failure"/> (MeshWeaver#7322), rendered in the VIEWER's language
    /// (<see cref="RebootText.Localize"/>) — used only while it reproduces <see cref="Failure"/>.</summary>
    public RebootText? FailureText { get; init; }

    /// <summary>When the request reached a terminal status. Executor-owned.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Which restart attempt this is: 0 for the first, incremented by each re-arm of a
    /// <see cref="InstanceRebootStatus.Faulted"/> request (<see cref="InstanceReboot.Rearm"/>).</summary>
    public int Attempt { get; init; }

    /// <summary>When the current attempt's restart request faulted — the start of its backoff
    /// (<see cref="InstanceReboot.RetryDueAt"/>). Executor-owned.</summary>
    public DateTimeOffset? FaultedAt { get; init; }

    /// <summary>
    /// 🚨 The request's DECLARED execution window (MeshWeaver#6625): the longest it may go without
    /// recording progress (<see cref="ProgressAt"/>) and still count as in progress. Stamped by
    /// <see cref="InstanceReboot.Request"/> from <see cref="InstanceRebootOptions.ExecutionWindow"/>;
    /// null on a request written before the field existed, which is judged by the reader's option.
    /// A request past its window is STALLED (<see cref="InstanceReboot.Stalled"/>): it no longer
    /// suppresses the watchdog's self-reboot, and the reconcile pass ends it
    /// (<see cref="InstanceReboot.FailStalled"/>).
    /// </summary>
    public TimeSpan? ExecutionWindow { get; init; }

    /// <summary>When the request last recorded progress: stamped on every executor write and on a
    /// re-arm. Read with every other instant the request carries (<see cref="InstanceReboot.LastProgressAt"/>).</summary>
    public DateTimeOffset? ProgressAt { get; init; }

    /// <summary>A reader that saw the request as stalled asks its OWNER to end it (the control-plane
    /// <c>RequestedX</c> shape): the request's own hub re-judges against its own state and either ends it
    /// or clears this field (<see cref="InstanceReboot.EndIfStalled"/>). Null when nothing is asked.</summary>
    public DateTimeOffset? RequestedEndIfStalledAt { get; init; }

    /// <summary>Every step, in order — the audit trail. Executor-owned.</summary>
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// The keyed form of each <see cref="Log"/> line (MeshWeaver#7322), aligned with <see cref="Log"/>
    /// from its END — both only grow at the end and are trimmed at the front — so a request written
    /// before keys existed simply has fewer of them. An empty entry is a line with no keyed form.
    /// Read through <see cref="InstanceReboot.LocalizedLog"/>.
    /// </summary>
    public ImmutableList<RebootText>? LogText { get; init; }
}

/// <summary>One step of a reboot and how it ended.</summary>
public record InstanceRebootStep
{
    /// <summary>The step — <see cref="InstanceRebootSteps"/>.</summary>
    public string Name { get; init; } = "";

    /// <summary>How it ended — <see cref="InstanceRebootStepOutcome"/>.</summary>
    public string Outcome { get; init; } = InstanceRebootStepOutcome.Pending;

    /// <summary>The step's own sentence: what it did, or why it was skipped or failed, by name.</summary>
    public string? Detail { get; init; }

    /// <summary>The keyed form of <see cref="Detail"/> (MeshWeaver#7322) — used only while it reproduces it.</summary>
    public RebootText? DetailText { get; init; }

    /// <summary>When the outcome was recorded.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>What ONE process booted after the restart reports from its own checks.</summary>
public record InstanceRebootReplica
{
    /// <summary>The process (silo identity, else machine and process id).</summary>
    public string Process { get; init; } = "";

    /// <summary>When the process started — only a process started after the restart counts.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the report was written.</summary>
    public DateTimeOffset ReportedAt { get; init; }

    /// <summary>The platform version this process runs.</summary>
    public string? Image { get; init; }

    /// <summary>Every check this process ran, in order.</summary>
    public ImmutableList<InstanceRebootCheck> Checks { get; init; } = ImmutableList<InstanceRebootCheck>.Empty;
}

/// <summary>How one verification check ended — open string constants.</summary>
public static class InstanceRebootCheckOutcome
{
    /// <summary>Measured and green.</summary>
    public const string Passed = "Passed";

    /// <summary>Measured and red — the detail names the failure.</summary>
    public const string Failed = "Failed";

    /// <summary>The instrument is not registered on this host: NOTHING was measured, and the detail says
    /// so by name. Never read as green, never counted as red — reported beside the verdict.</summary>
    public const string NotMeasured = "NotMeasured";
}

/// <summary>One verification check one process ran after the restart.</summary>
public record InstanceRebootCheck
{
    /// <summary>The check (a health check's name, or a smoke check's).</summary>
    public string Name { get; init; } = "";

    /// <summary>How it ended — <see cref="InstanceRebootCheckOutcome"/>.</summary>
    public string Outcome { get; init; } = InstanceRebootCheckOutcome.NotMeasured;

    /// <summary>The check's own sentence.</summary>
    public string? Detail { get; init; }

    /// <summary>The keyed form of <see cref="Detail"/> (MeshWeaver#7322) — used only while it reproduces it.
    /// A check that sets none (a module's own smoke check) renders its <see cref="Detail"/> as written.</summary>
    public RebootText? DetailText { get; init; }
}

/// <summary>What <see cref="InstanceReboot.Request"/> answers: the request's path, or why nothing was written.</summary>
/// <param name="Path">The request node, or null when refused.</param>
/// <param name="Refusal">Why the request was refused (nothing was written), or null.</param>
public sealed record InstanceRebootTicket(string? Path, string? Refusal)
{
    /// <summary>True when the request was written.</summary>
    public bool Accepted => Path is not null;
}

/// <summary>The verdict <see cref="InstanceReboot.EvaluateVerification"/> reaches over the counted replica reports.</summary>
/// <param name="Passed">Every counted replica passed every measured check, and at least one smoke check was measured.</param>
/// <param name="Detail">The sentence naming every failed check by replica, and every check nobody could measure.</param>
public sealed record InstanceRebootVerdict(bool Passed, string Detail)
{
    /// <summary>The keyed form of <see cref="Detail"/> (MeshWeaver#7322).</summary>
    public RebootText? DetailText { get; init; }
}

/// <summary>
/// The ONE way to ask an instance to reboot: <see cref="Request"/> writes the durable request, the
/// instance's executor (registered by the plugin catalog) does the rest. See
/// <see cref="InstanceRebootRequest"/> and <c>Doc/Architecture/InstanceReboot</c>.
/// </summary>
public static class InstanceReboot
{
    /// <summary>The longest caller-supplied request id.</summary>
    public const int MaxIdLength = 80;

    /// <summary>The name a smoke check that starts a thread reports under — the incident's failure shape
    /// (a <c>MissingMethodException</c> on every thread start). A verification that measured no check
    /// of the smoke kind is NOT green: see <see cref="EvaluateVerification"/>.</summary>
    public const string SmokePrefix = "smoke:";

    /// <summary>Why <paramref name="request"/> must not be written, or null. Pure.</summary>
    public static string? Validate(InstanceRebootRequest request) => ValidateText(request)?.ToEnglish();

    /// <summary>The keyed form of <see cref="Validate"/>'s answer, or null. Pure.</summary>
    /// <param name="request">The request to check.</param>
    public static RebootText? ValidateText(InstanceRebootRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return RebootText.Of("reboot.invalid.noReason");
        if (!string.Equals(request.Trigger, InstanceRebootTrigger.Person, StringComparison.Ordinal)
            && !string.Equals(request.Trigger, InstanceRebootTrigger.Watchdog, StringComparison.Ordinal))
            return RebootText.Of("reboot.invalid.trigger",
                ("trigger", ActivationRecycle.SanitizeReason(request.Trigger)),
                ("known", $"{InstanceRebootTrigger.Person}, {InstanceRebootTrigger.Watchdog}"));
        return null;
    }

    /// <summary>A check result whose detail is stored in English AND keyed (MeshWeaver#7322). Pure.</summary>
    /// <param name="name">The check's name.</param>
    /// <param name="outcome">How it ended — <see cref="InstanceRebootCheckOutcome"/>.</param>
    /// <param name="detail">Its keyed sentence.</param>
    public static InstanceRebootCheck CheckOf(string name, string outcome, RebootText detail) =>
        new() { Name = name, Outcome = outcome, Detail = detail.ToEnglish(), DetailText = detail };

    /// <summary>The time stamp a log line starts with — the universal sortable form every language reads.</summary>
    /// <param name="at">The instant.</param>
    public static string Stamp(DateTimeOffset at) => at.ToString("u", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="request"/>'s log with <paramref name="lines"/> appended — the English line in
    /// <see cref="InstanceRebootRequest.Log"/> and its keyed form in <see cref="InstanceRebootRequest.LogText"/>,
    /// both trimmed at the front to <paramref name="maxLines"/> so they stay aligned from the end. Pure.
    /// </summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="maxLines">How many lines to keep, or a non-positive value for all.</param>
    /// <param name="lines">The keyed lines (each already carries its time stamp).</param>
    public static InstanceRebootRequest AppendLog(InstanceRebootRequest request, int maxLines, params RebootText[] lines)
    {
        if (lines.Length == 0)
            return request;
        var log = request.Log.AddRange(lines.Select(l => l.ToEnglish()));
        var keyed = (request.LogText ?? ImmutableList<RebootText>.Empty).AddRange(lines);
        if (maxLines > 0 && log.Count > maxLines)
            log = log.RemoveRange(0, log.Count - maxLines);
        if (keyed.Count > log.Count)
            keyed = keyed.RemoveRange(0, keyed.Count - log.Count);
        return request with { Log = log, LogText = keyed };
    }

    /// <summary>
    /// The log as <paramref name="locale"/> reads it: each line's keyed form (aligned from the end)
    /// while it reproduces the English line, otherwise the line verbatim. Pure.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>One rendered line per <see cref="InstanceRebootRequest.Log"/> line, in order.</returns>
    public static ImmutableList<string> LocalizedLog(InstanceRebootRequest request, string? locale)
    {
        var keyed = request.LogText ?? ImmutableList<RebootText>.Empty;
        var offset = keyed.Count - request.Log.Count;
        return request.Log
            .Select((line, i) => i + offset >= 0 && i + offset < keyed.Count
                ? RebootText.Localize(line, keyed[i + offset], locale) ?? line
                : line)
            .ToImmutableList();
    }

    /// <summary>
    /// When a <see cref="InstanceRebootStatus.Faulted"/> request is due for its next restart attempt —
    /// pure: <c>FaultedAt + unit × 2^min(Attempt, ModuleReload.MaxBackoffDoublings)</c>, the same
    /// backoff a faulted module reload uses (<see cref="ModuleReload.RetryDueAt"/>). Null for any other
    /// status. No timer reads this: the plugin catalog's reconcile pass asks whether it is due.
    /// </summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="unit">The backoff unit — production passes the reconcile safety-net interval.</param>
    public static DateTimeOffset? RetryDueAt(InstanceRebootRequest request, TimeSpan unit)
    {
        if (!string.Equals(request.Status, InstanceRebootStatus.Faulted, StringComparison.Ordinal))
            return null;
        var since = request.FaultedAt ?? request.RequestedAt;
        var factor = 1L << Math.Clamp(request.Attempt, 0, ModuleReload.MaxBackoffDoublings);
        return since + TimeSpan.FromTicks(Math.Max(0, unit.Ticks) * factor);
    }

    /// <summary>
    /// The re-armed request — pure: the same node, the next attempt, back to
    /// <see cref="InstanceRebootStatus.AwaitingRestart"/> with the restart stamp, the replica reports
    /// and the fault cleared, so the executor asks for the ONE restart again. Steps 1–3 (sync, land,
    /// image) are NOT redone — they succeeded or were recorded red already; only the restart crashed.
    /// The audit log is kept and extended.
    /// </summary>
    /// <param name="request">The faulted request.</param>
    /// <param name="now">The re-arm instant, for the log line.</param>
    public static InstanceRebootRequest Rearm(InstanceRebootRequest request, DateTimeOffset now) => AppendLog(request with
    {
        Status = InstanceRebootStatus.AwaitingRestart,
        Attempt = request.Attempt + 1,
        RestartRequestedAt = null,
        Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty,
        Failure = null,
        FailureText = null,
        FaultedAt = null,
        CompletedAt = null,
        ProgressAt = now,
    }, 0, RebootText.Verbatim(Stamp(now))
        .Then(RebootText.Of("reboot.log.rearmed",
            ("attempt", (request.Attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))))
        .Then(RebootText.Faithful(request.Failure ?? "", request.FailureText), glue: "")
        .Then(RebootText.Verbatim("'"), glue: ""));

    /// <summary>
    /// 🚨 The retry half of <see cref="InstanceRebootStatus.Faulted"/>: lists the reboot requests,
    /// reads each one the listing does not show as finished from its OWN node stream, and re-arms
    /// (<see cref="Rearm"/>) every one whose <see cref="RetryDueAt"/> is due — a
    /// <c>GetMeshNodeStream(path).Update</c> as System, which also activates the request's own hub
    /// where the executor runs. Called by the plugin catalog's reconcile pass next to
    /// <see cref="ModuleReload.RetryFaulted"/>; no timer of its own. Emits the paths it re-armed,
    /// once; never faults, and a read it cannot make is logged at Warning, never skipped silently.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="unit">The backoff unit (<see cref="RetryDueAt"/>).</param>
    /// <param name="now">The pass's clock.</param>
    public static IObservable<ImmutableList<string>> RetryFaulted(IMessageHub hub, TimeSpan unit, DateTimeOffset now)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null || access is null)
            return Observable.Return(ImmutableList<string>.Empty);
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceReboot));
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(change => change.Items
                .Where(n => n.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions) is not { } listed
                            || !InstanceRebootStatus.IsTerminal(listed.Status))
                .Select(n => n.Path)
                .ToObservable()
                .Select(path => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
                    .Select(node => node.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions))
                    .SelectMany(current => current is not null && RetryDueAt(current, unit) is { } due && due <= now
                        ? access.RunAsSystem(() => hub.GetMeshNodeStream(path)
                                .Update<InstanceRebootRequest>(r =>
                                    RetryDueAt(r, unit) is { } stillDue && stillDue <= now ? Rearm(r, now) : r))
                            .Take(1)
                            .Select(_ => path)
                        : Observable.Empty<string>())
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex,
                            "[Reboot] {Path}: the faulted-reboot retry could not read or re-arm it this pass ({Reason}) — the next pass asks again",
                            path, ex.Message);
                        return Observable.Empty<string>();
                    }))
                .Concat()
                .ToList())
            .Select(paths => paths.ToImmutableList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[Reboot] the faulted-reboot retry could not list the requests this pass ({Reason}) — the next pass asks again",
                    ex.Message);
                return Observable.Return(ImmutableList<string>.Empty);
            });
    }

    /// <summary>
    /// When <paramref name="request"/> last moved — pure: the latest of every instant it carries
    /// (<see cref="InstanceRebootRequest.ProgressAt"/>, <see cref="InstanceRebootRequest.RequestedAt"/>,
    /// each step's <see cref="InstanceRebootStep.At"/>, the restart stamp, each replica's report). A
    /// <see cref="InstanceRebootStatus.Faulted"/> request is waiting for its scheduled re-arm, so its next
    /// move is due at <see cref="RetryDueAt"/>, and that instant counts too.
    /// </summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="retryUnit">The backoff unit of the re-arm (<see cref="RetryDueAt"/>).</param>
    public static DateTimeOffset LastProgressAt(InstanceRebootRequest request, TimeSpan retryUnit)
    {
        var instants = new[] { request.ProgressAt, request.RequestedAt, request.RestartRequestedAt, RetryDueAt(request, retryUnit) }
            .Concat(request.Steps.Select(s => s.At))
            .Concat(request.Replicas.Values.Select(r => (DateTimeOffset?)r.ReportedAt))
            .Where(at => at is not null)
            .Select(at => at!.Value)
            .ToImmutableList();
        return instants.IsEmpty ? DateTimeOffset.MinValue : instants.Max();
    }

    /// <summary>
    /// 🚨 Why <paramref name="request"/> has STALLED, or null while it is terminal or still progressing
    /// — pure (MeshWeaver#6625). A request is stalled when it is not terminal and has recorded no
    /// progress (<see cref="LastProgressAt"/>) within its DECLARED execution window
    /// (<see cref="InstanceRebootRequest.ExecutionWindow"/>; <paramref name="defaultWindow"/> for a
    /// request filed before the window was declared). Existence alone never makes a request "in
    /// progress": a stalled one does not hold the watchdog's self-reboot back, and the reconcile pass
    /// ends it (<see cref="FailStalled"/>). The sentence names the status, the last progress and where
    /// the request stopped.
    /// </summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="now">The reader's clock.</param>
    /// <param name="defaultWindow">The window for a request that declares none (<see cref="InstanceRebootOptions.ExecutionWindow"/>).</param>
    /// <param name="retryUnit">The backoff unit of the faulted-restart re-arm (<see cref="RetryDueAt"/>).</param>
    public static string? Stalled(InstanceRebootRequest request, DateTimeOffset now, TimeSpan defaultWindow, TimeSpan retryUnit)
        => StalledText(request, now, defaultWindow, retryUnit)?.ToEnglish();

    /// <summary>The keyed form of <see cref="Stalled"/>'s sentence, or null while the request is not stalled. Pure.</summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="now">The reader's clock.</param>
    /// <param name="defaultWindow">The window for a request that declares none.</param>
    /// <param name="retryUnit">The backoff unit of the faulted-restart re-arm.</param>
    public static RebootText? StalledText(InstanceRebootRequest request, DateTimeOffset now, TimeSpan defaultWindow, TimeSpan retryUnit)
    {
        if (InstanceRebootStatus.IsTerminal(request.Status))
            return null;
        var window = request.ExecutionWindow is { } declared && declared > TimeSpan.Zero ? declared : defaultWindow;
        var last = LastProgressAt(request, retryUnit);
        if (now - last <= window)
            return null;
        var at = request.Steps.LastOrDefault(s => s.Outcome == InstanceRebootStepOutcome.Running)
                 ?? request.Steps.LastOrDefault(s => s.Outcome != InstanceRebootStepOutcome.Pending);
        var where = request.Status switch
        {
            InstanceRebootStatus.AwaitingRestart when request.RestartRequestedAt is { } restart =>
                RebootText.Of("reboot.stalled.awaitingRestart", ("at", Stamp(restart))),
            InstanceRebootStatus.Faulted =>
                RebootText.Of("reboot.stalled.faultedOpen")
                    .Then(request.Failure is null
                        ? RebootText.Of("reboot.noReasonRecorded")
                        : RebootText.Faithful(request.Failure, request.FailureText), glue: "")
                    .Then(RebootText.Of("reboot.stalled.faultedClose"), glue: ""),
            _ => at is null
                ? RebootText.Of("reboot.stalled.noStep")
                : RebootText.Of("reboot.stalled.lastStep", ("step", at.Name), ("outcome", at.Outcome)),
        };
        return RebootText.Of("reboot.stalled",
                ("status", request.Status),
                ("last", Stamp(last)),
                ("minutes", window.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))
            .Then(where, glue: "; ");
    }

    /// <summary>
    /// 🚨 The owner-side END of a stalled request — pure. Applied INSIDE the request's own hub's update
    /// (<c>InstanceRebootExecutor</c>), against the owner's CURRENT state, so the stall is judged and the
    /// terminal transition is made in one serialized step: a replica report or an executor step that
    /// landed after a reader saw the request as stalled keeps it open. Stalled ⇒
    /// <see cref="InstanceRebootStatus.Failed"/> naming the stall; otherwise the request is unchanged
    /// except that <see cref="InstanceRebootRequest.RequestedEndIfStalledAt"/> is cleared (asked and
    /// answered). Never touches <see cref="InstanceRebootRequest.ProgressAt"/>: answering is not progress.
    /// </summary>
    /// <param name="current">The request as the OWNER holds it.</param>
    /// <param name="now">The owner's clock.</param>
    /// <param name="defaultWindow">The window for a request that declares none.</param>
    /// <param name="retryUnit">The backoff unit (<see cref="RetryDueAt"/>).</param>
    public static InstanceRebootRequest EndIfStalled(InstanceRebootRequest current, DateTimeOffset now, TimeSpan defaultWindow, TimeSpan retryUnit)
    {
        if (StalledText(current, now, defaultWindow, retryUnit) is not { } whyText)
            return current with { RequestedEndIfStalledAt = null };
        var why = whyText.ToEnglish();
        // 🚨 The English Failure is composed exactly as before: StalledMarker is the MACHINE marker
        // EndedAsStalled (and the watchdog through it) reads back, so it never passes through a catalog.
        // The keyed form (MeshWeaver#7322) is stored BESIDE it, for a viewer to read in their language.
        var stalled = RebootText.Of("reboot.failure.stalled").Then(whyText);
        return AppendLog(current with
        {
            Status = InstanceRebootStatus.Failed,
            Failure = current.Failure is null ? $"{StalledMarker}{why}" : $"{current.Failure}; {StalledMarker}{why}",
            FailureText = current.Failure is null
                ? stalled
                : RebootText.Faithful(current.Failure, current.FailureText).Then(stalled, glue: "; "),
            CompletedAt = now,
            RequestedEndIfStalledAt = null,
        }, 0, RebootText.Verbatim(Stamp(now)).Then(RebootText.Of("reboot.log.endedStalled")).Then(whyText));
    }

    /// <summary>The marker <see cref="EndIfStalled"/> writes into <see cref="InstanceRebootRequest.Failure"/>,
    /// before the stall's sentence — what <see cref="EndedAsStalled"/> reads back.</summary>
    public const string StalledMarker = "stalled: ";

    /// <summary>
    /// Why <paramref name="request"/> was ENDED as stalled — read back from the persisted
    /// <see cref="InstanceRebootRequest.Failure"/> marker (<see cref="StalledMarker"/>) — or null when it
    /// was not (still open, Done, or Failed for another reason). Pure. Whoever ended it (the reconcile
    /// pass's question, another process's watchdog), any reader can still name it.
    /// </summary>
    public static string? EndedAsStalled(InstanceRebootRequest request)
    {
        if (!string.Equals(request.Status, InstanceRebootStatus.Failed, StringComparison.Ordinal) || request.Failure is not { } failure)
            return null;
        var at = failure.IndexOf(StalledMarker, StringComparison.Ordinal);
        return at < 0 ? null : failure[(at + StalledMarker.Length)..];
    }

    /// <summary>
    /// Asks the owner of the request at <paramref name="path"/> to end it if it is stalled: writes
    /// <see cref="InstanceRebootRequest.RequestedEndIfStalledAt"/> (a <c>RequestedX</c> field — the
    /// control-plane shape), which the request's own hub answers with <see cref="EndIfStalled"/> against
    /// its own state. Writing the request is harmless at any moment: the decision is never made on this
    /// caller's mirror. The write also activates the owner. Cold; emits once.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="path">The request.</param>
    /// <param name="now">When it was asked.</param>
    public static IObservable<MeshNode> RequestEndIfStalled(IMessageHub hub, string path, DateTimeOffset now)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path)
                .Update<InstanceRebootRequest>(r => InstanceRebootStatus.IsTerminal(r.Status) ? r : r with { RequestedEndIfStalledAt = now }))
            .Take(1);
    }

    /// <summary>
    /// 🚨 The END of a stalled request (MeshWeaver#6625): lists the reboot requests, reads each one the
    /// listing does not show as finished from its OWN node stream, and for every one that looks
    /// <see cref="Stalled"/> ASKS its owner to end it (<see cref="RequestEndIfStalled"/>). The owner
    /// re-judges against its own current state and makes the transition itself (<see cref="EndIfStalled"/>),
    /// so a request that moved meanwhile stays open. Called by the plugin catalog's reconcile pass right
    /// after <see cref="RetryFaulted"/> (so a faulted request due for its re-arm is re-armed, never
    /// ended); no timer of its own. Emits the paths it asked about, once; never faults, and a read it
    /// cannot make is logged at Warning.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="defaultWindow">The window for a request that declares none.</param>
    /// <param name="retryUnit">The backoff unit (<see cref="RetryDueAt"/>).</param>
    /// <param name="now">The pass's clock.</param>
    public static IObservable<ImmutableList<string>> FailStalled(IMessageHub hub, TimeSpan defaultWindow, TimeSpan retryUnit, DateTimeOffset now)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null || access is null)
            return Observable.Return(ImmutableList<string>.Empty);
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceReboot));
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(change => change.Items
                .Where(n => n.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions) is not { } listed
                            || !InstanceRebootStatus.IsTerminal(listed.Status))
                .Select(n => n.Path)
                .ToObservable()
                // FirstAsync, never Take(1): a stream that completes empty must reach the Catch below.
                .Select(path => access.RunAsSystem(() => hub.GetMeshNodeStream(path).FirstAsync().Timeout(ActivationRecycle.ReadBudget))
                    .Select(node => node.ContentAs<InstanceRebootRequest>(hub.JsonSerializerOptions))
                    .SelectMany(current => current is not null && Stalled(current, now, defaultWindow, retryUnit) is { } why
                        ? RequestEndIfStalled(hub, path, now)
                            .Do(_ => logger?.LogWarning(
                                "[Reboot] {Path}: looks stalled ({Why}) — its owner is asked to end it as Failed if it still is",
                                path, why))
                            .Select(_ => path)
                        : Observable.Empty<string>())
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex,
                            "[Reboot] {Path}: whether it stalled could not be read or asked this pass ({Reason}) — the next pass asks again",
                            path, ex.Message);
                        return Observable.Empty<string>();
                    }))
                .Concat()
                .ToList())
            .Select(paths => paths.ToImmutableList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[Reboot] the stalled-reboot pass could not list the requests ({Reason}) — the next pass asks again",
                    ex.Message);
                return Observable.Return(ImmutableList<string>.Empty);
            });
    }

    /// <summary>The step rows a fresh request starts with: every step <see cref="InstanceRebootStepOutcome.Pending"/>. Pure.</summary>
    public static ImmutableList<InstanceRebootStep> FreshSteps() =>
        InstanceRebootSteps.All.Select(name => new InstanceRebootStep { Name = name }).ToImmutableList();

    /// <summary>
    /// Writes <paramref name="request"/> as System at <c>Admin/_Reboot/{id}</c>, which is what makes
    /// the instance's executor act on it. The CALLER authorises the requester (a global admin, or the
    /// instance's own watchdog) — this writer is the trust boundary the executor checks (it acts only
    /// on requests written by System). Refuses, writing nothing, when <see cref="Validate"/> does or
    /// when this mesh registers no executor. Cold; emits once.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">Why, who, and what started it.</param>
    /// <param name="id">The request's id — a dedupe key; letters, digits and '-' only.</param>
    public static IObservable<InstanceRebootTicket> Request(IMessageHub hub, InstanceRebootRequest request, string? id = null)
    {
        if (Validate(request) is { } refusal)
            return Observable.Return(new InstanceRebootTicket(null, refusal));
        if (id is { } given && (given.Length == 0 || given.Length > MaxIdLength || ActivationRecycle.Segment(given) != given))
            return Observable.Return(new InstanceRebootTicket(null,
                $"the request id '{ActivationRecycle.SanitizeReason(given)}' is refused: use letters, digits and '-' only, at most {MaxIdLength} characters"));
        if (hub.ServiceProvider.FindStaticNode(InstanceRebootRequest.NodeType) is null)
            return Observable.Return(new InstanceRebootTicket(null,
                "this mesh registers no reboot executor (the plugin catalog is not installed) — nothing was written"));

        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var options = hub.ServiceProvider.GetService<InstanceRebootOptions>() ?? new InstanceRebootOptions();
        var requestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt;
        var nodeId = id ?? ActivationRecycle.Segment($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..30]);
        var path = $"{InstanceRebootRequest.Namespace}/{nodeId}";
        var node = new MeshNode(nodeId, InstanceRebootRequest.Namespace)
        {
            Name = request.Trigger == InstanceRebootTrigger.Watchdog ? "Reboot (watchdog)" : "Reboot",
            NodeType = InstanceRebootRequest.NodeType,
            Content = request with
            {
                Reason = ActivationRecycle.SanitizeReason(request.Reason),
                WedgeEvidence = request.WedgeEvidence is null ? null : ActivationRecycle.SanitizeReason(request.WedgeEvidence),
                RequestedAt = requestedAt,
                Wedged = request.Wedged || request.Trigger == InstanceRebootTrigger.Watchdog,
                Status = InstanceRebootStatus.Requested,
                Steps = FreshSteps(),
                // 🚨 Declared on the record (MeshWeaver#6625): every reader judges "still in progress"
                // against the window the request was filed with.
                ExecutionWindow = request.ExecutionWindow is { } declared && declared > TimeSpan.Zero ? declared : options.ExecutionWindow,
                ProgressAt = requestedAt,
            },
        };
        // The id is a dedupe key; existence is a LISTING of the request namespace (the CQRS-sanctioned
        // shape for "may not exist yet"), and only ever matters for a caller-supplied id.
        IObservable<bool> Exists() => id is null
            ? Observable.Return(false)
            : access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}"))
                    .Take(1)
                    .Timeout(ActivationRecycle.ReadBudget))
                .Select(change => change.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));

        var ticket = new InstanceRebootTicket(path, null);
        return Exists()
            .SelectMany(there => there
                ? Observable.Return(ticket)
                : access.RunAsSystem(() => mesh.CreateNode(node).Take(1))
                    // One read of the request it just wrote ACTIVATES the request's own hub, where the
                    // executor runs — a reboot never depends on a change feed reaching some process.
                    .SelectMany(_ => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1)
                            .Timeout(ActivationRecycle.ReadBudget))
                        .Select(_ => ticket)
                        .Catch((Exception _) => Observable.Return(ticket))))
            .Take(1);
    }

    /// <summary>
    /// The verification verdict over the replica reports present — pure. Counts only reports from
    /// processes <paramref name="isLive"/> says are members and that STARTED after the restart was
    /// requested (an old pod on its way out is not evidence). Answers null while no counted report exists.
    ///
    /// <para>🚨 A verification step that cannot fail is not a verification step: a counted replica that
    /// measured NO smoke check (none registered on its host) makes the verdict RED by name — the
    /// incident's failure shape (every thread start threw) would otherwise pass unmeasured. A health
    /// check that is not registered is <see cref="InstanceRebootCheckOutcome.NotMeasured"/>: named in the
    /// detail, never green, never red.</para>
    /// </summary>
    public static InstanceRebootVerdict? EvaluateVerification(InstanceRebootRequest request, Func<string, bool> isLive)
    {
        if (request.RestartRequestedAt is not { } restart)
            return null;
        var counted = request.Replicas.Values
            .Where(r => isLive(r.Process) && r.StartedAt > restart)
            .OrderBy(r => r.Process, StringComparer.Ordinal)
            .ToImmutableList();
        if (counted.IsEmpty)
            return null;

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var failures = (from r in counted
                        from c in r.Checks
                        where string.Equals(c.Outcome, InstanceRebootCheckOutcome.Failed, StringComparison.Ordinal)
                        select RebootText.Of("reboot.verify.checkFailed", ("process", r.Process), ("check", c.Name))
                            .Then(c.Detail is null ? RebootText.Of("reboot.word.failed") : RebootText.Faithful(c.Detail, c.DetailText)))
            .Concat(counted
                .Where(r => !r.Checks.Any(c => c.Name.StartsWith(SmokePrefix, StringComparison.Ordinal)
                                              && c.Outcome != InstanceRebootCheckOutcome.NotMeasured))
                .Select(r => RebootText.Of("reboot.verify.noSmoke", ("process", r.Process))))
            .ToImmutableList();
        var unmeasured = (from r in counted
                          from c in r.Checks
                          where string.Equals(c.Outcome, InstanceRebootCheckOutcome.NotMeasured, StringComparison.Ordinal)
                          select RebootText.Of("reboot.verify.notMeasured", ("process", r.Process), ("check", c.Name))
                              .Then(c.Detail is null ? RebootText.Of("reboot.word.notRegistered") : RebootText.Faithful(c.Detail, c.DetailText), glue: "")
                              .Then(RebootText.Verbatim(")"), glue: ""))
            .ToImmutableList();
        RebootText WithNote(RebootText detail) => unmeasured.IsEmpty
            ? detail
            : detail.Then(RebootText.Join("; ", unmeasured), glue: " [").Then(RebootText.Verbatim("]"), glue: "");
        var text = failures.IsEmpty
            ? WithNote(RebootText.Of("reboot.verify.green", ("count", counted.Count.ToString(invariant)))
                .Then(RebootText.Join(", ", counted.Select(r => RebootText.Of("reboot.verify.greenReplica",
                    ("process", r.Process), ("image", r.Image ?? "?"),
                    ("passed", r.Checks.Count(c => c.Outcome == InstanceRebootCheckOutcome.Passed).ToString(invariant)))))))
            : WithNote(RebootText.Join("; ", failures));
        return new InstanceRebootVerdict(failures.IsEmpty, text.ToEnglish()) { DetailText = text };
    }

    /// <summary>The red steps of <paramref name="request"/>, each by name with its detail — empty when none is red. Pure.</summary>
    public static ImmutableList<string> RedSteps(InstanceRebootRequest request) =>
        RedStepsText(request).Select(s => s.ToEnglish()).ToImmutableList();

    /// <summary>The keyed form of <see cref="RedSteps"/> (MeshWeaver#7322). Pure.</summary>
    /// <param name="request">The request.</param>
    public static ImmutableList<RebootText> RedStepsText(InstanceRebootRequest request) =>
        request.Steps
            .Where(s => InstanceRebootStepOutcome.IsRed(s.Outcome))
            .Select(s => RebootText.Of("reboot.redStep", ("step", s.Name))
                .Then(s.Detail is null ? RebootText.Of("reboot.word.failed") : RebootText.Faithful(s.Detail, s.DetailText)))
            .ToImmutableList();

    /// <summary>Sets the outcome of step <paramref name="name"/> (adding the row when a request predates it). Pure.</summary>
    public static ImmutableList<InstanceRebootStep> WithStep(
        ImmutableList<InstanceRebootStep> steps, string name, string outcome, string? detail, DateTimeOffset at)
        => WithStep(steps, name, outcome, detail is null ? null : RebootText.Verbatim(detail), at);

    /// <summary>Sets the outcome of step <paramref name="name"/> with a keyed detail (MeshWeaver#7322). Pure.</summary>
    /// <param name="steps">The step rows.</param>
    /// <param name="name">The step.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="detail">Its keyed sentence, or null.</param>
    /// <param name="at">When.</param>
    public static ImmutableList<InstanceRebootStep> WithStep(
        ImmutableList<InstanceRebootStep> steps, string name, string outcome, RebootText? detail, DateTimeOffset at)
    {
        var row = new InstanceRebootStep { Name = name, Outcome = outcome, Detail = detail?.ToEnglish(), DetailText = detail, At = at };
        var index = steps.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        return index < 0 ? steps.Add(row) : steps.SetItem(index, row);
    }
}

/// <summary>
/// One verification check a process runs after it booted from a reboot (step 5). Registered in DI;
/// the platform registers the health readings (<c>nodetype_bake</c>, <c>pending_module_activation</c>,
/// <c>content-types</c>), and a module registers its smoke checks (MeshWeaver.Plugins' AI module: a
/// thread can start) under a name that starts with <see cref="InstanceReboot.SmokePrefix"/>. The plugin
/// catalog's <c>InstanceRebootAgent</c> runs every registration.
/// </summary>
public interface IInstanceRebootCheck
{
    /// <summary>The check's name, as reported.</summary>
    string Name { get; }

    /// <summary>Runs the check in THIS process. Cold; emits ONE result; never faults (a fault is a <see cref="InstanceRebootCheckOutcome.Failed"/> result naming it).</summary>
    /// <param name="meshHub">This process's mesh hub.</param>
    /// <param name="request">The reboot being verified — its <see cref="InstanceRebootRequest.RestartRequestedAt"/>
    /// is what "after the reboot" means.</param>
    IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request);

    /// <summary>How long this check may take, or null for the reboot's default check budget. A check that
    /// waits for something to happen AFTER the restart (a singleton's next pass) states its own.</summary>
    TimeSpan? Budget => null;
}
