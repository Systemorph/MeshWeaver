using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Where a <see cref="ModuleReloadRequest"/> has got to — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>): a value this platform does not know reads as
/// unknown and is reported by name, never folded into a default that means something.
/// </summary>
public static class ModuleReloadStatus
{
    /// <summary>Written, not yet picked up by the instance's executor.</summary>
    public const string Requested = "Requested";

    /// <summary>The executor is resolving the newest compatible published version and landing it.</summary>
    public const string Landing = "Landing";

    /// <summary>Landed; the running processes are swapping the new generation in (live activation).</summary>
    public const string Activating = "Activating";

    /// <summary>Landed; exactly one restart was requested through the self-update restart path and
    /// the request completes when a process booted after it reports what it loaded.</summary>
    public const string AwaitingRestart = "AwaitingRestart";

    /// <summary>Every module the request names is loaded at the version it resolved, on every
    /// replica that reported.</summary>
    public const string Done = "Done";

    /// <summary>Red, with <see cref="ModuleReloadRequest.Failure"/> naming why.</summary>
    public const string Failed = "Failed";

    /// <summary>Whether <paramref name="status"/> is terminal. An unknown value is NOT terminal —
    /// it is reported, never silently treated as finished.</summary>
    public static bool IsTerminal(string? status) =>
        string.Equals(status, Done, StringComparison.Ordinal)
        || string.Equals(status, Failed, StringComparison.Ordinal);
}

/// <summary>How a reload made its new generation run — open string constants.</summary>
public static class ModuleReloadActivation
{
    /// <summary>Swapped in the running process(es) through the live module loader.</summary>
    public const string Live = "Live";

    /// <summary>Activated by exactly one automatic restart through the self-update restart path.</summary>
    public const string Restart = "Restart";

    /// <summary>Nothing to activate: the resolved version was already the one loaded.</summary>
    public const string NotNeeded = "NotNeeded";
}

/// <summary>
/// "Reload module M on this instance" — or every installed module when <see cref="Module"/> is
/// blank — as ONE durable request at <c>Admin/_ModuleReload/{id}</c>
/// (<c>Doc/Architecture/ModuleReload</c>).
///
/// <para>The requester writes the request (always through <see cref="ModuleReload.Request"/>,
/// which writes it as System after the caller was authorised); the instance's executor owns every
/// other field: it resolves the newest COMPATIBLE published version (declared platform floor ≤ the
/// running platform — never a seal, never a green platform build), lands it through the existing
/// landing path, activates it (live when the process can swap it, otherwise exactly one automatic
/// restart through the self-update restart path — no approval, no confirmation) and reports, here,
/// which version was found, what landed, how it was activated and what every replica loaded.</para>
/// </summary>
public record ModuleReloadRequest
{
    /// <summary>The node type of a request.</summary>
    public const string NodeType = "ModuleReload";

    /// <summary>Where requests live: platform infrastructure, in the Admin partition.</summary>
    public const string Namespace = "Admin/_ModuleReload";

    /// <summary>The module's entry-assembly name (<c>MeshWeaver.AI</c>) or its package id
    /// (<c>AI</c>); blank reloads every installed module.</summary>
    public string? Module { get; init; }

    /// <summary>WHY — carried into the restart announcement and every log line. Required.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Who or what asked (a user id, an agent, a watcher's name).</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When it was asked.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>Where the request has got to — <see cref="ModuleReloadStatus"/>. Executor-owned.</summary>
    public string Status { get; init; } = ModuleReloadStatus.Requested;

    /// <summary>One row per module the request covers. Executor-owned.</summary>
    public ImmutableList<ModuleReloadItem> Items { get; init; } = ImmutableList<ModuleReloadItem>.Empty;

    /// <summary>How the new generation was made to run — <see cref="ModuleReloadActivation"/>.
    /// Executor-owned.</summary>
    public string? Activation { get; init; }

