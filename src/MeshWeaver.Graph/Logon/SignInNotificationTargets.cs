using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Who has asked to hear <see cref="MeshWeaver.Mesh.UserSignedIn"/>. Empty by default — core
/// notifies nobody until something opts in.
///
/// <para>🚨 <b>Opt-in, rather than core posting to a known address.</b> The obvious shape was for
/// core to post straight to the Store's root hub and tolerate a NotFound when the Store is absent.
/// That works, and it quietly puts "the Store exists, and this is where it lives" into core — the
/// same knowledge core deliberately does not hold anywhere else about apps (which is why
/// <c>AppRecordSpecs</c> covers the platform defaults only and nothing else). A subscriber
/// registering itself keeps the direction of knowledge right: the Store knows about core, core does
/// not know about the Store.</para>
///
/// <para>It also generalises for free. The second thing that wants a sign-in hook — and there will
/// be one — adds a line at its own registration instead of another address hard-coded here.</para>
///
/// <para>Instance state on a mesh-scoped singleton, never static: it dies with the mesh, so nothing
/// leaks between tests (NoStaticState).</para>
/// </summary>
public sealed class SignInNotificationTargets
{
    private ImmutableList<Address> targets = [];

    /// <summary>The registered addresses, in registration order.</summary>
    public IReadOnlyList<Address> Targets => targets;

    /// <summary>Adds an address to notify. Idempotent — registering the same address twice does not
    /// double-post, which matters because a plugin's configuration can run more than once.</summary>
    public SignInNotificationTargets Add(Address address)
    {
        if (address is null)
            return this;
        if (!targets.Contains(address))
            targets = targets.Add(address);
        return this;
    }
}
