using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// <see cref="ControlLaneOperation.Reboot"/> on the target — files ONE <see cref="InstanceRebootRequest"/>
/// there through <see cref="InstanceReboot.Request"/> (as System, the executor's trust boundary), carrying
/// the control-side requester as <c>requestedBy</c> (Doc/Architecture/InstanceReboot). The plan is FIXED —
/// one step, the same for every reboot of a deployment (<see cref="PlanFor"/>) — so the control instance
/// binds its digest without a dry run, and the requester's own act is the signature: the control side
/// sends <c>approvedBy</c> = the requester. The lane's run ends when the request is FILED; the reboot then
/// reports on its own node on the target (a reboot restarts the very process that would otherwise have
/// to report its end).
/// </summary>
public sealed class RebootOperation : IControlLaneOperation
{
    /// <summary>The one target every reboot names: the request namespace on the target.</summary>
    public const string Target = InstanceRebootRequest.Namespace;

    /// <inheritdoc />
    public string Operation => ControlLaneOperation.Reboot;

    /// <inheritdoc />
    public string? ShapeRefusal(ControlLaneRequest request) =>
        string.Equals(request.Target, Target, StringComparison.Ordinal)
            ? null
            : $"a reboot names the target '{Target}' (where its request is filed), not '{request.Target}'";

    /// <summary>The fixed plan of a reboot of <paramref name="deployment"/> — identical on both sides of the lane. Pure.</summary>
    public static ControlLanePlan PlanFor(string deployment) =>
        ControlLanePlan.OfSteps(ControlLaneOperation.Reboot, deployment,
        [
            new ControlLanePlanStep
            {
                Name = "File the reboot",
                Command = $"InstanceReboot.Request as system at {Target}/{{id}} — the target's executor then syncs every module "
                          + "source, lands every module, picks the newest admitted image, takes ONE roll/restart and verifies",
                Destructive = false,
            },
        ]);

    /// <inheritdoc />
    public IObservable<ControlLanePreparation> Prepare(IMessageHub hub, ControlLaneRequest request)
    {
        var plan = PlanFor(request.Deployment);
        return Observable.Return(new ControlLanePreparation(plan, () =>
            InstanceReboot.Request(hub, new InstanceRebootRequest
                {
                    Reason = ControlLaneText.ReasonLine(request),
                    RequestedBy = ControlLaneText.Or(request.RequestedBy, "(unattributed control-lane requester)"),
                    Trigger = InstanceRebootTrigger.Person,
                })
                .Select(ticket => ticket.Accepted
                    ? $"[Reboot] filed {ticket.Path} on '{request.Deployment}' through the control lane — requested by "
                      + $"{ControlLaneText.Or(request.RequestedBy, "(unattributed)")}; plan {ControlLaneText.Short(plan.Digest())}; "
                      + $"request {request.RequestId}. The reboot reports on that node."
                    : throw new InvalidOperationException($"the reboot could not be filed: {ticket.Refusal}"))));
    }
}
