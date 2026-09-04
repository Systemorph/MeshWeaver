using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Removes expired notifications from the partitions the signing-in user is the reader of — their
/// own, plus <c>Admin</c> when they are a global admin. The platform's notification retention pass
/// (Systemorph/MeshWeaver#3250); <see cref="NotificationRetention"/> is the policy it applies.
///
/// <para>🚨 <b>Why a logon action and not a startup <c>Job</c>, a hosted timer, or an operations
/// script.</b> Those were the three alternatives and each fails on something this one gets for
/// free:</para>
/// <list type="bullet">
///   <item><b>A startup <c>Job</c></b> — the shape a database migration uses — can stop a portal
///     from serving (<c>DbVersionGate</c>). Reclaiming three-month-old notifications must never be
///     able to do that, which is the constraint #3250 states first.</item>
///   <item><b>A process-wide timer</b> has no reader to scope it to, so it would have to enumerate
///     partitions and sweep them as <c>System</c> — a cross-partition pass over 201 schemas, run by
///     nobody's identity, which is precisely the unbounded shape the ticket forbids.</item>
///   <item><b>A Code-MeshNode operation</b> (inputs + <c>RequestedStatus = Running</c> + progress)
///     is the house pattern for work a PERSON runs, and it is a good fit for a one-off audited
///     purge. It is the wrong fit here for one reason: a tail that only shrinks when somebody
///     remembers to press Run does not shrink, and "it ages out" turning into "never" is the entire
///     defect. Retention has no inputs a person supplies and no output a person reads.</item>
/// </list>
///
/// <para>A logon action already IS one person, one partition, one identity: it runs off the
/// authentication path under the signing-in user's own context, so the only rows it can reach are
/// the ones that user could delete by hand, no impersonation is involved, and
/// <see cref="LogonActionRunner"/>'s own budget and catch mean the worst case is a missed sweep,
/// never a failed login.</para>
///
/// <para>🚨 <b>EveryLogon, not RunOnce.</b> Notifications keep arriving and keep expiring, so a
/// ledger entry saying "retention has run for this user" would be false the next day. The cost of
/// running every time is bounded by a CHECK, not by a ledger, exactly as
/// <see cref="AppIconAdoptionLogonAction"/> is: in the steady state this issues one capped,
/// partition-anchored query per partition and deletes nothing, because nothing is old enough.</para>
///
/// <para>See <c>Doc/Architecture/NotificationRetention</c>.</para>
/// </summary>
/// <param name="retention">The policy — resolved from configuration by <c>AddNotificationType</c>.</param>
public sealed class NotificationRetentionLogonAction(NotificationRetention retention) : ILogonAction
{
    /// <summary>Stable id. Not a ledger key (this action is every-logon) but still unique.</summary>
    public string Id => "platform.notification-retention";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>
    /// Runs LAST. Everything else at logon adds or repairs something the user is about to look at;
    /// this only takes away things they will not. An action that seeds or pins should get its work
    /// done before the housekeeping, and if the budget runs out, housekeeping is what should be
    /// dropped.
    /// </summary>
    public int Order => 900;

    /// <summary>Bound on each mesh read. A slow index costs the sweep, never the logon.</summary>
    private static readonly TimeSpan QueryBound = TimeSpan.FromSeconds(10);

    /// <summary>Bound on one deletion. A wedged owner costs that row, not the whole run.</summary>
    private static readonly TimeSpan DeleteBound = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        // Disarmed ⇒ a complete no-op: not even a query is issued.
        if (!retention.Enabled)
            return Observable.Return(LogonActionOutcome.Nothing);

        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return(LogonActionOutcome.Nothing);
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.NotificationRetention");

        // ONE cutoff for the whole run, taken once. Two partitions swept against two different
        // "now"s would be two policies; and a pure function of a fixed instant is what makes the
        // run idempotent — re-running it in the same session selects the same set, finds it gone,
        // and does nothing.
        var now = DateTimeOffset.UtcNow;

