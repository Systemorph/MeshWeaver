using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// The "no answer from the kernel yet" notice of a C# run (#7362), decided where the run's
/// <see cref="ActivityLog"/> is authoritative: on the hub that OWNS the activity.
///
/// <para><b>Why the owner, not the dispatcher.</b> The dispatcher (<c>CodeNodeType</c>) is the hub
/// whose wait for the kernel's reply runs out, but it does not own the activity: its
/// <c>stream.Update</c> runs on a MIRROR and ships a merge patch. A notice appended there, guarded
/// on the mirror's "still Running", could land after the kernel's terminal write and either survive
/// on a finished run or — because a changed array travels whole — replace the kernel's final
/// transcript with the mirror's stale one. So the dispatcher states only a FACT,
/// <see cref="ActivityLog.ReplyOverdueSince"/>, unconditionally, and this fold — run on the owner's
/// serialised write path against its CURRENT state — draws the conclusion
/// (<c>Doc/Architecture/ConditionalWritesAcrossHubs</c> → "state a fact, let the owner act").</para>
/// </summary>
public static class KernelReplyOverdue
{
    /// <summary>The catalog key of <see cref="Notice"/> (<c>strings.{en,de}.json</c>).</summary>
    public const string NoticeKey = "activity.codeRun.noKernelReplyYet";

    /// <summary>
    /// The transcript entry for a run whose kernel has not answered within the dispatcher's
    /// request budget. Not a verdict: the run stays <see cref="ActivityStatus.Running"/>. Logged at
    /// <see cref="LogLevel.Information"/> so it never raises <see cref="ActivityLog.MaxSeverity"/>
    /// — a run that then succeeds must not read "completed with warnings" because it was slow.
    /// </summary>
    /// <returns>The keyed notice.</returns>
    public static LogMessage Notice() =>
        new LogMessage(
                "No answer from the kernel yet. The run is still in progress and completes when the "
                + "kernel answers.",
                LogLevel.Information)
            .WithKey(NoticeKey);

    /// <summary>Whether <paramref name="message"/> is the no-answer-yet notice.</summary>
    /// <param name="message">A transcript entry.</param>
    /// <returns><c>true</c> for the notice.</returns>
    public static bool IsNotice(LogMessage message) => message.MessageKey == NoticeKey;

    /// <summary>
    /// Whether <paramref name="log"/> has something for the owner to fold: a pending
    /// <see cref="ActivityLog.ReplyOverdueSince"/> fact, or a notice left on a finished run.
    /// </summary>
    /// <param name="log">The activity's current state.</param>
    /// <returns><c>true</c> when <see cref="Fold"/> would change it.</returns>
    public static bool NeedsFold(ActivityLog log) =>
        log.ReplyOverdueSince is not null
        || (log.Status.IsTerminal() && log.Messages.Any(IsNotice));

    /// <summary>
    /// The owner's decision, against its CURRENT state. Consumes the fact; on a still-running run
    /// appends the notice once; on a finished run appends nothing and removes a notice that is
    /// there (it reached the transcript before the kernel's terminal write, and that write did not
    /// rewrite the transcript). Returns <paramref name="log"/> itself when there is nothing to do.
    /// </summary>
    /// <param name="log">The activity's current state on its owning hub.</param>
    /// <returns>The folded state, or <paramref name="log"/> unchanged.</returns>
    public static ActivityLog Fold(ActivityLog log)
    {
        var hasNotice = log.Messages.Any(IsNotice);
        var finished = log.Status.IsTerminal();
        if (log.ReplyOverdueSince is null)
            return finished && hasNotice ? WithoutNotice(log) : log;

        var consumed = log with { ReplyOverdueSince = null };
        if (finished)
            return hasNotice ? WithoutNotice(consumed) : consumed;
        return hasNotice ? consumed : consumed.Append(Notice());
    }

    private static ActivityLog WithoutNotice(ActivityLog log)
    {
        var kept = log.Messages.RemoveAll(IsNotice);
        var removed = log.Messages.Count - kept.Count;
        return log with
        {
            Messages = kept,
            MessageCount = Math.Max(kept.Count, log.TotalMessageCount - removed),
        };
    }

    /// <summary>
    /// Installs the owner-side fold on <paramref name="hub"/>, the hub that owns its activity node:
    /// every state of its own node that <see cref="NeedsFold"/> is folded on the owner's serialised
    /// write path. There is no single-flight guard on purpose — the fold is computed on the
    /// owner's current state and is idempotent, so a write issued for a state that has since moved
    /// on is a no-op, and no state that needs a fold can be dropped. A transient node probe has no
    /// node and gets no watcher.
    /// </summary>
    /// <param name="hub">The activity's owning hub.</param>
    /// <param name="logger">Optional diagnostics.</param>
    /// <returns>The subscription; the caller registers it with the hub's lifetime.</returns>
    internal static IDisposable Watch(IMessageHub hub, ILogger? logger)
    {
        if (hub.Configuration.Get<TransientNodeProbe>() is not null)
            return System.Reactive.Disposables.Disposable.Empty;

        var options = hub.JsonSerializerOptions;
        var workspace = hub.GetWorkspace();
        return ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => workspace.GetMeshNodeStream()
                .Where(node => node?.ContentAs<ActivityLog>(options, logger) is { } log && NeedsFold(log))
                .SelectMany(_ => FoldOnOwner(hub)),
            _ => { },
            logger,
            "Kernel reply-overdue fold");
    }

    /// <summary>
    /// One fold write on the owner. As System: this is infrastructure progress on the activity (the
    /// same identity <c>ActivityLogLogger</c> writes it with), and the trigger is a node emission
    /// whose thread carries no user context. A SYNCHRONOUS scope around the Subscribe, because the
    /// own-node write captures its identity inside Subscribe — a reactive scope would leak it onto
    /// the echo's thread.
    /// </summary>
    private static IObservable<Unit> FoldOnOwner(IMessageHub hub) =>
        Observable.Create<Unit>(observer =>
        {
            var access = hub.ServiceProvider.GetService<AccessService>();
            using (access?.ImpersonateAsSystem())
                return hub.GetWorkspace().GetMeshNodeStream()
                    .Update<ActivityLog>((node, log) =>
                    {
                        if (log is null)
                            return node;
                        var folded = Fold(log);
                        return ReferenceEquals(folded, log) ? node : node with { Content = folded };
                    })
                    // A failed write faults the watcher, which SubscribeHubWatcher classifies:
                    // a transient fault re-establishes it, and the re-established subscription
                    // re-reads the current state and folds again; anything else is reported there.
                    .Select(_ => Unit.Default)
                    .Subscribe(observer);
        });
}