    /// <summary>The activation's own sentence: the restart lane's verdict, or why a live swap could
    /// not be taken. Executor-owned.</summary>
    public string? ActivationDetail { get; init; }

    /// <summary>When the live swap was requested of every process. Executor-owned.</summary>
    public DateTimeOffset? LiveSwapRequestedAt { get; init; }

    /// <summary>When the ONE restart was requested. Set before the restart is issued, and its
    /// presence is what makes a resumed executor never issue a second. Executor-owned.</summary>
    public DateTimeOffset? RestartRequestedAt { get; init; }

    /// <summary>What each process reports it LOADED, keyed by process identity. Each process writes
    /// only its own key, so concurrent reports merge rather than clobber.</summary>
    public ImmutableDictionary<string, ModuleReloadReplica> Replicas { get; init; } =
        ImmutableDictionary<string, ModuleReloadReplica>.Empty;

    /// <summary>Why the request is red, by name. Executor-owned.</summary>
    public string? Failure { get; init; }

    /// <summary>When the request reached a terminal status. Executor-owned.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Every step, in order — the audit trail. Executor-owned.</summary>
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;
}

/// <summary>What the reload decided and did for ONE module.</summary>
public record ModuleReloadItem
{
    /// <summary>The module's entry-assembly name.</summary>
    public string Module { get; init; } = "";

    /// <summary>The package that declares it (<c>Plugins/{Package}</c>).</summary>
    public string? Package { get; init; }

    /// <summary>The version the requesting process was running when the reload started (null when
    /// it ran none, or the copy it ran states no version).</summary>
    public string? RunningVersion { get; init; }

    /// <summary>The newest version a configured registry publishes for it.</summary>
    public string? FoundVersion { get; init; }

    /// <summary>That version's declared platform floor (<c>minMeshVersion</c>), as published.</summary>
    public string? FoundFloor { get; init; }

    /// <summary>The registry that answered.</summary>
    public string? Registry { get; init; }

    /// <summary>The version the instance's activation record names after the landing step — what a
    /// process booting or swapping now loads.</summary>
    public string? TargetVersion { get; init; }

    /// <summary>Whether this reload wrote new bytes (false when the target was already landed).</summary>
    public bool Landed { get; init; }

    /// <summary>The update decision's own sentence (landed, already landed, held below floor, …).</summary>
    public string? Decision { get; init; }

    /// <summary>Why this module could not be reloaded, by name; null when it could.</summary>
    public string? Failure { get; init; }
}

/// <summary>What ONE process reports it loaded for a reload — written by that process alone.</summary>
public record ModuleReloadReplica
{
    /// <summary>The process (silo identity, else machine and process id).</summary>
    public string Process { get; init; } = "";

    /// <summary>When the process started — a report from a process older than the restart is not
    /// evidence of what the restart loaded.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the report was written.</summary>
    public DateTimeOffset ReportedAt { get; init; }

    /// <summary>Module → the version this process has loaded, or a sentence naming why none can be
    /// stated (not loaded, image copy, a generation the record no longer names).</summary>
    public ImmutableDictionary<string, string> Loaded { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The live swap's failure on this process, when it attempted one and it failed.</summary>
    public string? SwapFailure { get; init; }
}

/// <summary>What <see cref="ModuleReload.Request"/> answers: the request's path, or why nothing was written.</summary>
/// <param name="Path">The request node, or null when refused.</param>
/// <param name="Refusal">Why the request was refused (nothing was written), or null.</param>
public sealed record ModuleReloadTicket(string? Path, string? Refusal)
{
    /// <summary>True when the request was written.</summary>
    public bool Accepted => Path is not null;
}

/// <summary>
/// The ONE way to ask an instance to reload a module: <see cref="Request"/> writes the durable
/// request, the instance's executor (registered by the plugin catalog) does the rest. See
/// <see cref="ModuleReloadRequest"/> and <c>Doc/Architecture/ModuleReload</c>.
/// </summary>
public static class ModuleReload
{
    /// <summary>The longest caller-supplied request id; longer ids are refused rather than cut.</summary>
    public const int MaxIdLength = 80;

