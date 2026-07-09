using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// succeeds (<see cref="OnCompileSucceeded"/>). The registry is mesh-scoped and held in
/// memory, so a process restart also clears it — a redeployed fix recompiles fresh.</para>
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

    /// <summary>Record that a real Roslyn compile was kicked off for the NodeType.</summary>
    public void RecordAttempt(string nodeTypePath) =>
        _attempts.AddOrUpdate(nodeTypePath, 1, (_, n) => n + 1);

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

    /// <summary>A compile succeeded — clear any parked failure / retry budget for the type.</summary>
    public void OnCompileSucceeded(string nodeTypePath)
    {
        _parked.TryRemove(nodeTypePath, out _);
        _failureCounts.TryRemove(nodeTypePath, out _);
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
    }

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
    public void OnCompileFailed(
        IMessageHub hub,
        string nodeTypePath,
        string error,
        bool deterministic,
        string? recipientUserId,
        ILogger? logger)
    {
        var failures = _failureCounts.AddOrUpdate(nodeTypePath, 1, (_, n) => n + 1);
        if (deterministic || failures >= MaxCompileAttempts)
            ParkAndNotify(hub, nodeTypePath, error, recipientUserId, logger);
    }

    /// <summary>
    /// Transition a NodeType to the terminal PARKED state and emit a one-time, user-visible
    /// notification. Idempotent: only the FIRST caller parks + notifies (a broken type yields
    /// exactly one notification — never a storm).
    /// </summary>
    private void ParkAndNotify(
        IMessageHub hub, string nodeTypePath, string error, string? recipientUserId, ILogger? logger)
    {
        if (!_parked.TryAdd(nodeTypePath, new ParkedCompileFailure(error, DateTimeOffset.UtcNow)))
            return; // already parked — do not re-notify

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
        // fall back to the ambient user; finally, when neither is a real user (System-driven
        // first-build / seed compile), make the notification a satellite of the failing TYPE so
        // it is still visible — in every per-user bell that can read the type (RLS-filtered).
        var recipient = !string.IsNullOrEmpty(recipientUserId) && recipientUserId != WellKnownUsers.System
            ? recipientUserId
            : null;
        recipient ??= NonSystem(accessService?.Context?.ObjectId)
                      ?? NonSystem(accessService?.CircuitContext?.ObjectId);

        var mainNodePath = recipient ?? nodeTypePath;

        var typeName = nodeTypePath.Contains('/')
            ? nodeTypePath[(nodeTypePath.LastIndexOf('/') + 1)..]
            : nodeTypePath;
        var title = $"Type '{typeName}' failed to compile";
        var message =
            $"The node type '{nodeTypePath}' was parked after a compile failure and will not be " +
            $"retried until its source is fixed. {SummarizeError(error)}";

        // Dispatch runs the whole flow as System itself (the compile runs as System; the recipient's
        // bell partition — or the failing type's read-only partition, e.g. Doc — admits no ambient
        // user write). Infrastructure observability under the System notification category. When
        // recipient is null (System-driven build), it falls to the failing type (in-app only).
        NotificationService.Dispatch(
                hub,
                recipient: recipient,
                mainNodePath: mainNodePath,
                title: title,
                message: message,
                type: NotificationType.System,
                targetNodePath: nodeTypePath,
                createdBy: "system")
            .Subscribe(
                _ => logger?.LogInformation(
                    "Emitted compile-failure notification for {NodeTypePath} (recipient {Recipient})",
                    nodeTypePath, mainNodePath),
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

    /// <summary>Record of a parked terminal compile failure: the error text and when it parked.</summary>
    private sealed record ParkedCompileFailure(string Error, DateTimeOffset ParkedAt);
}
