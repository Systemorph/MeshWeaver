using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🅿️ PARK registry — the wedge cure for a NodeType whose source does not compile.
///
/// <para><b>The defect this contains.</b> A NodeType compile that reaches a terminal
/// FAILED state must NOT keep re-running Roslyn. In the stateless, Release-based compile
/// model every re-trigger (a self-heal Ok→Pending flip, a stray release request, a
/// recovery kickoff) lands on the per-NodeType hub's single-threaded action block; an
/// un-bounded recompile loop on a broken type therefore saturates that block and wedges
/// the portal/user. Parking makes the failure <b>bounded + terminal</b> (the type stops
/// recompiling) and <b>visible</b> (one user notification).</para>
///
/// <para><b>Bounded.</b> A deterministic failure — a real source error (a
/// <see cref="CompilationException"/> or Roslyn diagnostics) — parks on the FIRST failure:
/// it would fail identically until the source changes. A non-deterministic failure (a
/// transient infra fault) is retried at most <see cref="MaxCompileAttempts"/> times, then
/// parked — so EVERY failure path is bounded, never an unbounded loop.</para>
///
/// <para><b>Short-circuit.</b> While a type is parked, the compile watcher's Pending
/// handler short-circuits WITHOUT dispatching Roslyn (see
/// <c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>), so a single broken type can
/// never drive the recompile storm.</para>
///
/// <para><b>Un-park.</b> The single un-park trigger is a DELIBERATE retry — the user
/// requests a fresh release (<c>InstallReleaseRequestWatcher</c> calls
/// <see cref="Unpark"/> before promoting the request to Pending), or a compile genuinely
/// succeeds (<see cref="OnCompileSucceeded"/>), or automatically when a parked type's SOURCE
/// snapshot changes after the failure (<see cref="ShouldRetryForSourceChange"/> — the "retry
/// only if the sources changed" path). The registry is mesh-scoped and held in
/// memory, so a process restart also clears it — a redeployed fix recompiles fresh.</para>
///
/// <para>🅿️ <b>An AUTOMATIC re-drive does NOT un-park (#2260).</b> The failed-verdict kickoff
/// (#1793) takes its one fresh attempt through <see cref="AdmitOneRetry"/> instead: the park entry
/// stays, so <see cref="IsParked"/> is true at every instant and there is no window in which a
/// broken type reads as un-parked and a stray trigger is admitted. Lifting the park is reserved
/// for the two events that genuinely END the failure — a human-requested build and a successful
/// compile.</para>
///
/// <para>Mesh-scoped singleton (registered in <c>AddGraph</c>): one instance shared by
/// every per-NodeType hub, with instance maps only — NO static state.</para>
/// </summary>
public sealed class NodeTypeCompileParkRegistry
{
    /// <summary>A non-deterministic failure is retried at most this many times before parking.</summary>
    private const int MaxCompileAttempts = 3;

    // PARKED terminal compile failures by nodeTypePath. While parked, the compile watcher
    // serves the cached error instead of re-running Roslyn.
    private readonly ConcurrentDictionary<string, ParkedCompileFailure> _parked = new();

    // Consecutive compile-failure counts by nodeTypePath. Bounds retries for
    // *non-deterministic* failures. A deterministic failure parks on the first failure.
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();

    // Total real Roslyn compile kick-offs per nodeTypePath since the last un-park
    // (diagnostic). Proves boundedness: a parked type holds at its small attempt count
    // instead of climbing on every access.
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    // 🅿️ #2260 — ONE-SHOT RETRY ADMISSIONS. An AUTOMATIC re-drive needs its single fresh attempt
    // to reach the compile watcher without being swallowed by the parked short-circuit; it does
    // NOT need — and must not have — the park itself lifted. See AdmitOneRetry.
    private readonly ConcurrentDictionary<string, byte> _retryAdmissions = new();

    /// <summary>Record that a real Roslyn compile was kicked off for the NodeType.</summary>
    public void RecordAttempt(string nodeTypePath) =>
        _attempts.AddOrUpdate(nodeTypePath, 1, (_, n) => n + 1);

