using System;
using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// The thread-lifecycle state machine expressed as ONE authoritative table of
/// legal <c>(from → to)</c> <see cref="ThreadExecutionStatus"/> edges, applied
/// through <see cref="Transition"/> inside every
/// <c>GetMeshNodeStream(...).Update</c> lambda that changes
/// <see cref="MeshThread.Status"/>.
///
/// <para>Two invariants this enforces by construction:</para>
/// <list type="number">
///   <item><b>We always start from the lambda parameter.</b> The Update lambda
///     runs on the owning hub's single-threaded action block against the LATEST
///     state, so the from-state we validate (<c>current.Status</c>) is never
///     stale — no transition is decided from a snapshot read earlier.</item>
///   <item><b>🚨 From <see cref="ThreadExecutionStatus.Executing"/> you may only
///     reach <see cref="ThreadExecutionStatus.Idle"/> (complete) or
///     <see cref="ThreadExecutionStatus.Cancelled"/> (cancel), or stay
///     Executing.</b> You may NEVER write
///     <see cref="ThreadExecutionStatus.StartingExecution"/> from Executing —
///     that inverse of the commit edge (StartingExecution→Executing) is the
///     re-dispatch ping-pong: the exec round watcher commits forward while a
///     self-healing recovery observer flips it back, and the two volley under
///     load. To continue or resume an interrupted round you STAY Executing and
///     re-launch (<see cref="ThreadSubmissionServer.ResumeInterruptedRound"/>).
///     The edge is deliberately ABSENT from <see cref="Legal"/>, so
///     <see cref="Transition"/> refuses it.</item>
/// </list>
///
/// <para>Legal edges (self-edges — payload-only updates that keep Status — are
/// always allowed):</para>
/// <code>
///   Idle              → StartingExecution   submission watcher claims a round
///   Cancelled         → StartingExecution   re-claim queued input after a stop
///   StartingExecution → Executing           exec round watcher commits + launches
///   StartingExecution → Idle                rollback: claim found nothing
///   StartingExecution → Cancelled           cancel during claim
///   Executing         → Idle                round complete
///   Executing         → Cancelled           cancel during execution
///   Idle              → Cancelled            honor a cancel requested while idle
///   Idle              → Done                 user marks the thread done
///   Cancelled         → Done                 user marks the thread done
///   Done              → Idle                 a new submission reopens the thread
/// </code>
/// </summary>
public static class ThreadStateTransitions
{
    // Immutable, initialised once, never written at runtime — a constant lookup,
    // not a cache (allowed static readonly under the No-Static-State rule).
    private static readonly ImmutableHashSet<(ThreadExecutionStatus From, ThreadExecutionStatus To)> Legal =
    [
        (ThreadExecutionStatus.Idle, ThreadExecutionStatus.StartingExecution),
        (ThreadExecutionStatus.Cancelled, ThreadExecutionStatus.StartingExecution),
        (ThreadExecutionStatus.StartingExecution, ThreadExecutionStatus.Executing),
        (ThreadExecutionStatus.StartingExecution, ThreadExecutionStatus.Idle),
        (ThreadExecutionStatus.StartingExecution, ThreadExecutionStatus.Cancelled),
        (ThreadExecutionStatus.Executing, ThreadExecutionStatus.Idle),
        (ThreadExecutionStatus.Executing, ThreadExecutionStatus.Cancelled),
        (ThreadExecutionStatus.Idle, ThreadExecutionStatus.Cancelled),
        (ThreadExecutionStatus.Idle, ThreadExecutionStatus.Done),
        (ThreadExecutionStatus.Cancelled, ThreadExecutionStatus.Done),
        (ThreadExecutionStatus.Done, ThreadExecutionStatus.Idle),
    ];

    /// <summary>
    /// True when <paramref name="from"/> → <paramref name="to"/> is a legal
    /// lifecycle edge. Self-edges (<c>from == to</c>) are always legal — they are
    /// payload-only updates that keep <see cref="MeshThread.Status"/>.
    /// 🚨 <c>Executing → StartingExecution</c> is deliberately NOT legal.
    /// </summary>
    public static bool CanTransition(ThreadExecutionStatus from, ThreadExecutionStatus to)
        => from == to || Legal.Contains((from, to));

    /// <summary>
    /// True iff the thread is in an execution phase
    /// (<see cref="ThreadExecutionStatus.StartingExecution"/> or
    /// <see cref="ThreadExecutionStatus.Executing"/>). Mirrors
    /// <see cref="MeshThread.IsExecuting"/> for use on a bare status value.
    /// </summary>
    public static bool IsExecuting(this ThreadExecutionStatus status)
        => status is ThreadExecutionStatus.StartingExecution or ThreadExecutionStatus.Executing;

    /// <summary>
    /// Apply <paramref name="mutate"/> to the <see cref="MeshThread"/> content of
    /// <paramref name="current"/> (the value the <c>Update</c> lambda received),
    /// but commit the result ONLY IF the implied <see cref="MeshThread.Status"/>
    /// edge is legal. An illegal edge — above all
    /// <see cref="ThreadExecutionStatus.Executing"/> →
    /// <see cref="ThreadExecutionStatus.StartingExecution"/> — is REFUSED: the
    /// node is returned unchanged (and logged), so the engine stays in its current
    /// valid state instead of oscillating. Bumps
    /// <see cref="MeshNode.LastModified"/> on a real change.
    ///
    /// <para>Usage — the from-state is read from the lambda parameter, never a
    /// stale snapshot:</para>
    /// <code>
    /// stream.Update(node => node.Transition(t => t with { Status = ThreadExecutionStatus.Idle, ... }, logger));
    /// </code>
    /// </summary>
    public static MeshNode Transition(
        this MeshNode current, Func<MeshThread, MeshThread> mutate, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (current.Content is not MeshThread thread)
            return current;

        var next = mutate(thread);
        if (!CanTransition(thread.Status, next.Status))
        {
            logger?.LogWarning(
                "[ThreadState] Refused illegal transition {From}->{To} on {Path} — staying put.",
                thread.Status, next.Status, current.Path);
            return current;
        }

        if (ReferenceEquals(next, thread))
            return current;

        return current with { LastModified = DateTime.UtcNow, Content = next };
    }
}