    /// <summary>
    /// Why <paramref name="request"/> must not be written, or null. Pure. A reload needs a reason
    /// (#3510: an unstated reason costs every later reader), and a module name that is not blank
    /// must not carry path or whitespace characters.
    /// </summary>
    public static string? Validate(ModuleReloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return "a reload needs a reason — it is carried into the restart announcement and every log line";
        if (request.Module is { } module && module.Trim().Length > 0
            && module.Trim().Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '@'))
            return $"'{ActivationRecycle.SanitizeReason(module)}' is not a module or package name";
        return null;
    }

    /// <summary>
    /// Writes <paramref name="request"/> as System at <c>Admin/_ModuleReload/{id}</c>, which is
    /// what makes the instance's executor act on it. The CALLER is responsible for having
    /// authorised the requester (a global admin, or the platform's own watcher) — this writer is
    /// the trust boundary the executor checks (it acts only on requests written by System).
    /// Refuses, writing nothing, when <see cref="Validate"/> does or when this mesh registers no
    /// executor (<see cref="ModuleReloadRequest.NodeType"/> is not a known type). Cold; emits once.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">What to reload and why.</param>
    /// <param name="id">The request's id — a dedupe key: an existing request at that id is the
    /// answer and nothing is written again. Letters, digits and '-' only.</param>
    public static IObservable<ModuleReloadTicket> Request(IMessageHub hub, ModuleReloadRequest request, string? id = null)
    {
        if (Validate(request) is { } refusal)
            return Observable.Return(new ModuleReloadTicket(null, refusal));
        if (id is { } given && (given.Length == 0 || given.Length > MaxIdLength || ActivationRecycle.Segment(given) != given))
            return Observable.Return(new ModuleReloadTicket(null,
                $"the request id '{ActivationRecycle.SanitizeReason(given)}' is refused: use letters, digits and '-' only, at most {MaxIdLength} characters"));
        if (hub.ServiceProvider.FindStaticNode(ModuleReloadRequest.NodeType) is null)
            return Observable.Return(new ModuleReloadTicket(null,
                "this mesh registers no module reload executor (the plugin catalog is not installed) — nothing was written"));

        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var nodeId = id ?? ActivationRecycle.Segment($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..30]);
        var path = $"{ModuleReloadRequest.Namespace}/{nodeId}";
        var module = string.IsNullOrWhiteSpace(request.Module) ? null : request.Module.Trim();
        var node = new MeshNode(nodeId, ModuleReloadRequest.Namespace)
        {
            Name = module is null ? "Reload all modules" : $"Reload {module}",
            NodeType = ModuleReloadRequest.NodeType,
            Content = request with
            {
                Module = module,
                Reason = ActivationRecycle.SanitizeReason(request.Reason),
                RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt,
                Status = ModuleReloadStatus.Requested,
            },
        };
        // The id is the dedupe key. The existence check is a listing of the request namespace — the
        // CQRS-sanctioned shape for "may not exist yet" (a point read of an absent node opens the
        // storm breaker) — and only ever matters for a caller-supplied id.
        IObservable<bool> Exists() => id is null
            ? Observable.Return(false)
            : access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"namespace:{ModuleReloadRequest.Namespace} scope:children nodeType:{ModuleReloadRequest.NodeType}"))
                    .Take(1)
                    .Timeout(ActivationRecycle.ReadBudget))
                .Select(change => change.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));

        var ticket = new ModuleReloadTicket(path, null);
        return Exists()
            .SelectMany(there => there
                ? Observable.Return(ticket)
                : access.RunAsSystem(() => mesh.CreateNode(node).Take(1))
                    // One read of the request it just wrote: that is what ACTIVATES the request's own
                    // hub, where the executor runs — so a reload never depends on a change feed
                    // reaching some process. The executor also resumes on any later read.
                    .SelectMany(_ => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1)
                            .Timeout(ActivationRecycle.ReadBudget))
                        .Select(_ => ticket)
                        .Catch((Exception _) => Observable.Return(ticket))))
            .Take(1);
    }

    /// <summary>
    /// The verdict over the replica reports present — pure, so the rule is pinned without a mesh.
    /// Considers only reports from processes <paramref name="isLive"/> says are still members and,
    /// after a restart, only processes STARTED after it was requested (an old pod reporting the old
    /// version on its way out is not evidence). Answers null while no counted report exists yet.
    /// <para>🚨 <b>Loaded is a verdict over the WHOLE roster, never over the first report</b> (#6124
    /// review): when <paramref name="aliveMembers"/> names the running processes, a Loaded verdict
    /// waits until every one of them has a COUNTED report — so a second pod that is still booting,
    /// or whose live swap is still running (and may yet fail and need the restart fallback), keeps
    /// the request open, and an old pod still alive after a restart keeps it open until it is gone.
    /// A swap failure or a version mismatch on a counted report is decisive at once. Without a
    /// roster (<c>null</c>: no cluster) the counted reports are all there is.</para>
    /// </summary>
    /// <param name="request">The request as it stands.</param>
    /// <param name="isLive">Whether the process that wrote a report is still a member of the cluster.</param>
    /// <param name="aliveMembers">Every process the cluster records as running, or null when unknown.</param>
    public static ModuleReloadVerdict? Evaluate(ModuleReloadRequest request, Func<string, bool> isLive,
        IReadOnlyCollection<string>? aliveMembers = null)
    {
        var targets = request.Items
            .Where(i => i.Failure is null && !string.IsNullOrWhiteSpace(i.TargetVersion))
            .ToImmutableList();
        var counted = request.Replicas.Values
            .Where(r => isLive(r.Process))
            .Where(r => request.RestartRequestedAt is not { } restart || r.StartedAt > restart)
            .Where(r => request.LiveSwapRequestedAt is not { } swap || r.ReportedAt >= swap)
            .OrderBy(r => r.Process, StringComparer.Ordinal)
            .ToImmutableList();
        if (counted.IsEmpty)
            return null;

        var swapFailures = counted.Where(r => r.SwapFailure is not null).ToImmutableList();
        if (!swapFailures.IsEmpty)
            return new ModuleReloadVerdict(false, true,
                string.Join("; ", swapFailures.Select(r => $"the live swap failed on {r.Process}: {r.SwapFailure}")));

        var unreported = aliveMembers is null
            ? ImmutableList<string>.Empty
            : aliveMembers.Where(m => !counted.Any(r => string.Equals(r.Process, m, StringComparison.Ordinal)))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToImmutableList();

        var mismatches = (from replica in counted
                          from item in targets
                          let loaded = replica.Loaded.TryGetValue(item.Module, out var v) ? v : "nothing reported"
                          where !string.Equals(loaded, item.TargetVersion, StringComparison.OrdinalIgnoreCase)
                          select $"{replica.Process} loads {item.Module} {loaded}, not {item.TargetVersion}")
            .ToImmutableList();
        if (mismatches.IsEmpty && !unreported.IsEmpty)
            return null;
        return mismatches.IsEmpty
            ? new ModuleReloadVerdict(true, false,
                $"{counted.Count} replica(s) report "
                + string.Join(", ", targets.Select(i => $"{i.Module} {i.TargetVersion}")) + " loaded")
            : new ModuleReloadVerdict(false, false, string.Join("; ", mismatches));
    }
}

/// <summary>The verdict <see cref="ModuleReload.Evaluate"/> reaches over the counted replica reports.</summary>
/// <param name="Loaded">Every counted replica loads every target version.</param>
/// <param name="SwapFailed">A live swap failed on a counted replica — the fallback is one restart.</param>
/// <param name="Detail">The sentence that says which replica loads what.</param>
public sealed record ModuleReloadVerdict(bool Loaded, bool SwapFailed, string Detail);
