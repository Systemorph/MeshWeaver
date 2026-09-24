using MeshWeaver.Layout;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 WHY a release request did not start — the failure CLASS, decided at the arm that holds the
/// exception and carried to the caller so the class can reach the log TEMPLATE.
///
/// <para><b>What this exists for (#1549).</b> Every release refusal used to reach the recompile
/// channel as a bare sentence and was logged through ONE template —
/// <c>"[Recompile] Release request for {Path} failed: {Message}"</c> — with the whole reason inside
/// a structured parameter. An incident fingerprint discriminates on the fault's own words, and
/// those words are the FIRST body line of the burst
/// (<c>Doc/Architecture/ReleaseFailureClasses</c>): a second line — <c>Object name:
/// 'MeshNodeStreamCache'</c> — never reaches it at all. Four unrelated conditions therefore arrived
/// as one ticket that could not be closed, because closing it against any one cause was
/// immediately followed by a recurrence on a different one: a base-state stall, a routing
/// no-verdict, a host teardown, and a release requested for a node that does not exist — which is
/// not a defect at all.</para>
///
/// <para>🚨 <b>The class is decided where the exception is, never recovered from the text.</b> A
/// consumer that pattern-matched the reason string would re-derive, badly, what the producing arm
/// already knew — and would silently mis-file the day a message is reworded. Every arm of
/// <see cref="NodeTypeReleaseExtensions.ObserveNodeTypeRelease(MeshWeaver.Messaging.IMessageHub,string,bool,string,System.Action{string})"/> states its own class, and the
/// trigger-write arm — the only one whose shape varies — asks
/// <see cref="NodeTypeReleaseFailureClassifier"/>, which reads the TYPED
/// <see cref="MeshNodeErrorCode"/> first and the shared <see cref="AreaErrorClassifier"/>
/// predicates second. No second classifier is written here.</para>
///
/// <para><see cref="Unclassified"/> is named on purpose: a shape no arm claims must be visible as
/// exactly that, not absorbed by whichever class happens to sit last in the chain.</para>
/// </summary>
public enum NodeTypeReleaseFailure
{
    /// <summary>
    /// 🚨 The explicit fallback: a trigger-write fault whose shape NO rule recognises. It is its own
    /// class so the bucket that collects unnamed shapes is visible and shrinks as shapes get names —
    /// rather than an existing class quietly growing to mean "and everything else".
    /// </summary>
    Unclassified = 0,

    /// <summary>No NodeType path was supplied — a caller defect, not a mesh condition.</summary>
    NoNodeTypePath,

    /// <summary>
    /// The caller does not hold <see cref="Mesh.Security.Permission.Compile"/> on the target. NOT a
    /// defect: the gate answered, correctly, and nothing was released.
    /// </summary>
    CompileDenied,

    /// <summary>
    /// The permission check itself faulted — nothing was decided about the caller's rights, so this
    /// is neither a grant nor a denial. The arm is the class here: the remedy is that the check
    /// could not RUN, whatever shape the fault took.
    /// </summary>
    PermissionCheckFailed,

    /// <summary>
    /// The permission check ENDED without answering — it neither granted, nor denied, nor faulted.
    /// 🚨 Distinct from <see cref="CompileDenied"/> on purpose: rendering a no-verdict as a denial is
    /// what #974 forbids, because nothing was decided about the caller's rights. Distinct from
    /// <see cref="PermissionCheckFailed"/> too — there is no exception to look at, which is a
    /// different investigation.
    /// </summary>
    PermissionCheckNoVerdict,

    /// <summary>
    /// There is no node at the path — routing answered <c>NotFound</c>, or the owner did. NOT a
    /// defect in the release path: a release was requested for something that does not exist, and
    /// the fix is at whoever asked.
    /// </summary>
    NodeMissing,