        return PartitionsToSweep(context)
            .SelectMany(partitions => partitions
                // Concat, never Merge: one partition at a time, and within a partition one delete
                // at a time. Retention is background work on a login path — it must cost the mesh
                // a trickle, never a burst.
                .Select(partition => Sweep(mesh, partition, now, logger))
                .Concat()
                .DefaultIfEmpty(Unit.Default))
            .TakeLast(1)
            .Select(_ => LogonActionOutcome.Nothing)
            // Retention changes nothing about the user's profile, so there is no outcome to commit
            // and nothing to roll back. A failure is a missed sweep: the next logon tries again
            // against a cutoff that has only moved further into the past.
            .Catch<LogonActionOutcome, Exception>(ex =>
            {
                logger?.LogDebug(ex, "Notification retention skipped for {User}", context.UserPath);
                return Observable.Return(LogonActionOutcome.Nothing);
            });
    }

    /// <summary>
    /// The partitions this user is the READER of, and therefore the only ones they may prune: their
    /// own, plus <c>Admin</c> when they are a global admin.
    ///
    /// <para>🚨 This is the whole boundedness argument, and it is structural rather than a limit
    /// somebody remembered to write: nothing here enumerates partitions. The set of partitions a
    /// portal sweeps is the set of people who signed in, one person at a time, each in a separate
    /// run — so there is no code path on which this becomes one statement over 201 schemas, whatever
    /// the policy is set to.</para>
    ///
    /// <para>The <c>Admin</c> leg is what retires the PLATFORM bell. Since the addressing change
    /// every operator notice is delivered to <c>Admin/_Notification</c>, whose readers are exactly
    /// the global admins — so its retention runs when one of them signs in, which is also the only
    /// moment anyone could have looked at it.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<string>> PartitionsToSweep(LogonActionContext context)
    {
        var own = PartitionOf(context.UserPath);
        if (string.IsNullOrEmpty(own))
            return Observable.Return<IReadOnlyList<string>>([]);

        // Already the platform partition (nobody signs in AS Admin today, but the fold must not
        // sweep it twice if that ever changes).
        if (string.Equals(own, NotificationService.PlatformAddressee, StringComparison.OrdinalIgnoreCase))
            return Observable.Return<IReadOnlyList<string>>([own]);

        return context.Hub.IsGlobalAdmin(context.UserPath)
            // 🚨 TakeDecisionOutsideGate, never a bare Take(1): DELETES follow this decision, and
            // the permission fold emits synchronously while holding its own Rx gate on a warm cache
            // (#899). Running a delete inside that gate is the recursive-delete wedge.
            .TakeDecisionOutsideGate()
            .Timeout(QueryBound, Observable.Return(false))
            // A permission fold that cannot answer means "not an admin" — the fail-closed
            // direction, which here costs a deferred sweep of Admin and nothing else.
            .Catch(Observable.Return(false))
            .Select(isGlobalAdmin => isGlobalAdmin
                ? (IReadOnlyList<string>)[own, NotificationService.PlatformAddressee]
                : [own]);
    }

    /// <summary>The partition a path belongs to: its first segment. Never strips it, never
    /// lower-cases it (Doc/Architecture/PostgresSchemaArchitecture).</summary>
    private static string PartitionOf(string path)
        => path.Trim().TrimStart('/').Split('/', 2)[0];

    /// <summary>
    /// One partition's sweep: read the oldest capped window of its notifications, keep the ones the
    /// policy calls expired, delete those.
    ///
    /// <para>🚨 <b>The query bounds the work; the policy decides the deletion.</b> They are
    /// deliberately two steps. If a backend ever ignores <c>sort:LastModified-asc</c> the window is
    /// simply an arbitrary capped page — the sweep then removes fewer rows per run and takes longer
    /// to drain, which is a liveness cost. It can never remove a row the policy has not called
    /// expired, which would be a correctness cost.</para>
    /// </summary>
    private IObservable<Unit> Sweep(
        IMeshService mesh, string partition, DateTimeOffset now, ILogger? logger)
    {
        var query = NotificationService.RetentionQuery(partition, retention.MaxDeletionsPerRun);
        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Timeout(QueryBound)
            .SelectMany(change =>
            {
                var expired = change.Items
                    .Where(node => retention.IsExpired(node, now))
                    // Belt and braces on top of limit:, so the cap holds even if a backend returns
                    // more rows than it was asked for.
                    .Take(retention.MaxDeletionsPerRun)
                    .ToArray();
                // The steady state, and what makes an every-logon action affordable: one query,
                // nothing old enough, no writes.
                if (expired.Length == 0)
                    return Observable.Return(Unit.Default);

                // 🚨 Information, and it is a deliberate cost/value call rather than debug volume.
                // This line records the platform DESTROYING someone's rows, which is the kind of
                // thing an operator must be able to see in Loki without redeploying — and it is
                // cheap because it is one line per partition per run and ONLY when something was
                // actually removed: the steady state emits nothing at all. Per-row failures below
                // stay at Debug, where they are diagnostics rather than an audit trail.
                logger?.LogInformation(
                    "Notification retention: removing {Count} notifications older than {MaxAge} from {Partition}",
                    expired.Length, retention.MaxAge, partition);
                return expired
                    .Select(node => Delete(mesh, node.Path, logger))
                    .Concat()
                    .TakeLast(1);
            })
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogDebug(ex, "Notification retention skipped for partition {Partition}", partition);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// Deletes one notification through the framework — <see cref="IMeshService.DeleteNode"/>, which
    /// routes a <c>DeleteNodeRequest</c> under the caller's identity and keeps the workspace cache
    /// in step. 🚨 Never a raw <c>psql DELETE</c>: that bypasses the cache, and a portal would keep
    /// serving rows that are no longer in the database.
    ///
    /// <para>A failure is per-ROW, not per-run: an already-deleted node (two devices signing in at
    /// once, the second run's window still naming rows the first removed) answers NodeNotFound, and
    /// that is exactly what idempotence looks like from here.</para>
    /// </summary>
    private static IObservable<Unit> Delete(IMeshService mesh, string path, ILogger? logger)
        => mesh.DeleteNode(path)
            .Take(1)
            .Timeout(DeleteBound)
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogDebug(ex, "Notification {Path} was not removed by retention", path);
                return Observable.Return(Unit.Default);
            });
}
