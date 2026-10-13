using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Approve all / Reject all for the inbox's approval requests. Each row of the Approvals source is
/// the "approval required" notification its approval flow sent the viewer; its
/// <see cref="Notification.TargetNodePath"/> is the node being decided (an approval, an instance
/// action, …). The inbox sends that node a <see cref="DecideApprovalRequest"/> AS THE CLICKER — the
/// node's own type decides what deciding means and authorizes it — and files the request away once
/// the decision is recorded. The inbox knows no approval kind.
/// </summary>
public static class InboxApprovals
{
    /// <summary>How long one decision may take to be answered before the item reports "no answer".</summary>
    internal static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Decides the approval request at <paramref name="notificationPath"/>. The request to the target
    /// carries the caller's identity EXPLICITLY (captured here, in the call): it is sent after a read,
    /// on whatever thread that read answers, where the ambient identity is gone.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="notificationPath">The approval request row.</param>
    /// <param name="decision">An <see cref="ApprovalDecisions"/> constant.</param>
    /// <param name="noAnswer">What the row says when the target does not answer (localized by the caller).</param>
    public static IObservable<InboxItemOutcome> Decide(IMessageHub hub, string notificationPath, string decision, string noAnswer)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context ?? access?.CircuitContext;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxApprovals));
        return hub.GetMeshNodeStream(notificationPath).Take(1)
            .Select(node => node.ContentAs<Notification>(hub.JsonSerializerOptions)?.TargetNodePath)
            .SelectMany(target => string.IsNullOrWhiteSpace(target)
                ? Observable.Return(new InboxItemOutcome(notificationPath, false, noAnswer))
                : Send(hub, target, decision, caller, noAnswer)
                    .SelectMany(outcome => outcome.Done
                        // Decided: the request leaves the inbox (soft — the notification stays). The
                        // filing's OWN outcome is the row's: a request that could not be filed away is
                        // still in the inbox, and the row says so rather than reporting success.
                        ? Dismiss(hub, access, caller, notificationPath)
                        : Observable.Return(outcome with { Path = notificationPath })))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Inbox: deciding {Path} faulted", notificationPath);
                return Observable.Return(new InboxItemOutcome(notificationPath, false));
            });
    }

    private static IObservable<InboxItemOutcome> Send(
        IMessageHub hub, string target, string decision, AccessContext? caller, string noAnswer)
    {
        var responses = hub.NodeOperationIssuingHub().Observe(
            new DecideApprovalRequest(decision),
            options =>
            {
                options = options.WithTarget(new Address(target));
                return caller is null ? options : options.WithAccessContext(caller);
            });
        return (responses ?? Observable.Empty<IMessageDelivery<DecideApprovalResponse>>())
            .Take(1)
            .Timeout(AnswerWithin)
            .Select(reply => reply.Message switch
            {
                // 🚨 HELD before decided: a decision that still needs the approver's step-up is only
                // recorded, whatever Done says — it is never filed away, and its binding comes back so
                // the inbox can confirm the whole batch in ONE step-up (core Doc/Architecture/ApprovalStepUp).
                { StepUpRequired: true, StepUpBinding: { Length: > 0 } binding } answer =>
                    new InboxItemOutcome(target, false, answer.Reason)
                    {
                        StepUp = new StepUpTarget { ActionPath = target, Binding = binding },
                    },
                { StepUpRequired: true } answer => new InboxItemOutcome(target, false, answer.Reason),
                { Done: true } => new InboxItemOutcome(target, true),
                var answer => new InboxItemOutcome(target, false, answer.Reason),
            })
            .DefaultIfEmpty(new InboxItemOutcome(target, false, noAnswer))
            .Catch((TimeoutException _) => Observable.Return(new InboxItemOutcome(target, false, noAnswer)));
    }

    private static IObservable<InboxItemOutcome> Dismiss(IMessageHub hub, AccessService? access, AccessContext? caller, string notificationPath)
    {
        // Built after the round trip, where the ambient identity is gone — re-established for the write.
        using (access?.SwitchAccessContext(caller))
            return InboxItemHandlers.Write<Notification>(hub, notificationPath, n => n with { Dismissed = true, IsRead = true }, n => n.Dismissed);
    }
}
