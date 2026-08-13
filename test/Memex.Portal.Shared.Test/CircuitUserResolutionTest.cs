using MeshWeaver.Blazor.Portal;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the click-time identity contract behind the AI menu's "New thread" entry (and every other
/// portal-chrome action that resolves "who is the signed-in user" in a Blazor event handler).
/// <para>
/// Why this exists: the entry was reported dead — clicking it did nothing. The handler resolved the
/// user from <c>AccessService.Context</c> alone, but that AsyncLocal is populated per HUB message
/// delivery; a UI click is a circuit inbound activity, where <c>CircuitAccessHandler</c> stamps
/// <c>CircuitContext</c> instead. So <c>Context</c> read null and the handler early-returned without
/// a trace. These tests pin the resolution ORDER (CircuitContext first) and the principal filtering,
/// so the regression cannot ship silently again.
/// </para>
/// </summary>
public class CircuitUserResolutionTest
{
    private static AccessContext User(string id) => new() { ObjectId = id, Name = id };

    [Fact]
    public void Resolves_From_CircuitContext_When_Context_Is_Null()
    {
        // The click-time shape: CircuitAccessHandler stamped the circuit user; no delivery is active.
        var access = new AccessService();
        access.SetCircuitContext(User("alice"));

        Assert.Equal("alice", CircuitUser.ResolveUserId(access));
    }

    [Fact]
    public void CircuitContext_Wins_Over_Context()
    {
        // Context can carry a leaked identity from an unrelated delivery; the durable circuit
        // identity is authoritative.
        var access = new AccessService();
        access.SetCircuitContext(User("alice"));
        access.SetContext(User("bob"));

        Assert.Equal("alice", CircuitUser.ResolveUserId(access));
    }

    [Fact]
    public void Falls_Back_To_Context_When_No_CircuitContext()
    {
        // Code reached from within a hub delivery (no circuit activity) still resolves.
        var access = new AccessService();
        access.SetContext(User("bob"));

        Assert.Equal("bob", CircuitUser.ResolveUserId(access));
    }

    [Fact]
    public void Skips_System_And_Hub_Principals()
    {
        // A leaked system-security / hub-shaped principal is never a real user — fall through to
        // the next candidate rather than acting as the platform.
        var access = new AccessService();
        access.SetCircuitContext(User(WellKnownUsers.System));
        access.SetContext(User("carol"));

        Assert.Equal("carol", CircuitUser.ResolveUserId(access));

        access.SetCircuitContext(User("portal/some-circuit-id"));
        Assert.Equal("carol", CircuitUser.ResolveUserId(access));
    }

    [Fact]
    public void Null_When_No_Identity_At_All()
    {
        Assert.Null(CircuitUser.ResolveUserId(new AccessService()));
        Assert.Null(CircuitUser.ResolveUserId(null));
    }
}