    /// <summary>
    /// The owning per-node hub reached NO VERDICT on the trigger write — unreachable, or it never
    /// answered. The patch may still apply and is NOT confirmed; the assembly stays as it was.
    /// </summary>
    OwnerUnreachable,

    /// <summary>
    /// The owning activation was disposing or had not finished loading — the write provably never
    /// applied, and the address typically comes straight back. A lifecycle event, not a fault.
    /// </summary>
    OwnerRecycling,

    /// <summary>The owner refused the patch: the caller may not write this node.</summary>
    WriteDenied,

    /// <summary>
    /// The owner refused the merged value — a concurrency conflict, a validation rejection, or
    /// content it could not materialize. The write was DECIDED and rejected.
    /// </summary>
    WriteRejected,

    /// <summary>
    /// The write's base read ran out its own bound: no initial state for the node ever arrived.
    /// 🚨 This is #1549's ORIGINAL cause and the one #1990 addressed, so it must stay separable
    /// from every routing timeout that shares its exception type — the terminal carries a census
    /// saying which silence it waited on (<c>Doc/Architecture/BaseStateTimeoutCensus</c>).
    /// </summary>
    BaseStateNeverArrived,

    /// <summary>
    /// The host was tearing down under the in-flight write — a disposed stream cache, a disposed
    /// hub. Silent between rolls and loud during one; the content landed either way.
    /// </summary>
    HostTearingDown,

    /// <summary>The data store could not be reached — infrastructure, not this NodeType.</summary>
    StorageUnavailable,

    /// <summary>
    /// A transient hub/routing miss — a request that timed out, a target hub not yet addressable, a
    /// hub announcing its own shutdown. Self-healing shapes, named so they are not read as defects.
    /// </summary>
    TransientHubFailure,

    /// <summary>
    /// The composed leg produced NO answer inside its ordered bound — neither an emission, nor a
    /// fault, nor a completion (<see cref="NodeTypeReleaseExtensions.ReleaseRequestBound"/>). A
    /// non-terminating leg, which is a different defect from every bounded failure above.
    /// </summary>
    NoAnswerWithinBound,

    /// <summary>
    /// 🚨 THIS host is LEAVING (<c>hub.IsLeaving()</c>: the hub is shutting down, or the process
    /// has begun stopping), so the trigger write was never possible from here — the router
    /// refuses every outbound route with <c>"Host/Mesh is shutting down, cannot route to …"</c>
    /// (issue #5629). Decided two ways, both from evidence in hand: the release leg declines
    /// BEFORE it writes when the hub is already leaving, and a write that raced past that check
    /// and came back with the router's shutdown refusal is classified here while the leaving
    /// probe still answers yes.
    ///
    /// <para>Distinct from <see cref="HostTearingDown"/> (a disposed dependency UNDER an in-flight
    /// write) and from <see cref="TransientHubFailure"/> (a miss a retry from this host can
    /// recover): nothing on this host can land the write, and the release is NOT requested — the
    /// type keeps the assembly it already had until a release is requested from a pod that
    /// stays. Logged at Warning, not Error: a pod that is leaving is not a defect, and it is the
    /// one class whose level was decided with it (Doc/Architecture/NodeTypeCompilation → "A
    /// LEAVING pod never touches shared NodeType state").</para>
    /// </summary>
    HostLeaving
}

/// <summary>
/// One release refusal, as the producing arm knows it: the target, the CLASS, and the same sentence
/// the caller's <c>onError</c> sink receives.
///
/// <para>Carried on an ADDITIVE sink (<c>onRefused</c>) rather than by changing <c>onError</c>,
/// because <c>Action&lt;string&gt;? onError</c> is public with callers in this repository and in
/// <c>MeshWeaver.Plugins</c> (its release wave, its provisioning flow and its tests) — and one of
/// those callers is in-mesh C# that no compiler in either repository ever sees.</para>
/// </summary>
/// <param name="NodeTypePath">The NodeType the release was requested for.</param>
/// <param name="Failure">The class, decided at the arm that holds the exception.</param>
/// <param name="Reason">The human-readable sentence — byte for byte what <c>onError</c> received,
/// so a reader can never be shown two different diagnoses of one refusal.</param>
public sealed record NodeTypeReleaseRefusal(
    string NodeTypePath,
    NodeTypeReleaseFailure Failure,
    string Reason);

