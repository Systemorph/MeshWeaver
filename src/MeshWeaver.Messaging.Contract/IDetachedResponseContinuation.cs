namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>A request whose RESPONSE continuation must not resume on the responding hub's action
/// block</b> — the opt-in that stops one hub's queue from serialising work that has nothing to do
/// with it (#2543).
///
/// <para><b>Why a continuation ends up there at all.</b> A response subject is signalled from
/// INSIDE the turn that handled the response, and Rx runs a continuation on whichever thread
/// signalled it. So the whole remainder of every <c>hub.Observe(x).SelectMany(…)</c> chain executes
/// on the RESPONDING hub's action block, inside that turn — and a hub processes one turn at a
/// time, so the turn cannot end until the chain does.</para>
///
/// <para><b>What that costs, measured.</b> On <c>portal/nodeops</c> — the mesh's ONE node-CRUD
/// execution hub — a create correctly returns <c>Processed()</c> in about a millisecond and detaches
/// its pipeline. The pipeline then does a partition bootstrap whose nested response comes back to
/// the SAME hub, and the rest of the create (validators, save, change feed) ran inline in that
/// response's turn. With one create parked in its validator, a second, unrelated create sat
/// unprocessed at <c>Queue(buffer=1,…) Executing(CreateNodeResponse, …)</c> for the whole budget:
/// every node write in the mesh serialised behind one create's continuation. Delete, move and copy
/// inherit it identically.</para>
///
/// <para>🚨 <b>Why this is an OPT-IN and not the default.</b> Hopping every response continuation
/// off the block was tried and is not safe: the action block is not only a serialiser, it is an
/// ERROR BOUNDARY and a DISPOSAL FENCE, and it is where an impersonation <c>AsyncLocal</c> is
/// still in scope. Moving every continuation off it crashed the test host repeatedly and broke
/// several invariants that had never been written down. So the hop is declared per REQUEST TYPE, by
/// the requests whose continuations demonstrably must not hold a shared execution hub, and every
/// other request keeps the semantics it has always had.</para>
///
/// <para>A marker: no members, nothing to implement. Requests declare it; the hub reads it
/// (<c>MessageHub.Observe</c>).</para>
/// </summary>
public interface IDetachedResponseContinuation;