    // 🚨 #1793 — the AUTOMATIC failure re-drive ledger. Two counters, because the two failure
    // shapes they bound are different questions:
    //
    //   _redrivesByInputs — how often THIS process re-drove a type for EXACTLY the same compile
    //     inputs. The kickoff stamps the live inputs in the same write that flips Pending, so this
    //     can only exceed 1 if something is rewriting that stamp — i.e. the re-drive is feeding
    //     itself. That is the 257,000-version write-storm shape (#223), and it must be LOUD rather
    //     than merely bounded: the second occurrence logs an ERROR naming the path.
    //   _redriveTotals — how many automatic re-drives this process has issued for the type at all,
    //     across every input change. The give-up bound: past it the type is left alone, loudly,
    //     for a human to Compile.
    private readonly ConcurrentDictionary<(string Path, string Inputs), int> _redrivesByInputs = new();
    private readonly ConcurrentDictionary<string, int> _redriveTotals = new();

    /// <summary>
    /// How many automatic failure re-drives one NodeType may receive from a single process before
    /// the framework stops trying and says so. A SAFETY VALVE, not the primary bound — the primary
    /// bound is structural (one re-drive per distinct set of compile inputs, enforced by the stamp
    /// the re-drive writes). This exists so that a stamp which somehow fails to stick converges on
    /// a loud stop instead of a spin.
    /// </summary>
    public const int MaxAutomaticFailureRedrives = 5;

    /// <summary>
    /// Record an automatic failure re-drive of <paramref name="nodeTypePath"/> for the compile
    /// inputs <paramref name="inputs"/> (<c>NodeTypeCompilationHelpers.BuildInputsToken</c>).
    /// </summary>
    /// <returns>
    /// <c>ForTheseInputs</c> — how many times this process has now re-driven the type for exactly
    /// these inputs; anything above 1 means the re-drive did not converge. <c>Total</c> — how many
    /// automatic re-drives this process has issued for the type in total, compared against
    /// <see cref="MaxAutomaticFailureRedrives"/>.
    /// </returns>
    public (int ForTheseInputs, int Total) RecordFailureRedrive(string nodeTypePath, string inputs)
    {
        var forTheseInputs = _redrivesByInputs.AddOrUpdate((nodeTypePath, inputs), 1, (_, n) => n + 1);
        var total = _redriveTotals.AddOrUpdate(nodeTypePath, 1, (_, n) => n + 1);
        return (forTheseInputs, total);
    }

    /// <summary>How many automatic failure re-drives this process has issued for the NodeType —
    /// the observable proof that the recovery path is bounded.</summary>
    public int GetFailureRedriveCount(string nodeTypePath) =>
        _redriveTotals.GetValueOrDefault(nodeTypePath);

    /// <summary>
    /// Total real compile kick-offs for the NodeType since the last un-park. A parked
    /// (broken) type holds at its small attempt count rather than climbing on every
    /// access — the observable proof that the failure is bounded.
    /// </summary>
    public int GetCompileAttemptCount(string nodeTypePath) =>
        _attempts.GetValueOrDefault(nodeTypePath);

    /// <summary>
    /// <c>true</c> if the NodeType is in the terminal PARKED state — its compile failed and
    /// is no longer being retried (contained). Diagnostic surface for ops / overlays / tests.
    /// </summary>
    public bool IsParked(string nodeTypePath) => _parked.ContainsKey(nodeTypePath);

    /// <summary>The cached error text for a parked NodeType, or <c>null</c> when not parked.</summary>
    public string? GetParkedError(string nodeTypePath) =>
        _parked.TryGetValue(nodeTypePath, out var p) ? p.Error : null;

    /// <summary>
    /// <c>true</c> when the NodeType is parked AND <paramref name="currentSources"/> differs from
    /// the source snapshot captured when it parked — i.e. a source edit/add/remove landed SINCE
    /// the terminal failure, so the (presumed) fix warrants an automatic recompile. A parked type
    /// whose sources are UNCHANGED returns <c>false</c>: the failure would reproduce identically,
    /// so there is nothing to retry (and no storm). This is the "retry only if the sources
    /// changed" gate — <c>NodeTypeCompilationHelpers.InstallSourcesWatcher</c> consults it on
    /// every source change and, when it is <c>true</c>, calls <see cref="Unpark"/> and re-drives
    /// the compile (a fresh <c>CompilationStatus = Pending</c>) with no deliberate Compile/recycle.
    /// </summary>
    public bool ShouldRetryForSourceChange(
        string nodeTypePath, IReadOnlyDictionary<string, long> currentSources) =>
        _parked.TryGetValue(nodeTypePath, out var p)
        && !SnapshotEquals(p.Sources, currentSources);