/// <summary>
/// The ONE rule that turns a trigger-write fault into a <see cref="NodeTypeReleaseFailure"/>.
///
/// <para>🚨 It reads classifications that already exist and writes none of its own: the typed
/// <see cref="MeshNodeErrorCode"/> the owner put on the wire, then the shared
/// <see cref="AreaErrorClassifier"/> predicates the layout layer already uses for exactly these
/// shapes. A second classifier here would drift from those two the first time either moves, and the
/// drift would be invisible — both would keep answering, differently.</para>
/// </summary>
internal static class NodeTypeReleaseFailureClassifier
{
    /// <summary>
    /// Classifies the exception that ended a trigger write. Order is load-bearing and is documented
    /// inline: the typed code wins over every text rule, and every specific rule wins over the
    /// broad ones that would otherwise swallow it.
    /// </summary>
    /// <param name="ex">The fault the write arm caught; may be null.</param>
    /// <param name="scopeDisposed">🚨 Probe — has the CALLING host's own DI scope gone? A bare
    /// <see cref="ObjectDisposedException"/> means "something was disposed", which is a teardown race
    /// only when the host is actually tearing down; while the scope is ALIVE it is a genuine disposal
    /// defect and must keep reporting as one. That is the existing contract on
    /// <see cref="AreaErrorClassifier.IsHubDisposalRace(Exception?, Func{bool}?)"/> and
    /// <c>ScopeTeardown.IsScopeTeardown</c>, and it matters more here than anywhere: labelling
    /// a real disposal bug <see cref="NodeTypeReleaseFailure.HostTearingDown"/> would fold it under
    /// the teardown family's incident, which is the exact mistake this taxonomy exists to end. A null
    /// probe keeps the typed-only answer, so an unrecognised
    /// <see cref="ObjectDisposedException"/> lands in <see cref="NodeTypeReleaseFailure.Unclassified"/>
    /// — loud, and on nobody else's ticket. Callers that hold a hub pass
    /// <c>hub.IsServiceScopeDisposed</c>.</param>
    /// <param name="hostLeaving">🚨 Probe — is the CALLING host leaving (<c>hub.IsLeaving</c>)? It
    /// turns the router's shutdown refusal into <see cref="NodeTypeReleaseFailure.HostLeaving"/>
    /// only while it answers yes; a null probe leaves that refusal to the rules below, exactly as
    /// before (issue #5629).</param>
    internal static NodeTypeReleaseFailure ClassifyTriggerWriteFault(
        Exception? ex,
        Func<bool>? scopeDisposed = null,
        Func<bool>? hostLeaving = null)
    {
        if (ex is null)
            return NodeTypeReleaseFailure.Unclassified;

        // 0. 🚨 #5629 — the ROUTER's own shutdown refusal on a host that IS leaving. It outranks
        //    even the typed code, because the owner never saw this write: the refusal was minted
        //    locally ("Host/Mesh is shutting down, cannot route to …") and arrives wrapped as
        //    MeshNodeErrorCode.Unknown, which says nothing. Both halves are required — the text
        //    alone is also what a DIFFERENT, still-serving host would report about ITS peer, and
        //    the probe alone would claim every unrelated fault that happens to land during a drain.
        if (hostLeaving?.Invoke() == true && IsRouterShutdownRefusal(ex))
            return NodeTypeReleaseFailure.HostLeaving;

        // 1. The owner's OWN classification, typed, from the wire. It outranks every text rule for
        //    the reason MeshNodeErrorCode exists: the producer classified once, where the condition
        //    was known. Code Unknown is the reserved zero and says nothing, so it deliberately
        //    falls through to the shape rules rather than becoming a class of its own.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is not MeshNodeStreamException { Error: { } error })
                continue;
            switch (error.Code)
            {
                case MeshNodeErrorCode.NotFound:
                    return NodeTypeReleaseFailure.NodeMissing;
                case MeshNodeErrorCode.AccessDenied:
                    return NodeTypeReleaseFailure.WriteDenied;
                case MeshNodeErrorCode.OwnerUnreachable:
                    return NodeTypeReleaseFailure.OwnerUnreachable;
                case MeshNodeErrorCode.OwnerDisposing:
                case MeshNodeErrorCode.OwnerNotReady:
                    return NodeTypeReleaseFailure.OwnerRecycling;
                case MeshNodeErrorCode.Conflict:
                case MeshNodeErrorCode.Validation:
                case MeshNodeErrorCode.Deserialization:
                    return NodeTypeReleaseFailure.WriteRejected;
                case MeshNodeErrorCode.Unknown:
                default:
                    break;
            }
        }

