using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Why the request is red, by name. Executor-owned.</summary>
    public string? Failure { get; init; }

    /// <summary>When the request reached a terminal status. Executor-owned.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Every step, in order — the audit trail. Executor-owned.</summary>
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;
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
public sealed record InstanceRebootVerdict(bool Passed, string Detail);

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
    public static string? Validate(InstanceRebootRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return "a reboot needs a reason — it is carried into the restart announcement and every log line";
        if (!string.Equals(request.Trigger, InstanceRebootTrigger.Person, StringComparison.Ordinal)
            && !string.Equals(request.Trigger, InstanceRebootTrigger.Watchdog, StringComparison.Ordinal))
            return $"'{ActivationRecycle.SanitizeReason(request.Trigger)}' is not a reboot trigger this platform knows "
                   + $"({InstanceRebootTrigger.Person}, {InstanceRebootTrigger.Watchdog})";
        return null;
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
                RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt,
                Wedged = request.Wedged || request.Trigger == InstanceRebootTrigger.Watchdog,
                Status = InstanceRebootStatus.Requested,
                Steps = FreshSteps(),
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

        var failures = (from r in counted
                        from c in r.Checks
                        where string.Equals(c.Outcome, InstanceRebootCheckOutcome.Failed, StringComparison.Ordinal)
                        select $"{r.Process}: {c.Name} — {c.Detail ?? "failed"}")
            .Concat(counted
                .Where(r => !r.Checks.Any(c => c.Name.StartsWith(SmokePrefix, StringComparison.Ordinal)
                                              && c.Outcome != InstanceRebootCheckOutcome.NotMeasured))
                .Select(r => $"{r.Process}: no smoke check was measured (none registered on this host) — "
                             + "whether a thread can start is NOT established"))
            .ToImmutableList();
        var unmeasured = (from r in counted
                          from c in r.Checks
                          where string.Equals(c.Outcome, InstanceRebootCheckOutcome.NotMeasured, StringComparison.Ordinal)
                          select $"{r.Process}: {c.Name} not measured ({c.Detail ?? "not registered"})")
            .ToImmutableList();
        var unmeasuredNote = unmeasured.IsEmpty ? "" : " [" + string.Join("; ", unmeasured) + "]";
        return failures.IsEmpty
            ? new InstanceRebootVerdict(true,
                $"{counted.Count} replica(s) booted after the restart verified green: "
                + string.Join(", ", counted.Select(r => $"{r.Process} on {r.Image ?? "?"} ({r.Checks.Count(c => c.Outcome == InstanceRebootCheckOutcome.Passed)} check(s) passed)"))
                + unmeasuredNote)
            : new InstanceRebootVerdict(false, string.Join("; ", failures) + unmeasuredNote);
    }

    /// <summary>The red steps of <paramref name="request"/>, each by name with its detail — empty when none is red. Pure.</summary>
    public static ImmutableList<string> RedSteps(InstanceRebootRequest request) =>
        request.Steps
            .Where(s => InstanceRebootStepOutcome.IsRed(s.Outcome))
            .Select(s => $"{s.Name}: {s.Detail ?? "failed"}")
            .ToImmutableList();

    /// <summary>Sets the outcome of step <paramref name="name"/> (adding the row when a request predates it). Pure.</summary>
    public static ImmutableList<InstanceRebootStep> WithStep(
        ImmutableList<InstanceRebootStep> steps, string name, string outcome, string? detail, DateTimeOffset at)
    {
        var row = new InstanceRebootStep { Name = name, Outcome = outcome, Detail = detail, At = at };
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
    IObservable<InstanceRebootCheck> Run(IMessageHub meshHub);
}