    /// <summary>Source-snapshot equality treating <c>null</c> and an empty map as equal.</summary>
    private static bool SnapshotEquals(
        IReadOnlyDictionary<string, long>? a, IReadOnlyDictionary<string, long>? b)
    {
        var ca = a?.Count ?? 0;
        var cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var kv in a!)
            if (!b!.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        return true;
    }

    /// <summary>
    /// 🅿️ #2260 — admit ONE sanctioned automatic retry through the compile watcher's parked
    /// short-circuit, WITHOUT un-parking.
    ///
    /// <para><b>Why this exists.</b> The automatic re-drives (the failed-verdict kickoff) used to
    /// call <see cref="Unpark"/> so their <c>CompilationStatus = Pending</c> flip would not be
    /// swallowed by the short-circuit. That conflates two different things — "this terminal
    /// failure is cleared" and "let this one flip through" — and it costs the guarantee the park
    /// exists for: between the un-park and the re-park that follows a second refusal, the type is
    /// NOT parked, so every reader (<c>IsParked</c>, the framework-stale kickoff's guard, the
    /// adopt-only refusal's own test) can observe an un-parked broken type, and any trigger
    /// arriving in that window is admitted. On a mesh that sets <c>Modules:RequirePrebuilt</c> the
    /// window opens on EVERY refusal, because the refusal's settle records no verdict inputs and
    /// therefore always reads as "never attempted".</para>
    ///
    /// <para>An admission is one-shot (<see cref="TryConsumeRetryAdmission"/> removes it), so it
    /// widens nothing: exactly one Pending flip gets through, every later stray trigger still
    /// short-circuits on the park that never moved. A DELIBERATE retry keeps using
    /// <see cref="Unpark"/> — a human asking for a build really is clearing the failure.</para>
    ///
    /// <para>🚨 <b>Grant this ONLY while <see cref="IsParked"/> is true.</b> An admission for an
    /// un-parked type is never consumed (the short-circuit it exists to pass is not taken) and
    /// would linger until some LATER park, where a stray trigger could spend it. Callers gate on
    /// the park, which establishes "an admission implies a standing park"; both paths that remove
    /// a park (<see cref="Unpark"/>, <see cref="OnCompileSucceeded"/>) clear admissions with it,
    /// so an admission can never outlive the park it was granted against.</para>
    /// </summary>
    public void AdmitOneRetry(string nodeTypePath) => _retryAdmissions[nodeTypePath] = 0;

    /// <summary>
    /// Consume a pending one-shot admission (see <see cref="AdmitOneRetry"/>). <c>true</c> when
    /// this Pending flip IS the sanctioned automatic retry and must be let through the parked
    /// short-circuit; <c>false</c> for every other trigger.
    /// </summary>
    public bool TryConsumeRetryAdmission(string nodeTypePath) =>
        _retryAdmissions.TryRemove(nodeTypePath, out _);

    /// <summary>A compile succeeded — clear any parked failure / retry budget for the type.</summary>
    public void OnCompileSucceeded(string nodeTypePath)
    {
        _parked.TryRemove(nodeTypePath, out _);
        _failureCounts.TryRemove(nodeTypePath, out _);
        _retryAdmissions.TryRemove(nodeTypePath, out _);
        // The automatic re-drive CONVERGED, so its budget is spent and returned: a type that
        // breaks again later gets a fresh set of attempts rather than inheriting a used-up one.
        ClearFailureRedrives(nodeTypePath);
    }

    /// <summary>Forget the automatic-re-drive ledger for one NodeType (a converged or deliberately
    /// retried type starts clean).</summary>
    private void ClearFailureRedrives(string nodeTypePath)
    {
        _redriveTotals.TryRemove(nodeTypePath, out _);
        foreach (var key in _redrivesByInputs.Keys)
            if (string.Equals(key.Path, nodeTypePath, StringComparison.Ordinal))
                _redrivesByInputs.TryRemove(key, out _);
    }

    /// <summary>
    /// Un-park: the single trigger that clears a terminal compile failure (a deliberate
    /// retry — a fresh release request). Resets the attempt budget so the next compile
    /// starts clean.
    /// </summary>
    public void Unpark(string nodeTypePath)
    {
        if (_parked.TryRemove(nodeTypePath, out _))
            _attempts.TryRemove(nodeTypePath, out _);
        _failureCounts.TryRemove(nodeTypePath, out _);
        // An un-parked type needs no admission — and a leftover one would let a later stray
        // trigger through the short-circuit if the type parks again.
        _retryAdmissions.TryRemove(nodeTypePath, out _);
    }