        // 2. Teardown, before anything else untyped — the typed HubDisposingException, OR a bare
        //    ObjectDisposedException while the host's own scope is PROVABLY gone. The shape #1549
        //    last reopened on (a disposed MeshNodeStreamCache during a roll) is the second form; a
        //    disposed dependency while the scope is alive is deliberately NOT this class.
        if (AreaErrorClassifier.IsHubDisposalRace(ex, scopeDisposed))
            return NodeTypeReleaseFailure.HostTearingDown;

        // 3. "The node is gone" before "access" and before "transient": a routing NotFound is a
        //    definite answer, and retrying it forever is the storm this codebase has paid for.
        if (AreaErrorClassifier.IsNodeGoneNotFound(ex))
            return NodeTypeReleaseFailure.NodeMissing;

        // 4. A denial is a decision; an availability failure is the ABSENCE of one. Both predicates
        //    already short-circuit each other in the classifier, so neither can claim the other's
        //    case — the order here only documents which question is asked first.
        if (AreaErrorClassifier.IsAccessDenied(ex))
            return NodeTypeReleaseFailure.WriteDenied;
        if (AreaErrorClassifier.IsAvailabilityFailure(ex))
            return NodeTypeReleaseFailure.OwnerUnreachable;

        // 5. The store, before the generic transient rule — a connect fault is infrastructure with
        //    its own remedy, and the bounded retry that covers it has already run and lost.
        if (AreaErrorClassifier.IsStorageUnavailable(ex))
            return NodeTypeReleaseFailure.StorageUnavailable;

        // 6. 🚨 BEFORE the transient rule, and this order is the whole point of the class: the
        //    base-state terminal IS a TimeoutException, so IsTransientHubFailure would claim it and
        //    #1549's original cause would become indistinguishable from any routing timeout.
        if (MeshNodeStreamHandle.IsBaseStateTimeout(ex))
            return NodeTypeReleaseFailure.BaseStateNeverArrived;

        if (AreaErrorClassifier.IsTransientHubFailure(ex))
            return NodeTypeReleaseFailure.TransientHubFailure;

        return NodeTypeReleaseFailure.Unclassified;
    }

    /// <summary>
    /// The routing layer's refusal to route anything out of a stopping process — minted by
    /// <c>OrleansRoutingService</c> (<c>"Host is shutting down, cannot route to …"</c>) and
    /// <c>MonolithRoutingService</c> (<c>"Mesh is shutting down, cannot route to …"</c>). Neither
    /// carries an owner banner (<see cref="MeshWeaver.Messaging.ShutdownNack.IsAnsweredByOwner"/>):
    /// the subject is the host, never the address.
    /// </summary>
    private static bool IsRouterShutdownRefusal(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains(" is shutting down, cannot route to ", StringComparison.Ordinal))
                return true;
        return false;
    }
}
