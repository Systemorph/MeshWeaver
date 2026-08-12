using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the cross-user contamination ROOT CAUSE behind the 2026-06-24 prod "boom" wedge:
/// <see cref="AccessService.CircuitContext"/> used to fall back to a process-wide instance field
/// (<c>persistentCircuitContext</c>) whenever the <c>circuitContext</c> AsyncLocal was null —
/// which is the case on ANY scheduler / Rx hop, i.e. on every hub action-block thread, every
/// IIoPool worker and every background service. <c>SetCircuitContext</c> wrote that shared field
/// unconditionally, and <c>CircuitAccessHandler</c> calls it on every Blazor inbound activity of
/// every circuit — so on a shared host user B's keystroke clobbered it globally and user A's
/// (or an anonymous visitor's) hopped read observed <b>B</b>. That wrong identity then entered
/// the RLS seams (<c>AccessControlPipeline.ResolveIdentity</c>, the query providers'
/// <c>GetEffectiveUserId</c>, <c>MeshNodeStreamCache</c>'s read gate, the PostPipeline).
///
/// <para>The field is gone. <c>SetCircuitContext</c> now writes ONLY the AsyncLocal, so identity
/// resolution fails CLOSED off the circuit's own call tree. The two legitimate "identity must
/// survive every hop" cases are served by correctly-scoped APIs instead:
/// <see cref="AccessService.SetHostIdentity"/> (a single-identity process — MAUI device client,
/// xUnit host) and <see cref="AccessService.SetStandingIdentity"/> (per-hub owner injection).</para>
/// </summary>
public class CircuitContextIsolationTest
{
    [Fact(Timeout = 20_000)]
    public async Task CircuitContext_DoesNotLeakAnotherUserAcrossAnAsyncLocalHop()
    {
        var access = new AccessService();

        // User B's circuit activity stamps its identity on B's OWN execution context (a child
        // Task) — exactly what CircuitAccessHandler's inbound-activity handler does. The
        // AsyncLocal change does not flow back to this method (ExecutionContext copy-on-write).
        await Task.Run(() =>
            access.SetCircuitContext(new AccessContext { ObjectId = "userB", Name = "User B" }));

        // Back on THIS context (user A's logical flow): circuitContext.Value is null here —
        // exactly the workspace-emission / Rx-hop thread where the AsyncLocal is null.
        var observed = access.CircuitContext;

        Assert.True(observed is null,
            "AccessService.CircuitContext must NOT surface another user's identity when the " +
            "AsyncLocal is null on a hopped thread — it must fail closed so RLS denies rather " +
            $"than answering as a stranger. Observed ObjectId='{observed?.ObjectId ?? "(null)"}'.");
    }

    [Fact(Timeout = 20_000)]
    public async Task SetCircuitContext_DoesNotEstablishAProcessWideIdentity()
    {
        var access = new AccessService();
        access.SetCircuitContext(new AccessContext { ObjectId = "userA", Name = "User A" });

        // A hub action block / IIoPool worker / background service runs work that did NOT capture
        // user A's ExecutionContext — it was queued by someone else, or by the hub itself.
        // SuppressFlow models exactly that: the continuation starts without A's AsyncLocals.
        // (A plain Task.Run would inherit them, which is correct and not the bug.)
        // The work is QUEUED inside the suppressed scope (so it captures no ExecutionContext) and
        // awaited outside it — an await inside the scope cannot restore flow and throws.
        Task<AccessContext?> foreignRead;
        using (ExecutionContext.SuppressFlow())
            foreignRead = Task.Run(() => access.CircuitContext);
        var observedOnForeignFlow = await foreignRead;

        Assert.True(observedOnForeignFlow is null,
            "a circuit's identity must stay inside its own call tree; publishing it process-wide " +
            "is what let one user's identity answer another user's — and an anonymous visitor's — " +
            $"read. Observed ObjectId='{observedOnForeignFlow?.ObjectId ?? "(null)"}'.");
    }

    [Fact(Timeout = 20_000)]
    public async Task SetHostIdentity_SurvivesHops_ForSingleIdentityHosts()
    {
        // The MAUI device client and the xUnit test host serve exactly one user and have no
        // circuit, so their standing identity legitimately survives every hop. This is the
        // contract TestUsers.DevLogin and MauiProgram depend on.
        var access = new AccessService();
        access.SetHostIdentity(new AccessContext { ObjectId = "device-user", Name = "Device User" });

        // SuppressFlow so the assertion cannot pass merely by inheriting this flow's AsyncLocal —
        // it must be the standing host identity that answers on a foreign continuation.
        Task<AccessContext?> read;
        using (ExecutionContext.SuppressFlow())
            read = Task.Run(() => access.CircuitContext);
        var observed = await read;

        Assert.Equal("device-user", observed?.ObjectId);

        access.ClearHostIdentity();
        Task<AccessContext?> readAfterClear;
        using (ExecutionContext.SuppressFlow())
            readAfterClear = Task.Run(() => access.CircuitContext);
        var afterClear = await readAfterClear;
        Assert.True(afterClear is null,
            "clearing the host identity must fail closed, not revert to a stale value. Observed " +
            $"ObjectId='{afterClear?.ObjectId ?? "(null)"}'.");
    }
}

/// <summary>
/// Owner injection must be scoped to the hub that owns the node — never process-wide. A per-node
/// / thread / activity hub carries its node OWNER so deferred sync writes (where both AsyncLocals
/// are wiped) are attributed to the owner instead of failing closed. Before this fix that standing
/// identity was the SAME shared field as the circuit fallback, so the owner of whichever thread
/// hub activated last became the ambient identity for every other hub, every other user, and every
/// anonymous render in the process.
/// </summary>
public class StandingIdentityIsolationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30_000)]
    public void StandingIdentity_IsScopedToItsOwnHub()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var hubA = GetClient();
        var hubB = GetClient();

        access.SetStandingIdentity(hubA, new AccessContext { ObjectId = "ownerA", Name = "Owner A" });

        Assert.Equal("ownerA", access.GetStandingIdentity(hubA)?.ObjectId);
        Assert.True(access.GetStandingIdentity(hubB) is null,
            "hub B's owner identity must never resolve to hub A's owner — that cross-hub bleed " +
            "attributed one user's deferred writes to another. Observed " +
            $"ObjectId='{access.GetStandingIdentity(hubB)?.ObjectId ?? "(null)"}'.");
    }

    [Fact(Timeout = 30_000)]
    public void StandingIdentity_DoesNotBecomeTheAmbientCircuitContext()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var hubA = GetClient();

        access.SetStandingIdentity(hubA, new AccessContext { ObjectId = "ownerA", Name = "Owner A" });

        // Owner injection is a per-hub lookup, never an ambient fallback: a reader that does not
        // name a hub must not pick it up.
        Assert.Equal("ownerA", access.GetStandingIdentity(hubA)?.ObjectId);
        Assert.True(access.CircuitContext?.ObjectId != "ownerA",
            "a hub's standing owner must not leak into the ambient identity used by unrelated readers");
    }

    [Fact(Timeout = 30_000)]
    public void StandingIdentity_RefusesHubShapedPrincipals()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var hubA = GetClient();

        access.SetStandingIdentity(hubA, new AccessContext { ObjectId = "sync/abc", Name = "sync/abc" });

        Assert.True(access.GetStandingIdentity(hubA) is null,
            "a hub address is not a user identity — recording one would re-introduce the CreatedBy=sync/xxx leak");
    }
}