    /// <summary>
    /// A DELIBERATE retry (the Compile button / a fresh release request) also returns the automatic
    /// re-drive budget: a human asking for a build is the strongest possible signal that the
    /// give-up should be reconsidered. Separate from <see cref="Unpark"/> so the automatic path
    /// (which un-parks too) cannot refund its own budget.
    /// </summary>
    public void ResetFailureRedrives(string nodeTypePath) => ClearFailureRedrives(nodeTypePath);

    /// <summary>
    /// A compile reached a terminal FAILED state. Bound every failure path: a
    /// <paramref name="deterministic"/> failure parks immediately; a non-deterministic one
    /// is retried up to <see cref="MaxCompileAttempts"/> then parked. On the transition
    /// INTO the parked state (idempotent — only the first caller), emit a user-visible
    /// notification carrying the failing type path + error summary.
    /// </summary>
    /// <param name="hub">The per-NodeType hub (its ServiceProvider resolves IMeshService / AccessService).</param>
    /// <param name="nodeTypePath">Path of the NodeType whose compile failed.</param>
    /// <param name="error">The compile error summary.</param>
    /// <param name="deterministic"><c>true</c> for a real source error (parks immediately).</param>
    /// <param name="recipientUserId">The user who requested the release
    /// (<see cref="NodeTypeDefinition.RequestedReleaseBy"/>) — the bell to notify. <c>null</c>
    /// for a System-driven first-build / seed compile, in which case the notification is a
    /// satellite of the failing type (visible to whoever can read the type).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="sources">The source-version snapshot (<c>{path → LastModified.Ticks}</c>)
    /// that this failing compile consumed — stored with the park so a later source edit can be
    /// detected (<see cref="ShouldRetryForSourceChange"/>) and auto-retried.</param>
    public void OnCompileFailed(
        IMessageHub hub,
        string nodeTypePath,
        string error,
        bool deterministic,
        string? recipientUserId,
        IReadOnlyDictionary<string, long>? sources,
        ILogger? logger)
    {
        var failures = _failureCounts.AddOrUpdate(nodeTypePath, 1, (_, n) => n + 1);
        if (deterministic || failures >= MaxCompileAttempts)
            ParkAndNotify(hub, nodeTypePath, error, sources, recipientUserId, logger);
    }

    /// <summary>
    /// Transition a NodeType to the terminal PARKED state and emit a one-time, user-visible
    /// notification. Idempotent: only the FIRST caller parks + notifies (a broken type yields
    /// exactly one notification — never a storm).
    /// </summary>
    private void ParkAndNotify(
        IMessageHub hub, string nodeTypePath, string error,
        IReadOnlyDictionary<string, long>? sources, string? recipientUserId, ILogger? logger)
    {
        if (!_parked.TryAdd(nodeTypePath, new ParkedCompileFailure(error, DateTimeOffset.UtcNow, sources)))
        {
            // 🅿️ Already parked — an ADMITTED automatic retry (AdmitOneRetry) re-failed, which is
            // now the normal shape: the park is never lifted for one, so the second refusal lands
            // here rather than adding a fresh entry. REFRESH the record in place so the cached
            // error and — decisively — the source snapshot describe the LATEST verdict:
            // ShouldRetryForSourceChange compares that snapshot against the live one, and a stale
            // snapshot would keep re-arming the source-change retry against a source set nothing
            // has actually changed. Compare-and-replace, never a blind write: a concurrent Unpark
            // (a human pressing Compile) must not be resurrected by this refresh.
            if (_parked.TryGetValue(nodeTypePath, out var standing))
                _parked.TryUpdate(
                    nodeTypePath, standing with { Error = error, Sources = sources }, standing);
            return; // …and never re-notify: a broken type yields exactly one notification.
        }

        logger?.LogError(
            "NodeType '{NodeTypePath}' PARKED after compile failure — further activations serve the " +
            "cached error without recompiling (failure contained, no retry storm). Error: {Error}",
            nodeTypePath, error);

        EmitFailureNotification(hub, nodeTypePath, error, recipientUserId, logger);
    }

