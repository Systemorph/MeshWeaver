using System.Reactive.Linq;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// The domain side of a published action: the one thing that knows what an
/// <see cref="ActionInvocation"/> with its key MEANS. A publisher resolves it by key and version and
/// never learns the domain.
///
/// <para>🚨 <b>On a portal a handler is DECLARED as a mesh node, never registered as a service.</b>
/// Every handler a portal resolves comes from an <see cref="ActionHandlerRule"/> node, read live from
/// the mesh (<see cref="ActionHandlerRules.Bind"/>), so content the mesh compiles — a package that is
/// not a compiled module — contributes one by writing a node. This interface is what the pure
/// <see cref="ActionHandlerResolution.Resolve"/> chooses among; the rule binding implements it.</para>
///
/// <para>🚨 <b>There is no "execute" member, and that is the contract.</b> A handler validates a
/// publication and prepares a recipient — both READ-ONLY — and answers where the person goes next.
/// Whatever the action then changes is changed on the handler's own typed node through the mesh's
/// one mutation API (<c>GetMeshNodeStream(path).Update(…)</c>), by the authenticated person
/// confirming on that node's page, under the node type's own access rules. Nothing here can be made
/// to run a script, pick a hub, or act as another identity, because nothing here mutates.</para>
///
/// <para>🚨 <b>Reactive end to end, and every returned observable is COLD.</b> Nothing here returns
/// a <c>Task</c>; the work runs on <c>Subscribe</c>. An implementation that needs IO bridges it
/// through the mesh's bounded <c>IIoPool</c>.</para>
///
/// <para>Both calls ANSWER; they do not fault for an ordinary refusal. An expired offer, an
/// ineligible recipient and an unreadable payload are outcomes with a status word.</para>
/// </summary>
public interface IActionHandler
{
    /// <summary>
    /// What this handler answers to and can offer on this portal now. Read each time it is needed:
    /// <see cref="ActionHandlerCapability.Capabilities"/> reflects the deployment's current state.
    /// </summary>
    ActionHandlerCapability Capability { get; }

    /// <summary>
    /// Whether <paramref name="invocation"/> may be PUBLISHED by this publisher to this site —
    /// asked before a link exists, so a publication the destination cannot honour fails with a
    /// named outcome instead of producing a link that later dead-ends. Read-only.
    ///
    /// <para>The default answers the structural check alone
    /// (<see cref="ActionHandlerCapability.Supports"/>). A handler whose domain restricts WHO may
    /// distribute an action — the right to hand out a coupon is not the right to run an activity —
    /// overrides it and answers <see cref="ActionOutcomeStatus.Unauthorized"/> itself.</para>
    /// </summary>
    /// <param name="invocation">The invocation to be published.</param>
    /// <param name="context">Who publishes, to which site, under which request id.</param>
    /// <returns>A cold observable emitting one outcome.</returns>
    IObservable<ActionOutcome> Validate(ActionInvocation invocation, ActionPublicationContext context) =>
        Observable.Return(Capability.Supports(invocation));

    /// <summary>
    /// Prepares <paramref name="invocation"/> for one authenticated recipient and answers where
    /// they go: <see cref="ActionOutcomeStatus.Ready"/> with the handler's own review page in
    /// <see cref="ActionOutcome.Navigation"/>, <see cref="ActionOutcomeStatus.Completed"/> with the
    /// existing result when this recipient already finished, or a refusal.
    ///
    /// <para>🚨 READ-ONLY. This is what a scan, a refresh, a link preview and a sign-in callback
    /// reach, any number of times. It grants nothing, reserves nothing, saves nothing and charges
    /// nothing; calling it twice is indistinguishable from calling it once.</para>
    ///
    /// <para>The caller has already established admission
    /// (<see cref="ActionRecipientContext.Admission"/>) and structural support. The handler
    /// re-validates its own domain — the offer still exists, the recipient is eligible — from
    /// current state.</para>
    /// </summary>
    /// <param name="invocation">The published invocation.</param>
    /// <param name="context">The authenticated recipient, the serving site and the link followed.</param>
    /// <returns>A cold observable emitting one outcome.</returns>
    IObservable<ActionOutcome> Prepare(ActionInvocation invocation, ActionRecipientContext context);
}

