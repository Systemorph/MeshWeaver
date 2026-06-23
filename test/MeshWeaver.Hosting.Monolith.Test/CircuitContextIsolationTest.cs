using System.Threading.Tasks;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the cross-user contamination ROOT CAUSE behind the 2026-06-24 atioz "boom" wedge:
/// <see cref="AccessService.CircuitContext"/> falls back to the process-wide instance field
/// <c>persistentCircuitContext</c> (AccessService.cs:44/69) whenever the <c>circuitContext</c>
/// AsyncLocal is null — which is the case on ANY scheduler / Rx hop. <see cref="AccessService.SetCircuitContext"/>
/// writes that shared field UNCONDITIONALLY (AccessService.cs:104), so on a shared host user B's
/// circuit activity clobbers it globally and user A's hopped read observes <b>B</b>. That wrong
/// identity then enters the infra read/post seams (MeshService.CaptureContext, the PostPipeline,
/// the cache RLS gate) and faults a shared hub → FAILED → the node wedges for its owner.
///
/// <para>This is a pure <c>AccessService</c> unit repro (no mesh): it is RED today and GREEN after
/// Stage 5 of the AccessContext-forwarding plan removes the shared fallback. See
/// MeshNodeStreamCache.md and the plan.</para>
/// </summary>
public class CircuitContextIsolationTest
{
    [Fact(Timeout = 20_000, Skip = "Pinned repro — RED until Stage 5 of the AccessContext-forwarding plan " +
        "removes the persistentCircuitContext shared field. Confirmed RED 2026-06-24 (Observed ObjectId='userB'). " +
        "Un-skip when Stage 5 lands.")]
    public async Task CircuitContext_DoesNotLeakAnotherUserAcrossAnAsyncLocalHop()
    {
        var access = new AccessService();

        // User B's circuit activity stamps its identity on B's OWN execution context (a child
        // Task). SetCircuitContext writes the AsyncLocal AND the shared persistentCircuitContext
        // field. The AsyncLocal change does NOT flow back to this method (ExecutionContext
        // copy-on-write) — but the shared field write IS globally visible.
        await Task.Run(() =>
            access.SetCircuitContext(new AccessContext { ObjectId = "userB", Name = "User B" }));

        // Back on THIS context (user A's logical flow): circuitContext.Value is null here because
        // B's set happened in a child context that didn't flow back — exactly the workspace-emission
        // / Rx-hop thread where the AsyncLocal is null. CircuitContext therefore evaluates
        // `null ?? persistentCircuitContext`.
        var observed = access.CircuitContext;

        // BUG: observed.ObjectId == "userB" (the shared field leaked B into A's flow).
        // FIX (Stage 5 removes the field): observed is null (no cross-user fallback).
        Assert.True(observed?.ObjectId != "userB",
            "AccessService.CircuitContext must NOT surface another user's identity via the shared " +
            "persistentCircuitContext field when the AsyncLocal is null on a hopped thread — that is the " +
            $"cross-user wedge. Observed ObjectId='{observed?.ObjectId ?? "(null)"}'.");
    }
}