    /// <summary>
    /// Emit a user-visible <c>Notification</c> (the same bell-databound satellite mechanism as
    /// approvals / completions) for a parked compile failure. Fully reactive: the cold
    /// <c>CreateNotification</c> observable is subscribed here with explicit error handling — a
    /// notification-write failure is logged, never thrown back onto the compile path.
    /// </summary>
    private static void EmitFailureNotification(
        IMessageHub hub, string nodeTypePath, string error, string? recipientUserId, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
        {
            logger?.LogWarning(
                "Cannot emit compile-failure notification for {NodeTypePath}: IMeshService unavailable.",
                nodeTypePath);
            return;
        }

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // Recipient resolution: prefer the user who requested the release (RequestedReleaseBy);
        // fall back to the ambient user; finally, when neither is a real user (a System-driven
        // first-build / seed compile), leave it NULL — which the notification service reads as the
        // PLATFORM addressee, so the failure lands in the operators' bell.
        //
        // 🚨 It used to make the notification a satellite of the failing TYPE instead, "so it is
        // still visible — in every per-user bell that can read the type". That is the mis-addressing
        // #3156 is about: a compile failure nobody asked for is operator material, and putting it in
        // the bell of everyone who can read the type is both noise for them and invisible to the one
        // person who can fix it. The type stays the click target (targetNodePath below).
        var recipient = !string.IsNullOrEmpty(recipientUserId) && recipientUserId != WellKnownUsers.System
            ? recipientUserId
            : null;
        recipient ??= NonSystem(accessService?.Context?.ObjectId)
                      ?? NonSystem(accessService?.CircuitContext?.ObjectId);

        // The ENTITY the notification is about — no longer the thing that chooses the partition.
        var mainNodePath = nodeTypePath;

        var typeName = nodeTypePath.Contains('/')
            ? nodeTypePath[(nodeTypePath.LastIndexOf('/') + 1)..]
            : nodeTypePath;
        var title = $"Type '{typeName}' failed to compile";
        var message =
            $"The node type '{nodeTypePath}' was parked after a compile failure and will not be " +
            $"retried until its source is fixed. {SummarizeError(error)}";

        // Dispatch runs the whole flow as System itself (the compile runs as System; the recipient's
        // bell partition admits no ambient user write). Infrastructure observability under the
        // System notification category. When recipient is null (a System-driven build), Dispatch
        // addresses the platform operators (in-app only — there is no collective mailbox).
        // Inverted through ICompileFailureNotifier (the graph/compiler split): delivery is
        // NotificationService in MeshWeaver.Graph, which reads the Notification* node types this
        // assembly must not depend on. OPTIONAL by design — a hub composed without AddGraph has no
        // notification model, and that must stay a missing bell rather than a faulted compile.
        var notifier = hub.ServiceProvider.GetService<ICompileFailureNotifier>();
        if (notifier is null)
        {
            logger?.LogDebug(
                "No ICompileFailureNotifier registered; skipping compile-failure notification for {NodeTypePath}.",
                nodeTypePath);
            return;
        }

        notifier.NotifyCompileFailed(
                hub,
                recipient: recipient,
                mainNodePath: mainNodePath,
                title: title,
                message: message,
                targetNodePath: nodeTypePath)
            .Subscribe(
                _ => logger?.LogInformation(
                    "Emitted compile-failure notification for {NodeTypePath} (recipient {Recipient})",
                    nodeTypePath, recipient ?? "platform admins"),
                ex => logger?.LogWarning(ex,
                    "Failed to emit compile-failure notification for {NodeTypePath}", nodeTypePath));
    }

    private static string? NonSystem(string? userId) =>
        string.IsNullOrEmpty(userId) || userId == WellKnownUsers.System ? null : userId;

    /// <summary>Trims a Roslyn error blob to a single readable, capped summary for a notification.</summary>
    private static string SummarizeError(string error)
    {
        var trimmed = error.Replace("\r", "").Trim();
        const int max = 500;
        return trimmed.Length <= max ? trimmed : string.Concat(trimmed.AsSpan(0, max), " …");
    }

    /// <summary>Record of a parked terminal compile failure: the error text, when it parked, and
    /// the source-version snapshot that failed (used to detect a later source change).</summary>
    private sealed record ParkedCompileFailure(
        string Error, DateTimeOffset ParkedAt, IReadOnlyDictionary<string, long>? Sources);
}