/// <summary>
/// The handler chosen for one invocation, or the outcome that says why none was.
/// </summary>
public sealed record ActionHandlerResolution
{
    /// <summary>The handler, when <see cref="Outcome"/> is <see cref="ActionOutcomeStatus.Ready"/>; otherwise null.</summary>
    public IActionHandler? Handler { get; init; }

    /// <summary>The verdict: <see cref="ActionOutcomeStatus.Ready"/>, or the named reason no handler was chosen.</summary>
    public required ActionOutcome Outcome { get; init; }

    /// <summary>
    /// Chooses the handler for <paramref name="invocation"/> among <paramref name="handlers"/>, by
    /// key and version, and fails CLOSED with a named outcome otherwise. Pure — it reads nothing
    /// but its arguments, so the same verdict comes from the live rule chain, a list a test builds,
    /// or any other source.
    ///
    /// <para>In order: a foreign envelope version is
    /// <see cref="ActionOutcomeStatus.UnsupportedContract"/>; no handler under the key is
    /// <see cref="ActionOutcomeStatus.UnknownHandler"/>; a key match that does not read the version
    /// is <see cref="ActionOutcomeStatus.UnsupportedVersion"/> (the detail lists what IS
    /// supported); among the handlers that read the key and version the LOWEST
    /// <see cref="ActionHandlerCapability.Order"/> is chosen, and two sharing that lowest order is
    /// <see cref="ActionOutcomeStatus.AmbiguousHandler"/> — neither is picked by registration or
    /// list order; a missing required capability on the chosen handler is
    /// <see cref="ActionOutcomeStatus.MissingCapability"/>.</para>
    /// </summary>
    /// <param name="handlers">Every handler on this portal — on a live mesh, the chain bound from its rule nodes.</param>
    /// <param name="invocation">The invocation to resolve.</param>
    /// <returns>The resolution.</returns>
    public static ActionHandlerResolution Resolve(IEnumerable<IActionHandler> handlers, ActionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.ContractVersion < 1 || invocation.ContractVersion > ActionInvocation.CurrentContractVersion)
            return Unresolved(ActionOutcome.Of(ActionOutcomeStatus.UnsupportedContract, invocation,
                $"contract {invocation.ContractVersion}; this build reads 1..{ActionInvocation.CurrentContractVersion}"));

        var keyed = handlers
            .Select(handler => (Handler: handler, handler.Capability))
            .Where(x => string.Equals(x.Capability.HandlerKey, invocation.HandlerKey, StringComparison.Ordinal))
            .ToArray();
        if (keyed.Length == 0)
            return Unresolved(ActionOutcome.Of(ActionOutcomeStatus.UnknownHandler, invocation));

        var versioned = keyed
            .Where(x => invocation.HandlerVersion >= x.Capability.MinVersion
                        && invocation.HandlerVersion <= x.Capability.MaxVersion)
            .ToArray();
        if (versioned.Length == 0)
            return Unresolved(ActionOutcome.Of(ActionOutcomeStatus.UnsupportedVersion, invocation,
                "supported " + string.Join(", ",
                    keyed.Select(x => $"{x.Capability.MinVersion}..{x.Capability.MaxVersion}"))));
        var first = versioned.Min(x => x.Capability.Order);
        var chosen = versioned.Where(x => x.Capability.Order == first).ToArray();
        if (chosen.Length > 1)
            return Unresolved(ActionOutcome.Of(ActionOutcomeStatus.AmbiguousHandler, invocation,
                $"{chosen.Length} handlers claim this key and version at order {first}"));

        var outcome = chosen[0].Capability.Supports(invocation);
        return outcome.Status == ActionOutcomeStatus.Ready
            ? new ActionHandlerResolution { Handler = chosen[0].Handler, Outcome = outcome }
            : Unresolved(outcome);
    }

    private static ActionHandlerResolution Unresolved(ActionOutcome outcome) => new() { Outcome = outcome };
}
